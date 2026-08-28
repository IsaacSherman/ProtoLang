using System.Xml;
using System.Xml.Linq;
using ProtoLang.Diagnostics;

namespace ProtoLang.Config;

/// <summary>How an arithmetic operation behaves when its result leaves the range of its type.</summary>
public enum OverflowPolicy
{
    /// <summary>Two's-complement wraparound. The default, and what unmodified C# does.</summary>
    Wrapping,

    /// <summary>Terminal failure, through the same path as <c>on_zero fail</c>.</summary>
    Checked,

    /// <summary>Clamp to the bound the true result exceeded.</summary>
    Saturating,
}

/// <summary>What an explicit numeric conversion does when the value does not fit (spec 10.3).</summary>
public enum ConversionPolicy
{
    /// <summary>
    /// Integer targets take the low bits; a floating-point source truncates toward zero, clamps,
    /// and maps NaN to zero. One member today, matching what the .NET runtime produces.
    /// </summary>
    WrapOrSaturate,
}

/// <summary>What an integer division does about a zero divisor (spec 10.2.1).</summary>
public enum DivideByZeroPolicy
{
    /// <summary>The author must write an <c>on_zero</c> clause. One member today.</summary>
    RequireOnZero,
}

/// <summary>What reading an unset singular message field means (spec 13.1).</summary>
public enum UnsetMessageReadPolicy
{
    /// <summary>
    /// Using the value requires an established presence test, or it is a compile error. One member
    /// today; see <c>docs/reference-semantics.md</c> for why this is not C#'s own answer.
    /// </summary>
    RequireGuard,
}

