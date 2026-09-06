namespace ProtoLang.LanguageServer.Workspace;

/// <summary>Where a resolved configuration value came from.</summary>
/// <remarks>
/// The list is the precedence order, most specific first, and it is deliberately one list rather than
/// a rule stated in prose beside an enum that happens to agree with it. The failure this answers is a
/// user whose setting is being ignored and who cannot find out why; every value the server resolves
/// carries the member that produced it, so the question is answerable from the running server rather
/// than by reading the code.
/// </remarks>
public enum ConfigurationSource
{
    /// <summary>An editor setting written for one workspace folder.</summary>
    FolderSetting,

    /// <summary>An editor setting written for the workspace.</summary>
    WorkspaceSetting,

    /// <summary>An editor setting written at user scope, applying to every workspace.</summary>
    UserSetting,

    /// <summary>An environment variable, which is the machine's answer rather than the project's.</summary>
    Environment,

    /// <summary>A <c>protolang.config.xml</c>, whether discovered or named by a setting.</summary>
    ConfigFile,

    /// <summary>Nothing stated it, so the compiler will go looking when it needs one.</summary>
    Discovery,

    /// <summary>Nothing stated it and there is nothing to look for; the built-in answer applies.</summary>
    Default,
}

/// <summary>What each <see cref="ConfigurationSource"/> is called, in a diagnostic and to a reader.</summary>
public static class ConfigurationSources
{
    /// <summary>Every source in precedence order, most specific first.</summary>
    /// <remarks>
    /// The enum's own order, read back as data. A resolver that walks this list cannot disagree with a
    /// document that quotes it, which is the whole point of there being one order.
    /// </remarks>
    public static IReadOnlyList<ConfigurationSource> Precedence { get; } = Enum.GetValues<ConfigurationSource>();

    /// <summary>
    /// The name a diagnostic about this source carries where a compiler diagnostic carries a file
    /// name.
    /// </summary>
    /// <remarks>
    /// Angle brackets, matching <see cref="SourceIdentity.UnsavedName"/> and
    /// <see cref="Diagnostics.SourceSpan.None"/>: a reader who meets <c>&lt;workspace settings&gt;</c>
    /// can tell at once that it is a place in the editor's configuration rather than a file they
    /// should go and open.
    /// </remarks>
    public static string Label(this ConfigurationSource source) => source switch
    {
        ConfigurationSource.FolderSetting => "<folder settings>",
        ConfigurationSource.WorkspaceSetting => "<workspace settings>",
        ConfigurationSource.UserSetting => "<user settings>",
        ConfigurationSource.Environment => "<environment>",
        ConfigurationSource.ConfigFile => Config.ProjectConfig.FileName,
        ConfigurationSource.Discovery => "<discovery>",
        ConfigurationSource.Default => "<defaults>",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unhandled configuration source."),
    };

    /// <summary>How this source reads in a sentence, for the resolved-configuration report.</summary>
    public static string Describe(this ConfigurationSource source) => source switch
    {
        ConfigurationSource.FolderSetting => "an editor setting for this workspace folder",
        ConfigurationSource.WorkspaceSetting => "an editor setting for this workspace",
        ConfigurationSource.UserSetting => "an editor setting at user scope",
        ConfigurationSource.Environment => $"the {Binding.ProtocLocator.OverrideEnvironmentVariable} environment variable",
        ConfigurationSource.ConfigFile => Config.ProjectConfig.FileName,
        ConfigurationSource.Discovery => "not stated; located when a compilation needs it",
        ConfigurationSource.Default => "the built-in default",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unhandled configuration source."),
    };

    /// <summary>Whether this source is an editor setting, as opposed to a file or the environment.</summary>
    public static bool IsEditorSetting(this ConfigurationSource source)
        => source is ConfigurationSource.FolderSetting
            or ConfigurationSource.WorkspaceSetting
            or ConfigurationSource.UserSetting;
}
