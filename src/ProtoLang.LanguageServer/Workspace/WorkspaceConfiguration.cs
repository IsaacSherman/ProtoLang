using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;

namespace ProtoLang.LanguageServer.Workspace;

/// <summary>
/// Everything the editor has told the server about how to compile, and the one place that turns it
/// into an answer for a document.
/// </summary>
/// <remarks>
/// <para>
/// ProtoLang configuration already had three independent sources before an editor was involved --
/// command-line arguments, the <c>PROTOLANG_PROTOC</c> environment variable, and a
/// <c>protolang.config.xml</c> discovered by walking upward from the source file. An editor adds a
/// fourth axis, the workspace, which the command line never had: settings written at user, workspace,
/// and folder scope, over a workspace that may hold several folders at once. Left to each client,
/// that becomes two dialects of a settings model and a server receiving both. It is settled here, in
/// the server, once.
/// </para>
/// <para>
/// <b>The precedence order</b>, most specific first, for the two settings that are not language
/// policy:
/// </para>
/// <list type="number">
/// <item>an editor setting written for the workspace folder holding the document;</item>
/// <item>an editor setting written for the workspace;</item>
/// <item>an editor setting written at user scope;</item>
/// <item>the <c>PROTOLANG_PROTOC</c> environment variable, for protoc only;</item>
/// <item>discovery -- <c>PATH</c>, then the NuGet package cache -- again for protoc only.</item>
/// </list>
/// <para>
/// A setting beats the environment because a setting is the project's answer and the environment is
/// the machine's, and because the setting is the one the user can see and edit in front of them. The
/// order is <see cref="ConfigurationSource"/>'s own declaration order, read back as data, so a report
/// quoting it cannot disagree with the resolver walking it.
/// </para>
/// <para>
/// <b>Language policy is not on that list.</b> Spec 10.4 settles it in <c>protolang.config.xml</c>
/// and says the file wins; an editor may point at a different file
/// (<see cref="ProtoLangSettings.ConfigPathKey"/>) and may not restate what is in one. A setting that
/// tries is reported rather than ignored in silence -- see <see cref="ProtoLangSettings"/>.
/// </para>
/// <para>
/// <b>Every setting takes effect on the next compilation, and none requires a restart.</b> That falls
/// out of the shape rather than being maintained: this object is immutable, a change produces a new
/// one with a higher <see cref="Generation"/>, and nothing is resolved until a document asks. A
/// changed protoc is not even a special case -- #48 keys the descriptor cache on which protoc ran, so
/// entries loaded under the old one are simply never matched again.
/// </para>
/// <para>
/// Resolution is recomputed per request rather than cached per document. The expensive part of it is
/// reading a <c>protolang.config.xml</c>, which is one small file; caching that would need its own
/// invalidation on a file write, which is a second cache with a second way to serve a stale answer.
/// #57 measures whether this needs revisiting.
/// </para>
/// </remarks>
public sealed record WorkspaceConfiguration
{
    /// <summary>A server that has been told nothing yet.</summary>
    public static WorkspaceConfiguration Empty { get; } = new();

    /// <summary>
    /// How many times this configuration has been changed. Stamped onto everything it resolves.
    /// </summary>
    /// <inheritdoc cref="DocumentConfiguration"/>
    public int Generation { get; init; }

    /// <summary>The folders the editor has open, each with the settings written for it.</summary>
    public IReadOnlyList<WorkspaceFolder> Folders { get; init; } = [];

    /// <summary>Settings written at user scope, applying to every workspace.</summary>
    public ProtoLangSettings User { get; init; } = ProtoLangSettings.None;

    /// <summary>Settings written for this workspace.</summary>
    public ProtoLangSettings Workspace { get; init; } = ProtoLangSettings.None;

