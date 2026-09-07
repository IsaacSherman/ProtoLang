using System.Collections.Concurrent;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// One completion request, pinned to the buffer and the configuration it was asked about.
/// </summary>
/// <remarks>
/// Everything here is settled while messages are still being read in order, and none of it can move
/// afterwards: <see cref="OpenDocument"/> and <see cref="WorkspaceConfiguration"/> are both immutable,
/// so holding the objects holds the question. What comes later -- listing directories -- may take as
/// long as it takes without changing what the answer is about, and can be checked against these two
/// before it is sent.
/// </remarks>
public sealed class CompletionRequest
{
    internal CompletionRequest(
        DocumentUri uri,
        OpenDocument document,
        WorkspaceConfiguration configuration,
        ImportPathContext context)
    {
        Uri = uri;
        Document = document;
        Configuration = configuration;
        Context = context;
    }

    public DocumentUri Uri { get; }

    /// <summary>The buffer this is about, as an object rather than as a version.</summary>
    public OpenDocument Document { get; }

    /// <summary>The settings this is about, as an object rather than as a generation.</summary>
    public WorkspaceConfiguration Configuration { get; }

    internal ImportPathContext Context { get; }
}

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
/// <b>Read in order, answered out of it.</b> <see cref="Read"/> runs on the one worker that reads the
/// wire and <see cref="AnswerAsync"/> runs anywhere, and the split is where the correctness lives. A
/// request is about a position in a buffer, and which buffer that is has to be settled before the
/// next message is dequeued -- otherwise a <c>didChange</c> queued behind the request lands first,
/// the position is measured against text the client was not looking at, and every check afterwards
/// agrees with itself because it is asking about the wrong document.
/// </para>
/// <para>
/// <b>It answers about what it read, or it refuses.</b> This is the first handler in this server that
/// can genuinely go stale -- semantic tokens reads and answers in one instant, while this one goes to
/// the file system in between -- so before replying it checks that the store still holds the same
/// document object and that the configuration has not moved on, and returns LSP's
/// <c>ContentModified</c> otherwise. The check is object identity rather than a version number
/// because a version number is only unique within one open session: close a document and reopen it and
/// the client starts again at one, which compares equal to the version this request read and describes
/// entirely different text. Spec 26.1 requires the refusal, and the stakes are higher than for
/// diagnostics -- a stale completion does not merely mislead, it inserts text at an offset that has
/// stopped meaning what it meant.
/// </para>
/// <para>
/// <b>One walk per document, four at a time.</b> A completion supersedes the outstanding one for its
/// document, exactly as a keystroke supersedes a scheduled compile, so a client that asks on every
/// character cannot accumulate walks; across documents a semaphore bounds how many run at once. Both
/// exist because the per-walk budget bounds one walk and says nothing about how many there are, and a
/// slow root turns that into threads and open handles piling up behind a user who is simply typing.
/// </para>
/// <para>
/// <b>Waiting for a slot is part of the request, not part of the queue.</b> Most of a busy request's
/// life is spent waiting, and most of the reasons a request ends -- withdrawn, superseded, closed --
/// arrive while it waits. So the wait sits inside the same cleanup as the walk: whichever way it ends
/// the entry is retired, and the slot is given back only if it was ever taken. The alternative, an
/// unconditional release, hands the pool a slot for a wait that failed and quietly raises the limit by
/// one for every completion the client thought better of.
/// </para>
/// </remarks>
public sealed class CompletionProvider
{
    /// <summary>How many completions may be walking the file system at once.</summary>
    /// <remarks>
    /// The same figure and the same reasoning as <see cref="CompileScheduler.DefaultConcurrency"/>:
    /// ten open documents must not mean ten simultaneous walks of an include root. #57 pins it, and
    /// <see cref="PeakInFlight"/> is what shows whether whatever it is pinned to is honoured.
    /// </remarks>
    public const int DefaultConcurrency = 4;

    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly LoaderPool _loaders;
    private readonly SemaphoreSlim _concurrency;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _outstanding = new(StringComparer.Ordinal);

