using ProtoLang.Diagnostics;

namespace ProtoLang.Semantics;

/// <summary>
/// Which node is at an offset, and what stands above it: the rule, written once, for both trees.
/// </summary>
/// <remarks>
/// <para>
/// Generic over the node type rather than written twice, because the two trees have to agree at the
/// awkward positions -- exactly on a node's end, between two adjacent nodes, on an empty range -- and
/// two copies of a tie-break rule are two answers waiting to disagree. The syntax tree and the IR
/// differ only in what their children are, which is the delegate.
/// </para>
/// <para>
/// The descent does not prune on containment, and cannot: the IR is not span-nested. An
/// <see cref="Ir.IrMissingMemberAccess"/> is the empty point after a dot, while the receiver it
/// carries lies before that point -- so a child can fall outside its parent, and refusing to look
/// inside a parent that does not contain the offset would lose exactly the node an editor is asking
/// about. Every node is visited and the best is kept, which is a linear scan of a method body per
/// request. No index is built until something measures a need for one; #57 is the issue that would
/// measure it.
/// </para>
/// </remarks>
internal static class PositionSearch
{
    /// <summary>
    /// Whether <paramref name="offset"/> is inside <paramref name="span"/>, <b>both ends
    /// inclusive</b>.
    /// </summary>
    /// <remarks>
    /// Half-open ranges say the end is not part of the range, and for containment that is the wrong
    /// answer to the question editors actually ask. The caret sits immediately after an identifier
    /// the moment the author finishes typing it, and that is when completion, hover and signature
    /// help are requested. A span with no location at all is never a candidate: it describes nowhere,
    /// and its zero offsets would otherwise make it the tightest possible match at the start of a
    /// file.
    /// </remarks>
    public static bool Contains(SourceSpan span, int offset)
        => !span.IsNone && span.Start.Offset <= offset && offset <= span.End.Offset;

    /// <summary>
    /// The innermost node containing <paramref name="offset"/>, and the chain of nodes above it,
    /// root first. Null when nothing covers the offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tie-break.</b> Among containing nodes the shortest span wins. Among nodes whose spans
    /// are equally short -- which includes two nodes sharing one span exactly, and two equally long
    /// neighbours meeting at the caret -- the one reached first in this pre-order walk wins: the
    /// outermost of a nested pair, so the implicit receiver the binder introduced never hides the
    /// field access the author wrote, and otherwise the leftmost.
    /// </para>
    /// <para>
    /// Length is compared before order and nothing else is compared at all, so where two nodes of
    /// different lengths merely meet -- a statement whose semicolon abuts the statement after it --
    /// the shorter wins rather than the earlier. The case that matters is unaffected: what follows an
    /// identifier is an operator, a delimiter or whitespace, none of which is a node, so a caret at
    /// the end of a word still finds the word. There is no side-preference on top of length, because
    /// a rule that preferred the node on the left would have to know the two are siblings, and the
    /// IR is not nested tightly enough to answer that.
    /// </para>
    /// <para>
    /// The path is snapshotted when a better node is found rather than reconstructed afterwards,
    /// which is what makes ancestry free: the walk already holds it.
    /// </para>
    /// <para id="depth">
    /// <b>The walk carries its own stack rather than using the call stack.</b> The parser's nesting
    /// budget bounds the depth it recurses to and not the depth of the tree it produces: its postfix
    /// loop builds member accesses and calls iteratively, so a file of 5000 unbalanced parentheses
    /// recovers into an invocation chain 2436 nodes deep. Recursing over that is how a language
    /// server meets a <see cref="StackOverflowException"/>, which cannot be caught and takes the
    /// process with it. An explicit stack also keeps the cost linear: nested iterators would ask each
    /// node for its children once per level above it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TNode>? Find<TNode>(
        IEnumerable<TNode> roots,
        int offset,
        Func<TNode, SourceSpan> spanOf,
        Func<TNode, IReadOnlyList<TNode>> childrenOf)
        where TNode : class
    {
        var pending = new Stack<(TNode Node, int Depth)>();
        foreach (var root in roots.Reverse())
        {
            pending.Push((root, 0));
        }

        var path = new List<TNode>();
        IReadOnlyList<TNode>? best = null;
        var bestLength = int.MaxValue;

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();

            // Everything past this node's depth belongs to a subtree the walk has finished with, so
            // what remains below it is exactly this node's ancestors.
            path.RemoveRange(depth, path.Count - depth);
            path.Add(node);

            var span = spanOf(node);
            if (span.Length < bestLength && Contains(span, offset))
            {
                best = [.. path];
                bestLength = span.Length;
            }

            var children = childrenOf(node);
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push((children[index], depth + 1));
            }
        }

        return best;
    }

    /// <summary>The first node in a pre-order walk whose span is exactly <paramref name="span"/>.</summary>
    /// <remarks>
    /// How one tree finds the other's node, and why it is first-in-pre-order rather than "the one":
    /// spans are not unique. A bare field of the receiver binds to a field access over an implicit
    /// <c>this</c>, and both carry the range of the one identifier that was written. Taking the
    /// first is the same tie-break <see cref="Find"/> applies, so the two queries cannot answer
    /// differently about the same position.
    /// </remarks>
    public static TNode? WithSpan<TNode>(IEnumerable<TNode> nodes, SourceSpan span, Func<TNode, SourceSpan> spanOf)
        where TNode : class
        => span.IsNone ? null : nodes.FirstOrDefault(node => spanOf(node) == span);
}
