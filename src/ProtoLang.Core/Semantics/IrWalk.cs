using ProtoLang.Ir;
using ProtoLang.Symbols;

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
    /// <inheritdoc cref="PositionSearch.Find" path="/remarks/para[@id='depth']"/>
    public static IEnumerable<IrNode> DescendantsAndSelf(IrNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var pending = new Stack<IrNode>();
        pending.Push(node);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            var children = ChildrenOf(current);
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }

    /// <summary>
    /// Every declaration a module makes: each method, its parameters, and every local and loop
    /// binding in its body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of what a reference index needs. <see cref="IrModule.References"/> says where
    /// each name was used; this says where each was introduced, and it is a walk rather than a
    /// second list because <see cref="DeclarationSite"/> already holds the answer -- recording it
    /// twice would be two places for it to be wrong.
    /// </para>
    /// <para>
    /// A <c>test</c> contributes nothing. It declares no name of its own, and its target's
    /// parameters are declared by the method: walking them here would report one parameter once per
    /// test that supplies it.
    /// </para>
    /// <para>
    /// A declaration whose name was never written is included. It has an identity, an extent, and an
    /// empty range where the name would go, and a buffer being typed into is full of them; leaving
    /// them out would mean the index disagreed with itself about what exists for as long as someone
    /// was mid-word.
    /// </para>
    /// </remarks>
    public static IEnumerable<DeclarationSite> DeclarationsOf(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return module.Methods.SelectMany(method =>
            new[] { method.Signature.Declaration }
                .Concat(method.Parameters.Select(parameter => parameter.Declaration))
                .Concat(DescendantsAndSelf(method).SelectMany(DeclarationsIn)));
    }

    /// <inheritdoc cref="DeclarationsOf"/>
    private static IReadOnlyList<DeclarationSite> DeclarationsIn(IrNode node) => node switch
    {
        IrVariableDeclaration declaration => [declaration.Local.Declaration],
        IrForEach loop => [loop.Loop.Declaration],
        _ => [],
    };

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
            IrAssignment assignment => [assignment.Target, assignment.Value],
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
