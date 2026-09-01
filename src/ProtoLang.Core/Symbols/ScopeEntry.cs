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
/// </remarks>
/// <param name="Declaration">
/// What was declared. Carries the identity, the kind, the spelling and both ranges already, so none
/// of that is restated here.
/// </param>
/// <param name="Type">
/// What the name is worth. The one thing a <see cref="DeclarationSite"/> does not hold, because a
/// declaration site is written by the parser's shape and a type is settled by the binder.
/// </param>
/// <param name="Region">
/// The extent of the scope this name entered -- the whole method for a parameter, the block for a
/// local, the whole <c>for</c> statement for a loop binding. A position outside it cannot see the
/// name however far along the file it is.
/// </param>
/// <param name="VisibleFrom">
/// The offset from which the name resolves. Not the same as the start of <paramref name="Region"/>,
/// and that difference is the whole of the declaration-order rule: a local enters scope after its
/// own initializer is bound, so <c>var x: int64 = x;</c> reports an unknown name, and a loop binding
/// enters after its collection, so <c>for x in x { }</c> does too. Recording the point rather than
/// the rule means neither case has to be restated anywhere else.
/// </param>
public sealed record ScopeEntry(
    DeclarationSite Declaration,
    PlType Type,
    SourceSpan Region,
    int VisibleFrom);
