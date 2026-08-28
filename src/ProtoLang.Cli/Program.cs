using ProtoLang;
using ProtoLang.Backend;
using ProtoLang.Config;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;

var options = CommandLineOptions.Parse(args);

if (options is null)
{
    CommandLineOptions.PrintUsage();
    return 2;
}

if (!File.Exists(options.SourcePath))
{
    Console.Error.WriteLine($"error: source file not found: {options.SourcePath}");
    return 2;
}

if (options.Scaffold && options.TestOutputDirectory is null)
{
    Console.Error.WriteLine("error: --scaffold needs --test-out, because it writes the build file beside the generated tests");
    return 2;
}

var configDiagnostics = new DiagnosticBag();
var config = ResolveConfig(options, configDiagnostics);
PrintDiagnostics(configDiagnostics);

if (config is null)
{
    Console.Error.WriteLine("configuration failed");
    return 2;
}

var result = Compilation.Compile(options.SourcePath, options.IncludePaths, config: config);
PrintDiagnostics(result.Diagnostics);

if (!result.Success)
{
    Console.Error.WriteLine($"compilation failed: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)");
    return 1;
}

var backends = new List<IBackend>();
if (options.Targets.Contains("csharp"))
{
    backends.Add(new CSharpBackend());
}

if (options.Targets.Contains("cpp"))
{
    backends.Add(new CppBackend());
}

// The resolved policy travels into the header of every generated file, so a reader can tell what
// produced the code in front of them without re-running the compiler to find out.
var backendOptions = new BackendOptions(Path.GetFileName(options.SourcePath))
{
    PolicyDescription = result.Config.DescribeForHeader(),
};

var backendDiagnostics = new DiagnosticBag();
var written = new List<string>();

foreach (var backend in backends)
{
    var files = backend.Emit(
        result.Module!,
        backendOptions,
        backendDiagnostics);

    var outputDirectory = Path.Combine(options.OutputDirectory, backend.Name);
    Directory.CreateDirectory(outputDirectory);

    foreach (var file in files)
    {
        var path = Path.Combine(outputDirectory, file.RelativePath);
        File.WriteAllText(path, file.Contents);
        written.Add(path);
    }

    if (options.TestOutputDirectory is not null && backend is ITestBackend testBackend)
    {
        var testFiles = testBackend.EmitTests(
            result.Module!,
            backendOptions,
            backendDiagnostics);

        var testOutputDirectory = Path.Combine(options.TestOutputDirectory, backend.Name);
        Directory.CreateDirectory(testOutputDirectory);

        foreach (var file in testFiles)
        {
            var path = Path.Combine(testOutputDirectory, file.RelativePath);
            File.WriteAllText(path, file.Contents);
            written.Add(path);
        }

        if (options.Scaffold && backend is ITestProjectScaffold scaffold)
        {
            var scaffoldOptions = ScaffoldOptions.Create(
                result.SearchPaths,
                result.Descriptors,
                outputDirectory,
                testOutputDirectory,
                testFiles.Select(file => file.RelativePath).ToList());

            foreach (var file in scaffold.EmitTestProject(scaffoldOptions, backendDiagnostics))
            {
                var path = Path.Combine(testOutputDirectory, file.RelativePath);
                File.WriteAllText(path, file.Contents);
                written.Add(path);
            }
        }
    }
}

PrintDiagnostics(backendDiagnostics);

if (backendDiagnostics.HasErrors)
{
    Console.Error.WriteLine("code generation failed");
    return 1;
}

foreach (var path in written)
{
    Console.WriteLine(Path.GetRelativePath(Directory.GetCurrentDirectory(), path));
}

return 0;

/// <summary>
/// Settles the project policy from the config file and the command line (spec 10.4).
/// </summary>
/// <remarks>
/// The config file wins. A flag that contradicts a setting the file states is refused rather than
/// silently applied, because the point of tracking policy in the repository is that the generated
/// code means the same thing however it was built. <c>--override-config</c> exists so that trying
/// another policy stays one flag away, while leaving a trace in the command that no one can
/// mistake for the project's own answer.
/// </remarks>
static ProjectConfig? ResolveConfig(CommandLineOptions options, DiagnosticBag diagnostics)
{
    ProjectConfig config;

    if (options.NoConfig)
    {
        config = ProjectConfig.Default;
    }
    else if (options.ConfigPath is { } explicitPath)
    {
        if (!File.Exists(explicitPath))
        {
            Console.Error.WriteLine($"error: configuration file not found: {explicitPath}");
            return null;
        }

        var loaded = ProjectConfig.Load(explicitPath, diagnostics);
        if (loaded is null)
        {
            return null;
        }

        config = loaded;
    }
    else
    {
        // The same discovery the compiler would have done, asked for by name rather than repeated
        // here, so the CLI and the library can never disagree about which file settles the policy.
        var discovered = Compilation.ResolveConfig(
            SourceIdentity.FromPath(options.SourcePath).Directory,
            diagnostics);

        if (discovered is null)
        {
            return null;
        }

        config = discovered;
    }

    if (options.Overflow is not { } overflow)
    {
        return config;
    }

    if (!config.TryOverrideOverflow(overflow, options.OverrideConfig, out var overridden, out var conflict))
    {
        Console.Error.WriteLine($"error: {conflict}");
        Console.Error.WriteLine(
            "       The config file wins, so that a build means the same thing however it was run.");
        Console.Error.WriteLine("       Pass --override-config to use the flag anyway.");
        return null;
    }

    return overridden;
}

static void PrintDiagnostics(DiagnosticBag diagnostics)
{
    foreach (var diagnostic in diagnostics)
    {
        var writer = diagnostic.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
        writer.WriteLine(diagnostic.ToString());
        writer.WriteLine();
    }
}

