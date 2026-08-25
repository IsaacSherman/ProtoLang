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
public sealed record IrMethodSignature(
    MessageDescriptor Receiver,
    string Name,
    PlType ReturnType,
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<PlType> ParameterTypes);

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

public sealed record IrLiteral(object? Value, PlType LiteralType, SourceSpan Span)
    : IrExpression(LiteralType, Span);

public sealed record IrTest(
    IrMethodSignature Target,
    string Name,
    IrTestMessageValue Receiver,
    IReadOnlyList<IrTestArgument> Arguments,
    IrTestExpectation Expectation,
    SourceSpan Span);

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
