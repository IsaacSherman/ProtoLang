using Google.Protobuf.Reflection;
using ProtoLang.Diagnostics;
using ProtoLang.Symbols;
using ProtoLang.Types;

namespace ProtoLang.Ir;

/// <summary>
/// The typed intermediate representation. Per spec 22.2 this preserves source locations, resolved
/// protobuf type references, exact numeric operation kinds, evaluation order, and virtual
/// annotations. Backends consume only this; they never see the AST.
/// </summary>
public sealed record IrModule(IReadOnlyList<IrMethod> Methods, IReadOnlyList<IrTest> Tests)
{
    /// <summary>
    /// Every place a name was written and resolved to a symbol, in
    /// <see cref="SymbolReference.InSourceOrder">source order</see>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half of spec 22.2's "resolved protobuf type references" the IR was not keeping. It kept
    /// what a name resolved <em>to</em> -- a <see cref="PlType"/> on a local, a
    /// <see cref="FieldDescriptor"/> on a field access -- and dropped where the name was written,
    /// which is the half a declaration needs in order to find its uses. Some of it was never
    /// expressible here at all: a type reference resolves to a type and leaves no node behind, so
    /// <c>fn f(x: Money)</c> mentions <c>Money</c> nowhere in this tree.
    /// </para>
    /// <para>
    /// Recorded by the binder as it resolves, because that is the only place holding both the
    /// identity and the range of the name alone; see <see cref="SymbolReference"/>. Declarations are
    /// deliberately <b>not</b> here -- <see cref="DeclarationSite"/> is their one home, and a second
    /// copy of where a name was introduced is a second thing that can be wrong. The reference index
    /// composes the two.
    /// </para>
    /// <para>
    /// Init-only with an empty default, so every existing construction of a module stays valid and
    /// a caller that hand-builds one for a test gets a module that answers "no references" rather
    /// than a null.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SymbolReference> References { get; init; } = [];
}

/// <summary>Anything in the IR that is somewhere in the source text.</summary>
/// <remarks>
/// <para>
/// What <see cref="Syntax.SyntaxNode"/> has always been for the other tree, and what the IR did
/// without for as long as nothing asked it a question about a position. Every record below already
/// ended in a <see cref="SourceSpan"/>; this only says so in one place, so that "the innermost node
/// containing this offset" and "the chain of nodes above it" are expressible at all -- a list needs
/// an element type, and statements and expressions had no type in common.
/// </para>
/// <para>
/// Four things stay outside it. <see cref="IrModule"/> is the container and is nowhere in
/// particular. <see cref="IrMethodSignature"/>, <see cref="IrLocal"/> and <see cref="IrParameter"/>
/// are symbols rather than tree nodes: what they have is a <see cref="Symbols.DeclarationSite"/>,
/// which is two ranges and an identity, and collapsing that to one span would be choosing which of
/// the two an editor meant.
/// </para>
/// </remarks>
public abstract record IrNode(SourceSpan Span);

/// <summary>
/// Identifies a method without carrying its body, so a call can reference a method declared later
/// in the file (or in another extend block) without a construction cycle.
/// </summary>
/// <param name="Declaration">
/// Where the method was declared, so a call can find its callee. The compiler always knew this --
/// <see cref="IrMethod"/> had a span -- but a call reaches the signature and not the method, and the
/// signature is the half that used to say nothing about where it came from.
/// </param>
/// <param name="Parameters">
/// What the method takes, in order. One list rather than parallel names and types, and the same
/// objects <see cref="IrMethod.Parameters"/> hands out, because a parameter is one declaration and
/// two representations of it are two things to keep in step. It is also what retired the
/// whole-signature <c>ParametersAreNamed</c> flag: whether a name was written is a fact about one
/// parameter, and asking it per parameter is what lets a missing-argument check still speak about
/// the parameters that do have names.
/// </param>
public sealed record IrMethodSignature(
    MessageDescriptor Receiver,
    DeclarationSite Declaration,
    PlType ReturnType,
    IReadOnlyList<IrParameter> Parameters)
{
    public string Name => Declaration.Name.Text;

    /// <summary>What identifies this method, and every call that resolves to it.</summary>
    public SymbolId Id => Declaration.Id;
}

public sealed record IrParameter(DeclarationSite Declaration, PlType Type)
{
    public string Name => Declaration.Name.Text;

    /// <inheritdoc cref="IrMethodSignature.Id"/>
    public SymbolId Id => Declaration.Id;
}

/// <summary>
/// A local variable or a <c>for</c> loop binding; <see cref="DeclarationSite.Kind"/> says which.
/// </summary>
public sealed record IrLocal(DeclarationSite Declaration, PlType Type)
{
    public string Name => Declaration.Name.Text;

