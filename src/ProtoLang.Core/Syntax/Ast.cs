using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

public abstract record SyntaxNode(SourceSpan Span);

public sealed record CompilationUnit(
    IReadOnlyList<ImportDeclaration> Imports,
    IReadOnlyList<ExtendDeclaration> Extends,
    IReadOnlyList<TestDeclaration> Tests,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>An <c>import proto "path.proto";</c> declaration (spec 5.2).</summary>
public sealed record ImportDeclaration(string Path, SourceSpan Span) : SyntaxNode(Span);

/// <summary>An <c>extend MessageName { ... }</c> block (spec 16.1).</summary>
public sealed record ExtendDeclaration(
    string MessageName,
    IReadOnlyList<MethodDeclaration> Methods,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record MethodDeclaration(
    string Name,
    bool IsVirtual,
    IReadOnlyList<ParameterDeclaration> Parameters,
    TypeReference? ReturnType,
    BlockStatement Body,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record ParameterDeclaration(string Name, TypeReference Type, SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// A syntactic type reference. Names are resolved later against protobuf descriptors, since
/// spec 8.1 defines the ProtoLang type universe as exactly the protobuf type universe.
/// </summary>
public sealed record TypeReference(string Name, SourceSpan Span) : SyntaxNode(Span);

public sealed record TestDeclaration(
    string TargetName,
    string Name,
    TestReceiverFixture Receiver,
    IReadOnlyList<TestArgumentDeclaration> Arguments,
    TestExpectation Expectation,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record TestReceiverFixture(
    IReadOnlyList<TestFieldInitializer> Fields,
    SourceSpan Span) : SyntaxNode(Span);

public abstract record TestFieldInitializer(string FieldName, SourceSpan Span) : SyntaxNode(Span);

public sealed record TestScalarFieldInitializer(
    string Name,
    Expression Value,
    SourceSpan Span) : TestFieldInitializer(Name, Span);

public sealed record TestMessageFieldInitializer(
    string Name,
    IReadOnlyList<TestFieldInitializer> Fields,
    SourceSpan Span) : TestFieldInitializer(Name, Span);

public sealed record TestArgumentDeclaration(string Name, Expression Value, SourceSpan Span) : SyntaxNode(Span);

public abstract record TestExpectation(SourceSpan Span) : SyntaxNode(Span);

public sealed record TestReturnExpectation(Expression Value, SourceSpan Span) : TestExpectation(Span);

public sealed record TestFailExpectation(SourceSpan Span) : TestExpectation(Span);

public abstract record Statement(SourceSpan Span) : SyntaxNode(Span);

public sealed record BlockStatement(IReadOnlyList<Statement> Statements, SourceSpan Span) : Statement(Span);

public sealed record VariableDeclarationStatement(
    string Name,
    TypeReference? DeclaredType,
    Expression Initializer,
    SourceSpan Span) : Statement(Span);

public sealed record ReturnStatement(Expression? Value, SourceSpan Span) : Statement(Span);

public sealed record ForInStatement(
    string VariableName,
    Expression Collection,
    BlockStatement Body,
    SourceSpan Span) : Statement(Span);

public sealed record AssignmentStatement(Expression Target, Expression Value, SourceSpan Span) : Statement(Span);

public sealed record ExpressionStatement(Expression Expression, SourceSpan Span) : Statement(Span);

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A bare identifier: a local, a parameter, or an implicit field of the receiver.</summary>
public sealed record NameExpression(string Name, SourceSpan Span) : Expression(Span);

public sealed record MemberAccessExpression(Expression Receiver, string Name, SourceSpan Span) : Expression(Span);

public sealed record InvocationExpression(
    Expression Callee,
    IReadOnlyList<Expression> Arguments,
    SourceSpan Span) : Expression(Span);

public enum BinaryOperatorKind
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

public enum UnaryOperatorKind
{
    Negate,
    LogicalNot,
}

/// <summary>
/// A binary operation. <paramref name="OnZero"/> carries the <c>on_zero</c> clause and is only ever
/// set for <c>/</c> and <c>%</c>.
/// </summary>
public sealed record BinaryExpression(
    BinaryOperatorKind Operator,
    Expression Left,
    Expression Right,
    SourceSpan Span,
    OnZeroClause? OnZero = null) : Expression(Span);

/// <summary>
/// The <c>on_zero</c> clause of an integer division: either a fallback value, or <c>fail</c>,
/// which terminates deterministically.
/// </summary>
/// <param name="Fallback">The replacement value, or null when the clause is <c>fail</c>.</param>
public sealed record OnZeroClause(Expression? Fallback, SourceSpan Span) : SyntaxNode(Span)
{
    public bool IsFail => Fallback is null;
}

public sealed record UnaryExpression(
    UnaryOperatorKind Operator,
    Expression Operand,
    SourceSpan Span) : Expression(Span);

public sealed record IntegerLiteralExpression(long Value, SourceSpan Span) : Expression(Span);

public sealed record FloatLiteralExpression(double Value, SourceSpan Span) : Expression(Span);

public sealed record BooleanLiteralExpression(bool Value, SourceSpan Span) : Expression(Span);

public sealed record StringLiteralExpression(string Value, SourceSpan Span) : Expression(Span);

/// <summary>Placeholder produced at a parse error so later phases can keep walking the tree.</summary>
public sealed record ErrorExpression(SourceSpan Span) : Expression(Span);
