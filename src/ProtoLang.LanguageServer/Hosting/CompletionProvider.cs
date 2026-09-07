using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// What could be typed at a position. Today that is one thing: the schemas an <c>import proto</c>
/// path could name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Context first, candidates second.</b> This asks which of the language's completion contexts the
/// cursor is in before it asks what belongs there, and it currently recognizes exactly one. That is
/// one indirection more than #56 needs on its own and one less reshaping than #43 would otherwise
/// have to do: member completion after a dot, a bare identifier, a type position are three more
/// contexts, and each is an arm here rather than a second entry point beside it. A cursor in no
/// recognized context offers nothing, which is the only correct answer -- an editor that guesses
/// produces a list the user has to dismiss on every keystroke.
/// </para>
/// <para>
/// <b>Nothing is compiled and protoc never runs.</b> The buffer is lexed and the include roots are
/// listed, both of which answer while the user is still typing; waiting on a schema load would make
/// completion arrive after the character that invalidated it. It follows that a document whose
/// configuration file was refused still completes, which is deliberate: a broken
/// <c>protolang.config.xml</c> stops compilation, and being unable to fix an import while it is
/// broken would be the second problem caused by the first.
/// </para>
/// <para>
/// <b>It answers about the version it read, or it refuses.</b> This is the first handler in this
/// server that can genuinely go stale -- semantic tokens reads and answers in one instant, while this
/// one goes to the file system in between -- so it re-reads the version before replying and returns
/// LSP's <c>ContentModified</c> when the buffer has moved. Spec 26.1 requires exactly that, and the
/// stakes are higher than for diagnostics: a stale completion does not merely mislead, it inserts
/// text at an offset that no longer means what it meant.
/// </para>
/// </remarks>
public sealed class CompletionProvider
{
    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly LoaderPool _loaders;

