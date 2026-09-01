using ProtoLang.Symbols;
using ProtoLang.Types;

namespace ProtoLang.Semantics;

/// <summary>
/// What a bare identifier may mean at one position: every name that resolves there, and the message
/// an unqualified field name is looked up against.
/// </summary>
/// <remarks>
/// <para>
/// One object rather than two queries, because these are the two halves of one request. A completion
/// list needs the names; the receiver is what makes the list explicable -- and hover on a bare field
/// wants it without the names. Asking twice would walk the module twice for one keystroke.
/// </para>
/// <para>
/// <b>Names, not everything nameable.</b> A method is absent: a bare <c>total_cents</c> is
/// <c>PL0040</c> -- a method must be called -- so a method name resolves in call position and
/// nowhere else, and offering one here would be offering something that does not bind. Types are
/// absent for the same reason in the other direction: they resolve in type position, which is a
/// different question asked of the syntax tree.
/// </para>
/// </remarks>
/// <param name="Receiver">
/// The message whose fields a bare name may reach. Present even where none of them are visible --
/// inside a <c>test</c>, where an unqualified name resolves to nothing -- because it is still what
/// the code under the caret is written against, and it is what an editor labels the context with.
/// </param>
/// <param name="Names">
/// Every name that resolves here, each exactly once, ordered by where it was declared and then by
/// the schema's own field order. A name is in this list at most once even where two things bear it:
/// see <see cref="VisibleName"/>.
/// </param>
public sealed record ScopeAtPosition(MessageType Receiver, IReadOnlyList<VisibleName> Names);

/// <summary>One name that resolves at a position, and what it resolves to.</summary>
/// <remarks>
/// <para>
/// <b>The winner, and only the winner.</b> Where a local and a field of the receiver share a
/// spelling the local is here and the field is not, because that is what the binder does with the
/// name and a list saying otherwise would offer an author a word that then means something else.
/// ProtoLang forbids the other shadowing outright -- a <c>var</c> colliding with an enclosing name
/// is <c>PL0029</c> and never enters scope -- so no two entries can ever share a spelling.
/// </para>
/// <para>
/// Distinct from <see cref="ScopeEntry"/>, which is what the binder published: that one carries the
/// ranges a query needs and a caller must not care about, and exists only for names ProtoLang
/// declares. This one is the answer, and covers fields of the receiver as well, which have no
/// declaration in ProtoLang source at all.
/// </para>
/// </remarks>
/// <param name="Symbol">
/// Which symbol this is, and by way of <see cref="SymbolId.Kind"/> what kind of thing it is -- which
/// is not restated as a member of its own, because a second copy is a second thing to keep in step.
/// </param>
/// <param name="Name">The spelling to write. Taken from the declaration, or from the descriptor.</param>
/// <param name="Type">What writing it yields.</param>
/// <param name="Declaration">
/// Where it was declared, or null for a field of the receiver -- whose declaration is in a
/// <c>.proto</c> this compiler does not own. The same boundary
/// <see cref="SemanticModel.DeclarationOf"/> draws, for the same reason, and #41 is what answers
/// past it.
/// </param>
public sealed record VisibleName(
    SymbolId Symbol,
    string Name,
    PlType Type,
    DeclarationSite? Declaration);
