namespace ProtoLang.LanguageServer.Workspace;

/// <summary>
/// A document as an editor names it, and as the compiler names it: one object holding both, so the
/// conversion happens once.
/// </summary>
/// <remarks>
/// <para>
/// Editors speak URIs and the compiler speaks paths, and every place that converts between them is a
/// place two spellings of one file can become two files. The failure is not theoretical: VS Code
/// sends <c>file:///c%3A/src/x.protolang</c> where Visual Studio sends <c>file:///C:/src/x.protolang</c>,
/// a client that has round-tripped a path through its own settings may send either drive-letter case,
/// and a folder arrives with a trailing slash about half the time. Each of those, converted
/// separately at four call sites, produces a document the server has diagnostics for and cannot find
/// again.
/// </para>
/// <para>
/// So the conversion is here and nowhere else. <see cref="Text"/> is what goes back to the client --
/// exactly what it sent, because a URI it does not recognize routes nothing -- and <see cref="Path"/>
/// is what goes to the compiler. Identity is neither: it is <see cref="PathIdentity"/>'s key over the
/// path, so the four spellings above are one document however they arrived.
/// </para>
/// <para>
/// A scheme other than <c>file</c> is kept whole rather than rejected. <c>untitled:Untitled-1</c> is a
/// real buffer an editor will ask for diagnostics on, it has no path, and its identity is its text --
/// which is why <see cref="Path"/> is nullable rather than the type refusing to exist without one.
/// That nullability is the same shape <see cref="SourceIdentity"/> already uses for a buffer that has
/// never been saved, and for the same reason.
/// </para>
/// </remarks>
public sealed class DocumentUri : IEquatable<DocumentUri>
{
    /// <summary>The scheme of a document that is a file on disk.</summary>
    public const string FileScheme = "file";

    private DocumentUri(string text, string scheme, string? path)
    {
        Text = text;
        Scheme = scheme;
        Path = path;
        Key = scheme + "\n" + (path is null ? text : PathIdentity.KeyFor(path));
    }

    /// <summary>The URI as the client wrote it, which is what a response must echo back.</summary>
    public string Text { get; }

    /// <summary>The URI scheme, lowercased by the parser: <c>file</c>, <c>untitled</c>, or another.</summary>
    public string Scheme { get; }

    /// <summary>
    /// The absolute path of the file, with no trailing separator, or null when this document is not a
    /// file on disk.
    /// </summary>
    /// <remarks>
    /// Null covers two cases a caller treats the same way and should not have to tell apart: a scheme
    /// that names no file, and a <c>file:</c> URI whose path the operating system will not accept.
    /// Both mean there is nothing here to open, to search from, or to discover a configuration file
    /// above.
    /// </remarks>
    public string? Path { get; }

    /// <summary>The directory holding this document, or null when it has no path.</summary>
    public string? Directory
        => Path is null ? null : System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } d ? d : null;

    /// <summary>Whether this document is a file on disk.</summary>
    public bool IsFile => Path is not null;

    /// <summary>
    /// What makes two of these the same document. Compare it ordinally; never show it to anyone.
    /// </summary>
    public string Key { get; }

    /// <summary>Reads a URI, or a fully qualified path, as a document.</summary>
    /// <remarks>
    /// A path is accepted as well as a URI because a Windows path parses as an absolute URI --
    /// <c>C:\src\x.protolang</c> has scheme <c>file</c> as far as <see cref="Uri"/> is concerned -- so
    /// the choice is between handling it deliberately and handling it by accident. A path that arrives
    /// here comes back out as the <c>file:</c> URI for it, so a client is never handed a path where it
    /// expected a URI.
    /// </remarks>
    public static bool TryParse(string? text, out DocumentUri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (System.IO.Path.IsPathFullyQualified(text))
        {
            // Not FromPath directly. That one throws on a path the platform will not accept -- an
            // embedded NUL from a mangled decode, a length past what a URI can hold -- and a Try
            // method that throws is one every caller has to guard anyway. The server may not fail a
            // request over a string a client made up.
            return TryFromPath(text, out uri);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        uri = new DocumentUri(text, parsed.Scheme, parsed.IsFile ? FullPathOrNull(parsed.LocalPath) : null);
        return true;
    }

    /// <inheritdoc cref="TryParse"/>
    /// <exception cref="ArgumentException">The text is neither a usable URI nor a usable path.</exception>
    public static DocumentUri Parse(string text)
        => TryParse(text, out var uri)
            ? uri
            : throw new ArgumentException($"'{text}' is neither a usable URI nor a usable path.", nameof(text));

    /// <summary>Names the file at <paramref name="path"/>.</summary>
    /// <exception cref="ArgumentException">The platform will not accept the path.</exception>
    public static DocumentUri FromPath(string path)
        => TryFromPath(path, out var uri)
            ? uri
            : throw new ArgumentException($"'{path}' is not a path this platform accepts.", nameof(path));

    /// <inheritdoc cref="FromPath"/>
    private static bool TryFromPath(string? path, out DocumentUri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            uri = new DocumentUri(new Uri(full).AbsoluteUri, FileScheme, full);
            return true;
        }
        catch (Exception ex)
            when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException
                or UriFormatException)
        {
            return false;
        }
    }

    public bool Equals(DocumentUri? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as DocumentUri);

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Text;

    /// <remarks>
    /// A <c>file:</c> URI can carry a path this platform will not accept -- a Windows client naming a
    /// POSIX path, a name holding a character the file system reserves. There is nothing to open and
    /// nothing to report against, so the document keeps its text and admits it has no path, which is
    /// the same answer an unsaved buffer gives and needs no second branch anywhere downstream.
    /// </remarks>
    private static string? FullPathOrNull(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(WithoutRootBeforeADriveLetter(localPath)));
        }
        catch (Exception ex)
            when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes the one place <see cref="Uri.LocalPath"/> gets a Windows path wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client that percent-encodes the drive colon -- <c>file:///c%3A/src/x.protolang</c>, which is
    /// what VS Code sends -- gives <see cref="Uri"/> nothing that looks like a drive, so it hands back
    /// <c>/c:/src/x.protolang</c> with the URI's own root slash still attached.
    /// <see cref="Path.GetFullPath(string)"/> then reads that as rooted on the current drive and
    /// produces <c>C:\c:\src\x.protolang</c>: a path that exists nowhere, that no file watcher will
    /// ever match, and that differs from the same file opened through the unencoded spelling.
    /// </para>
    /// <para>
    /// Windows only. On a POSIX file system <c>/c:/src</c> is a perfectly ordinary path to a directory
    /// named <c>c:</c>, and stripping its root would name something else entirely.
    /// </para>
    /// </remarks>
    private static string WithoutRootBeforeADriveLetter(string localPath)
        => OperatingSystem.IsWindows()
            && localPath.Length >= 3
            && localPath[0] is '/' or '\\'
            && char.IsAsciiLetter(localPath[1])
            && localPath[2] == ':'
                ? localPath[1..]
                : localPath;
}
