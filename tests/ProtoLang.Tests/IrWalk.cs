using ProtoLang.Ir;
using ProtoLang.Symbols;

namespace ProtoLang.Tests;

/// <summary>
/// Every node of a bound module, for tests that assert a property of all of them.
/// </summary>
/// <remarks>
/// <para>
/// The IR has no visitor and no parent pointers; #38 is the issue that gives the compiler a real
/// walker, and until it lands a test that wants to sweep a module has to descend by hand. This is
/// that descent, written once. Two of them would be one more place for a new node kind to be
/// forgotten, and a sweep that silently stops covering a construct is exactly the test with no
/// teeth.
/// </para>
/// <para>
/// Test-only, and deliberately not in <c>ProtoLang.Core</c>: publishing a walker is #38's design
/// decision to make, not a side effect of needing one here.
/// </para>
/// </remarks>
internal static class IrWalk
{
    /// <summary>
    /// Every statement in every method body, each before the statements it contains. A <c>test</c>
    /// declaration holds expressions but no statements, so there is nothing there to walk.
    /// </summary>
    public static IEnumerable<IrStatement> Statements(IrModule module)
        => module.Methods.SelectMany(method => Statements(method.Body));

    /// <inheritdoc cref="Statements(IrModule)"/>
    public static IEnumerable<IrStatement> Statements(IrStatement statement)
    {
        yield return statement;

        IReadOnlyList<IrStatement> nested = statement switch
        {
            IrBlock block => block.Statements,
            IrForEach loop => [loop.Body],
            IrIf branch => branch.Else is { } otherwise ? [branch.Then, otherwise] : [branch.Then],
            IrWhile loop => [loop.Body],
            _ => [],
        };

        foreach (var inner in nested.SelectMany(Statements))
        {
            yield return inner;
        }
    }

    /// <summary>
    /// Every expression in a module, however deeply nested: method bodies, and the three places a
    /// <c>test</c> declaration holds one -- its receiver fixture, its arguments, and its expectation.
    /// </summary>
    /// <remarks>
    /// The test half is easy to leave out and was, at first. Nothing in this suite noticed, because
    /// the tests written against it are about method bodies -- which is exactly how a sweep quietly
    /// stops covering a subtree and keeps reporting that everything holds.
    /// </remarks>
    public static IEnumerable<IrExpression> Expressions(IrModule module)
        => [
            .. module.Methods.SelectMany(method => Expressions(method.Body)),
            .. module.Tests.SelectMany(Expressions),
        ];

    /// <inheritdoc cref="Expressions(IrModule)"/>
    public static IEnumerable<IrExpression> Expressions(IrStatement statement)
        => Statements(statement).SelectMany(OwnExpressions).SelectMany(Expressions);

    /// <inheritdoc cref="Expressions(IrModule)"/>
    public static IEnumerable<IrExpression> Expressions(IrTest test)
        => [
            .. Expressions(test.Receiver),
            .. test.Arguments.SelectMany(argument => Expressions(argument.Value)),
            .. test.Expectation is IrTestReturnExpectation expected
                ? Expressions(expected.Value)
                : Enumerable.Empty<IrExpression>(),
        ];

    /// <summary>Every expression in a receiver fixture, through however many nested messages.</summary>
    private static IEnumerable<IrExpression> Expressions(IrTestMessageValue value)
        => value.Fields.SelectMany(OwnExpressions);

    /// <inheritdoc cref="Expressions(IrTestMessageValue)"/>
    private static IEnumerable<IrExpression> OwnExpressions(IrTestFieldValue field)
    {
        if (field.ScalarValue is { } scalar)
        {
            return Expressions(scalar);
        }

        return field.MessageValue is { } message
            ? Expressions(message)
            : Enumerable.Empty<IrExpression>();
    }

    /// <inheritdoc cref="Expressions(IrModule)"/>
    public static IEnumerable<IrExpression> Expressions(IrExpression expression)
    {
        yield return expression;

        IReadOnlyList<IrExpression> operands = expression switch
        {
            IrFieldAccess field => [field.Receiver],
            IrFieldPresence presence => [presence.Receiver],
            IrMethodCall call => [call.Receiver, .. call.Arguments],
            IrBinary binary => [binary.Left, binary.Right],
            IrIntegerDivision division => division.OnZero is { } onZero
                ? [division.Left, division.Right, onZero]
                : [division.Left, division.Right],
            IrUnary unary => [unary.Operand],
            IrConversion conversion => [conversion.Operand],
            IrMissingMemberAccess awaiting => [awaiting.Receiver],
            _ => [],
        };

        foreach (var operand in operands.SelectMany(Expressions))
        {
            yield return operand;
        }
    }

    /// <summary>
    /// Every declaration a module makes: each method, its parameters, and every local and loop
    /// binding in its body.
    /// </summary>
    /// <remarks>
    /// Tests contribute none. A <c>test</c> declares nothing of its own -- it names a method that
    /// was declared elsewhere, and walking its target's parameters would report one declaration
    /// once per test that calls it.
    /// </remarks>
    public static IEnumerable<DeclarationSite> Declarations(IrModule module)
        => module.Methods.SelectMany(Declarations);

    /// <inheritdoc cref="Declarations(IrModule)"/>
    public static IEnumerable<DeclarationSite> Declarations(IrMethod method)
        => [
            method.Signature.Declaration,
            .. method.Parameters.Select(parameter => parameter.Declaration),
            .. Statements(method.Body).SelectMany(OwnDeclarations),
        ];

    /// <summary>The expressions a statement holds itself, not counting those in statements it holds.</summary>
    private static IReadOnlyList<IrExpression> OwnExpressions(IrStatement statement) => statement switch
    {
        IrVariableDeclaration declaration => [declaration.Initializer],
        IrAssignment assignment => [assignment.Value],
        IrReturn { Value: { } value } => [value],
        IrForEach loop => [loop.Collection],
        IrIf branch => [branch.Condition],
        IrWhile loop => [loop.Condition],
        IrExpressionStatement expression => [expression.Expression],
        _ => [],
    };

    /// <inheritdoc cref="OwnExpressions"/>
    private static IReadOnlyList<DeclarationSite> OwnDeclarations(IrStatement statement) => statement switch
    {
        IrVariableDeclaration declaration => [declaration.Local.Declaration],
        IrForEach loop => [loop.Loop.Declaration],
        _ => [],
    };
}
