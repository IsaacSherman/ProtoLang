using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;

namespace ProtoLang.LanguageServer.Workspace;

/// <summary>One include directory, where it came from, and how it was written there.</summary>
/// <param name="Path">The absolute directory the compiler will search.</param>
/// <param name="AsWritten">
/// The setting's own text, which is what the user will recognize. A relative path resolved against a
/// folder is unrecognizable by the time it is absolute, and "which of my settings produced this?" is
/// the question this whole type exists to answer.
/// </param>
/// <param name="Source">The scope that supplied it.</param>
public sealed record ResolvedIncludePath(string Path, string AsWritten, ConfigurationSource Source);

/// <summary>One line of the resolved-configuration report: a setting, its value, and its origin.</summary>
/// <remarks>
/// What #58 prints when a user asks why their build is behaving the way it is. Kept as data rather
/// than as formatted text so the same facts can be rendered into a log, a status panel, or a test
/// assertion without three renderings drifting apart.
/// </remarks>
public sealed record ConfigurationFact(string Setting, string Value, ConfigurationSource Source);

/// <summary>
/// The configuration one document compiles under: every value settled, and every value able to say
/// where it came from.
/// </summary>
/// <remarks>
/// <para>
/// Resolved per document rather than per server, because two files in one editor window legitimately
/// need different answers -- a multi-root workspace is two projects, and even one project may hold a
/// subdirectory with its own <c>protolang.config.xml</c>. A server that resolved settings once at
/// startup would be right about the first folder and quietly wrong about the rest.
/// </para>
/// <para>
/// A snapshot, taken from one <see cref="WorkspaceConfiguration"/> and stamped with its
/// <see cref="Generation"/>. Settings can change while a compilation is in flight; the work that is
/// already running keeps the answer it started with, and the generation is what lets a host tell that
/// a result it has just been handed was computed under configuration that no longer applies. What it
/// does about that -- cancel, discard, recompute -- is #54's, and this is the handle it needs.
/// </para>
/// </remarks>
public sealed record DocumentConfiguration
{
    public DocumentConfiguration(DocumentUri document, int generation)
    {
        ArgumentNullException.ThrowIfNull(document);

        Document = document;
        Generation = generation;
    }

    /// <summary>The document this was resolved for.</summary>
    public DocumentUri Document { get; }

    /// <summary>The configuration generation this was resolved from.</summary>
    /// <inheritdoc cref="DocumentConfiguration"/>
    public int Generation { get; }

    /// <summary>The workspace folder this document belongs to, or null when it belongs to none.</summary>
    public WorkspaceFolder? Folder { get; init; }

    /// <summary>
    /// The protoc to run, or null when nothing named one and the compiler should locate its own.
    /// </summary>
    /// <remarks>
    /// Null is a real answer rather than a missing one, and it is the common case. Locating protoc
    /// probes <c>PATH</c> and then the NuGet package caches, which is far too much work to repeat per
    /// keystroke merely so that this property could be non-null -- and pointless besides, since
    /// <see cref="DescriptorLoader"/> already does it once and holds the result. A host that wants the
    /// concrete answer for a report asks the loader that ran, through
    /// <see cref="Compilation.Loader"/>.
    /// </remarks>
    public string? ProtocPath { get; init; }

    /// <inheritdoc cref="ProtocPath"/>
    public ConfigurationSource ProtocPathSource { get; init; } = ConfigurationSource.Discovery;

    /// <summary>
    /// The directories imports are searched in, most specific scope first, deduplicated.
    /// </summary>
    /// <remarks>
    /// Every scope contributes rather than the nearest one winning outright. An include path is a
    /// place to look, not a value: a user-scope entry pointing at a shared schema checkout and a
    /// folder-scope entry pointing into the repository are both true at once, and a rule where the
    /// folder silences the user would make the common arrangement unexpressible. Order carries the
    /// precedence instead, which is what first-match resolution already means.
    /// </remarks>
    public IReadOnlyList<ResolvedIncludePath> IncludePaths { get; init; } = [];

    /// <summary>
    /// The language policy this document compiles under, or null when a configuration file was found
    /// and could not be read.
    /// </summary>
    /// <remarks>
    /// Null stops the compilation, exactly as it does in <see cref="Compilation.ResolveConfig"/>: a
    /// project that states a policy and is then silently ignored is worse off than one that states
    /// nothing. The reason is in <see cref="Diagnostics"/>, and <see cref="IsUsable"/> is the question
    /// to ask.
    /// </remarks>
    public ProjectConfig? Config { get; init; }

