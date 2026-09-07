namespace ProtoLang.Binding;

/// <summary>
/// One thing an <c>import proto</c> path could name: a schema, or a directory holding more of them.
/// </summary>
/// <param name="Path">
/// The path as protobuf spells it -- forward slashes, relative to a root, a directory carrying a
/// trailing one -- and never as this file system spells it. An editor inserts this string verbatim,
/// so a Windows spelling here produces a source file that compiles on one machine and on no other.
/// </param>
/// <param name="Root">
/// The include directory this came from: the one <see cref="SchemaLookup.Find"/> would return, since
/// both walk the roots in the same order and stop at the same place.
/// </param>
public sealed record SchemaCandidate(string Path, bool IsDirectory, string Root)
{
    /// <summary>
    /// The other roots holding this same relative path, in the order they were searched.
    /// </summary>
    /// <remarks>
    /// Empty for almost everything, and the whole point for the rest. First-match resolution means
    /// exactly one of these files is ever compiled, and which one is decided by the order of
    /// <c>--proto_path</c> arguments that nothing on screen shows. A user with a schema checkout
    /// shadowing a vendored copy needs to be told, because the alternative is editing one file and
    /// compiling the other.
    /// </remarks>
    public IReadOnlyList<string> ShadowedRoots { get; init; } = [];
}

/// <summary>What one directory holds across the roots, and whether the walk saw all of it.</summary>
/// <param name="SawEverything">
/// False when the walk stopped on its budget, which means the list is a prefix of the truth rather
/// than the truth. A caller offering these to a person may still offer them; a caller reasoning about
/// which of them is nearest to something must not, because the nearest of an arbitrary prefix is not
/// the nearest.
/// </param>
public sealed record SchemaListing(IReadOnlyList<SchemaCandidate> Candidates, bool SawEverything)
{
    /// <summary>Nothing, seen in full: the answer for a prefix that could not name a place.</summary>
    public static SchemaListing Nothing { get; } = new([], SawEverything: true);
}

/// <summary>
/// What is importable, asked one directory at a time: which roots to search, what they hold, and --
/// when a path named nothing -- what it very nearly named.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="SchemaLookup"/> and for the same reason it exists. That type answers
/// "which file does this path name"; this one answers "what paths are there", and the two have to
/// agree exactly. An editor offering a candidate the compiler then fails to resolve is worse than an
/// editor offering nothing: it invites the user to type something and then blames them for it.
/// </para>
/// <para>
/// <b>One directory level, on demand, never cached.</b> Every question here costs one directory
/// listing per root and no recursion. That is not a performance compromise, it is what makes the rest
/// of the design unnecessary: there is no depth to bound before enumeration has to go lazy, no index
/// to build, and nothing to invalidate when a file appears on disk that no process here is watching
/// for. A prefix that names a deeper directory is simply another question of the same shape.
/// </para>
/// <para>
/// Nothing here throws on the file system. A root that does not exist, one that cannot be read, one
/// that a path length or a permission refuses -- all of them contribute nothing and let the remaining
/// roots answer. An include path that is wrong is the ordinary case this feature exists to make
/// visible, and it is diagnosed by the list coming back short rather than by an exception reaching an
/// editor.
/// </para>
/// </remarks>
public static class SchemaCatalog
{
    /// <summary>
    /// How many file system entries one question may look at before it stops and says it did.
    /// </summary>
    /// <remarks>
    /// A guard, not a measurement. Nobody picks a schema out of two thousand names, so a directory
    /// wider than this is not one a person is choosing from -- it is a vendored tree, a build output,
    /// or a network mount somebody added to their include paths, and reading all of it would turn a
    /// keystroke or a diagnostic into a wait. #57 pins the real figure, and pins it once for both
    /// callers, since completion and the near match on a failed import walk through the same door.
    /// </remarks>
    public const int MostEntriesExamined = 2048;

    /// <summary>
    /// The directories an <c>import proto</c> path is resolved against, in the order protoc searches
    /// them: everything the compilation settled, then whatever the loader adds of its own.
    /// </summary>
    /// <param name="searchPaths">
    /// <see cref="Compilation.SearchPaths"/> -- the caller's include paths followed by each source's
    /// own directory, already deduplicated.
    /// </param>
    /// <param name="loader">
    /// The loader that will run, or null when there is none. Null is an ordinary answer rather than a
    /// missing one: protoc may not be installed, and the user's own roots still resolve everything
    /// but the well-known schemas.
    /// </param>
    /// <remarks>
    /// Three lines with a name, because the expression was written out twice -- once where the
    /// compilation checks its imports and once where the host resolves protoc's own error messages --
    /// and import completion would have been the third. Two spellings of one rule is how the two come
    /// to disagree about which root wins, and a disagreement here shows up as an editor offering a
    /// schema the compiler will not find, or as a diagnostic naming a directory protoc never looked
    /// in.
    /// </remarks>
    public static IReadOnlyList<string> RootsFor(IReadOnlyList<string> searchPaths, DescriptorLoader? loader)
    {
        ArgumentNullException.ThrowIfNull(searchPaths);

        return [.. searchPaths, .. loader?.ImplicitIncludePaths ?? []];
    }