internal sealed record CommandLineOptions(
    string SourcePath,
    IReadOnlyList<string> IncludePaths,
    string OutputDirectory,
    string? TestOutputDirectory,
    bool Scaffold,
    IReadOnlySet<string> Targets,
    string? ConfigPath,
    bool NoConfig,
    OverflowPolicy? Overflow,
    bool OverrideConfig)
{
    private static readonly string[] KnownTargets = ["csharp", "cpp"];

    public static CommandLineOptions? Parse(string[] args)
    {
        string? sourcePath = null;
        var includePaths = new List<string>();
        var outputDirectory = "generated";
        string? testOutputDirectory = null;
        var scaffold = false;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? configPath = null;
        var noConfig = false;
        OverflowPolicy? overflow = null;
        var overrideConfig = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-I" or "--proto_path":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a directory");
                        return null;
                    }

                    includePaths.Add(args[i]);
                    break;

                case "-o" or "--out":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a directory");
                        return null;
                    }

                    outputDirectory = args[i];
                    break;

                case "--test-out":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a directory");
                        return null;
                    }

                    testOutputDirectory = args[i];
                    break;

                case "--scaffold":
                    scaffold = true;
                    break;

                case "--config":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a file path");
                        return null;
                    }

                    configPath = args[i];
                    break;

                case "--no-config":
                    noConfig = true;
                    break;

                case "--override-config":
                    overrideConfig = true;
                    break;

                case "--arithmetic-overflow":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a mode");
                        return null;
                    }

                    if (!TryParseOverflow(args[i], out var parsed))
                    {
                        Console.Error.WriteLine(
                            $"error: unknown overflow mode '{args[i]}' (expected one of: "
                            + "wrapping, checked, saturating)");
                        return null;
                    }

                    overflow = parsed;
                    break;

                case "-t" or "--target":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a target name");
                        return null;
                    }

                    foreach (var target in args[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = target.Trim();
                        if (!KnownTargets.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine(
                                $"error: unknown target '{trimmed}' (expected one of: {string.Join(", ", KnownTargets)})");
                            return null;
                        }

                        targets.Add(trimmed.ToLowerInvariant());
                    }

                    break;

                case "-h" or "--help":
                    return null;

                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"error: unknown option '{arg}'");
                        return null;
                    }

                    if (sourcePath is not null)
                    {
                        Console.Error.WriteLine("error: only one source file may be given");
                        return null;
                    }

                    sourcePath = arg;
                    break;
            }
        }

        if (sourcePath is null)
        {
            return null;
        }

        if (targets.Count == 0)
        {
            foreach (var target in KnownTargets)
            {
                targets.Add(target);
            }
        }

        if (noConfig && configPath is not null)
        {
            Console.Error.WriteLine("error: --no-config and --config say opposite things; pass one");
            return null;
        }

        return new CommandLineOptions(
            sourcePath,
            includePaths,
            outputDirectory,
            testOutputDirectory,
            scaffold,
            targets,
            configPath,
            noConfig,
            overflow,
            overrideConfig);
    }

    private static bool TryParseOverflow(string text, out OverflowPolicy policy)
    {
        // Lowercase on the command line, PascalCase in the file. A flag is typed by hand and a
        // config value is read by a parser, so they answer to different conventions.
        switch (text.Trim().ToLowerInvariant())
        {
            case "wrapping":
                policy = OverflowPolicy.Wrapping;
                return true;
            case "checked":
                policy = OverflowPolicy.Checked;
                return true;
            case "saturating":
                policy = OverflowPolicy.Saturating;
                return true;
            default:
                policy = default;
                return false;
        }
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("protolangc - ProtoLang compiler");
        Console.Error.WriteLine();
        Console.Error.WriteLine("usage: protolangc <source.protolang> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("options:");
        Console.Error.WriteLine("  -I, --proto_path <dir>   Directory searched for imported .proto files.");
        Console.Error.WriteLine("                           May be repeated. The source directory is always searched.");
        Console.Error.WriteLine("  -o, --out <dir>          Output directory (default: generated).");
        Console.Error.WriteLine("                           Each backend writes to <dir>/<target>/.");
        Console.Error.WriteLine("  --test-out <dir>         Optional generated test output directory.");
        Console.Error.WriteLine("                           Each test backend writes to <dir>/<target>/.");
        Console.Error.WriteLine("  --scaffold               Also write the build file that builds and runs the");
        Console.Error.WriteLine("                           generated tests: a .csproj for csharp, a CMakeLists.txt");
        Console.Error.WriteLine("                           for cpp. Requires --test-out.");
        Console.Error.WriteLine("  -t, --target <list>      Comma-separated targets: csharp, cpp (default: all).");
        Console.Error.WriteLine("  -h, --help               Show this help.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("policy (spec 10.4):");
        Console.Error.WriteLine("  --config <file>          Use this protolang.config.xml instead of searching.");
        Console.Error.WriteLine("  --no-config              Ignore any config file and use the built-in defaults.");
        Console.Error.WriteLine("  --arithmetic-overflow <mode>");
        Console.Error.WriteLine("                           wrapping (default), checked, or saturating.");
        Console.Error.WriteLine("  --override-config        Let a policy flag win over a setting the config file");
        Console.Error.WriteLine("                           states. Without it, the conflict is an error: the file");
        Console.Error.WriteLine("                           is the project's answer and a flag is not.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  With no --config or --no-config, protolang.config.xml is searched for in the");
        Console.Error.WriteLine("  source file's directory and every directory above it, nearest first.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("environment:");
        Console.Error.WriteLine("  PROTOLANG_PROTOC         Path to protoc. Otherwise PATH and the NuGet");
        Console.Error.WriteLine("                           Grpc.Tools package are searched.");
    }
}