    /// <inheritdoc cref="IrMethodSignature.Id"/>
    public SymbolId Id => Declaration.Id;
}

/// <remarks>
/// <para>
/// Its <see cref="IrNode.Span"/> is the whole declaration, <c>fn</c> through the closing brace of
/// the body, which is what it has always been -- taken from the signature's declaration site rather
/// than recorded twice.
/// </para>
/// <para>
/// It is computed once, at construction, and not on each read, which is the one thing to know before
/// writing <c>method with { Signature = … }</c>: the copy carries the span of the declaration it was
/// copied from, and would then report a location belonging to another method. Nothing does that
/// today -- an <see cref="IrMethod"/> is built in one place, from the signature it keeps -- and if
/// something needs to, it should build a new one rather than amend this.
/// </para>
/// </remarks>
public sealed record IrMethod(IrMethodSignature Signature, IrBlock Body, bool IsVirtual)
    : IrNode(Signature.Declaration.Extent)
{
    public MessageDescriptor Receiver => Signature.Receiver;

    public string Name => Signature.Name;

    public PlType ReturnType => Signature.ReturnType;

    public IReadOnlyList<IrParameter> Parameters => Signature.Parameters;
}

public abstract record IrStatement(SourceSpan Span) : IrNode(Span);

public sealed record IrBlock(IReadOnlyList<IrStatement> Statements, SourceSpan Span) : IrStatement(Span);

public sealed record IrVariableDeclaration(IrLocal Local, IrExpression Initializer, SourceSpan Span)
    : IrStatement(Span);

/// <param name="Target">
/// The local being written, as a reference to it rather than as the local itself.
/// </param>
/// <remarks>
/// <see cref="IrLocalReference"/> and not <see cref="IrLocal"/>, which is what it held for as long
/// as the only question asked of it was which storage to emit into. An <see cref="IrLocal"/> is a
/// symbol and has no span, so the <c>total</c> on the left of <c>total = 5;</c> was the one written
/// name in the whole IR that was nowhere: every other use of a name is a spanned node, and a
/// position query on this one reached the statement and stopped. The two backends spell the target
/// by asking the expression emitter for it, which is what they already did for a local read.
/// </remarks>
public sealed record IrAssignment(IrLocalReference Target, IrExpression Value, SourceSpan Span)
    : IrStatement(Span);

public sealed record IrReturn(IrExpression? Value, SourceSpan Span) : IrStatement(Span);

/// <summary>Iteration over a repeated field, in protobuf field order (spec 14).</summary>
public sealed record IrForEach(IrLocal Loop, IrExpression Collection, IrBlock Body, SourceSpan Span)
    : IrStatement(Span);

/// <summary>
/// A conditional (spec 15.1). <paramref name="Else"/> is an <see cref="IrBlock"/>, a nested
/// <see cref="IrIf"/> for an <c>else if</c> chain, or null when there is no else branch.
/// </summary>
public sealed record IrIf(IrExpression Condition, IrBlock Then, IrStatement? Else, SourceSpan Span)
    : IrStatement(Span);

/// <summary>
/// A <c>while</c> loop (spec 15.2). The compiler performs no termination analysis; that was
/// decided against in 15.2.
/// </summary>
public sealed record IrWhile(IrExpression Condition, IrBlock Body, SourceSpan Span) : IrStatement(Span);

/// <summary>Exits the innermost enclosing loop.</summary>
public sealed record IrBreak(SourceSpan Span) : IrStatement(Span);

/// <summary>Advances the innermost enclosing loop to its next iteration.</summary>
public sealed record IrContinue(SourceSpan Span) : IrStatement(Span);

public sealed record IrExpressionStatement(IrExpression Expression, SourceSpan Span) : IrStatement(Span);

public abstract record IrExpression(PlType Type, SourceSpan Span) : IrNode(Span);

/// <summary>The implicit receiver of the enclosing method.</summary>
public sealed record IrThis(MessageType MessageType, SourceSpan Span) : IrExpression(MessageType, Span);

public sealed record IrLocalReference(IrLocal Local, SourceSpan Span) : IrExpression(Local.Type, Span);

public sealed record IrParameterReference(IrParameter Parameter, SourceSpan Span)
    : IrExpression(Parameter.Type, Span);

public sealed record IrFieldAccess(
    IrExpression Receiver,
    FieldDescriptor Field,
    PlType FieldType,
    SourceSpan Span) : IrExpression(FieldType, Span);

/// <summary>
/// A presence test on one field (spec 8.4). Satisfies the "presence checks" requirement in 22.2,
/// which the IR previously had no way to express.
/// </summary>
/// <remarks>
/// The two backends spell this in unrelated ways -- a null test on a property in C#, a
/// <c>has_x()</c> call in C++ -- and for message fields protoc's C# generator emits no
/// <c>HasX</c> at all, so the descriptor has to survive into the backend rather than being reduced
/// to a boolean expression here.
/// </remarks>
public sealed record IrFieldPresence(
    IrExpression Receiver,
    FieldDescriptor Field,
    SourceSpan Span) : IrExpression(ScalarType.BoolType, Span);