    /// <summary>
    /// What may follow <paramref name="directory"/> in an import path: the schemas and the
    /// subdirectories the roots hold there, merged so that the root which would win says so.
    /// </summary>
    /// <param name="directory">
    /// A directory in protobuf's spelling, with or without a trailing slash, and empty for the top
    /// level. A backslash is read as a separator and answered with forward slashes, so a Windows user
    /// who typed one is corrected rather than refused.
    /// </param>
    /// <param name="budget">
    /// The most file system entries this may look at, across every root, before it stops and says so.
    /// </param>
    /// <remarks>
    /// Directories first and then alphabetically, which is a presentation order rather than a
    /// resolution one -- <see cref="SchemaCandidate.Root"/> already carries the resolution. Merging
    /// happens in root order, before the sort, so the first root holding a name is the one that keeps
    /// it however the list is later arranged.
    /// </remarks>
    public static SchemaListing Enumerate(
        string directory,
        IReadOnlyList<string> roots,
        int budget = MostEntriesExamined)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(roots);

        if (!TryDescend(directory, out var prefix))
        {
            return SchemaListing.Nothing;
        }

        var found = new List<SchemaCandidate>();
        var seen = new Dictionary<string, int>(PathIdentity.Comparer);
        var examined = 0;

        foreach (var root in roots)
        {
            foreach (var entry in Listing(Path.Combine(root, prefix)))
            {
                // Counted per entry rather than per candidate, because the cost is in walking a
                // directory and not in what walking it turned up. A root holding fifty thousand
                // images and one schema is exactly the case this is here for.
                if (++examined > budget)
                {
                    return Sorted(found, sawEverything: false);
                }

                if (Candidate(entry, prefix, root) is { } candidate)
                {
                    Merge(found, seen, candidate);
                }
            }
        }