    /// <summary>
    /// What a relative path written at workspace scope resolves against: the directory holding the
    /// workspace file.
    /// </summary>
    /// <remarks>
    /// Null in a workspace that has no file of its own, which is the ordinary single-folder case. That
    /// folder is then the base, because it is where the settings were written -- a
    /// <c>.vscode/settings.json</c> inside one open folder is a workspace-scope setting whose relative
    /// paths obviously mean "under this folder", and refusing them on a technicality would refuse the
    /// most common arrangement there is. With several folders open and no workspace file there is no
    /// such answer, and a relative path is reported instead of guessed at.
    /// </remarks>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>Reads an environment variable. Replaceable so a test does not have to set one.</summary>
    public Func<string, string?> ReadEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The same configuration with a different set of open folders.</summary>
    public WorkspaceConfiguration WithFolders(IEnumerable<WorkspaceFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return this with { Folders = [.. folders], Generation = Generation + 1 };
    }

    /// <summary>The same configuration with different user-scope settings.</summary>
    public WorkspaceConfiguration WithUserSettings(ProtoLangSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return this with { User = settings, Generation = Generation + 1 };
    }

    /// <summary>The same configuration with different workspace-scope settings.</summary>
    public WorkspaceConfiguration WithWorkspaceSettings(ProtoLangSettings settings, string? workspaceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return this with
        {
            Workspace = settings,
            WorkspaceDirectory = workspaceDirectory ?? WorkspaceDirectory,
            Generation = Generation + 1,
        };
    }

    /// <summary>
    /// The folder <paramref name="document"/> belongs to, or null when it belongs to none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The innermost folder wins where folders nest, which they do: opening a repository and a
    /// subdirectory of it as two roots is a normal thing to do, and the nearer one is the one whose
    /// settings were written about this file.
    /// </para>
    /// <para>
    /// A document with no path -- an untitled buffer -- belongs to the only folder if there is exactly
    /// one, and to none otherwise. The alternative would be to follow whichever folder the editor
    /// calls active, which LSP does not report and which would make the same buffer compile
    /// differently depending on what the user last clicked. One folder is unambiguous; several is a
    /// question with no answer, and it resolves against workspace and user scope alone.
    /// </para>
    /// </remarks>
    public WorkspaceFolder? FolderFor(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.IsFile)
        {
            return Folders.Count == 1 ? Folders[0] : null;
        }

        WorkspaceFolder? innermost = null;

        foreach (var folder in Folders)
        {
            if (folder.Contains(document) && (innermost is null || folder.Key.Length > innermost.Key.Length))
            {
                innermost = folder;
            }
        }