    private int _inFlight;
    private int _peakInFlight;

    public CompletionProvider(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        int concurrency = DefaultConcurrency)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _concurrency = new SemaphoreSlim(concurrency, concurrency);
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

    /// <summary>Documents with a completion still outstanding.</summary>
    /// <remarks>
    /// At most one per document, because a newer request for a document replaces the entry the last
    /// one left. The bound is therefore the number of open documents however fast anybody types --
    /// the same bound, for the same reason, as <see cref="CompileScheduler.Pending"/>.
    /// </remarks>
    public int Outstanding => _outstanding.Count;

    /// <summary>Walks that are past the gate and have not yet returned.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The most walks that have ever been past the gate at one moment.</summary>
    /// <inheritdoc cref="CompileScheduler.PeakInFlight" path="/remarks"/>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>Nothing to offer, which is not the same as a failure to offer it.</summary>
    public static CompletionList Nothing { get; } = new() { Items = [] };

    /// <summary>
    /// Which buffer a request is about and where in it -- settled while messages are still being read
    /// in order -- or null when the position is nowhere anything can be offered.
    /// </summary>
    /// <remarks>
    /// <b>This half must not be deferred.</b> Everything after it is allowed to take as long as the
    /// file system does, and none of it is allowed to decide <em>which text</em> the answer is about:
    /// a <c>didChange</c> sitting behind this request in the queue would otherwise be applied first,
    /// and the position would be measured against a buffer the client had not sent when it asked. The
    /// configuration is captured here for the same reason and by the same means -- it is immutable and
    /// stamped with a generation, so holding the object holds the answer.
    /// </remarks>
    public CompletionRequest? Read(CompletionParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri) || _documents.Find(uri) is not { } document)
        {
            // Closed before it was read, or never opened. Nothing rather than an error: the client has
            // done nothing wrong, and there is genuinely nothing to offer.
            return null;
        }

        var offset = document.Lines.OffsetOf(message.Position.Line + 1, message.Position.Character + 1);

        return ImportPathContext.TryFind(document.Text, offset, out var context)
            ? new CompletionRequest(uri!, document, _configuration.Current, context!)
            : null;
    }

    /// <summary>Everything that could be typed at the position <see cref="Read"/> settled.</summary>
    /// <exception cref="JsonRpcException">
    /// The buffer or the configuration moved while this was being answered. See the type's remarks.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The client withdrew the request, or a newer one for the same document superseded it. Either
    /// way nobody is waiting on the answer, and finishing the walk is work spent on nothing.
    /// </exception>
    public async Task<CompletionList> AnswerAsync(CompletionRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        var work = Supersede(asked.Uri, cancellationToken);
        var token = work.Token;
        var acquired = false;

        try
        {
            // Inside the cleanup, because waiting is where a request spends most of its life and
            // cancelling one is the ordinary case: withdrawn by the client, superseded by the next
            // keystroke, or abandoned because the document closed. Left outside, every one of those
            // leaves its entry behind, and an entry nothing will ever retire makes this document look
            // permanently busy to anything that counts what is outstanding.
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            // Task.Run rather than trusting the await above to have yielded. A semaphore with a slot
            // free completes synchronously, and the continuation would then run the walk on whichever
            // thread called this -- which is the one reading the wire, and the whole reason this is
            // two methods.
            return await Task.Run(() => Answer(asked, token), token).ConfigureAwait(false);
        }
        finally
        {
            // Only what was taken is given back. Releasing unconditionally would hand the pool a slot
            // for a wait that never succeeded, and the limit would climb by one for every request the
            // client withdrew.
            if (acquired)
            {
                _concurrency.Release();
            }

            Retire(asked.Uri, work);
        }
    }

    private CompletionList Answer(CompletionRequest asked, CancellationToken cancellationToken)
    {
        // Before the walk as well as after it. A request that queued behind a slow one may have been
        // waiting for a while, and a buffer that moved in the meantime has already decided the answer:
        // walking a root to produce something that will be refused spends the slot a live request is
        // waiting for. Cancellation covers the cases that have a token -- withdrawal, supersession, a
        // close -- and an edit is the one that does not.
        Require(asked);

        Enter();

        try
        {
            var items = Schemas(asked, cancellationToken);

            Require(asked);

            return new CompletionList { Items = items };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Refuses an answer about a document, or a configuration, that has moved on.</summary>
    /// <remarks>
    /// Object identity rather than a version number, and the difference is not academic: closing a
    /// document and reopening it starts the client's numbering again at one, so a request read at
    /// version one and answered after a close and a reopen compares equal to a buffer that may hold
    /// something else entirely. The store hands out a fresh <see cref="OpenDocument"/> for every edit
    /// and every open, so "is this still the object I read?" answers editing, closing and reopening in
    /// one question, and spec 26.1's rule about a closed document falls out of it.
    /// </remarks>
    private void Require(CompletionRequest asked)
    {
        if (!ReferenceEquals(_documents.Find(asked.Uri), asked.Document))
        {
            throw Refuse(
                $"'{asked.Uri}' was edited, closed or reopened while this completion was being "
                    + "answered, so the answer describes text that is no longer there.");
        }

        // The same question the compile scheduler asks, and for the same reason: an include path
        // removed while this ran would otherwise be advertised as a place to import from.
        if (_configuration.Current.Generation != asked.Configuration.Generation)
        {
            throw Refuse(
                $"The configuration for '{asked.Uri}' changed while this completion was being "
                    + "answered, so the roots it searched are not the ones that now apply.");
        }
    }

    private static JsonRpcException Refuse(string reason)
        => new(new ResponseError(ErrorCodes.ContentModified, reason + " Ask again."));

    /// <summary>Abandons a document's outstanding completion, because it is no longer open.</summary>
    /// <remarks>
    /// The same obligation <see cref="CompileScheduler.ForgetAsync"/> already discharges for compiles,
    /// and spec 26.1 states it once for both: what is outstanding for a document is abandoned when the
    /// document closes. Without it a completion queued behind a slow one still takes its turn, walks a
    /// root for a buffer the editor has shut, and is refused at the end -- having spent one of the few
    /// slots a live request was waiting for. Refusing it later is correct and too late.
    /// </remarks>
    public void Forget(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_outstanding.TryRemove(document.Key, out var work))
        {
            Cancel(work);
        }
    }

    /// <summary>
    /// Takes this document's outstanding slot, cancelling whoever had it, and links the client's own
    /// withdrawal to it.
    /// </summary>
    /// <inheritdoc cref="CompileScheduler.Supersede" path="/remarks"/>
    private CancellationTokenSource Supersede(DocumentUri document, CancellationToken cancellationToken)
    {
        var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _outstanding.AddOrUpdate(
            document.Key,
            work,
            (_, previous) =>
            {
                Cancel(previous);
                return work;
            });

        return work;
    }

    /// <remarks>
    /// Both halves of "remove it only if it is still mine" in one operation, because a newer request
    /// lands between a look and a remove and this would then retire that one instead -- leaving a walk
    /// nothing holds a handle to, which no later keystroke can supersede.
    /// </remarks>
    private void Retire(DocumentUri document, CancellationTokenSource work)
        => _outstanding.TryRemove(KeyValuePair.Create(document.Key, work));

    private static void Cancel(CancellationTokenSource work)
    {
        try
        {
            work.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc cref="CompileScheduler.Enter" path="/remarks"/>
    private void Enter()
    {
        var current = Interlocked.Increment(ref _inFlight);

        var peak = Volatile.Read(ref _peakInFlight);
        while (current > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peakInFlight, current, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }

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
    private IReadOnlyList<CompletionItem> Schemas(CompletionRequest asked, CancellationToken cancellationToken)
    {
        var document = asked.Document;
        var context = asked.Context;

        // Resolved from the configuration this request read rather than from whatever is current, so
        // the roots walked and the roots checked at the end are the same roots.
        var settings = asked.Configuration.ResolveImportRoots(asked.Uri);

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
