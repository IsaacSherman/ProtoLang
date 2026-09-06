using System.Text;

namespace ProtoLang.Binding;

/// <summary>
/// One descriptor load, described completely: the schemas asked for, the roots they resolve against,
/// and the protoc that will be run over them.
/// </summary>
/// <remarks>
/// <para>
/// One object serves three purposes that must not be allowed to disagree. It is what protoc is
/// invoked with, it is what the closure is later re-resolved against, and it is the cache's key.
/// Splitting those apart is how a cache comes to validate an entry against a search order the
/// compiler no longer uses.
/// </para>
/// <para>
/// As a key, the naive answer is "the files I asked for", and it is wrong in four separate ways, each
/// of which this type exists to answer. Resolution is first-match, so <em>reordering</em> the include paths
/// can change which schema wins even when the set of directories is identical. The implicit roots are
/// discovered beside the located protoc, so a different protoc means different well-known schemas and
/// must not reach an entry populated under the other one. protoc itself can be upgraded in place,
/// which changes what it emits for identical input. And the file contents, which are the remaining
/// axis, are deliberately not here: they are recorded on the bundle's
/// <see cref="DescriptorBundle.Closure"/>, because until protoc has run nobody knows which files the
/// closure even contains.
/// </para>
/// <para>
/// protoc is identified by path, length and write time rather than by content hash. It is the one
/// input measured in megabytes and the one input that is not a document being edited; hashing it on
/// every keystroke would cost more than the run being avoided.
/// </para>
/// <para>
/// A class with hand-written equality rather than a record. A record compares its members, and three
/// of these are lists -- which records compare by reference, so every request would be unequal to
/// every other and the cache would never hit. Equality is over a canonical rendering built once at
/// construction, which is also cheaper to compare than six members.
/// </para>
/// </remarks>
public sealed class DescriptorRequest : IEquatable<DescriptorRequest>
{
    /// <remarks>
    /// The NUL character, because it is the one character a path on any supported platform cannot
    /// contain. A printable separator would let a directory name holding that character render as two
    /// components, so two different include lists could produce one key.
    /// </remarks>
    private const char Separator = (char)0;

    /// <summary>
    /// The invariant the separator rests on, checked rather than assumed.
    /// </summary>
    /// <remarks>
    /// "No real path contains a NUL" is a fact about file systems, not about this constructor, which
    /// is public and takes whatever it is handed. A caller that passes one -- from a corrupted
    /// setting, a mangled decode, or a deliberate attempt -- could otherwise manufacture two
    /// different requests that render to one key and share one entry, which is the collision the
    /// separator exists to make impossible. Rejecting is right where reporting is not: this is a
    /// caller handing the compiler something no file system could have produced, which is programmer
    /// error, not user input.
    /// </remarks>
    private static void RejectSeparator(IReadOnlyList<string> values, string parameterName)
    {
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException("A path or file name must not be null.", parameterName);
            }

