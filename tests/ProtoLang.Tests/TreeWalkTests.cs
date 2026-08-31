using System.Collections;
using System.Reflection;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The two walkers see everything the trees hold, in the order the author wrote it.
/// </summary>
/// <remarks>
/// A walker is a hand-written switch over node kinds, and the way one fails is silently: a construct
/// is added to the language, the arm is not, and every sweep built on the walker goes on reporting
/// that all is well over a subtree it no longer visits. So the first test below does not trust the
/// switch -- it asks each record what it holds and checks the switch against the answer.
/// </remarks>
public class TreeWalkTests
{
    // ------- the walkers see what the records declare

    [Fact]
    public void EveryChildASyntaxRecordDeclaresIsOneTheWalkerYields()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var tree = source.Result.SyntaxTree;
            Assert.NotNull(tree);

            foreach (var node in SyntaxWalk.DescendantsAndSelf(tree))
            {
                AssertYieldsEveryChild(source, node, ReflectedChildren<SyntaxNode>(node), SyntaxWalk.ChildrenOf(node));
            }
        }
    }

    [Fact]
    public void EveryChildAnIrRecordDeclaresIsOneTheWalkerYields()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var module = source.Result.Module;
            Assert.NotNull(module);

            foreach (var node in IrWalk.DescendantsAndSelf(module))
            {
                AssertYieldsEveryChild(source, node, ReflectedChildren<IrNode>(node), IrWalk.ChildrenOf(node));
            }
        }
    }

    /// <summary>
    /// Nothing in the IR is reachable twice or from two parents, which is what lets a walk be a tree
    /// walk rather than a graph traversal with a visited set.
    /// </summary>
    [Fact]
    public void NoIrNodeIsReachedTwiceInOneWalk()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var seen = new HashSet<IrNode>(ReferenceEqualityComparer.Instance);

            foreach (var node in IrWalk.DescendantsAndSelf(source.Result.Module!))
            {
                Assert.True(seen.Add(node), $"{source.Name}: {node.GetType().Name} at {node.Span} was reached twice");
            }
        }
    }

    // ------- order and location

    /// <summary>
    /// Children come back in the order they were written, so a caller rendering an outline or
    /// scanning for the construct before the caret can take the walker's word for it.
    /// </summary>
    [Fact]
    public void SyntaxChildrenComeBackInSourceOrder()
    {
        foreach (var source in CompiledCorpus.All)
        {
            foreach (var node in SyntaxWalk.DescendantsAndSelf(source.Result.SyntaxTree!))
            {
                var starts = SyntaxWalk.ChildrenOf(node).Select(child => child.Span.Start.Offset).ToList();

                Assert.Equal(starts.Order(), starts);
            }
        }
    }

    /// <summary>
    /// Every node is somewhere. A node carrying <see cref="SourceSpan.None"/> would be the tightest
    /// possible match at the start of a file and would answer every query made there, which is why
    /// containment refuses one -- but the refusal is a guard, and this is the property it guards.
    /// </summary>
    [Fact]
    public void NoNodeInTheCorpusIsNowhere()
    {
        foreach (var source in CompiledCorpus.All)
        {
            foreach (var node in SyntaxWalk.DescendantsAndSelf(source.Result.SyntaxTree!))
            {
                Assert.False(node.Span.IsNone, $"{source.Name}: {node.GetType().Name} has no location");
            }

            foreach (var node in IrWalk.DescendantsAndSelf(source.Result.Module!))
            {
                Assert.False(node.Span.IsNone, $"{source.Name}: {node.GetType().Name} has no location");
            }
        }
    }

    /// <summary>
    /// A walk of a module reaches the three places a <c>test</c> declaration holds an expression --
    /// its receiver fixture, its arguments, and its expectation -- which is the half of the IR that
    /// is easiest to leave out of a walker and hardest to notice the loss of.
    /// </summary>
    [Fact]
    public void AWalkOfAModuleReachesInsideTestDeclarations()
    {
        var module = CompiledCorpus.SimpleScript.Result.Module!;

        Assert.NotEmpty(module.Tests);

        var reached = IrWalk.DescendantsAndSelf(module).ToList();

        foreach (var test in module.Tests)
        {
            Assert.Contains(reached, node => ReferenceEquals(node, test.Receiver));
            Assert.Contains(reached, node => ReferenceEquals(node, test.Expectation));
        }
    }

    // ------- helpers

    private static void AssertYieldsEveryChild<TNode>(
        CorpusSource source,
        TNode node,
        IReadOnlyList<TNode> declared,
        IReadOnlyList<TNode> walked)
        where TNode : class
    {
        Assert.True(
            declared.Count == walked.Count,
            $"{source.Name}: {node.GetType().Name} holds {declared.Count} nodes and the walker yields "
            + $"{walked.Count}");

        foreach (var child in declared)
        {
            Assert.True(
                walked.Any(yielded => ReferenceEquals(yielded, child)),
                $"{source.Name}: the walker does not yield a {child.GetType().Name} held by "
                + $"{node.GetType().Name}");
        }
    }

    /// <summary>
    /// What a record says it holds, asked of the record rather than of the walker: every public
    /// property that is a node, and every element of every property that is a collection of them.
    /// </summary>
    /// <remarks>
    /// Reflection is the point. Any second hand-written list of children would be as forgettable as
    /// the first and would be forgotten at the same time, by the same commit.
    /// </remarks>
    private static IReadOnlyList<TNode> ReflectedChildren<TNode>(TNode node)
        where TNode : class
    {
        var children = new List<TNode>();

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            switch (property.GetValue(node))
            {
                case TNode child:
                    children.Add(child);
                    break;

                case IEnumerable items and not string:
                    children.AddRange(items.OfType<TNode>());
                    break;
            }
        }

        return children;
    }
}