public sealed record IrMethodCall(
    IrExpression Receiver,
    IrMethodSignature Target,
    IReadOnlyList<IrExpression> Arguments,
    SourceSpan Span) : IrExpression(Target.ReturnType, Span);

public enum IrBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
}

public enum IrUnaryOperator
{
    Negate,
    LogicalNot,
}

/// <summary>
/// A binary operation. <paramref name="Behavior"/> is meaningful only for arithmetic operators on
/// integer operands; it is what stops each backend from silently falling back to its own overflow
/// rules.
/// </summary>
public sealed record IrBinary(
    IrBinaryOperator Operator,
    IrExpression Left,
    IrExpression Right,
    PlType ResultType,
    ArithmeticBehavior Behavior,
    SourceSpan Span) : IrExpression(ResultType, Span)
{
    public bool IsArithmetic => Operator
        is IrBinaryOperator.Add or IrBinaryOperator.Subtract or IrBinaryOperator.Multiply
        or IrBinaryOperator.Divide or IrBinaryOperator.Modulo;
}

/// <summary>What an integer division does when its divisor is zero.</summary>
public enum ZeroDivisorBehavior
{
    /// <summary>
    /// The divisor is a non-zero literal, so a zero divisor is unreachable and no runtime check is
    /// emitted.
    /// </summary>
    Unreachable,

    /// <summary>Produce the declared fallback value instead.</summary>
    Fallback,

    /// <summary>Terminate the program deterministically. The author declared no valid result.</summary>
    Fail,
}

/// <summary>
/// Integer <c>/</c> or <c>%</c>. Separate from <see cref="IrBinary"/> because it is the only
/// arithmetic that can fail on a value rather than merely overflow, and because every backend has
/// to emit a zero check rather than the bare operator.
/// </summary>
/// <param name="OnZero">
/// The declared fallback value. Non-null exactly when <paramref name="ZeroBehavior"/> is
/// <see cref="ZeroDivisorBehavior.Fallback"/>.
/// </param>
public sealed record IrIntegerDivision(
    IrBinaryOperator Operator,
    IrExpression Left,
    IrExpression Right,
    ZeroDivisorBehavior ZeroBehavior,
    IrExpression? OnZero,
    PlType ResultType,
    ArithmeticBehavior Behavior,
    SourceSpan Span) : IrExpression(ResultType, Span);

public sealed record IrUnary(
    IrUnaryOperator Operator,
    IrExpression Operand,
    PlType ResultType,
    ArithmeticBehavior Behavior,
    SourceSpan Span) : IrExpression(ResultType, Span);

/// <summary>
/// A named protobuf enum constant, such as <c>TopLevelStatus.TOP_LEVEL_STATUS_OK</c>.
/// </summary>
/// <remarks>
/// Not an <see cref="IrLiteral"/> carrying the number. The backends spell the constant by name, and
/// the two targets name it in completely different shapes -- protoc strips the enum prefix and
/// PascalCases for C#, and flattens nested enums into the namespace for C++ -- so the descriptor
/// has to survive into the backend rather than being reduced to an integer here.
/// </remarks>
public sealed record IrEnumValue(EnumValueDescriptor Value, EnumPlType EnumType, SourceSpan Span)
    : IrExpression(EnumType, Span);

/// <summary>
/// An explicit numeric conversion (spec 10.3). ProtoLang applies no implicit conversions, so every
/// one of these was written by the author.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> is what the backends switch on, because the four conversion families need
/// different treatment in each target and the pair of scalar kinds alone would make every emitter
/// rediscover the classification.
/// </remarks>
public sealed record IrConversion(
    IrExpression Operand,
    ScalarType TargetType,
    ConversionKind Kind,
    ConversionBehavior Behavior,
    SourceSpan Span) : IrExpression(TargetType, Span);

/// <summary>The family a conversion belongs to, which is what decides how each backend spells it.</summary>
public enum ConversionKind
{
    /// <summary>Source and target are the same type; the conversion states nothing new.</summary>
    Identity,

    /// <summary>Between integer types, including across signedness.</summary>
    IntegerToInteger,

    /// <summary>From an integer type to <c>float</c> or <c>double</c>.</summary>
    IntegerToFloat,

    /// <summary>Between <c>float</c> and <c>double</c>.</summary>
    FloatToFloat,

    /// <summary>From <c>float</c> or <c>double</c> to an integer type.</summary>
    FloatToInteger,
}

public sealed record IrLiteral(object? Value, PlType LiteralType, SourceSpan Span)
    : IrExpression(LiteralType, Span);