            if (value.Contains(Separator))
            {
                throw new ArgumentException(
                    $"'{value.Replace(Separator, '?')}' contains a NUL character, which no path can. "
                    + "Two such values could render to one cache key.",
                    parameterName);
            }
        }
    }

    private readonly string _canonical;

    public DescriptorRequest(
        string protocPath,
        long protocLength,
        DateTime protocLastWriteUtc,
        IReadOnlyList<string> includePaths,
        IReadOnlyList<string> implicitIncludePaths,
        IReadOnlyList<string> protoFiles)
    {
        ArgumentNullException.ThrowIfNull(protocPath);
        ArgumentNullException.ThrowIfNull(includePaths);
        ArgumentNullException.ThrowIfNull(implicitIncludePaths);
        ArgumentNullException.ThrowIfNull(protoFiles);

        RejectSeparator([protocPath], nameof(protocPath));
        RejectSeparator(includePaths, nameof(includePaths));
        RejectSeparator(implicitIncludePaths, nameof(implicitIncludePaths));
        RejectSeparator(protoFiles, nameof(protoFiles));

        ProtocPath = protocPath;
        ProtocLength = protocLength;
        ProtocLastWriteUtc = protocLastWriteUtc;
        IncludePaths = includePaths;
        ImplicitIncludePaths = implicitIncludePaths;
        ProtoFiles = protoFiles;
        SearchRoots = [.. includePaths, .. implicitIncludePaths];

        _canonical = Render();
    }

    /// <summary>The protoc that would run, and the identity of that executable on disk.</summary>
    public string ProtocPath { get; }

    /// <inheritdoc cref="ProtocPath"/>
    public long ProtocLength { get; }

    /// <inheritdoc cref="ProtocPath"/>
    public DateTime ProtocLastWriteUtc { get; }

    /// <summary>The caller's include directories, in the order protoc will be given them.</summary>
    public IReadOnlyList<string> IncludePaths { get; }

    /// <summary>The well-known schema directories the loader adds behind the caller's.</summary>
    public IReadOnlyList<string> ImplicitIncludePaths { get; }

    /// <summary>The schema paths this load asked for, as they were written.</summary>
    public IReadOnlyList<string> ProtoFiles { get; }

    /// <summary>
    /// Every root a schema name resolves against, in priority order: the caller's, then the implicit
    /// ones.
    /// </summary>
    /// <remarks>
    /// Derived here rather than at each use, because it must be the same list protoc was handed and
    /// the same list a closure is re-checked against. Two places computing "the roots" is how a cache
    /// starts validating against a search order the compiler no longer uses.
    /// </remarks>
    public IReadOnlyList<string> SearchRoots { get; }

    /// <summary>
    /// Whether the executable this request names could actually be measured, and so whether the
    /// request really does account for which protoc would run.
    /// </summary>
    /// <remarks>
    /// False when nothing was found at <see cref="ProtocPath"/> -- a name the process launcher may
    /// still resolve some way of its own. The claim that "which protoc" is part of the key is only
    /// true of a protoc this compiler could identify, and a request that cannot make that claim says
    /// so rather than being keyed on as though it could. A zero-length executable cannot run, so
    /// length is the whole test.
    /// </remarks>
    public bool IdentifiesItsProtoc => ProtocLength > 0;

    public bool Equals(DescriptorRequest? other)
        => other is not null && Comparer.Equals(_canonical, other._canonical);

    public override bool Equals(object? obj) => Equals(obj as DescriptorRequest);

    public override int GetHashCode() => Comparer.GetHashCode(_canonical);

    /// <remarks>
    /// Ordinal, over a rendering that has already settled how each component compares. A comparer
    /// cannot be right for the whole key, because the key holds two kinds of string: locations, where
    /// two spellings may name one directory, and names, where they may not. Rendering each component
    /// through the rule that fits it leaves one string to compare, one way.
    /// </remarks>
    private static StringComparer Comparer => StringComparer.Ordinal;

    /// <remarks>
    /// <para>
    /// Paths render through <see cref="PathIdentity.KeyFor"/> and schema names do not, and the
    /// asymmetry is the point. An include root is a place: <c>C:\schemas</c> and <c>c:/schemas/</c>
    /// are one directory, protoc searches it once, and keying them apart buys two entries and a
    /// second protoc run for one answer. A schema name is not a place -- it is the string protoc
    /// echoes back as <c>FileDescriptor.Name</c>, which the descriptors then carry and the generated
    /// code depends on. Folding <c>Leaf.proto</c> into <c>leaf.proto</c> would answer a request for one
    /// with descriptors named after the other, and the closure check cannot catch it: the stored
    /// closure names the file it really loaded, and that file really is unchanged.
    /// </para>
    /// <para>
    /// protoc itself is a location, and the two spellings that fold together here would have produced
    /// the same length and write time anyway.
    /// </para>
    /// </remarks>
    private string Render()
    {
        var builder = new StringBuilder();

        builder.Append(PathIdentity.KeyFor(ProtocPath)).Append(Separator);
        builder.Append(ProtocLength).Append(Separator);
        builder.Append(ProtocLastWriteUtc.Ticks).Append(Separator);

        Append(IncludePaths, PathIdentity.KeyFor);
        Append(ImplicitIncludePaths, PathIdentity.KeyFor);
        Append(ProtoFiles, name => name);

        return builder.ToString();

        void Append(IReadOnlyList<string> values, Func<string, string> render)
        {
            // The count is rendered as well as the values, so that a component of two entries and a
            // component of one cannot render identically by having the shorter one end where the
            // longer one's second entry begins.
            builder.Append(values.Count).Append(Separator);

            foreach (var value in values)
            {
                builder.Append(render(value)).Append(Separator);
            }
        }
    }
}
