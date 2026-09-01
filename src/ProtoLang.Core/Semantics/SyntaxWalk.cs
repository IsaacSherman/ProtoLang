using ProtoLang.Syntax;

namespace ProtoLang.Semantics;

/// <summary>Down through the syntax tree: what a node holds, and everything below it.</summary>
/// <remarks>
/// <para>
/// The tree has no visitor and no parent pointers, and this does not add either. It answers one
/// question -- what does this node hold -- and every walk in this namespace is built out of that,
/// so a node kind can be forgotten in exactly one place. <c>TreeWalkTests</c> checks this switch
/// against what the records themselves declare, which is what keeps a sweep from quietly ceasing to
/// cover a construct.
/// </para>
/// <para>
/// A node kind with no arm yields nothing rather than throwing. A walker that throws on an
/// unexpected node takes a language server down with it, and the compiler's standing rule is that
/// nothing survivable becomes an exception; a hole here costs an editor one construct it cannot see
/// inside, and the test above is what stops one existing.
/// </para>
/// </remarks>
public static class SyntaxWalk
{
    /// <summary>What a node holds directly, in source order.</summary>
    public static IReadOnlyList<SyntaxNode> ChildrenOf(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node switch
        {
            // The parser keeps the three top-level kinds in three lists, and a file may interleave
            // them, so this is the one place where source order has to be restored rather than
            // inherited.
            CompilationUnit unit => [
                .. unit.Imports.Cast<SyntaxNode>().Concat(unit.Extends).Concat(unit.Tests)
                    .OrderBy(child => child.Span.Start.Offset),
            ],

            ExtendDeclaration extend => [.. extend.Methods],
            MethodDeclaration method => method.ReturnType is { } returnType
                ? [.. method.Parameters, returnType, method.Body]
                : [.. method.Parameters, method.Body],
            ParameterDeclaration parameter => [parameter.Type],

            TestDeclaration test => [test.Target, test.Receiver, .. test.Arguments, test.Expectation],
            TestReceiverFixture fixture => [.. fixture.Fields],
            TestScalarFieldInitializer field => [field.Value],
            TestMessageFieldInitializer field => [.. field.Fields],
            TestArgumentDeclaration argument => [argument.Value],
            TestReturnExpectation expectation => [expectation.Value],

            BlockStatement block => [.. block.Statements],
            VariableDeclarationStatement declaration => declaration.DeclaredType is { } declaredType
                ? [declaredType, declaration.Initializer]
                : [declaration.Initializer],
            ReturnStatement { Value: { } returned } => [returned],
            ForInStatement loop => [loop.Collection, loop.Body],
            IfStatement branch => branch.Else is { } otherwise
                ? [branch.Condition, branch.Then, otherwise]
                : [branch.Condition, branch.Then],
            WhileStatement loop => [loop.Condition, loop.Body],
            AssignmentStatement assignment => [assignment.Target, assignment.Value],
            ExpressionStatement statement => [statement.Expression],

            MemberAccessExpression member => [member.Receiver],
            InvocationExpression invocation => [invocation.Callee, .. invocation.Arguments],
            BinaryExpression binary => binary.OnZero is { } onZero
                ? [binary.Left, binary.Right, onZero]
                : [binary.Left, binary.Right],
            OnZeroClause { Fallback: { } fallback } => [fallback],
            UnaryExpression unary => [unary.Operand],
            HasExpression has => [has.Operand],
            CastExpression cast => [cast.Operand, cast.TargetType],

            _ => [],
        };
    }

    /// <summary>A node and everything below it, each node before the nodes it holds.</summary>
    /// <inheritdoc cref="PositionSearch.Find" path="/remarks/para[@id='depth']"/>
    public static IEnumerable<SyntaxNode> DescendantsAndSelf(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var pending = new Stack<SyntaxNode>();
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
}