        return innermost;
    }

    /// <summary>Settles every value for one document, and says where each of them came from.</summary>
    /// <inheritdoc cref="WorkspaceConfiguration"/>
    public DocumentConfiguration Resolve(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new DiagnosticBag();
        var folder = FolderFor(document);
        var scopes = ScopesFor(folder);

        // Every resolution runs before the bag is copied, and each is a statement rather than a
        // member of the initializer below: an initializer that reported into a bag it was also
        // copying would depend on the order its members happen to be written in, and a later hand
        // sorting them would drop diagnostics with nothing to say so.
        var (protoc, protocSource) = ResolveProtoc(scopes, diagnostics);
        var includePaths = ResolveIncludePaths(scopes, diagnostics);
        var (config, configSource) = ResolveConfig(document, folder, scopes, diagnostics);

        return new DocumentConfiguration(document, Generation)
        {
            Folder = folder,
            ProtocPath = protoc,
            ProtocPathSource = protocSource,
            IncludePaths = includePaths,
            Config = config,
            ConfigSource = configSource,
            Diagnostics = [.. diagnostics],
        };
    }

    /// <summary>One place a setting can be written, and what a relative path there means.</summary>
    private sealed record Scope(ConfigurationSource Source, ProtoLangSettings Settings, string? BaseDirectory);

    /// <summary>The scopes that apply to a document, most specific first.</summary>
    /// <remarks>
    /// A document outside every folder, and an untitled buffer in a multi-root workspace, simply have
    /// no folder scope -- the list is shorter and nothing else about resolution changes. That is why
    /// the awkward cases the issue names need no branches of their own further down.
    /// </remarks>
    private List<Scope> ScopesFor(WorkspaceFolder? folder)
    {
        var scopes = new List<Scope>();

        if (folder is not null)
        {
            scopes.Add(new Scope(ConfigurationSource.FolderSetting, folder.Settings, folder.Path));
        }

        scopes.Add(new Scope(ConfigurationSource.WorkspaceSetting, Workspace, WorkspaceBaseDirectory));
        scopes.Add(new Scope(ConfigurationSource.UserSetting, User, null));

        return scopes;
    }

    /// <inheritdoc cref="WorkspaceDirectory"/>
    private string? WorkspaceBaseDirectory
        => WorkspaceDirectory ?? (Folders.Count == 1 ? Folders[0].Path : null);

    /// <remarks>
    /// A scope naming a protoc that cannot be found is reported and passed over, rather than being
    /// used and failing later: falling through to the next source is the behavior a user gets today
    /// from the environment variable, and the warning is what makes it visible instead of mysterious.
    /// </remarks>
    private (string? Path, ConfigurationSource Source) ResolveProtoc(List<Scope> scopes, DiagnosticBag diagnostics)
    {
        foreach (var scope in scopes)
        {
            if (scope.Settings.ProtocPath is not { } stated)
            {
                continue;
            }

            if (TryUseProtoc(stated, scope, ProtoLangSettings.ProtocPathKey, diagnostics, out var fromSetting))
            {
                return (fromSetting, scope.Source);
            }
        }

        var variable = ProtocLocator.OverrideEnvironmentVariable;
        var fromEnvironment = ReadEnvironmentVariable(variable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var environment = new Scope(ConfigurationSource.Environment, ProtoLangSettings.None, null);

            if (TryUseProtoc(fromEnvironment, environment, variable, diagnostics, out var located))
            {
                return (located, ConfigurationSource.Environment);
            }
        }

        return (null, ConfigurationSource.Discovery);
    }

    /// <remarks>
    /// A bare tool name is left alone: <c>protoc</c> means "whichever one is on <c>PATH</c>", and
    /// resolving it against a workspace folder would look for it in a directory nobody expected it to
    /// be in. <see cref="ProtocLocator.Resolve"/> settles the rest -- it is the one place that decides
    /// which executable a string will actually run -- and existence is checked against its answer
    /// rather than against the string, so a name found on <c>PATH</c> counts as found.
    /// </remarks>
    private static bool TryUseProtoc(
        string stated,
        Scope scope,
        string origin,
        DiagnosticBag diagnostics,
        out string? protoc)
    {
        protoc = null;

        var candidate = stated;

        if (PathIdentity.NamesALocation(stated) && !TryResolvePath(stated, scope, origin, diagnostics, out candidate))
        {
            return false;
        }

        var resolved = ProtocLocator.Resolve(candidate);

        if (File.Exists(resolved))
        {
            protoc = resolved;
            return true;
        }

        diagnostics.Warning(
            "PL2105",
            "protoc not found where it was named",
            $"'{stated}' does not name a protoc that exists, so {origin} is being ignored.",
            Span(scope),
            "Give the full path to a protoc executable, or remove the setting and let the compiler "
                + "search PATH and the NuGet package cache.");

        return false;
    }

    private static List<ResolvedIncludePath> ResolveIncludePaths(List<Scope> scopes, DiagnosticBag diagnostics)
    {
        var resolved = new List<ResolvedIncludePath>();

        foreach (var scope in scopes)
        {
            foreach (var stated in scope.Settings.IncludePaths)
            {
                if (!TryResolvePath(stated, scope, ProtoLangSettings.IncludePathsKey, diagnostics, out var full))
                {
                    continue;
                }

                // The same directory named at two scopes is one place to look, and searching it twice
                // would put it ahead of nothing it was not already ahead of. The first spelling stays,
                // so the entry keeps the scope that had priority.
                if (!resolved.Exists(include => PathIdentity.AreSame(include.Path, full)))
                {
                    resolved.Add(new ResolvedIncludePath(full, stated, scope.Source));
                }
            }
        }

        return resolved;
    }

    /// <remarks>
    /// A configuration file named by a setting and then not found is a warning and a fall-through, not
    /// a stop. The command line refuses that outright, because a build whose policy file is missing
    /// must not quietly produce different code; an editor answering questions about a buffer has the
    /// opposite obligation, and going dark over a stale path in a settings file would take away the
    /// diagnostics the user is trying to read. A file that exists and cannot be <em>read</em> still
    /// stops everything, exactly as it does on the command line -- that one is a project stating a
    /// policy and being ignored.
    /// </remarks>
    private (ProjectConfig? Config, ConfigurationSource Source) ResolveConfig(
        DocumentUri document,
        WorkspaceFolder? folder,
        List<Scope> scopes,
        DiagnosticBag diagnostics)
    {
        foreach (var scope in scopes)
        {
            if (scope.Settings.ConfigPath is not { } stated)
            {
                continue;
            }

            if (!TryResolvePath(stated, scope, ProtoLangSettings.ConfigPathKey, diagnostics, out var path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                diagnostics.Warning(
                    "PL2104",
                    "configuration file not found",
                    $"'{stated}' does not name a file that exists, so {ProtoLangSettings.ConfigPathKey} "
                        + "is being ignored.",
                    Span(scope),
                    $"Correct the path, or remove the setting and let {ProjectConfig.FileName} be "
                        + "searched for in the document's directory and every directory above it.");
                continue;
            }

            return (ProjectConfig.Load(path, diagnostics), ConfigurationSource.ConfigFile);
        }

        // The document's own directory, falling back to its folder's, which is what an untitled buffer
        // inside an open project has instead. Compilation.ResolveConfig is the same walk the command
        // line does, called rather than reproduced.
        var discovered = Compilation.ResolveConfig(document.Directory ?? folder?.Path, diagnostics);

        return (discovered, discovered?.Path is null ? ConfigurationSource.Default : ConfigurationSource.ConfigFile);
    }

    /// <summary>
    /// Makes one written path absolute against the scope that wrote it, or says why it cannot.
    /// </summary>
    /// <remarks>
    /// A relative path resolves against the scope that supplied it: a folder-scope setting against
    /// that folder, a workspace-scope setting against the workspace, and a user-scope setting against
    /// nothing at all -- a setting that applies to every workspace on the machine has no one directory
    /// it could mean. The alternative, resolving everything against whichever folder the document
    /// happens to be in, would give a single user-scope setting a different meaning in every project,
    /// which is a setting nobody could reason about.
    /// </remarks>
    private static bool TryResolvePath(
        string stated,
        Scope scope,
        string origin,
        DiagnosticBag diagnostics,
        out string full)
    {
        full = string.Empty;

        var combined = stated;

        if (!Path.IsPathRooted(stated))
        {
            if (scope.BaseDirectory is not { } baseDirectory)
            {
                diagnostics.Warning(
                    "PL2103",
                    "relative path has nothing to resolve against",
                    $"'{stated}' in {origin} is relative, and {scope.Source.Describe()} has no directory "
                        + "to resolve it against, so it is being ignored.",
                    Span(scope),
                    "Write an absolute path, or move the setting to a workspace or folder scope, which "
                        + "have a directory of their own.");
                return false;
            }

            combined = Path.Combine(baseDirectory, stated);
        }

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
            return true;
        }
        catch (Exception ex)
            when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            diagnostics.Warning(
                "PL2103",
                "path could not be used",
                $"'{stated}' in {origin} could not be resolved to a path: {ex.Message} It is being ignored.",
                Span(scope));
            return false;
        }
    }

    /// <remarks>
    /// Where a compiler diagnostic carries a file and a position, one about a setting carries the name
    /// of the place the setting was written and no position at all. There is no line to point at: a
    /// client sends settings as values, not as the text of the file it read them from.
    /// </remarks>
    private static SourceSpan Span(Scope scope)
        => new(scope.Source.Label(), SourcePosition.None, SourcePosition.None);
}
