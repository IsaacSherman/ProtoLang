using ProtoLang.Diagnostics;
using ProtoLang.Types;

namespace ProtoLang.Symbols;

/// <summary>
/// One name the binder put in scope: what it declares, over what range it can be written, and from
/// what point it means anything.
/// </summary>
/// <remarks>
/// <para>
/// What the binder used to throw away. Its <c>Scope</c> chain is built as it descends and released
/// as it comes back up, which is enough to bind a method body and nothing more; completion on a bare
/// identifier needs the same set back afterwards, at a position nobody knew about while binding.
/// </para>
/// <para>
/// <b>Only what actually entered scope is here.</b> A parameter whose name has not been written, a
/// second parameter of a name already taken, and a <c>var</c> that collides with a name from an
/// enclosing block are all still in the IR and are all absent from this list, because
/// <c>TryDeclareLocal</c> and <c>TryDeclareParameter</c> refused them and the binder went on
/// resolving those names to whatever it had already accepted. That is the fact a walk of the IR
/// cannot recover: nothing about an <see cref="Ir.IrLocal"/> says whether it won its name.
/// </para>
/// <para>
/// <b>Fields of the receiver are not here.</b> A bare identifier may also name a field of the
/// implicit receiver, but that set is a property of the message rather than of a position, and
/// copying it into every method would be one copy per method of something the descriptor already
/// holds. The query adds them; see <c>ScopeSearch</c>.
/// </para>
/// <para>
/// <b>A closed range of offsets, not a span.</b> The scope's own extent would be the obvious thing
/// to carry and is the wrong one twice over. Its start is redundant, because a name is visible from
/// a point inside its scope and never before it. And its end is one past the closing brace, since a
/// <see cref="Diagnostics.SourceSpan"/> is half-open -- so containment against it would keep a
/// block's locals alive for one offset after the brace that closed them, which is exactly where
/// <c>} else {</c> puts a caret. Two integers and two comparisons say what is meant and cannot be
/// read the other way.
/// </para>
/// </remarks>
/// <param name="Declaration">
/// What was declared. Carries the identity, the kind, the spelling and both ranges already, so none
/// of that is restated here.
/// </param>
/// <param name="Type">
/// What the name is worth. The one thing a <see cref="DeclarationSite"/> does not hold, because a
/// declaration site is written by the parser's shape and a type is settled by the binder.
/// </param>
/// <param name="VisibleFrom">
/// The first offset at which the name resolves. Not the start of the scope it entered, and that
/// difference is the whole of the declaration-order rule: a local enters scope after its own
/// initializer is bound, so <c>var x: int64 = x;</c> reports an unknown name, and a loop binding
/// enters after its collection, so <c>for x in x { }</c> does too. Recording the point rather than
/// the rule means neither case has to be restated anywhere else.
/// </param>
/// <param name="VisibleThrough">
/// The last offset at which the name resolves, inclusive -- the closing brace of the scope it
/// entered, or the end of the file when the parser never found one.
/// </param>
public sealed record ScopeEntry(
    DeclarationSite Declaration,
    PlType Type,
    int VisibleFrom,
    int VisibleThrough)
{
    /// <summary>The first offset inside <paramref name="block"/>: past the brace that opens it.</summary>
    /// <remarks>
    /// A name written at the brace itself is written <em>before</em> it, in the header the block
    /// follows -- <c>for x in items |{</c> is where the collection goes, and the binder resolves it
    /// against the enclosing scope. So the brace is the last offset outside, not the first inside.
    /// </remarks>
    public static int FirstOffsetInside(SourceSpan block) => block.Start.Offset + 1;

    /// <summary>The last offset inside <paramref name="block"/>: the brace that closes it.</summary>
    /// <param name="closed">
    /// Whether the parser found that brace; see <see cref="Syntax.BlockStatement.IsClosed"/>.
    /// </param>
    /// <remarks>
    /// One before the end when there is a brace, because a <see cref="SourceSpan"/> is half-open and
    /// the last character of a block is that brace: a caret one past it has left the block, and that
    /// is precisely where <c>} else {</c> and the end of every nested block put one.
    /// <para>
    /// When there is no brace the end is not a delimiter at all -- it is the hole the author is
    /// typing into, the position an editor asks about most while a file is incomplete -- so it is
    /// inside. This is asked of the parser rather than inferred from the end of the file, because a
    /// block that <em>was</em> closed is very often the last thing before the hole, and its brace is
    /// then the final character of the file.
    /// </para>
    /// </remarks>
    public static int LastOffsetInside(SourceSpan block, bool closed)
        => closed ? block.End.Offset - 1 : block.End.Offset;

    /// <summary>Whether <paramref name="offset"/> is inside <paramref name="block"/>'s braces.</summary>
    /// <inheritdoc cref="LastOffsetInside" path="/param"/>
    public static bool Inside(SourceSpan block, int offset, bool closed)
        => offset >= FirstOffsetInside(block) && offset <= LastOffsetInside(block, closed);
}