    /// <inheritdoc cref="Config"/>
    public ConfigurationSource ConfigSource { get; init; } = ConfigurationSource.Default;

    /// <summary>
    /// The configuration file this document was settled against, whether or not it could be read, and
    /// null when there was none to read.
    /// </summary>
    /// <remarks>
    /// <see cref="Config"/> carries the path of a file that <em>loaded</em>, and null tells a reader
    /// nothing about which file it was. That is the case where naming it matters most: the whole
    /// report a user gets is "your document is not being compiled", and the first question is which
    /// file to go and fix. See <see cref="ConfigRefused"/>.
    /// </remarks>
    public string? ConfigPath { get; init; }

    /// <summary>Whether a configuration file was found and then refused.</summary>
    /// <remarks>
    /// Distinct from having no policy file at all, which is not a problem and produces the defaults.
    /// A refusal is a project stating a policy and being ignored, which spec 10.4 stops the
    /// compilation over, so it is an error and not a warning.
    /// </remarks>
    public bool ConfigRefused => Config is null && ConfigPath is not null;

    /// <summary>
    /// What went wrong while settling this: settings being ignored, and any configuration file that
    /// could not be read.
    /// </summary>
    /// <remarks>
    /// Warnings, all but one of them, and every one of them is about something the user wrote that is
    /// not taking effect. A setting silently ignored is the failure this model was asked to prevent:
    /// the user cannot tell a typo from a refusal from a bug, and the server is the only party that
    /// knows which it was.
    /// </remarks>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>Whether a compilation may run under this configuration.</summary>
    public bool IsUsable => Config is not null;

    /// <summary>
    /// The options a compilation of this document runs with, or false when it must not run at all.
    /// </summary>
    /// <param name="loader">
    /// The loader to compile with, holding the shared descriptor cache and built for
    /// <see cref="ProtocPath"/>. Null lets the compilation locate protoc for itself, which is only
    /// legitimate when nothing named one -- a <see cref="ProtocPathSource"/> of
    /// <see cref="ConfigurationSource.Discovery"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// A protoc was resolved and no loader was built for it. Which protoc runs can only reach a
    /// compilation through its loader, so a null one here would discard the setting in silence.
    /// </exception>
    /// <remarks>
    /// The refusal is deliberate and is the only thing in this type that throws.
    /// <see cref="CompilationOptions"/> has no protoc of its own, so a caller that resolves a
    /// <c>protolang.protocPath</c> and then passes no loader compiles against whichever protoc the
    /// compiler located for itself -- while this object goes on reporting, through
    /// <see cref="Describe"/>, that the user's setting is in force. A wrong answer nobody can see is
    /// worse than an exception during development, and this one is programmer error: the value was
    /// resolved and then dropped on the floor.
    /// </remarks>
    public bool TryCreateCompilationOptions(DescriptorLoader? loader, out CompilationOptions? options)
    {
        if (loader is null && ProtocPath is not null)
        {
            throw new ArgumentNullException(
                nameof(loader),
                $"This document resolved protoc to '{ProtocPath}', from {ProtocPathSource.Describe()}, and "
                    + "a compilation can only be told which protoc to run through its loader. Build one "
                    + "for that path rather than passing null.");
        }

        if (Config is null)
        {
            options = null;
            return false;
        }

        options = new CompilationOptions
        {
            IncludePaths = [.. IncludePaths.Select(include => include.Path)],
            Config = Config,
            Loader = loader,
        };

        return true;
    }

    /// <summary>Every resolved value with the source that produced it, for a status report.</summary>
    /// <remarks>
    /// Configured values only. Which folder a document landed in is not a setting anybody wrote and
    /// has no source to name, so it stays on <see cref="Folder"/> where a report can read it directly
    /// rather than being given a fact with an invented origin.
    /// </remarks>
    public IReadOnlyList<ConfigurationFact> Describe()
    {
        var facts = new List<ConfigurationFact>
        {
            new("protoc", ProtocPath ?? "(located when needed)", ProtocPathSource),

            // A refused file is named, not summarized as "(defaults)". Reporting the defaults beside
            // the file that was rejected would say the file supplied them, when in truth no policy is
            // in force at all and the document is not being compiled.
            new("language policy", DescribePolicy(), ConfigSource),
        };

        facts.AddRange(
            IncludePaths.Select(include => new ConfigurationFact("include path", include.Path, include.Source)));

        return facts;
    }

    private string DescribePolicy()
    {
        if (ConfigRefused)
        {
            return $"(refused: {ConfigPath})";
        }

        return Config?.Path ?? "(defaults)";
    }
}
