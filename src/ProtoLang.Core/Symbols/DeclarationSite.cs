using ProtoLang.Diagnostics;
using ProtoLang.Syntax;

namespace ProtoLang.Symbols;

/// <summary>
/// Where a symbol was declared, and what it is. Everything an editor needs to navigate to a
/// declaration, hanging off the declaration itself so that any reference reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ranges, because editors want two.</b> <see cref="Extent"/> is the whole construct --
/// <c>var total: int64 = compute();</c> -- and is what a client shows as context. The narrower
/// range, <see cref="SyntaxName.Span"/> on <see cref="Name"/>, covers just <c>total</c>, and is what
/// a client selects when it navigates. LSP asks for both by name and derives neither from the other,
/// so both are recorded rather than one being reconstructed by re-lexing.
/// </para>
/// <para>
/// <b>The name is a <see cref="SyntaxName"/> and not a string</b>, because that type already answers
/// the three questions asked here -- the spelling, the range, and whether the author has written it
/// at all -- and a second copy of that shape would eventually disagree with the first. A declaration
/// whose name is missing is not an error to be filtered out: a buffer mid-edit is full of them, and
/// one still occupies a place in a parameter list and still has an identity of its own.
/// </para>
/// <para>
/// An explicit constructor rather than positional syntax, because it has two things to settle.
/// <see cref="Id"/> is derived from <see cref="Name"/>, and a <c>with</c> expression would carry the
/// old identity onto the new name; it is also computed once here rather than on each read, since a
/// reference index asks for it per reference.
/// </para>
/// <para>
/// And <see cref="Extent"/> is widened to cover the name, which is not the tautology it looks like.
/// A parameter's written extent runs from its name to its type, so a parameter still being typed --
/// <c>fn f(a: int64, : int64)</c> -- has an extent starting at the colon while its name is the empty
/// point after the comma, just outside. LSP requires the selection range to lie inside the range it
/// selects from, and a client handed the other way round has no defined behavior. Establishing it
/// here means no caller has to remember, and the widening is a no-op for every name that was
/// actually written.
/// </para>
/// </remarks>
public sealed record DeclarationSite
{
    /// <param name="kind">What is being declared. Also part of <see cref="Id"/>.</param>
    /// <param name="name">The declaring name, written or not yet written.</param>
    /// <param name="extent">The whole declaring construct, as far as it has been written.</param>
    public DeclarationSite(SymbolKind kind, SyntaxName name, SourceSpan extent)
    {
        Kind = kind;
        Name = name;
        Extent = SourceSpan.Union(extent, name.Span);
        Id = SymbolId.ForDeclaration(kind, name.Span);
    }

    /// <summary>What this declares.</summary>
    public SymbolKind Kind { get; }

    /// <summary>The declared name, and the narrow range covering just the name.</summary>
    public SyntaxName Name { get; }

    /// <summary>
    /// The whole declaring construct, for a client that wants the declaration in context. Always
    /// contains <see cref="Name"/>'s range.
    /// </summary>
    public SourceSpan Extent { get; }

    /// <summary>What identifies this declaration, and every reference that resolves to it.</summary>
    public SymbolId Id { get; }
}