        return Sorted(found, sawEverything: true);
    }

    /// <summary>
    /// The importable schema a path that resolved to nothing came closest to naming, or null when
    /// nothing there is close enough to be worth suggesting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Looked for beside the file the author was aiming at rather than everywhere: a path that names
    /// a directory correctly and misspells the file is the mistake this catches, and it is the common
    /// one. The case it deliberately misses is a file named right in a directory named wrong, which
    /// needs a walk of the whole tree -- and the depth and file count that walk would have to be
    /// capped at are numbers this issue is not the place to pin.
    /// </para>
    /// <para>
    /// Compared without regard to case, which is worth being deliberate about because it is the one
    /// thing that reaches the suggestion on one platform and cannot on another. Where the file system
    /// folds case the import would already have resolved and there is nothing to suggest; where it
    /// does not, a schema differing only in case is precisely what the author meant and is invisible
    /// to them in every other diagnostic they will see.
    /// </para>
    /// <para>
    /// <b>A directory too broad to be read within the budget suggests nothing at all.</b> This runs
    /// on a compiler's error path, on a machine whose include roots may be a network mount or a
    /// vendored tree with tens of thousands of files in it, and a diagnostic that takes seconds to
    /// print is a worse failure than one that says less. Silence rather than a best effort, because a
    /// partial walk cannot know that what it did not reach was further away than what it did: it
    /// would name the nearest of an arbitrary prefix of the directory and present it as the nearest
    /// of the whole. <paramref name="budget"/> is a guard rather than a measured figure, and #57 pins
    /// it for this and for completion together, since both walk through here.
    /// </para>
    /// </remarks>
    /// <param name="budget"><inheritdoc cref="Enumerate" path="/param[@name='budget']"/></param>
    public static string? NearestTo(
        string relativePath,
        IReadOnlyList<string> roots,
        int budget = MostEntriesExamined)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(roots);

        var written = relativePath.Replace('\\', '/');
        var cut = written.LastIndexOf('/');
        var name = written[(cut + 1)..];

        if (name.Length == 0 || name.Length > LongestComparable)
        {
            return null;
        }

        var listing = Enumerate(written[..(cut + 1)], roots, budget);
        if (!listing.SawEverything)
        {
            return null;
        }

        var allowed = Math.Max(1, name.Length / 3);

        string? best = null;
        var nearest = int.MaxValue;

        foreach (var candidate in listing.Candidates)
        {
            // The candidate's own last segment rather than a slice at the written path's separator:
            // the two agree only when the author spelled the directory exactly as Enumerate
            // normalized it, and a leading or doubled slash is enough to make them disagree.
            var offered = candidate.Path[(candidate.Path.LastIndexOf('/') + 1)..];

            if (candidate.IsDirectory || string.Equals(offered, name, StringComparison.Ordinal))
            {
                continue;
            }

            var distance = Distance(name, offered);

            // Strictly nearer, so that an exact tie keeps the earlier candidate and the answer follows
            // the order Enumerate settled rather than the order the file system happened to list in.
            if (distance <= allowed && distance < nearest)
            {
                best = candidate.Path;
                nearest = distance;
            }
        }

        return best;
    }

    /// <summary>The longest name worth measuring an edit distance against.</summary>
    /// <remarks>
    /// A guard on the quadratic below, not a judgement about path lengths. Nothing a person types as
    /// a schema name approaches it, and what does is a buffer holding something other than what its
    /// author thinks -- which must still not turn one failed import into a measurable pause.
    /// </remarks>
    private const int LongestComparable = 256;

    /// <summary>
    /// Turns a written prefix into a root-relative directory, or refuses one that could not name a
    /// place under a root at all.
    /// </summary>
    /// <remarks>
    /// The refusals are the point. A rooted path and a <c>..</c> segment both address a location the
    /// include roots do not contain, which protoc would not resolve either -- and combining one with a
    /// root here would enumerate somewhere outside the workspace and offer the user its contents.
    /// </remarks>
    private static bool TryDescend(string directory, out string prefix)
    {
        prefix = directory.Replace('\\', '/').Trim('/');

        if (prefix.Length == 0)
        {
            return true;
        }

        if (Path.IsPathRooted(prefix) || prefix.Split('/').Contains(".."))
        {
            prefix = string.Empty;
            return false;
        }

        prefix += "/";

        return true;
    }

    /// <summary>What one directory holds, lazily, and nothing at all when it cannot be read.</summary>
    /// <remarks>
    /// <para>
    /// Lazy on purpose. The array-returning form reads the whole directory into memory before the
    /// caller sees an entry, so a budget written around it would be a budget on nothing: the work and
    /// the allocation have already happened by the time the first entry can be counted. Enumerating
    /// lets the count stop the walk where it stands.
    /// </para>
    /// <para>
    /// The enumerator is driven by hand because a <c>try</c> cannot hold a <c>yield return</c>, and
    /// the failures here arrive on <c>MoveNext</c> rather than on the call: a directory that is not
    /// there, one this process may not read, one whose path this platform will not accept. Each
    /// contributes nothing and lets the remaining roots answer, since a wrong include path is the
    /// ordinary case this feature exists to make visible.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Listing(string directory)
    {
        IEnumerator<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            yield break;
        }

        using (entries)
        {
            while (true)
            {
                try
                {
                    if (!entries.MoveNext())
                    {
                        yield break;
                    }
                }
                catch (Exception ex) when (Unreadable(ex))
                {
                    yield break;
                }

                yield return entries.Current;
            }
        }
    }

    private static bool Unreadable(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>What one entry is, or null when it is nothing an import could name.</summary>
    /// <remarks>
    /// The extension is matched without regard to case, everywhere. An extension is not a name: a
    /// file called 'Invoice.PROTO' on a case-sensitive volume is one protoc resolves perfectly well
    /// when it is imported by that spelling, which is the spelling this offers.
    /// </remarks>
    private static SchemaCandidate? Candidate(string entry, string prefix, string root)
    {
        var name = Path.GetFileName(entry);
        if (name.Length == 0)
        {
            return null;
        }

        if (Directory.Exists(entry))
        {
            return new SchemaCandidate(prefix + name + "/", IsDirectory: true, root);
        }

        return name.EndsWith(".proto", StringComparison.OrdinalIgnoreCase)
            ? new SchemaCandidate(prefix + name, IsDirectory: false, root)
            : null;
    }

    private static SchemaListing Sorted(List<SchemaCandidate> found, bool sawEverything)
    {
        found.Sort(Presentation);

        return new SchemaListing(found, sawEverything);
    }

    /// <summary>
    /// Files a candidate under its path, or records that a root behind the winner holds it too.
    /// </summary>
    private static void Merge(List<SchemaCandidate> found, Dictionary<string, int> seen, SchemaCandidate candidate)
    {
        if (seen.TryGetValue(candidate.Path, out var index))
        {
            var winner = found[index];
            found[index] = winner with { ShadowedRoots = [.. winner.ShadowedRoots, candidate.Root] };

            return;
        }

        seen[candidate.Path] = found.Count;
        found.Add(candidate);
    }

    private static int Presentation(SchemaCandidate left, SchemaCandidate right)
        => left.IsDirectory == right.IsDirectory
            ? string.CompareOrdinal(left.Path, right.Path)
            : left.IsDirectory ? -1 : 1;

    /// <summary>How many single-character edits separate two names.</summary>
    /// <remarks>
    /// Two rows rather than a full matrix, which is the ordinary form of this and matters here only
    /// because it runs once per root per failed import rather than once per compilation.
    /// </remarks>
    private static int Distance(string written, string actual)
    {
        if (actual.Length > LongestComparable)
        {
            return int.MaxValue;
        }

        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];

        for (var column = 0; column <= actual.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= written.Length; row++)
        {
            current[0] = row;

            for (var column = 1; column <= actual.Length; column++)
            {
                var same = char.ToUpperInvariant(written[row - 1]) == char.ToUpperInvariant(actual[column - 1]);

                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (same ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[actual.Length];
    }
}
