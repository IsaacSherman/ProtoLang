using System.Runtime.InteropServices;

namespace ProtoLang;

/// <summary>
/// Whether two strings name the same file or directory, answered in one place.
/// </summary>
/// <remarks>
/// <para>
/// The compiler compares paths in several unrelated places -- deduplicating search paths, keying a
/// descriptor load, deciding whether a schema in a closure is still the schema that was read -- and
/// each of them had its own answer. One said <c>OrdinalIgnoreCase</c> everywhere, one said
/// <c>Ordinal</c> everywhere, and both were wrong on some platform. Two rules for one question is how
/// a cache comes to hold two entries for a path that a search treats as one, so the question is asked
/// here or not at all.
/// </para>
/// <para>
/// Case is folded on Windows and nowhere else. This is deliberately not a claim about what the file
/// system underneath actually does: macOS is case-insensitive on a default volume and case-sensitive
/// on others, and a Linux directory may be mounted from either. The two ways of being wrong are not
/// symmetric. Folding case where the file system does not merges two genuinely different files, and a
/// compilation then answers a question about one of them with the other. Not folding it where the
/// file system does costs a duplicate cache entry and a second protoc run. Only one of those is a
/// wrong answer, so the fold is applied exactly where it is certain.
/// </para>
/// <para>
/// Nothing here touches the file system, and nothing here throws. A key is a comparison, not a
/// resolution: a caller that wants a path made absolute calls <see cref="Path.GetFullPath(string)"/>
/// first and keeps that spelling for display. The key is only ever compared against another key.
/// </para>
/// </remarks>
public static class PathIdentity
{
    /// <summary>Whether two spellings differing only in case name two different files here.</summary>
    public static bool IsCaseSensitive { get; } = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Compares whole paths. Use it for a dictionary of paths, a <c>Contains</c>, or a set.
    /// </summary>
    /// <remarks>
    /// Case is the only difference this handles, because a <see cref="StringComparer"/> cannot
    /// normalize a separator or a trailing slash. Where those are possible -- anything reaching the
    /// compiler from a URI, a settings file, or a command line -- compare <see cref="KeyFor"/> values
    /// instead, which settle all three.
    /// </remarks>
    public static StringComparer Comparer { get; }
        = IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The spelling-independent form of <paramref name="path"/>: two strings naming one file produce
    /// one key, and <see cref="StringComparer.Ordinal"/> then tells them apart correctly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three differences are settled, all of them ones the operating system already ignores. A
    /// trailing separator, which an editor adds to a folder path and a command line usually does not.
    /// The alternate separator, because Windows accepts a forward slash everywhere and a URI produces
    /// nothing else. And case, on the platform where case is not part of a name.
    /// </para>
    /// <para>
    /// A root keeps its separator: <c>C:\</c> and <c>/</c> are directories, and trimming them
    /// produces a drive-relative path and an empty string respectively, neither of which names what
    /// was asked about.
    /// </para>
    /// <para>
    /// This is a key, not a path. It is not the string to print, to hand to protoc, or to open --
    /// uppercasing a path is fine for comparison and wrong for everything else.
    /// </para>
    /// </remarks>
    public static string KeyFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var key = IsCaseSensitive ? path : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        key = Path.TrimEndingDirectorySeparator(key);

        return IsCaseSensitive ? key : key.ToUpperInvariant();
    }

    /// <summary>
    /// Whether <paramref name="path"/> says where something is, as opposed to only what it is called.
    /// </summary>
    /// <remarks>
    /// The distinction a bare tool name rests on. <c>protoc</c> is a name the operating system will
    /// look up; <c>./protoc</c> and <c>C:\tools\protoc.exe</c> are places. Resolving the first as
    /// though it were the second -- against a working directory, or a workspace folder -- looks for
    /// one file in a directory that was never going to hold it, and the failure reads as if the tool
    /// were missing rather than as if the question were wrong.
    /// </remarks>
    public static bool NamesALocation(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Path.IsPathRooted(path)
            || path.Contains(Path.DirectorySeparatorChar)
            || path.Contains(Path.AltDirectorySeparatorChar);
    }

    /// <summary>Whether two paths name the same file or directory, however each is spelled.</summary>
    /// <remarks>Null equals null and nothing else, so a caller with an optional path asks once.</remarks>
    public static bool AreSame(string? left, string? right)
        => left is null || right is null
            ? left is null && right is null
            : string.Equals(KeyFor(left), KeyFor(right), StringComparison.Ordinal);
}
