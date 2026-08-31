using ProtoLang.Ir;
using ProtoLang.Symbols;

namespace ProtoLang.Semantics;

/// <summary>
/// Every symbol a compilation mentions, and every place it is mentioned: the inverse of the
/// declaration a reference already knew how to find.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composed rather than collected.</b> The binder recorded the uses as it resolved them, and
/// <see cref="DeclarationSite"/> has held the declarations since #39; this puts the two together and
/// owns neither. That is what keeps the two directions from disagreeing -- there is no second
/// resolution here to be right or wrong about, only a grouping of what the binder already decided.
/// </para>
/// <para>
/// <b>A schema symbol has references and no declaration.</b> A field, an enum constant, a message
/// type and an enum type are declared in a <c>.proto</c> this compiler does not own, so
/// <see cref="DeclarationOf"/> answers null for them. The asymmetry is the point rather than a gap:
/// reporting where they are used is in scope and editing them is not. #41 is what will map one back
/// to the <c>.proto</c> it came from.
/// </para>
/// <para>
/// Built in one pass into dictionaries, because occurrence highlighting asks on a cursor move and
/// asks about one symbol. Nothing is cached across compilations: a keystroke produces a new module
/// and a new index over it, which is the same bargain <see cref="SemanticModel"/> makes.
/// </para>
/// </remarks>
internal sealed class ReferenceIndex
{
    private readonly Dictionary<SymbolId, IReadOnlyList<SymbolReference>> _bySymbol;
    private readonly Dictionary<SymbolId, DeclarationSite> _declarations = [];
    private readonly IReadOnlyList<SymbolReference> _all;

    internal ReferenceIndex(IrModule module)
    {
        var everything = new List<SymbolReference>();

        foreach (var declaration in IrWalk.DeclarationsOf(module))
        {
            // TryAdd rather than an assignment: an identity is the offset its name was written at,
            // so two declarations cannot share one, and if that ever stopped holding the first is
            // the one every other query here would pick anyway.
            if (!_declarations.TryAdd(declaration.Id, declaration))
            {
                continue;
            }

            everything.Add(new SymbolReference(
                declaration.Id,
                declaration.Document,
                declaration.Name.Span,
                ReferenceKind.Declaration));
        }

        everything.AddRange(module.References);

        _all = SymbolReference.InSourceOrder(everything);
        _bySymbol = _all
            .GroupBy(reference => reference.Symbol)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SymbolReference>)[.. group]);
    }

    /// <inheritdoc cref="SemanticModel.ReferencesTo"/>
    internal IReadOnlyList<SymbolReference> ReferencesTo(SymbolId symbol)
        => _bySymbol.GetValueOrDefault(symbol) ?? [];

    /// <inheritdoc cref="SemanticModel.DeclarationOf"/>
    internal DeclarationSite? DeclarationOf(SymbolId symbol)
        => _declarations.GetValueOrDefault(symbol);

    /// <inheritdoc cref="SemanticModel.ReferenceAt"/>
    /// <remarks>
    /// A scan, and it stays one. Name ranges are flat rather than nested, so there is no descent to
    /// make. The shortest-wins tie-break is <see cref="PositionSearch.Find"/>'s, applied here so that
    /// one caret cannot be told one thing by a position query and another by this. Two names cannot
    /// in fact overlap -- what separates them is a dot, a delimiter or whitespace, none of which
    /// belongs to either -- so the comparison decides nothing today, and is here to stop the two
    /// answers drifting if that ever stops being true.
    /// </remarks>
    internal SymbolReference? ReferenceAt(int offset)
    {
        SymbolReference? best = null;

        foreach (var reference in _all)
        {
            if (PositionSearch.Contains(reference.Span, offset)
                && (best is null || reference.Span.Length < best.Span.Length))
            {
                best = reference;
            }
        }

        return best;
    }
}
