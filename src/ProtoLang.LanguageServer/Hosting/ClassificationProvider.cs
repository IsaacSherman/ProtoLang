using System.Collections.Concurrent;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>A request to classify a whole document, and what the client already holds of it.</summary>
/// <remarks>
/// The whole-file sibling of <see cref="PositionRequest"/>: no caret, because classification is about
/// the document rather than about a place in it, and one extra member, because a delta request names
/// the answer it is building on. One shape for both methods -- the difference between them is
/// whether <see cref="PreviousResultId"/> is there.
/// </remarks>
public sealed class ClassificationRequest : DocumentRequest
{
    private ClassificationRequest(
        DocumentUri uri,
        OpenDocument document,
        WorkspaceConfiguration configuration,
        string? previousResultId)
        : base(uri, document, configuration)
    {
        PreviousResultId = previousResultId;
    }

    /// <summary>
    /// The identifier of the answer the client is holding, or null when it asked for a whole one.
    /// </summary>
    public string? PreviousResultId { get; }

    /// <inheritdoc cref="PositionRequest.Read"/>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static ClassificationRequest? Read(
        DocumentStore documents,
        ConfigurationSync configuration,
        TextDocumentIdentifier document,
        string? previousResultId)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(document);

        if (!DocumentUri.TryParse(document.Uri, out var uri) || documents.Find(uri) is not { } open)
        {
            return null;
        }

        return new ClassificationRequest(uri!, open, configuration.Current, previousResultId);
    }
}

/// <summary>
/// What the editor colours a document with: served, bounded, and about the buffer it was asked of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read in order, answered out of it</b>, the same split hover, go-to-definition and completion
/// obey. <see cref="Read(SemanticTokensParams)"/> runs on the one worker that reads the wire and
/// settles which buffer is being classified before the next message is dequeued; the compile and the
/// walk happen anywhere. What that buys, and what it costs, is
/// <see cref="DeferredAnswers"/>'s to state -- supersession per document, a bounded gate, abandoning
/// work nobody waits for, and refusing rather than answering about text the user has replaced.
/// </para>
/// <para>
/// <b>This is the request that stopped being free.</b> #42 answered it from the lexer, on the ordered
/// worker, in the same instant it was read; refining an identifier needs the binder, so it now
/// compiles through <see cref="DocumentSemantics"/> like the other three. The compile is usually the
/// one a keystroke already scheduled, since that is what <see cref="DocumentSemantics"/> is for. What
/// it is never allowed to cost is colour: a compilation that produced nothing produces no references,
/// and a file with no references is classified exactly as #42 classified it.
/// </para>
/// <para>
/// <b>One answer per request, which is where "no flicker" comes from.</b> A client is not sent a
/// lexical answer and then a refined one; it is sent the refined answer or, if the buffer moved while
/// it was being made, a refusal it will re-ask after. The delta support below is a saving in
/// bandwidth and not a second paint.
/// </para>
/// </remarks>
public sealed class ClassificationProvider
{
    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    /// <summary>The last answer each open document was given, by the name it was given under.</summary>
    /// <inheritdoc cref="Publish" path="/remarks/para[2]"/>
    private readonly ConcurrentDictionary<string, Painted> _published =
        new(StringComparer.Ordinal);

    private volatile ClientLegend _client = ClientLegend.Everything;
    private volatile bool _deltas;
    private int _results;

    public ClassificationProvider(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        int concurrency = DeferredAnswers.DefaultConcurrency,
        DocumentSemantics? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(loaders);

        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        // Shared where it is given, for the reason hover and go-to-definition take it: a compile a
        // keystroke scheduled and a classification asked for between two keystrokes should be one
        // compile.
        _semantics = semantics ?? new DocumentSemantics(loaders);

        _deferred = new DeferredAnswers("classification", documents, configuration, concurrency);
    }

