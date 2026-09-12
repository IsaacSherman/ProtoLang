using ProtoLang.Symbols;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Everywhere the name under the caret is written: the other direction from go-to-definition.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must agree with go-to-definition, and it does because both read the same record.</b>
/// Following a reference to its declaration and then asking that declaration for its references has
/// to bring back the one you started from, and the usual way that fails is two indices built two
/// ways. There are not two here: <see cref="DeclaredSymbol"/> turns the caret into a symbol for both
/// features, and the list is the binder's own record of every name it resolved.
/// </para>
/// <para>
/// <b>The declaration is in the list and marked</b>, rather than beside it, because that is the
/// question LSP asks -- <c>includeDeclaration</c> means "is it in the list", which only has an answer
/// if it is there to be left out. <see cref="ReferenceKind"/> is how the two are told apart.
/// </para>
/// <para>
/// <b>A schema symbol is the one case that leaves this file.</b> A field, an enum constant, a message
/// and an enum are declared in a <c>.proto</c> this compiler does not own, so the index has their
/// uses and no declaration among them. Where the client asked for the declaration, it comes from the
/// other door -- the same one go-to-definition uses -- and is a location in the <c>.proto</c>. Where
/// that file cannot be read, the uses are still the answer: they are what a reader asked about.
/// </para>
/// <para>
/// Read in order and answered out of it, bounded and refused when stale, on the same terms as
/// <see cref="HoverProvider"/>. Its own outstanding work rather than shared, because a deliberate
/// click must not be cancelled by a caret moving past.
/// </para>
/// </remarks>
public sealed class ReferenceProvider
{
    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    public ReferenceProvider(
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
        _deferred = new DeferredAnswers("find references", documents, configuration, concurrency);
    }

    /// <inheritdoc cref="DeferredAnswers.Outstanding"/>
    public int Outstanding => _deferred.Outstanding;

    /// <inheritdoc cref="DeferredAnswers.InFlight"/>
    public int InFlight => _deferred.InFlight;

    /// <inheritdoc cref="DeferredAnswers.PeakInFlight"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <inheritdoc cref="PositionRequest.Read(DocumentStore, ConfigurationSync, TextDocumentIdentifier, Position)"/>
    public PositionRequest? Read(ReferenceParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return PositionRequest.Read(_documents, _configuration, message.TextDocument, message.Position);
    }

    /// <summary>
    /// Every place the name at the position <see cref="Read"/> settled is written, or null for a
    /// caret that is not on one.
    /// </summary>
    /// <param name="includeDeclaration">
    /// Whether the place the name was introduced belongs in the list. Passed beside the request
    /// rather than carried on it: it says what the answer should contain and nothing about which
    /// buffer the answer is about, which is the only thing a request has to settle before yielding.
    /// </param>
    /// <inheritdoc cref="DeferredAnswers.AnswerAsync" path="/exception"/>
    public Task<Location[]?> AnswerAsync(
        PositionRequest asked, bool includeDeclaration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(
            asked, token => Answer(asked, includeDeclaration, token), cancellationToken);
    }

    /// <inheritdoc cref="DeferredAnswers.Forget"/>
    public void Forget(DocumentUri document) => _deferred.Forget(document);

    /// <remarks>
    /// <para>
    /// The uses come in source order, because that is the order the index publishes and the order a
    /// reader expects to walk a file in. Nothing sorts here: a second ordering is a second answer, and
    /// <see cref="SymbolReference.InSourceOrder"/> is already total rather than merely deterministic
    /// so the list cannot shuffle between two identical requests.
    /// </para>
    /// <para>
    /// A schema symbol's declaration is appended after them rather than placed among them, because it
    /// is in a different file and its offsets say nothing about where it belongs in this one. The
    /// whole list is deterministic; only the part of it that is in this document is in source order.
    /// </para>
    /// </remarks>
    private Location[]? Answer(
        PositionRequest asked, bool includeDeclaration, CancellationToken cancellationToken)
    {
        var compiled = _semantics.For(asked.Document, asked.Configuration, cancellationToken);

        if (SymbolOccurrences.At(compiled, asked.Offset) is not { } occurrences)
        {
            return null;
        }

        var written = occurrences.References
            .Where(reference => includeDeclaration || reference.Kind is not ReferenceKind.Declaration)
            .Select(reference => SymbolLocations.LocationOf(reference, asked.Uri));

        return includeDeclaration
            ? [.. written, .. Elsewhere(occurrences.Symbol, asked.Uri)]
            : [.. written];
    }

    /// <summary>The declaration of a symbol this compilation does not declare, where there is one.</summary>
    /// <remarks>
    /// Empty for everything ProtoLang declares, because that declaration is already in the list above
    /// and marked -- adding it again would report one name twice. Empty as well for a schema element
    /// whose <c>.proto</c> cannot be read, which is ordinary rather than exceptional: a well-known
    /// type protoc resolved from descriptors compiled into itself is real and is nowhere on this
    /// machine.
    /// </remarks>
    private static IEnumerable<Location> Elsewhere(DeclaredSymbol symbol, DocumentUri asked)
        => symbol.Site is null && SymbolLocations.DeclarationOf(symbol, asked) is { } declared
            ? [new Location(declared.Uri, EditorPositions.RangeOf(declared.Name))]
            : [];
}
