using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

public abstract record SyntaxNode(SourceSpan Span);

public sealed record CompilationUnit(
    IReadOnlyList<ImportDeclaration> Imports,
    IReadOnlyList<ExtendDeclaration> Extends,
    IReadOnlyList<TestDeclaration> Tests,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>An <c>import proto "path.proto";</c> declaration (spec 5.2).</summary>
/// <param name="PathIsMissing">
/// Whether the path was never written, which is not the same as an empty one. The distinction
/// <see cref="SyntaxName"/> draws for names, for the same reason: a path that is absent has been
/// reported as a syntax error already, and looking for a schema called the empty string reports the
/// one mistake a second time.
/// </param>
public sealed record ImportDeclaration(
    string Path,
    SourceSpan Span,
    bool PathIsMissing = false) : SyntaxNode(Span);

/// <summary>An <c>extend MessageName { ... }</c> block (spec 16.1).</summary>
public sealed record ExtendDeclaration(
    SyntaxName MessageName,
    IReadOnlyList<MethodDeclaration> Methods,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record MethodDeclaration(
    SyntaxName Name,
    bool IsVirtual,
    IReadOnlyList<ParameterDeclaration> Parameters,
    TypeReference? ReturnType,
    BlockStatement Body,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record ParameterDeclaration(SyntaxName Name, TypeReference Type, SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// A syntactic type reference. Names are resolved later against protobuf descriptors, since
/// spec 8.1 defines the ProtoLang type universe as exactly the protobuf type universe.
/// </summary>
public sealed record TypeReference(SyntaxName Name, SourceSpan Span) : SyntaxNode(Span);

public sealed record TestDeclaration(
    SyntaxName TargetName,
    string Name,
    TestReceiverFixture Receiver,
    IReadOnlyList<TestArgumentDeclaration> Arguments,
    TestExpectation Expectation,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record TestReceiverFixture(
    IReadOnlyList<TestFieldInitializer> Fields,
    SourceSpan Span) : SyntaxNode(Span);

public abstract record TestFieldInitializer(SyntaxName FieldName, SourceSpan Span) : SyntaxNode(Span);

public sealed record TestScalarFieldInitializer(
    SyntaxName Name,
    Expression Value,
    SourceSpan Span) : TestFieldInitializer(Name, Span);

public sealed record TestMessageFieldInitializer(
    SyntaxName Name,
    IReadOnlyList<TestFieldInitializer> Fields,
    SourceSpan Span) : TestFieldInitializer(Name, Span);

public sealed record TestArgumentDeclaration(SyntaxName Name, Expression Value, SourceSpan Span) : SyntaxNode(Span);

public abstract record TestExpectation(SourceSpan Span) : SyntaxNode(Span);

public sealed record TestReturnExpectation(Expression Value, SourceSpan Span) : TestExpectation(Span);

public sealed record TestFailExpectation(SourceSpan Span) : TestExpectation(Span);

public abstract record Statement(SourceSpan Span) : SyntaxNode(Span);

public sealed record BlockStatement(IReadOnlyList<Statement> Statements, SourceSpan Span) : Statement(Span);

public sealed record VariableDeclarationStatement(
    SyntaxName Name,
    TypeReference? DeclaredType,
    Expression Initializer,
    SourceSpan Span) : Statement(Span);

public sealed record ReturnStatement(Expression? Value, SourceSpan Span) : Statement(Span);

public sealed record ForInStatement(
    SyntaxName VariableName,
    Expression Collection,
    BlockStatement Body,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// An <c>if</c> statement (spec 15.1). The condition is not parenthesized and the branches are
/// always braced. <paramref name="Else"/> is either a <see cref="BlockStatement"/> or a nested
/// <see cref="IfStatement"/>; the latter is how <c>else if</c> chains are represented.
/// </summary>
public sealed record IfStatement(
    Expression Condition,
    BlockStatement Then,
    Statement? Else,
    SourceSpan Span) : Statement(Span);

/// <summary>A <c>while</c> loop (spec 15.2).</summary>
public sealed record WhileStatement(
    Expression Condition,
    BlockStatement Body,
    SourceSpan Span) : Statement(Span);

public sealed record BreakStatement(SourceSpan Span) : Statement(Span);

public sealed record ContinueStatement(SourceSpan Span) : Statement(Span);

public sealed record AssignmentStatement(Expression Target, Expression Value, SourceSpan Span) : Statement(Span);

public sealed record ExpressionStatement(Expression Expression, SourceSpan Span) : Statement(Span);

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A bare identifier: a local, a parameter, or an implicit field of the receiver.</summary>
public sealed record NameExpression(SyntaxName Name, SourceSpan Span) : Expression(Span);

/// <summary>A field or method reached through a value: <c>customer.email</c>.</summary>
/// <remarks>
/// <c>Name.IsMissing</c> is the shape of a caret waiting for a completion list -- the author typed
/// the dot and stopped. It is a node rather than an absence precisely so that a consumer can ask
/// what the receiver is and get an answer, and <c>Name.Span</c> is the empty range where the member
/// name would go. See <see cref="SyntaxName"/>.
/// </remarks>
public sealed record MemberAccessExpression(Expression Receiver, SyntaxName Name, SourceSpan Span) : Expression(Span);

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

/// <summary>
/// A presence test, <c>has customer.email</c> (spec 8.4).
/// </summary>
/// <remarks>
/// Not a <see cref="UnaryExpression"/>, because its operand is not an expression in the usual
/// sense: <c>has</c> asks about a field rather than about a value, and reading the value is exactly
/// what it must not do. The binder enforces that the operand resolves to a field access.
/// </remarks>
public sealed record HasExpression(
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// An explicit numeric conversion, <c>x as int64</c> (spec 10.3). ProtoLang applies no implicit
/// numeric conversions, so this is the only way an expression changes width or signedness.
/// </summary>
public sealed record CastExpression(
    Expression Operand,
    TypeReference TargetType,
    SourceSpan Span) : Expression(Span);

public sealed record IntegerLiteralExpression(long Value, SourceSpan Span) : Expression(Span);

public sealed record FloatLiteralExpression(double Value, SourceSpan Span) : Expression(Span);

public sealed record BooleanLiteralExpression(bool Value, SourceSpan Span) : Expression(Span);

public sealed record StringLiteralExpression(string Value, SourceSpan Span) : Expression(Span);

/// <summary>Placeholder produced at a parse error so later phases can keep walking the tree.</summary>
public sealed record ErrorExpression(SourceSpan Span) : Expression(Span);
