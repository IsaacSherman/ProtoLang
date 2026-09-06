namespace ProtoLang.Binding;

/// <summary>
/// Finds the file a schema path names, by the rule protoc itself follows: the roots are searched in
/// order and the first match wins.
/// </summary>
/// <remarks>
/// <para>
/// One home for a rule that is asked twice and must answer identically both times. The compilation
/// asks it to turn an <c>import proto</c> declaration into a file, and the descriptor cache asks it
/// to work out which file backs each name in a closure protoc already resolved. If those two loops
/// were written separately they would eventually disagree about which root wins, and the disagreement
/// would show up as a cache that serves descriptors built from a file the compiler no longer thinks
/// is the one being imported.
/// </para>
/// <para>
/// First-match rather than best-match is not an arbitrary choice: it is what protoc does with
/// <c>--proto_path</c>, and the cache is only correct if this compiler predicts protoc's answer
/// rather than a reasonable one of its own.
/// </para>
/// </remarks>
public static class SchemaLookup
{
    /// <summary>
    /// The file <paramref name="relativePath"/> resolves to, or null when no root holds it.
    /// </summary>
    /// <param name="relativePath">
    /// A schema path as protoc understands it: relative to a root, forward-slashed. Both separators
    /// work on Windows, so the canonical names protoc puts in a descriptor set need no conversion.
    /// </param>
    /// <param name="roots">The directories to search, in priority order.</param>
    public static string? Find(string relativePath, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(roots);

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
