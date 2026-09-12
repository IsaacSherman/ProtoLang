using ProtoLang.Diagnostics;
using ProtoLang.Symbols;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Where the name under the caret was declared, in this buffer or in the <c>.proto</c> it came
/// from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The boundary crossing is the point of it.</b> Most of what a ProtoLang file talks about is
/// declared somewhere else: a field, an enum constant, a message and an enum are all written in a
/// <c>.proto</c> this compiler reads and does not own. #41 made those declarations answerable and
/// this is what asks. A local, a parameter, a loop binding and a method are declared in the buffer
/// itself, and #39 made those answerable; <see cref="DeclaredSymbol"/> is where the two are joined
/// so that neither this nor <see cref="HoverCard"/> has to know which side a symbol falls on.
/// </para>
/// <para>
/// <b>Unresolvable means no result, never a guess.</b> Everything here comes from what the binder
/// actually resolved, so a half-typed name, a misspelt one and a scalar type spelling all produce
/// nothing -- <c>int64</c> has no declaration, and sending the client somewhere plausible is worse
/// than sending it nowhere. So does a schema element whose file is not readable, which is ordinary:
/// a well-known type protoc resolved from descriptors compiled into itself is real and is nowhere
/// on this machine.
/// </para>
/// <para>
/// <b>Two ranges where the client can take them.</b> A declaration has an extent and a name inside
/// it, both recorded and neither derivable from the other, and LSP carries both only in a
/// <see cref="LocationLink"/> -- which a client gets if it declared <c>linkSupport</c> and not
/// otherwise, because the two shapes are not interchangeable on the wire. A client that took the
/// plain shape is sent the <em>name</em>: arriving with a whole method body selected is worse than
/// arriving with its name selected.
/// </para>
/// <para>
/// Read in order and answered out of it, bounded and refused when stale, all of it on the same terms
/// as <see cref="HoverProvider"/>. What differs is only that a click is deliberate where a hover is
/// incidental, which is why the two have outstanding work of their own rather than sharing it.
/// </para>
/// </remarks>
public sealed class DefinitionProvider
{
    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    /// <inheritdoc cref="LinkSupport"/>
    private volatile bool _linkSupport;

    public DefinitionProvider(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        int concurrency = DeferredAnswers.DefaultConcurrency,
        DocumentSemantics? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(loaders);

        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _semantics = semantics ?? new DocumentSemantics(loaders);
        _deferred = new DeferredAnswers("go to definition", documents, configuration, concurrency);
    }

    /// <summary>Whether the client accepts the shape that carries both of a declaration's ranges.</summary>
    /// <remarks>
    /// Set at <c>initialize</c>, on the worker that reads the wire, and read on whichever thread ends
    /// up answering -- so the field behind it is volatile, for the reason
    /// <see cref="LanguageServerHost.State"/>'s is. Nothing here would tear, and what would happen
    /// without it is subtler and worse: the first few answers of a session sent in the shape the
    /// client did not ask for, which it discards in silence.
    /// </remarks>
    public bool LinkSupport
    {
        get => _linkSupport;
        set => _linkSupport = value;
    }

    /// <inheritdoc cref="DeferredAnswers.Outstanding"/>
    public int Outstanding => _deferred.Outstanding;

    /// <inheritdoc cref="DeferredAnswers.InFlight"/>
    public int InFlight => _deferred.InFlight;

    /// <inheritdoc cref="DeferredAnswers.PeakInFlight"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <inheritdoc cref="PositionRequest.Read"/>
    public PositionRequest? Read(TextDocumentPositionParams message)
        => PositionRequest.Read(_documents, _configuration, message);

    /// <summary>
    /// Where the name at the position <see cref="Read"/> settled was declared, or null for nowhere.
    /// </summary>
    /// <remarks>
    /// A list of one or a null, never a bare location. One name has one declaration in this language
    /// -- there is no overloading (spec 16.1) and no partial declaration -- so the list is a
    /// container rather than a claim that several places answer, and it is the shape LSP asks for
    /// when links are in play.
    /// </remarks>
    /// <inheritdoc cref="DeferredAnswers.AnswerAsync" path="/exception"/>
    public Task<object?> AnswerAsync(PositionRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(asked, token => Answer(asked, token), cancellationToken);
    }

    /// <inheritdoc cref="DeferredAnswers.Forget"/>
    public void Forget(DocumentUri document) => _deferred.Forget(document);

    private object? Answer(PositionRequest asked, CancellationToken cancellationToken)
    {
        var compiled = _semantics.For(asked.Document, asked.Configuration, cancellationToken);

        if (DeclaredSymbol.At(compiled, asked.Offset) is not { } symbol
            || Target(symbol, asked.Uri) is not { } target)
        {
            return null;
        }

        return LinkSupport
            ? (object)new[]
            {
                new LocationLink(
                    EditorPositions.RangeOf(symbol.Span),
                    target.Uri,
                    EditorPositions.RangeOf(target.Extent),
                    EditorPositions.RangeOf(target.Name)),
            }
            : new[] { new Location(target.Uri, EditorPositions.RangeOf(target.Name)) };
    }

    /// <summary>Where a symbol is written, whichever compiler owns the file it is written in.</summary>
    /// <remarks>
    /// The ProtoLang side first, because it is the side with no reason to fail: a declaration this
    /// compilation produced is in the buffer that produced it. The schema side answers for
    /// everything else and may decline, which is what an unreadable <c>.proto</c> looks like from
    /// here.
    /// </remarks>
    private static Declared? Target(DeclaredSymbol symbol, DocumentUri asked)
    {
        if (symbol.Site is { } site)
        {
            return new Declared(UriOf(site.Document, asked), site.Extent, site.Name.Span);
        }

        return symbol.Schema?.Site is { } declared
            ? new Declared(DocumentUri.FromPath(declared.Path).ToString(), declared.Extent, declared.Name)
            : null;
    }

    /// <summary>Which document a ProtoLang declaration is in, as the client spells documents.</summary>
    /// <remarks>
    /// <para>
    /// <b>The spelling the client sent, wherever that is the file being described.</b> A URI derived
    /// from a path is this server's spelling of it, and the two differ: an editor sends
    /// <c>file:///c%3A/Users/...</c> and <see cref="DocumentUri.FromPath"/> produces
    /// <c>file:///C:/Users/...</c>. A client that matches the target against its own open documents
    /// by string then fails to recognize the buffer the caret is already in, and opens a second
    /// editor onto the same file. The request already carries the spelling that works, so nothing is
    /// gained by re-deriving one.
    /// </para>
    /// <para>
    /// Whether it <em>is</em> the same file is asked of <see cref="PathIdentity"/> rather than
    /// assumed, although a compilation holds one source today: #27 makes it stop being true, and a
    /// declaration that silently claimed to be in the wrong file would navigate to the right line of
    /// the wrong buffer. A source with no path is an unsaved buffer, whose only handle is the URI the
    /// client opened it under -- which is this one.
    /// </para>
    /// </remarks>
    private static string UriOf(SourceIdentity source, DocumentUri asked)
        => source.Path is { } path && !PathIdentity.AreSame(path, asked.Path)
            ? DocumentUri.FromPath(path).ToString()
            : asked.ToString();

    /// <summary>A declaration reduced to what the wire carries: a document and two ranges.</summary>
    private sealed record Declared(string Uri, SourceSpan Extent, SourceSpan Name);
}