/// <summary>
/// The project's language-dependent preferences, as read from <c>protolang.config.xml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 10.4. The point of a file rather than command-line switches is that the semantics of a
/// repository's generated code should travel with the repository, not with whoever remembered which
/// flags to type. A build that produces different arithmetic depending on the operator's shell
/// history is not reproducible in any sense worth having.
/// </para>
/// <para>
/// Several settings have exactly one legal value today. That is deliberate: this file's job is to
/// enumerate every language-dependent preference, including the settled ones, so that a reader can
/// see the whole contract in one place and a future option is an addition rather than a discovery.
/// </para>
/// </remarks>
public sealed record ProjectConfig(
    OverflowPolicy Overflow,
    ConversionPolicy Conversion,
    DivideByZeroPolicy DivideByZero,
    UnsetMessageReadPolicy UnsetMessageRead)
{
    /// <summary>The file name searched for, in the source directory and every directory above it.</summary>
    public const string FileName = "protolang.config.xml";

    /// <summary>The behavior a project gets when it states nothing.</summary>
    public static ProjectConfig Default { get; } = new(
        OverflowPolicy.Wrapping,
        ConversionPolicy.WrapOrSaturate,
        DivideByZeroPolicy.RequireOnZero,
        UnsetMessageReadPolicy.RequireGuard);

    /// <summary>
    /// Which settings the file stated explicitly. A command-line override has to know the
    /// difference between a value the project chose and a default that was merely left in place.
    /// </summary>
    public IReadOnlySet<string> ExplicitKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The file this came from, or null for <see cref="Default"/>.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The policy lines a generated file's header carries, so a reader can tell which policy
    /// produced the code below it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendered here rather than in each backend, for two reasons. Every target says the same thing
    /// about the same build, which is the point of a header that claims reproducibility. And a
    /// backend receives these as prose it can only print, never as a policy it could branch on --
    /// every emission decision has to come from the annotation the binder stamped onto the IR node,
    /// which is what makes a new mode a compile error at every emission site instead of a silently
    /// wrong default.
    /// </para>
    /// <para>
    /// Only the settings visible in the emitted code are listed. The other two govern what compiles
    /// rather than what is written out, and a header that repeated them in every file forever would
    /// be noise. No path is included: an absolute path would make otherwise identical output differ
    /// between machines.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> DescribeForHeader() =>
    [
        $"Language policy (spec 10.4): integer overflow = {DescribeOverflow(Overflow)},",
        $"numeric conversions = {Conversion}. Both are emitted explicitly, so a",
        "consumer's build settings cannot change what this code does.",
    ];

    private static string DescribeOverflow(OverflowPolicy overflow) => overflow switch
    {
        OverflowPolicy.Wrapping => "Wrapping (two's complement)",
        OverflowPolicy.Checked => "Checked (terminates, exit code 70)",
        OverflowPolicy.Saturating => "Saturating (clamps to the type's bounds)",
        _ => throw new ArgumentOutOfRangeException(nameof(overflow), overflow, "Unhandled overflow policy."),
    };

    /// <summary>
    /// Applies a command-line override to one setting, or refuses it.
    /// </summary>
    /// <remarks>
    /// The file wins. A flag that contradicts a setting the project states is refused rather than
    /// silently applied, because the point of tracking policy in the repository is that generated
    /// code means the same thing however it was built. <paramref name="allowOverride"/> exists so
    /// that trying another policy stays one flag away, while leaving a trace in the command that
    /// nobody can mistake for the project's own answer.
    /// </remarks>
    /// <param name="conflict">
    /// Null on success. Otherwise a sentence naming both answers, for the driver to print.
    /// </param>
    public bool TryOverrideOverflow(
        OverflowPolicy overflow,
        bool allowOverride,
        out ProjectConfig result,
        out string? conflict)
    {
        if (!allowOverride && ExplicitKeys.Contains("Arithmetic/Overflow") && Overflow != overflow)
        {
            result = this;
            conflict =
                $"--arithmetic-overflow {overflow.ToString().ToLowerInvariant()} contradicts "
                + $"Arithmetic/Overflow = {Overflow} in {Path}";
            return false;
        }

        result = this with { Overflow = overflow };
        conflict = null;
        return true;
    }

    /// <summary>
    /// Searches <paramref name="startDirectory"/> and each directory above it for
    /// <see cref="FileName"/>, the way <c>.editorconfig</c> and <c>Directory.Build.props</c> are
    /// found. Returns the nearest one, or null.
    /// </summary>
    public static string? Discover(string startDirectory)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetFullPath(startDirectory));

        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Reads a configuration file. Every problem is reported through <paramref name="diagnostics"/>
    /// with the line and column it occurred at; an unreadable or invalid file yields null rather
    /// than silently falling back to the defaults, because a project that states a policy and is
    /// then ignored is worse off than one that states nothing.
    /// </summary>
    public static ProjectConfig? Load(string path, DiagnosticBag diagnostics)
    {
        var file = System.IO.Path.GetFileName(path);

        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Error(
                "PL2003",
                "configuration file could not be read",
                ex.Message,
                new SourceSpan(file, LineOf(ex), ColumnOf(ex), 0));
            return null;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "ProtoLang")
        {
            diagnostics.Error(
                "PL2003",
                "configuration file could not be read",
                $"The root element must be <ProtoLang>, not <{root?.Name.LocalName ?? "(empty)"}>.",
                Span(file, root));
            return null;
        }

        var config = Default with { Path = path };
        var explicitKeys = new HashSet<string>(StringComparer.Ordinal);
        var failed = false;

        foreach (var section in root.Elements())
        {
            var sectionName = section.Name.LocalName;
            if (sectionName is not ("Arithmetic" or "Presence"))
            {
                UnknownElement(diagnostics, file, section, sectionName, "ProtoLang", KnownSections);
                failed = true;
                continue;
            }

            foreach (var setting in section.Elements())
            {
                var key = $"{sectionName}/{setting.Name.LocalName}";
                var text = setting.Value.Trim();

                switch (key)
                {
                    case "Arithmetic/Overflow":
                        if (TryParse<OverflowPolicy>(diagnostics, file, setting, key, text, out var overflow))
                        {
                            config = config with { Overflow = overflow };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    case "Arithmetic/Conversion":
                        if (TryParse<ConversionPolicy>(diagnostics, file, setting, key, text, out var conversion))
                        {
                            config = config with { Conversion = conversion };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    case "Arithmetic/DivideByZero":
                        if (TryParse<DivideByZeroPolicy>(diagnostics, file, setting, key, text, out var divide))
                        {
                            config = config with { DivideByZero = divide };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    case "Presence/UnsetMessageRead":
                        if (TryParse<UnsetMessageReadPolicy>(diagnostics, file, setting, key, text, out var unset))
                        {
                            config = config with { UnsetMessageRead = unset };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    default:
                        UnknownElement(
                            diagnostics,
                            file,
                            setting,
                            setting.Name.LocalName,
                            sectionName,
                            KnownSettings(sectionName));
                        failed = true;
                        continue;
                }

                if (!explicitKeys.Add(key))
                {
                    diagnostics.Error(
                        "PL2004",
                        "duplicate configuration setting",
                        $"'{key}' is stated more than once.",
                        Span(file, setting),
                        "Two answers to one question is not a configuration, it is a coin toss. Keep one.");
                    failed = true;
                }
            }
        }

        return failed ? null : config with { ExplicitKeys = explicitKeys };
    }

    private static readonly string[] KnownSections = ["Arithmetic", "Presence"];

    private static string[] KnownSettings(string section) => section switch
    {
        "Arithmetic" => ["Overflow", "Conversion", "DivideByZero"],
        "Presence" => ["UnsetMessageRead"],
        _ => [],
    };

    private static bool TryParse<T>(
        DiagnosticBag diagnostics,
        string file,
        XElement element,
        string key,
        string text,
        out T value)
        where T : struct, Enum
    {
        // Deliberately case-sensitive and exact. A configuration file that quietly accepts
        // "wrapping", "WRAPPING", and "Wrap" for the same setting invites a project to be written
        // one way and read another, which is the failure this whole file exists to prevent.
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        diagnostics.Error(
            "PL2002",
            "unknown configuration value",
            $"'{text}' is not a legal value for '{key}'.",
            Span(file, element),
            $"Legal values: {string.Join(", ", Enum.GetValues<T>().Select(v => v.ToString()))}.");

        value = default;
        return false;
    }

    private static void UnknownElement(
        DiagnosticBag diagnostics,
        string file,
        XElement element,
        string name,
        string parent,
        IReadOnlyList<string> known)
    {
        diagnostics.Error(
            "PL2001",
            "unknown configuration element",
            $"<{name}> is not a setting ProtoLang knows about inside <{parent}>.",
            Span(file, element),
            known.Count == 0
                ? null
                : $"Known elements inside <{parent}>: {string.Join(", ", known)}.");
    }

    private static SourceSpan Span(string file, XObject? node)
    {
        if (node is IXmlLineInfo info && info.HasLineInfo())
        {
            return new SourceSpan(file, info.LineNumber, info.LinePosition, 0);
        }

        return new SourceSpan(file, 0, 0, 0);
    }

    private static int LineOf(Exception ex) => ex is XmlException xml ? xml.LineNumber : 0;

    private static int ColumnOf(Exception ex) => ex is XmlException xml ? xml.LinePosition : 0;
}
