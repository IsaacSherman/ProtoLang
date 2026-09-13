using ProtoLang.Symbols;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The symbol a caret is on and every place that symbol is written, which is one question asked by
/// two features.
/// </summary>
/// <remarks>
/// <para>
/// Find-all-references and occurrence highlighting differ in what they render and in nothing else:
/// one sends locations a client can navigate to, the other sends ranges it tints in place. Asking the
/// question here rather than in each provider is what keeps them from drifting -- a highlight that
/// disagreed with the reference list about which occurrences belong to one symbol would be two
/// features contradicting each other about the same caret.
/// </para>
/// <para>
/// <b>Semantic by construction, which is the property that matters most.</b> Identity is a
/// <see cref="SymbolId"/> and never a spelling, so two locals named <c>total</c> in sibling blocks
/// answer with disjoint lists, one field reached bare and through a receiver answers with both, and a
/// string literal containing the word answers with nothing because the binder recorded no name there.
/// None of that is implemented here; all of it is inherited from #40, which is the point.
/// </para>
/// <para>
/// <b>What it costs is one scan and one lookup.</b> Finding the symbol is a walk of the names in the
/// file; the occurrences of that symbol are a dictionary hit. Both read an index that was built when
/// the buffer was last compiled and is shared by every question asked about it since, so a caret
/// moving through an unedited file rebuilds nothing. #57 made that a number: zero further
/// compilations, asserted on every build by <c>PerformanceCostTests</c>, and 1.2 ms at p95 for the
/// scan and the lookup on a symbol with 516 references.
/// </para>
/// </remarks>
internal sealed record SymbolOccurrences(
    DeclaredSymbol Symbol, IReadOnlyList<SymbolReference> References)
{
    /// <summary>
    /// What the caret at <paramref name="offset"/> is on, or null when it is not on a name that
    /// resolved.
    /// </summary>
    /// <remarks>
    /// Null covers more than a caret in whitespace: an unknown name, a half-typed one and a scalar
    /// type spelling all resolve to nothing, and answering about them would mean guessing which
    /// symbol was meant. Nothing is the honest answer and is what both callers send.
    /// </remarks>
    public static SymbolOccurrences? At(DocumentCompilation compiled, int offset)
    {
        ArgumentNullException.ThrowIfNull(compiled);

        if (compiled.Semantics is not { } model || DeclaredSymbol.At(compiled, offset) is not { } symbol)
        {
            return null;
        }

        return new SymbolOccurrences(symbol, model.ReferencesTo(symbol.Id));
    }
}