/// <summary>
/// A member access whose member name has not been written yet -- <c>line.</c> with the caret sitting
/// after the dot.
/// </summary>
/// <remarks>
/// <para>
/// The one case where an error-typed value is not enough. Every other binding failure collapses to
/// an <see cref="IrLiteral"/> of <see cref="ErrorType"/>, which is all a compiler needs, because a
/// compilation that got there is going to stop anyway. An editor asked for a completion list needs
/// the opposite: the thing it must answer with is precisely the type of the receiver, and collapsing
/// throws exactly that away.
/// </para>
/// <para>
/// <paramref name="Span"/> is the empty range where the member name would go, so a client can anchor
/// its list under the caret rather than over whatever token recovery landed on.
/// </para>
/// <para>
/// No backend handles this, and none has to. One exists only when the parser reported a missing
/// name, so the compilation has errors, so <c>CompilationResult.Success</c> is false and no backend
/// is ever handed the module.
/// </para>
/// </remarks>
public sealed record IrMissingMemberAccess(IrExpression Receiver, SourceSpan Span)
    : IrExpression(ErrorType.Instance, Span);

/// <summary>
/// A call that could not be made: the callee is not a method, not a method of that receiver, not
/// callable at all, or takes a different number of arguments than were written.
/// </summary>
/// <remarks>
/// <para>
/// The arguments are the reason this exists. Every failure path in <c>BindInvocation</c> used to
/// collapse to an error-typed literal spanning the whole call, which threw away expressions the
/// author had written and left nothing at their positions -- and a call that does not resolve is the
/// normal state of one being typed. Signature help and completion both ask about exactly the region
/// inside the parentheses, so the arguments have to survive the failure of the call around them.
/// </para>
/// <para>
/// <paramref name="Receiver"/> is null exactly where there is no receiver to speak of: a call
/// through an expression that could never name a method, <c>1()</c> or <c>(quantity + 1)()</c>, has
/// nothing that was resolved to hold. <b>Its callee is not bound either, and that is a limit rather
/// than an oversight.</b> The parser's nesting budget bounds its own recursion but not the chain its
/// postfix loop builds, so a file of 5000 unbalanced parentheses recovers into 2436 nested
/// invocations; descending them turned a bind that took 183ms into one that did not finish inside a
/// minute, and a language server may not be hung by a buffer. A position on such a callee is a
/// question for the syntax tree, which has the whole of it.
/// </para>
/// <para>
/// A wrong-typed argument does <em>not</em> produce one of these. That call resolved: the receiver,
/// the method and the signature are all known, and only one argument's type is wrong, so it stays an
/// <see cref="IrMethodCall"/> and keeps the callee that go-to-definition and signature help are
/// going to ask it for.
/// </para>
/// <para>
/// No backend handles this, and none has to, for the reason <see cref="IrMissingMemberAccess"/>
/// gives: one exists only when a diagnostic was reported, so <c>CompilationResult.Success</c> is
/// false and no backend is ever handed the module.
/// </para>
/// </remarks>
public sealed record IrUncallableInvocation(
    IrExpression? Receiver,
    IReadOnlyList<IrExpression> Arguments,
    SourceSpan Span) : IrExpression(ErrorType.Instance, Span);

public sealed record IrTest(
    IrMethodSignature Target,
    string Name,
    IrTestMessageValue Receiver,
    IReadOnlyList<IrTestArgument> Arguments,
    IrTestExpectation Expectation,
    SourceSpan Span) : IrNode(Span)
{
    /// <summary>
    /// A stable name for this test that does not depend on any target language.
    /// </summary>
    /// <remarks>
    /// Each backend has to mangle test names into an identifier its own language accepts, and the
    /// two do it differently. This is what they report instead, so a conformance harness can check
    /// that every backend ran the same set of tests rather than merely that each ran some.
    /// </remarks>
    public string Identity => $"{Target.Receiver.FullName}.{Target.Name}: {Name}";
}

public sealed record IrTestArgument(string Name, IrExpression Value, SourceSpan Span) : IrNode(Span);

public sealed record IrTestMessageValue(
    MessageDescriptor Descriptor,
    IReadOnlyList<IrTestFieldValue> Fields,
    SourceSpan Span) : IrNode(Span);

public sealed record IrTestFieldValue(
    FieldDescriptor Field,
    IrExpression? ScalarValue,
    IrTestMessageValue? MessageValue,
    SourceSpan Span) : IrNode(Span);

public abstract record IrTestExpectation(SourceSpan Span) : IrNode(Span);

public sealed record IrTestReturnExpectation(IrExpression Value, SourceSpan Span) : IrTestExpectation(Span);

public sealed record IrTestFailExpectation(SourceSpan Span) : IrTestExpectation(Span);