    /// <summary>What this client declared it can paint.</summary>
    /// <inheritdoc cref="DefinitionProvider.LinkSupport" path="/remarks"/>
    public ClientLegend Client
    {
        get => _client;
        set => _client = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Whether this client asked to be sent differences rather than whole answers.</summary>
    /// <remarks>
    /// Nothing is retained for a client that did not ask. The retention exists only so a later delta
    /// request has something to be a difference from, and holding one integer array per open document
    /// for a client that will never send that request is holding what nobody can ask for.
    /// </remarks>
    public bool Deltas
    {
        get => _deltas;
        set => _deltas = value;
    }

    /// <inheritdoc cref="DeferredAnswers.Outstanding"/>
    public int Outstanding => _deferred.Outstanding;

    /// <inheritdoc cref="DeferredAnswers.InFlight"/>
    public int InFlight => _deferred.InFlight;

    /// <inheritdoc cref="DeferredAnswers.PeakInFlight"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <summary>Documents whose last answer is being kept in case a delta is asked for.</summary>
    /// <remarks>
    /// Published so that "nothing is retained for a client that did not ask, and nothing survives a
    /// close" is a measurement rather than an argument, for the reason
    /// <see cref="LanguageServerHost.Semantics"/> is published.
    /// </remarks>
    public int Retained => _published.Count;

    /// <inheritdoc cref="ClassificationRequest.Read"/>
    public ClassificationRequest? Read(SemanticTokensParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return ClassificationRequest.Read(
            _documents, _configuration, message.TextDocument, previousResultId: null);
    }

    /// <inheritdoc cref="ClassificationRequest.Read"/>
    public ClassificationRequest? Read(SemanticTokensDeltaParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return ClassificationRequest.Read(
            _documents, _configuration, message.TextDocument, message.PreviousResultId);
    }

    /// <summary>
    /// The classification of the buffer <see cref="Read(SemanticTokensParams)"/> settled: a whole
    /// answer, or the difference from the one the client says it holds.
    /// </summary>
    /// <inheritdoc cref="DeferredAnswers.AnswerAsync" path="/exception"/>
    public Task<object?> AnswerAsync(ClassificationRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(asked, token => Publish(asked, token), cancellationToken);
    }

    /// <summary>Forgets a document's outstanding work, and the answer it was last given.</summary>
    /// <inheritdoc cref="DeferredAnswers.Forget" path="/remarks"/>
    public void Forget(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _deferred.Forget(document);
        _published.TryRemove(document.Key, out _);
    }

    /// <summary>Classifies the buffer, and names the answer so a later one can be a difference.</summary>
    /// <remarks>
    /// <para>
    /// The references are the binder's own record of what every name in the file resolved to, so the
    /// refinement is a transcription rather than a second opinion; see
    /// <see cref="SemanticTokenEncoder"/>. A compilation that produced no module produces none of
    /// them, and the answer is then exactly the lexical one.
    /// </para>
    /// <para>
    /// <b>A retained answer is paired with the name it was published under, which is what makes it
    /// safe to retain one that was never delivered.</b> This runs before the last staleness check, so
    /// an answer refused a moment later may already have replaced what is kept here. That cannot
    /// mislead anyone: a client asking for a delta names the answer <em>it</em> holds, and an
    /// identifier minted for an answer it never received matches nothing it can say. The cost of the
    /// race is one whole answer instead of a difference, which is the same cost as any other miss.
    /// </para>
    /// </remarks>
    private object? Publish(ClassificationRequest asked, CancellationToken cancellationToken)
    {
        var compiled = _semantics.For(asked.Document, asked.Configuration, cancellationToken);

        var classified = SemanticTokenEncoder.Encode(
            asked.Document.Text,
            asked.Uri.Text,
            compiled.Semantics?.AllReferences ?? [],
            Client);

        if (!Deltas)
        {
            return classified;
        }

        _published.TryGetValue(asked.Uri.Key, out var held);

        var published = new Painted(NextResultId(), classified.Data);
        _published[asked.Uri.Key] = published;

        return held is not null && string.Equals(held.ResultId, asked.PreviousResultId, StringComparison.Ordinal)
            ? new SemanticTokensDelta
            {
                ResultId = published.ResultId,
                Edits = SemanticTokenDiff.Between(held.Data, published.Data),
            }
            : classified with { ResultId = published.ResultId };
    }

    /// <remarks>
    /// Unique within this server's life, which is the whole requirement: the only thing an identifier
    /// is ever compared against is one this provider minted and handed out.
    /// </remarks>
    private string NextResultId()
        => Interlocked.Increment(ref _results).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One answer a client was given, and the name it was given under.</summary>
    private sealed record Painted(string ResultId, IReadOnlyList<int> Data);
}
