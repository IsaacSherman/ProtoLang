using ProtoLang.Config;
using ProtoLang.Diagnostics;

namespace ProtoLang.LanguageServer.Workspace;

/// <summary>One setting as a client sent it: a key, and the one or many strings under it.</summary>
/// <remarks>
/// Strings rather than JSON, because the model must not depend on how a client serializes its
/// settings. #42 owns the protocol and the deserializer; what reaches here is the flattened result,
/// which is also what a test can write by hand without building a document object model to describe
/// two directories.
/// </remarks>
public sealed record SettingValue(string Key, IReadOnlyList<string> Values)
{
    /// <summary>A setting with one value, which is what all but the path lists are.</summary>
    public SettingValue(string key, string value)
        : this(key, [value])
    {
    }

    /// <summary>The first value that says anything, or null when the setting is present but blank.</summary>
    /// <remarks>
    /// An editor writes an unset string setting as the empty string rather than leaving it out, so
    /// blank has to mean unset. Treating it as a value would have every default overridden by nothing
    /// the moment a user opens the settings page and closes it again.
    /// </remarks>
    public string? Stated => Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

/// <summary>
/// What an editor may state about ProtoLang, at one scope.
/// </summary>
/// <remarks>
/// <para>
/// Three settings, and the list is short on purpose. Language policy -- overflow, conversions,
/// divide-by-zero, unset message reads -- is not here and will not be: spec 10.4 settles it in
/// <c>protolang.config.xml</c>, tracked beside the code it governs, so that generated code means the
/// same thing however it was built. An editor that could restate it would make a buffer mean one
/// thing on the screen and another in the build, which is the failure the file exists to prevent.
/// What an editor may do is point at a different file, which is exactly what <c>--config</c> does for
/// the command line.
/// </para>
/// <para>
/// A setting that states policy anyway is <em>reported</em> rather than dropped in silence
/// (<c>PL2101</c>), as is one this server does not recognize (<c>PL2102</c>). A user who has written
/// a setting and sees no effect has no way to tell a typo from a refusal from a bug, and guessing
/// between those three is the most expensive minute in a support request.
/// </para>
/// </remarks>
public sealed record ProtoLangSettings
{
    /// <summary>The settings section a client is asked for, and the prefix a key may carry.</summary>
    public const string Section = "protolang";

    /// <summary>Which protoc to run. The editor's answer to <c>PROTOLANG_PROTOC</c>.</summary>
    public const string ProtocPathKey = "protolang.protocPath";

    /// <summary>Directories searched for imported schemas. The editor's answer to <c>-I</c>.</summary>
    public const string IncludePathsKey = "protolang.includePaths";

    /// <summary>A <c>protolang.config.xml</c> to use instead of searching. The answer to <c>--config</c>.</summary>
    public const string ConfigPathKey = "protolang.configPath";

    /// <summary>A scope that states nothing.</summary>
    public static ProtoLangSettings None { get; } = new();

    /// <summary>Every key this server understands, in the order they are documented.</summary>
    public static IReadOnlyList<string> Keys { get; } = [ProtocPathKey, IncludePathsKey, ConfigPathKey];

    /// <inheritdoc cref="ProtocPathKey"/>
    public string? ProtocPath { get; init; }

    /// <inheritdoc cref="IncludePathsKey"/>
    /// <remarks>
    /// As written, which may be relative. What they are relative to is a property of the scope that
    /// stated them and not of the list, so resolving happens in
    /// <see cref="WorkspaceConfiguration.Resolve"/> where the scope is known.
    /// </remarks>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];

    /// <inheritdoc cref="ConfigPathKey"/>
    public string? ConfigPath { get; init; }

    /// <summary>Whether this scope states anything at all.</summary>
    public bool StatesNothing => ProtocPath is null && ConfigPath is null && IncludePaths.Count == 0;

    /// <summary>
    /// Reads what a client sent for one scope, reporting every entry that will not be used.
    /// </summary>
    /// <param name="scope">
    /// Which scope these were written at, so a diagnostic can say which settings page to open.
    /// </param>
    public static ProtoLangSettings Read(
        IEnumerable<SettingValue> values,
        ConfigurationSource scope,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var settings = None;

        foreach (var value in values)
        {
            switch (NameOf(value.Key)?.ToLowerInvariant())
            {
                case "protocpath":
                    settings = settings with { ProtocPath = value.Stated };
                    break;

                case "includepaths":
                    settings = settings with
                    {
                        IncludePaths = [.. value.Values.Where(path => !string.IsNullOrWhiteSpace(path))],
                    };
                    break;

                case "configpath":
                    settings = settings with { ConfigPath = value.Stated };
                    break;

                default:
                    Refuse(value, scope, diagnostics);
                    break;
            }
        }

        return settings;
    }

    /// <remarks>
    /// A key stating language policy is told where that policy lives; anything else is told what this
    /// server does understand. The two are separate diagnostics because they call for different
    /// actions -- move the setting, or fix the spelling -- and a single "unknown setting" would send a
    /// user who wrote <c>protolang.overflow</c> looking for a typo that is not there.
    /// </remarks>
    private static void Refuse(SettingValue value, ConfigurationSource scope, DiagnosticBag diagnostics)
    {
        var span = new SourceSpan(scope.Label(), SourcePosition.None, SourcePosition.None);

        if (PolicyKeyFor(value.Key) is { } policyKey)
        {
            diagnostics.Warning(
                "PL2101",
                "editor setting ignored",
                $"'{value.Key}' is being ignored: language policy is stated in {ProjectConfig.FileName}, "
                    + "not in editor settings.",
                span,
                $"Put <{policyKey.Split('/')[^1]}> in the <{policyKey.Split('/')[0]}> section of a "
                    + $"{ProjectConfig.FileName}, so that a build and this editor agree about what the "
                    + $"code means. Use '{ConfigPathKey}' to point at a particular one.");
            return;
        }

        diagnostics.Warning(
            "PL2102",
            "unknown editor setting",
            $"'{value.Key}' is not a setting this server understands, so it is being ignored.",
            span,
            $"Settings this server reads: {string.Join(", ", Keys)}.");
    }

    /// <summary>
    /// The <c>protolang.config.xml</c> setting this key is trying to state, or null if it is not
    /// trying to state one.
    /// </summary>
    /// <remarks>
    /// Matched on the last segment, so <c>protolang.overflow</c>, <c>protolang.arithmetic.overflow</c>
    /// and a bare <c>Overflow</c> are all recognized as the same attempt. The list comes from
    /// <see cref="ProjectConfig.Keys"/> rather than being restated here, so a setting added to the
    /// file is refused in an editor without anyone remembering to add it in two places.
    /// </remarks>
    private static string? PolicyKeyFor(string key)
    {
        var leaf = Leaf(key);

        return ProjectConfig.Keys.FirstOrDefault(
            candidate => string.Equals(Leaf(candidate), leaf, StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// A client that was asked for the <c>protolang</c> section sends bare keys, and one that sends
    /// its whole settings tree sends qualified ones. Both are the same setting, so the prefix is
    /// stripped rather than being one more thing for a caller to get right.
    /// </remarks>
    private static string? NameOf(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = key.StartsWith(Section + ".", StringComparison.OrdinalIgnoreCase)
            ? key[(Section.Length + 1)..]
            : key;

        return name.Length == 0 ? null : name;
    }

    private static string Leaf(string key)
    {
        var separator = key.LastIndexOfAny(['.', '/', ':']);
        return separator < 0 ? key : key[(separator + 1)..];
    }
}
