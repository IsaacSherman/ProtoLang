using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// What the editor shows when the pointer rests somewhere: served, bounded, and about the buffer it
/// was asked of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read in order, answered out of it</b>, the same split completion obeys and for the same
/// reason. <see cref="Read"/> runs on the one worker that reads the wire and settles which buffer
/// the position is in before the next message is dequeued; without that, a <c>didChange</c> queued
/// behind the request would be applied first and the offset would be measured against text the
/// client had not sent when it asked. Everything after that -- resolving the configuration,
/// compiling, hashing the schemas -- leaves this process and so must not hold the reader.
/// </para>
/// <para>
/// <b>What it costs, and why it usually costs nothing.</b> A hover is a question only the binder can
/// answer, so it compiles the buffer it was read against -- through
/// <see cref="DocumentSemantics"/>, which is where the scheduler already put the compile the last
/// keystroke's debounce produced. A reader who has stopped typing long enough to point at something
/// is a reader whose buffer has already been compiled, so the ordinary hover is a dictionary lookup,
/// a configuration resolution and a hash per schema. The one that is not -- the first hover in a
/// session, or the first after an edit -- pays a lex, a parse and a bind, and that is why it is
/// answered off the worker rather than on it.
/// </para>
/// <para>
/// Everything about supersession, the concurrency bound and the staleness refusal is
/// <see cref="DeferredAnswers"/>'s, stated once there for all three surfaces that leave the process.
/// A hover superseded by the next hover is the ordinary case: a pointer crossing a line of code
/// produces one request per token it passes over.
/// </para>
/// </remarks>
public sealed class HoverProvider
{
    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    public HoverProvider(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        int concurrency = DeferredAnswers.DefaultConcurrency,
        DocumentSemantics? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(loaders);

        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        // Shared where it is given, for the reason the scheduler and completion both take it: a
        // compile a keystroke scheduled and a card asked for between two keystrokes should be one
        // compile. A caller with no interest in that gets one of its own.
        _semantics = semantics ?? new DocumentSemantics(loaders);

        _deferred = new DeferredAnswers("hover", documents, configuration, concurrency);
    }

    /// <inheritdoc cref="DeferredAnswers.Outstanding"/>
    public int Outstanding => _deferred.Outstanding;

    /// <inheritdoc cref="DeferredAnswers.InFlight"/>
    public int InFlight => _deferred.InFlight;

    /// <inheritdoc cref="DeferredAnswers.PeakInFlight"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <summary>
    /// Which buffer a request is about and where in it -- settled while messages are still being
    /// read in order -- or null when the document is not open.
    /// </summary>
    /// <inheritdoc cref="CompletionProvider.Read" path="/remarks"/>
    public PositionRequest? Read(TextDocumentPositionParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri)
            || _documents.Find(uri) is not { } document)
        {
            // Closed before it was read, or never opened. Nothing rather than an error: the client
            // has done nothing wrong, and there is genuinely nothing to say.
            return null;
        }

        return new PositionRequest(
            uri!, document, _configuration.Current, EditorPositions.OffsetOf(document.Lines, message.Position));
    }

    /// <summary>What to say about the position <see cref="Read"/> settled, or null for nothing.</summary>
    /// <inheritdoc cref="DeferredAnswers.AnswerAsync" path="/exception"/>
    public Task<Hover?> AnswerAsync(PositionRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(
            asked,
            token => HoverCard.For(_semantics.For(asked.Document, asked.Configuration, token), asked.Offset),
            cancellationToken);
    }

    /// <inheritdoc cref="DeferredAnswers.Forget"/>
    public void Forget(DocumentUri document) => _deferred.Forget(document);
}
