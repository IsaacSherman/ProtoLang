using Google.Protobuf.Reflection;
using ProtoLang.Diagnostics;
using ProtoLang.Types;

namespace ProtoLang.Ir;

/// <summary>
/// The typed intermediate representation. Per spec 22.2 this preserves source locations, resolved
/// protobuf type references, exact numeric operation kinds, evaluation order, and virtual
/// annotations. Backends consume only this; they never see the AST.
/// </summary>
public sealed record IrModule(IReadOnlyList<IrMethod> Methods, IReadOnlyList<IrTest> Tests);

/// <summary>
/// Identifies a method without carrying its body, so a call can reference a method declared later
/// in the file (or in another extend block) without a construction cycle.
/// </summary>
/// <param name="ParametersAreNamed">
/// Whether every parameter has a name. False for a parameter list still being typed, where the
/// entry in <paramref name="ParameterNames"/> is an empty string standing in for a name nobody
/// wrote. A caller checking whether it supplied every argument has to ask, because a list with a
/// hole in it cannot say what a complete call would look like -- and demanding an argument called
/// the empty string describes nothing the author did.
/// <para>
/// <b>A stopgap, and known to be one.</b> It answers for the whole signature, so one unnamed
/// parameter silences the missing-argument check for every other parameter too. The precise answer
/// wants per-parameter identity in the IR rather than a bare string, which reaches both backends;
/// #39 is modelling declaration sites already and is where that lands.
/// </para>
/// </param>
public sealed record IrMethodSignature(
    MessageDescriptor Receiver,
    string Name,
    PlType ReturnType,
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<PlType> ParameterTypes,
    bool ParametersAreNamed = true);

public sealed record IrParameter(string Name, PlType Type);

/// <summary>A local variable or a <c>for</c> loop binding.</summary>
public sealed record IrLocal(string Name, PlType Type);

public sealed record IrMethod(
    IrMethodSignature Signature,
    IReadOnlyList<IrParameter> Parameters,
    IrBlock Body,
    bool IsVirtual,
    SourceSpan Span)
{
    public MessageDescriptor Receiver => Signature.Receiver;

    public string Name => Signature.Name;

    public PlType ReturnType => Signature.ReturnType;
}

public abstract record IrStatement(SourceSpan Span);

public sealed record IrBlock(IReadOnlyList<IrStatement> Statements, SourceSpan Span) : IrStatement(Span);

public sealed record IrVariableDeclaration(IrLocal Local, IrExpression Initializer, SourceSpan Span)
    : IrStatement(Span);

public sealed record IrAssignment(IrLocal Target, IrExpression Value, SourceSpan Span) : IrStatement(Span);

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

public abstract record IrExpression(PlType Type, SourceSpan Span);

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

public sealed record IrTest(
    IrMethodSignature Target,
    string Name,
    IrTestMessageValue Receiver,
    IReadOnlyList<IrTestArgument> Arguments,
    IrTestExpectation Expectation,
    SourceSpan Span)
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

public sealed record IrTestArgument(string Name, IrExpression Value, SourceSpan Span);

public sealed record IrTestMessageValue(
    MessageDescriptor Descriptor,
    IReadOnlyList<IrTestFieldValue> Fields,
    SourceSpan Span);

public sealed record IrTestFieldValue(
    FieldDescriptor Field,
    IrExpression? ScalarValue,
    IrTestMessageValue? MessageValue,
    SourceSpan Span);

public abstract record IrTestExpectation(SourceSpan Span);

public sealed record IrTestReturnExpectation(IrExpression Value, SourceSpan Span) : IrTestExpectation(Span);

public sealed record IrTestFailExpectation(SourceSpan Span) : IrTestExpectation(Span);
