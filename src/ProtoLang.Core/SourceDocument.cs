namespace ProtoLang;

/// <summary>
/// What a piece of ProtoLang source calls itself: the label its diagnostics carry, and the
/// directory it belongs to, if it belongs to one.
/// </summary>
/// <remarks>
/// <para>
/// Everything the compiler used to take from a source path comes from here instead: the
/// <see cref="Diagnostics.SourceSpan.File"/> stamped on every token, the directory
/// <c>protolang.config.xml</c> discovery walks up from, and the directory appended to the import
/// search path behind the caller's include directories. One type answers all three, so that a
/// buffer an editor holds -- which has text, and may have no file at all -- is describable without
/// a second, parallel notion of "where this came from" growing up beside the first and drifting
/// away from it.
/// </para>
/// <para>
/// Path arithmetic happens once, here. <see cref="System.IO.Path.GetFullPath(string)"/> throws on
/// an empty string, so a compiler that reached for a source's directory in three separate places
/// had three separate ways to crash on a buffer that has no path. A pathless identity carries
/// <see cref="Path"/> and <see cref="Directory"/> as null, and every consumer asks one question.
/// </para>
/// </remarks>
public sealed record SourceIdentity
{
    /// <summary>The label an unsaved buffer takes when its caller does not name one.</summary>
    /// <remarks>
    /// Angle brackets, matching <see cref="Diagnostics.SourceSpan.None"/>'s <c>&lt;none&gt;</c>: a
    /// reader who meets <c>&lt;unsaved&gt;:3:7</c> can tell at once that it is the compiler
    /// describing a buffer, not a file they should go looking for and fail to find.
    /// </remarks>
    public const string UnsavedName = "<unsaved>";

    private SourceIdentity(string name, string? path, string? directory)
    {
        Name = name;
        Path = path;
        Directory = directory;
    }

    /// <summary>
    /// What diagnostics print, and nothing more.
    /// </summary>
    /// <remarks>
    /// For a file this is <see cref="System.IO.Path.GetFileName(string)"/> of the path as the caller
    /// wrote it -- deliberately not of its expanded form, which differs for a trailing separator or
    /// a path ending in <c>.</c> or <c>..</c>. That exact string is what the CLI has printed since
    /// the beginning, and normalizing it here would move published output for no gain.
    /// </remarks>
    public string Name { get; }

    /// <summary>The full path of the file, or null when this text has never been saved.</summary>
    public string? Path { get; }

    /// <summary>
    /// The directory that settles policy and anchors the import search path, or null when there is
    /// none to settle it.
    /// </summary>
    /// <remarks>
    /// Null rather than empty, so that one test answers the question everywhere rather than
    /// <c>is null</c> in one place and <c>IsNullOrEmpty</c> in another -- which is what the compiler
    /// did when it derived this value twice.
    /// </remarks>
    public string? Directory { get; }

    /// <summary>Identifies text by the file it is stored in.</summary>
    public static SourceIdentity FromPath(string path)
    {
        // Matches what Path.GetFullPath would throw anyway, just three frames earlier, where the
        // caller can see which argument was wrong.
        ArgumentException.ThrowIfNullOrEmpty(path);

        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full);

        return new SourceIdentity(
            System.IO.Path.GetFileName(path),
            full,
            string.IsNullOrEmpty(directory) ? null : directory);
    }

    /// <summary>Identifies text that has no file yet.</summary>
    /// <param name="name">
    /// What diagnostics should print. An editor should pass whatever it will use to route the
    /// diagnostic back -- an <c>untitled:</c> URI, say -- because this string is the only handle the
    /// compiler will give it back.
    /// </param>
    /// <param name="directory">
    /// Where the buffer will live, when that is known. An unsaved file inside an open project is the
    /// common case, and it should get the project's policy and the project's proto directory rather
    /// than being treated as if it came from nowhere.
    /// </param>
    public static SourceIdentity Unsaved(string name = UnsavedName, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new SourceIdentity(
            name,
            null,
            string.IsNullOrEmpty(directory) ? null : System.IO.Path.GetFullPath(directory));
    }

    /// <summary>The label alone, which is what a span prints.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A unit of ProtoLang source: the text, and what that text calls itself.
/// </summary>
/// <remarks>
/// The compiler is handed one of these rather than a path because the authoritative copy of a file
/// being edited lives in the editor, not on disk; the disk copy is stale between saves and absent
/// before the first one. Text and identity travel together, so there is no arrangement in which the
/// compiler holds one and re-derives the other from somewhere it should not have looked.
/// </remarks>
public sealed record SourceDocument(SourceIdentity Identity, string Text)
{
    /// <summary>Reads the file <paramref name="identity"/> names.</summary>
    /// <remarks>
    /// The only place the compiler reads ProtoLang source from the file system. Text a caller
    /// supplies reaches the pipeline without passing through here, which is what makes "the text
    /// wins over the disk" a property of the shape rather than a rule someone has to remember.
    /// </remarks>
    public static SourceDocument ReadFrom(SourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.Path is not { } path)
        {
            throw new ArgumentException(
                $"'{identity.Name}' has never been saved, so there is no file to read. Build the "
                + "document from the text you already hold.",
                nameof(identity));
        }

        return new SourceDocument(identity, File.ReadAllText(path));
    }

    /// <inheritdoc cref="ReadFrom(SourceIdentity)"/>
    public static SourceDocument ReadFrom(string path) => ReadFrom(SourceIdentity.FromPath(path));
}
