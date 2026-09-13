using ProtoLang.Symbols;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Where else in this file the name under the caret appears, tinted in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same question find-all-references asks, rendered where the reader is already looking.</b>
/// Both go through <see cref="SymbolOccurrences"/>, so the two cannot disagree about which
/// occurrences belong to one symbol -- which matters more here than anywhere else, because this
/// fires without being asked for and a reader who never requested it has no reason to doubt it.
/// </para>
/// <para>
/// <b>Semantic, never textual, and that is the whole of its value.</b> Two locals named
/// <c>total</c> in sibling blocks are two symbols and do not light each other up; a string containing
/// the word lights up nothing. A textual highlighter is easy and wrong, and wrong here is worse than
/// absent: it teaches a reader that the tool knows what a name means, and then it is believed about
/// the case where it does not.
/// </para>
/// <para>
/// <b>It fires on caret movement, which makes it the most latency-sensitive request here.</b> What
/// it does is a walk of the names in the file and a dictionary lookup, over an index built when the
/// buffer was last compiled -- so moving a caret through an unedited file rebuilds nothing and
/// compiles nothing. Nothing is cached on top of that, and #57 measured why nothing should be: 1.2 ms
/// at p95 on a file ten times normal size, against a 20 ms budget. The reference index is consulted
/// directly, which is what #57 was asked to confirm was affordable, and a cache here would be
/// complexity bought with latency nobody can perceive.
/// </para>
/// <para>
/// Read in order and answered out of it, bounded and refused when stale. Its own outstanding work,
/// because a caret sliding through a file supersedes only the highlight before it -- never the
/// reference list somebody deliberately asked for.
/// </para>
/// </remarks>
public sealed class HighlightProvider
{
    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    public HighlightProvider(
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
        _deferred = new DeferredAnswers("occurrence highlighting", documents, configuration, concurrency);
    }

    /// <inheritdoc cref="DeferredAnswers.Outstanding"/>
    public int Outstanding => _deferred.Outstanding;

    /// <inheritdoc cref="DeferredAnswers.InFlight"/>
    public int InFlight => _deferred.InFlight;

    /// <inheritdoc cref="DeferredAnswers.PeakInFlight"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <inheritdoc cref="PositionRequest.Read(DocumentStore, ConfigurationSync, TextDocumentPositionParams)"/>
    public PositionRequest? Read(TextDocumentPositionParams message)
        => PositionRequest.Read(_documents, _configuration, message);

    /// <summary>
    /// Every occurrence in this document of the name at the position <see cref="Read"/> settled, or
    /// null for a caret that is not on one.
    /// </summary>
    /// <inheritdoc cref="DeferredAnswers.AnswerAsync" path="/exception"/>
    public Task<DocumentHighlight[]?> AnswerAsync(
        PositionRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(asked, token => Answer(asked, token), cancellationToken);
    }

    /// <inheritdoc cref="DeferredAnswers.Forget"/>
    public void Forget(DocumentUri document) => _deferred.Forget(document);

    /// <remarks>
    /// <para>
    /// Every occurrence is in this document, so no filtering by file is needed: a reference is
    /// recorded where the name was written, and a schema symbol's declaration -- the one thing that
    /// lives elsewhere -- is not a reference and is not in the list.
    /// </para>
    /// <para>
    /// That rests on a compilation holding one ProtoLang source, as every position query on
    /// <see cref="Semantics.SemanticModel"/> does. When one holds several (#27) the references come
    /// from all of them and this has to drop the ones written in another file -- a range measured in
    /// one buffer and painted into a different one lands on unrelated text, or past the end of a
    /// shorter one.
    /// </para>
    /// </remarks>
    private DocumentHighlight[]? Answer(PositionRequest asked, CancellationToken cancellationToken)
    {
        var compiled = _semantics.For(asked.Document, asked.Configuration, cancellationToken);

        if (SymbolOccurrences.At(compiled, asked.Offset) is not { } occurrences)
        {
            return null;
        }

        return
        [
            .. occurrences.References.Select(reference => new DocumentHighlight
            {
                Range = EditorPositions.RangeOf(reference.Span),
                Kind = KindOf(reference.Kind),
            }),
        ];
    }

    /// <remarks>
    /// A declaration is <c>Text</c> rather than a read, because LSP has no kind for it and calling it
    /// a read would be saying something untrue about the one occurrence a reader most wants to pick
    /// out of the file.
    /// </remarks>
    private static DocumentHighlightKind KindOf(ReferenceKind kind)
        => kind switch
        {
            ReferenceKind.Read => DocumentHighlightKind.Read,
            ReferenceKind.Write => DocumentHighlightKind.Write,
            _ => DocumentHighlightKind.Text,
        };
}