    public CompletionProvider(DocumentStore documents, ConfigurationSync configuration, LoaderPool loaders)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
    }

    /// <summary>The characters that should make a client ask without being asked to.</summary>
    /// <remarks>
    /// The two that open a path segment. The quote starts a path and the separator starts a segment
    /// inside one, and since candidates are enumerated a directory at a time, the separator is
    /// precisely the keystroke after which the previous answer stopped describing anything.
    /// </remarks>
    public static IReadOnlyList<string> TriggerCharacters { get; } = ["\"", "/"];

    /// <summary>Where candidates come from.</summary>
    /// <remarks>
    /// <see cref="SchemaCatalog.Enumerate"/>, and a seam for a test. It is the one step here that
    /// leaves this process -- lexing and resolving are arithmetic, listing a directory is not -- so it
    /// is also the only window in which the buffer can move while a request is being answered. A test
    /// that has to prove the refusal above has to be able to move it inside that window, and there is
    /// nowhere else to stand.
    /// </remarks>
    public Func<string, IReadOnlyList<string>, CancellationToken, SchemaListing> Enumerate { get; set; }
        = (directory, roots, cancellationToken)
            => SchemaCatalog.Enumerate(directory, roots, cancellationToken: cancellationToken);

    /// <summary>Everything that could be typed at one position.</summary>
    /// <exception cref="JsonRpcException">
    /// The buffer moved while this was being answered. See the type's remarks.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The client withdrew the request. A superseded completion is one the client has already stopped
    /// showing, and finishing a directory walk for it is work nobody is waiting on.
    /// </exception>
    public CompletionList Complete(CompletionParams message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        cancellationToken.ThrowIfCancellationRequested();

        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri) || _documents.Find(uri) is not { } document)
        {
            // Closed between asking and being answered, or never opened. An empty list rather than an
            // error: the client has done nothing wrong, and there is genuinely nothing to offer.
            return Nothing;
        }

        var offset = document.Lines.OffsetOf(message.Position.Line + 1, message.Position.Character + 1);

        if (!ImportPathContext.TryFind(document.Text, offset, out var context))
        {
            return Nothing;
        }

        var items = Schemas(document, context!, cancellationToken);

        if (_documents.Find(uri) is not { } current || current.Version != document.Version)
        {
            throw new JsonRpcException(
                new ResponseError(
                    ErrorCodes.ContentModified,
                    $"'{uri}' was edited while this completion was being answered, so the answer "
                        + "describes text that is no longer there. Ask again."));
        }

        return new CompletionList { Items = items };
    }

    private static CompletionList Nothing { get; } = new() { Items = [] };

    /// <summary>What the include roots hold at the directory the cursor is in.</summary>
    /// <remarks>
    /// <para>
    /// The roots are the compilation's own, asked for through the same two functions the compilation
    /// asks -- so a candidate offered here is one <c>SchemaLookup</c> would find, in the root it would
    /// find it in. A loader that could not be built costs only the well-known schemas; the user's own
    /// roots still answer, which is the case that matters most, because a workspace with no protoc is
    /// one where nothing else is telling the user anything at all.
    /// </para>
    /// <para>
    /// <b>The cheap half of the configuration, not the whole of it.</b>
    /// <c>WorkspaceConfiguration.Resolve</c> also settles the language policy, which means searching
    /// upward for a <c>protolang.config.xml</c> and parsing it -- a directory walk and an XML parse,
    /// for a value completion never reads, on a request that runs per keystroke rather than per
    /// debounced compile. <c>ResolveImportRoots</c> is the same resolution minus that step, and it
    /// shares the two resolvers with <c>Resolve</c> rather than restating their precedence.
    /// </para>
    /// </remarks>
    private IReadOnlyList<CompletionItem> Schemas(
        OpenDocument document,
        ImportPathContext context,
        CancellationToken cancellationToken)
    {
        var settings = _configuration.Current.ResolveImportRoots(document.Uri);

        _loaders.TryGet(settings.ProtocPath, out var loader, out _);

        var searchPaths = Compilation.GetSearchPaths(
            document.ToSource(settings.Folder?.Path).Identity,
            settings.IncludeDirectories);

        var roots = SchemaCatalog.RootsFor(searchPaths, loader);

        // A listing cut short by its budget is still offered. The list is already declared incomplete
        // to the client, which re-asks on the next keystroke, and a person narrowing a very wide
        // directory by typing is better served by a prefix of it than by nothing. The near match on a
        // failed import makes the opposite choice, and says why.
        return
        [
            .. Enumerate(context.Directory, roots, cancellationToken).Candidates
                .Select(candidate => Item(candidate, context, document)),
        ];
    }

    private static CompletionItem Item(SchemaCandidate candidate, ImportPathContext context, OpenDocument document)
    {
        // Through PathIdentity rather than ordinally, because "is this the same path?" has one home
        // and the answer differs by platform. On a volume that folds case, an import already written
        // as 'Billing/Invoice.proto' resolves to the file offered here as 'billing/invoice.proto',
        // and an ordinal comparison would offer it again unmarked -- which is the one thing this line
        // exists to prevent.
        var imported = !candidate.IsDirectory
            && context.Imported.Contains(candidate.Path, PathIdentity.Comparer);

        return new CompletionItem
        {
            Label = Segment(candidate.Path),
            Kind = candidate.IsDirectory ? CompletionItemKind.Folder : CompletionItemKind.File,
            Detail = imported ? $"already imported, from {candidate.Root}" : candidate.Root,
            Documentation = Shadowing(candidate),

            // The whole path, not the segment the label shows. By the time a second segment is being
            // typed the user has a separator on screen, and a client filtering a multi-segment word
            // against a one-segment string discards every item it was about to show.
            FilterText = candidate.Path,

            // Directories ahead of schemas, because a directory is a step towards an answer and a
            // schema is one. Within each, the path's own order.
            SortText = (candidate.IsDirectory ? "0" : "1") + candidate.Path,
            InsertTextFormat = InsertTextFormat.PlainText,
            TextEdit = new TextEdit(Replacing(context, document), candidate.Path),
        };
    }

    /// <summary>The last segment of a path, keeping the separator that says it is a directory.</summary>
    private static string Segment(string path)
        => path[(path.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>What else holds this path, for a user who cannot otherwise tell.</summary>
    private static string? Shadowing(SchemaCandidate candidate)
        => candidate.ShadowedRoots.Count == 0
            ? null
            : $"Also in {string.Join(", ", candidate.ShadowedRoots)}. This one wins, because include "
                + "paths are searched in order and the first match is taken.";

    /// <summary>
    /// The whole path between the quotes, whatever part of it the cursor is in.
    /// </summary>
    /// <remarks>
    /// Not the segment being typed, and not the text before the cursor. A candidate is a complete
    /// path, so applying one has to replace a complete path -- otherwise picking
    /// <c>billing/invoice.proto</c> while the caret sits inside <c>billing/inv|oice.proto</c> leaves
    /// the tail of what was already there.
    /// </remarks>
    private static Range Replacing(ImportPathContext context, OpenDocument document)
        => new(At(document, context.Start), At(document, context.End));

    private static Position At(OpenDocument document, int offset)
    {
        var position = document.Lines.PositionOf(offset);

        return new Position(position.Line - 1, position.Column - 1);
    }
}
