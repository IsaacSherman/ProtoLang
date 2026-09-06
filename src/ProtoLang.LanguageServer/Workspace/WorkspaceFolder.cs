namespace ProtoLang.LanguageServer.Workspace;

/// <summary>
/// One folder the editor has open, and the settings written for it.
/// </summary>
/// <remarks>
/// <para>
/// A multi-root workspace has several of these, they may nest, and each may state its own include
/// paths and its own <c>protolang.config.xml</c>. Settings live on the folder rather than in a map
/// beside the folder list, so that closing a folder takes its settings with it -- a parallel map is
/// how a server comes to resolve a document against settings for a folder that is no longer open.
/// </para>
/// <para>
/// A folder is always a real directory. An editor can open a virtual workspace over a scheme with no
/// file system behind it, and this model does not describe one: the compiler reads <c>.proto</c> files
/// from disk, so a folder it cannot walk resolves nothing. Such a folder is refused at construction
/// rather than carried as a half-usable value.
/// </para>
/// </remarks>
public sealed record WorkspaceFolder
{
    /// <exception cref="ArgumentException"><paramref name="uri"/> does not name a directory on disk.</exception>
    public WorkspaceFolder(DocumentUri uri, string? name = null, ProtoLangSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Path is not { } path)
        {
            throw new ArgumentException(
                $"'{uri}' names no directory on disk, so it cannot be a workspace folder.",
                nameof(uri));
        }

        Uri = uri;
        Path = path;
        Name = name is { Length: > 0 } ? name : DirectoryName(path);
        Settings = settings ?? ProtoLangSettings.None;
        Key = PathIdentity.KeyFor(path);
    }

    /// <inheritdoc cref="WorkspaceFolder(DocumentUri, string?, ProtoLangSettings?)"/>
    public static WorkspaceFolder FromPath(string path, string? name = null, ProtoLangSettings? settings = null)
        => new(DocumentUri.FromPath(path), name, settings);

    /// <summary>How the editor names this folder.</summary>
    public DocumentUri Uri { get; }

    /// <summary>The directory on disk, with no trailing separator.</summary>
    /// <remarks>
    /// Also what a relative include path written at folder scope resolves against, which is the whole
    /// reason a folder carries a path rather than only a URI.
    /// </remarks>
    public string Path { get; }

    /// <summary>The name the editor shows for it.</summary>
    public string Name { get; }

    /// <summary>The settings written at this folder's scope.</summary>
    public ProtoLangSettings Settings { get; init; }

    /// <summary>What makes two folders the same folder. Compare it ordinally.</summary>
    public string Key { get; }

    /// <summary>Whether <paramref name="document"/> lives in this folder or below it.</summary>
    /// <remarks>
    /// A prefix match on the identity key, and the boundary check is the point of writing it out: a
    /// document under <c>C:\src\appx</c> is not in the folder <c>C:\src\app</c>, however much the two
    /// strings look alike. A root -- <c>C:\</c>, or <c>/</c> -- already ends in its separator, so it
    /// contains everything below it without a separator being inserted.
    /// </remarks>
    public bool Contains(DocumentUri document)
    {
        if (document?.Path is not { } path)
        {
            return false;
        }

        var key = PathIdentity.KeyFor(path);

        if (key.Length == Key.Length)
        {
            return string.Equals(key, Key, StringComparison.Ordinal);
        }

        return key.Length > Key.Length
            && key.StartsWith(Key, StringComparison.Ordinal)
            && (IsSeparator(Key[^1]) || IsSeparator(key[Key.Length]));
    }

    private static bool IsSeparator(char character)
        => character == System.IO.Path.DirectorySeparatorChar
            || character == System.IO.Path.AltDirectorySeparatorChar;

    /// <remarks>
    /// A root has no last segment to name it by, so it goes by its own spelling: <c>C:\</c> reads
    /// better in a settings list than an empty label does.
    /// </remarks>
    private static string DirectoryName(string path)
        => System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
}
