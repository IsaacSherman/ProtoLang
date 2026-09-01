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
    int VisibleThrough);
