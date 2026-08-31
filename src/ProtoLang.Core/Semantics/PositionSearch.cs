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
/// <para>
/// Recursion is bounded by the parser's own nesting budget (<c>MaxNestingDepth</c>), so a walk of a
/// tree the parser produced cannot run the stack out however hostile the source was.
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
    /// are equally short -- which includes the case of two nodes sharing one span exactly -- the one
    /// reached first in this pre-order walk wins. That is the outermost of a nested pair, so the
    /// implicit receiver the binder introduced never hides the field access the author wrote; and it
    /// is the leftmost of two adjacent ones, so a caret on the boundary belongs to the node it ends
    /// rather than to the one it begins.
    /// </para>
    /// <para>
    /// The path is snapshotted when a better node is found rather than reconstructed afterwards,
    /// which is what makes ancestry free: the descent already holds it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TNode>? Find<TNode>(
        IEnumerable<TNode> roots,
        int offset,
        Func<TNode, SourceSpan> spanOf,
        Func<TNode, IReadOnlyList<TNode>> childrenOf)
        where TNode : class
    {
        var path = new List<TNode>();
        IReadOnlyList<TNode>? best = null;
        var bestLength = int.MaxValue;

        foreach (var root in roots)
        {
            Descend(root);
        }

        return best;

        void Descend(TNode node)
        {
            path.Add(node);

            var span = spanOf(node);
            if (span.Length < bestLength && Contains(span, offset))
            {
                best = [.. path];
                bestLength = span.Length;
            }

            foreach (var child in childrenOf(node))
            {
                Descend(child);
            }

            path.RemoveAt(path.Count - 1);
        }
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
