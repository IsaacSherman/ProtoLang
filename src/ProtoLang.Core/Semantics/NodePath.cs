namespace ProtoLang.Semantics;

/// <summary>
/// A node that was found at a position, together with everything that stands above it.
/// </summary>
/// <remarks>
/// <para>
/// The answer to a position query is a path rather than a node, because almost every question an
/// editor asks is answered by context rather than by the node itself: which method body this is,
/// whether this identifier is in type position, whether it is the callee of a call. Returning the
/// chain the descent already walked makes all of those free, and means the trees need no parent
/// pointers, no index, and nothing cached that a keystroke could invalidate.
/// </para>
/// <para>
/// Generic so the two trees share the one shape. Both hold their path root first, which is the order
/// the descent produces; <see cref="Ancestors"/> is the other direction, because a caller looking for
/// context wants the nearest one first.
/// </para>
/// </remarks>
public abstract record NodePath<TNode>
    where TNode : class
{
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <remarks>
    /// An empty path is not a location. "Nothing is here" is representable already, as a null
    /// location, and a second spelling of it would be one every consumer had to check for.
    /// </remarks>
    protected NodePath(IReadOnlyList<TNode> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0)
        {
            throw new ArgumentException("A location needs at least the node it located.", nameof(path));
        }

        Path = path;
    }

    /// <summary>The root, then each node holding the next, ending at <see cref="Node"/>.</summary>
    public IReadOnlyList<TNode> Path { get; }

    /// <summary>The node that was found: the innermost one covering the position.</summary>
    public TNode Node => Path[^1];

    /// <summary>What holds <see cref="Node"/>, then what holds that, out to the root.</summary>
    public IEnumerable<TNode> Ancestors => Path.Take(Path.Count - 1).Reverse();

    /// <summary>
    /// The nearest node of a given kind at or above <see cref="Node"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// At or above, not strictly above: asking whether a position is inside a type reference should
    /// answer yes when the position is on the type reference itself.
    /// </remarks>
    public T? Enclosing<T>()
        where T : class, TNode
        => Path.OfType<T>().LastOrDefault();
}
