using System.Security.Cryptography;

namespace ProtoLang.Binding;

/// <summary>
/// One file protoc pulled into a compilation, and enough about it to tell whether it has changed.
/// </summary>
/// <param name="Name">
/// The path protoc knows the file by: relative to a proto root, forward-slashed. This is the name
/// that appears in the descriptor set and the one a <c>.proto</c> import names.
/// </param>
/// <param name="Path">
/// The file on disk it resolved to, or null when nothing on disk backs it. Null is not an error: from
/// version 33 protoc resolves <c>google/protobuf/*.proto</c> from descriptors compiled into the
/// binary, so a closure can legitimately contain a name no include root holds.
/// </param>
/// <param name="ContentHash">
/// A hash of the file's bytes, or null when there is no file. Content rather than a timestamp because
/// a checkout, a branch switch, and a network share all restore old timestamps to files whose bytes
/// are new, and a descriptor cache that believes them serves a schema the user has already changed.
/// A closure is a handful of small files; reading them costs a fraction of the protoc run being
/// avoided.
/// </param>
public sealed record SchemaFile(string Name, string? Path, string? ContentHash);

/// <summary>
/// Describes the set of files a descriptor set was built from, and answers whether that description
/// still holds.
/// </summary>
/// <remarks>
/// <para>
/// The description and the check are the same function run twice: <see cref="IsCurrent"/> re-describes
/// the names it was given and compares. Anything else would be two statements of one rule, and the
/// way they would eventually disagree is a cache that reports itself valid against files it would no
/// longer resolve the same way.
/// </para>
/// <para>
/// The one change a description cannot see is a write that lands after protoc read the file and
/// before this hashes it. The bundle then holds descriptors built from the old bytes beside a hash of
/// the new ones, and because that hash keeps matching, the entry stays valid until the file changes
/// again. Closing the window would mean hashing before the run, which is not possible: until protoc
/// reports the closure, nobody knows which files are in it. It is bounded by the length of a single
/// protoc invocation and it resolves on the next edit to that file, which is why it is written down
/// here rather than designed around.
/// </para>
/// </remarks>
public static class SchemaClosure
{
    /// <summary>Describes each named schema as it stands right now.</summary>
    public static IReadOnlyList<SchemaFile> Describe(IEnumerable<string> names, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(roots);

        return [.. names.Select(name => Describe(name, roots))];
    }

    /// <summary>Whether every file in <paramref name="closure"/> still resolves the same way.</summary>
    /// <remarks>
    /// This one comparison covers every invalidation trigger that is about files rather than about
    /// arguments. A transitively imported schema whose bytes changed differs by hash. A schema that
    /// was deleted resolves to nothing. A schema that <em>appeared</em> in a higher-priority root now
    /// resolves to a different path than the one recorded -- which is the case that a naive "have any
    /// of my files changed?" check misses entirely, because the file that shadows is not a file the
    /// old closure ever knew about. A name protoc supplied from its own descriptors is recorded with
    /// no path, so a file appearing where there was none is a change like any other.
    /// </remarks>
    public static bool IsCurrent(IReadOnlyList<SchemaFile> closure, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(roots);

        return closure.SequenceEqual(Describe(closure.Select(file => file.Name), roots));
    }

    private static SchemaFile Describe(string name, IReadOnlyList<string> roots)
    {
        var path = SchemaLookup.Find(name, roots);
        return new SchemaFile(name, path, path is null ? null : Hash(path));
    }

    /// <remarks>
    /// A file the compiler can see but cannot read gets a hash that will never match another, so the
    /// entry is treated as changed every time it is examined. That costs a protoc run this compiler
    /// might have avoided, which is the direction the failure has to fall: the alternative is serving
    /// descriptors for a file whose contents are unknown. Throwing is not an option at all -- a
    /// locked schema is a thing an editor meets, and the compiler may not fail its caller over it.
    /// </remarks>
    private static string Hash(string path)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
