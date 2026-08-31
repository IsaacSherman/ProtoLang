using ProtoLang.Ir;

namespace ProtoLang.Semantics;

/// <summary>Down through the typed IR: what a node holds, and everything below it.</summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="SyntaxWalk"/>, and everything said there applies here: one switch,
/// no visitor, a missing arm yields nothing rather than throwing, and <c>TreeWalkTests</c> checks
/// the switch against what the records declare.
/// </para>
/// <para>
/// <see cref="IrModule"/> is not a node -- it is nowhere in the source -- so a walk of a module
/// starts from each method and each test instead. A method's parameters are not children either:
/// an <see cref="IrParameter"/> is a symbol with a declaration site rather than a node with a span,
/// and a position on a parameter is a question for the syntax tree.
/// </para>
/// </remarks>
public static class IrWalk
{
    /// <summary>Every node a module contains, each before the nodes it holds.</summary>
    public static IEnumerable<IrNode> DescendantsAndSelf(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return RootsOf(module).SelectMany(DescendantsAndSelf);
    }

    /// <inheritdoc cref="DescendantsAndSelf(IrModule)"/>
    public static IEnumerable<IrNode> DescendantsAndSelf(IrNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        yield return node;

        foreach (var descendant in ChildrenOf(node).SelectMany(DescendantsAndSelf))
        {
            yield return descendant;
        }
    }

    /// <summary>Where a walk of a module begins: every method, then every test.</summary>
    public static IReadOnlyList<IrNode> RootsOf(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return [.. module.Methods, .. module.Tests];
    }

    /// <summary>What a node holds directly.</summary>
    /// <remarks>
    /// In source order wherever the source had an order. A receiver comes before what is read from
    /// it and an argument list follows its callee, but a node the binder anchored at a hole -- the
    /// empty point an <see cref="IrMissingMemberAccess"/> occupies -- can sit before the receiver it
    /// carries, because that is where the caret is rather than where the text is.
    /// </remarks>
    public static IReadOnlyList<IrNode> ChildrenOf(IrNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node switch
        {
            IrMethod method => [method.Body],

            IrBlock block => [.. block.Statements],
            IrVariableDeclaration declaration => [declaration.Initializer],
            IrAssignment assignment => [assignment.Value],
            IrReturn { Value: { } returned } => [returned],
            IrForEach loop => [loop.Collection, loop.Body],
            IrIf branch => branch.Else is { } otherwise
                ? [branch.Condition, branch.Then, otherwise]
                : [branch.Condition, branch.Then],
            IrWhile loop => [loop.Condition, loop.Body],
            IrExpressionStatement statement => [statement.Expression],

            IrFieldAccess field => [field.Receiver],
            IrFieldPresence presence => [presence.Receiver],
            IrMethodCall call => [call.Receiver, .. call.Arguments],
            IrUncallableInvocation call => call.Receiver is { } receiver
                ? [receiver, .. call.Arguments]
                : [.. call.Arguments],
            IrMissingMemberAccess awaiting => [awaiting.Receiver],
            IrBinary binary => [binary.Left, binary.Right],
            IrIntegerDivision division => division.OnZero is { } onZero
                ? [division.Left, division.Right, onZero]
                : [division.Left, division.Right],
            IrUnary unary => [unary.Operand],
            IrConversion conversion => [conversion.Operand],

            IrTest test => [test.Receiver, .. test.Arguments, test.Expectation],
            IrTestArgument argument => [argument.Value],
            IrTestMessageValue message => [.. message.Fields],
            IrTestFieldValue { ScalarValue: { } scalar } => [scalar],
            IrTestFieldValue { MessageValue: { } message } => [message],
            IrTestReturnExpectation expectation => [expectation.Value],

            _ => [],
        };
    }
}
