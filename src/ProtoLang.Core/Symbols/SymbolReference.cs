using ProtoLang.Diagnostics;

namespace ProtoLang.Symbols;

/// <summary>
/// One place a name was written, and which symbol it turned out to mean.
/// </summary>
/// <remarks>
/// <para>
/// <b>The range is the name and nothing else.</b> An IR node spans the construct it stands for --
/// an <see cref="Ir.IrMethodCall"/> covers its arguments, an <see cref="Ir.IrFieldAccess"/> covers
/// its receiver and the dot -- which is right for a position query and wrong for everything this
/// type exists for. Highlighting occurrences of <c>quantity</c> must light up <c>quantity</c> and
/// not <c>line.quantity</c>, and a rename that replaced the wider range would delete the receiver.
/// So the range recorded here is the one the author typed for this symbol, taken at the point the
/// binder resolved it, which is the only point that holds both halves at once.
/// </para>
/// <para>
/// <b><see cref="Document"/> is where the reference is, not where the symbol is.</b> For anything
/// the schema declares those are different files, and for anything ProtoLang declares they are the
/// same file only until #27. A span carries the label diagnostics print, which is the base file
/// name; this carries the identity, for the reason
/// <see cref="SymbolId.ForDeclaration"/> gives at length.
/// </para>
/// </remarks>
public sealed record SymbolReference(
    SymbolId Symbol,
    SourceIdentity Document,
    SourceSpan Span,
    ReferenceKind Kind)
{
    /// <summary>
    /// The order every published sequence of references is in: by document, then by where the name
    /// starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written once here because two callers need it -- the binder, publishing what it recorded,
    /// and the index, republishing that merged with the declarations -- and two sort expressions
    /// are two orders waiting to disagree about the case neither author thought of.
    /// </para>
    /// <para>
    /// <b>Total, not merely deterministic.</b> Sorting by offset alone leaves ties to whatever order
    /// the binder happened to resolve things in, which is not source order: an enum receiver is
    /// settled before the member it qualifies, and a call's method is resolved after its arguments
    /// are bound. Two names cannot in fact start at one offset in one document, so the last three
    /// keys never decide anything today -- they are here so that the day something does share a
    /// range, the list does not quietly start shuffling between runs.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SymbolReference> InSourceOrder(IEnumerable<SymbolReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        return
        [
            .. references
                .OrderBy(reference => reference.Document.Path ?? reference.Document.Name, StringComparer.Ordinal)
                .ThenBy(reference => reference.Span.Start.Offset)
                .ThenBy(reference => reference.Span.End.Offset)
                .ThenBy(reference => reference.Symbol.Kind)
                .ThenBy(reference => reference.Symbol.Key, StringComparer.Ordinal),
        ];
    }
}
