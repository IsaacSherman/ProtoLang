using ProtoLang;
using ProtoLang.Backend;
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

var result = Compilation.Compile(options.SourcePath, options.IncludePaths);
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

var backendDiagnostics = new DiagnosticBag();
var written = new List<string>();

foreach (var backend in backends)
{
    var files = backend.Emit(
        result.Module!,
        new BackendOptions(Path.GetFileName(options.SourcePath)),
        backendDiagnostics);

    var outputDirectory = Path.Combine(options.OutputDirectory, backend.Name);
    Directory.CreateDirectory(outputDirectory);

    foreach (var file in files)
    {
        var path = Path.Combine(outputDirectory, file.RelativePath);
        File.WriteAllText(path, file.Contents);
        written.Add(path);
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
    IReadOnlySet<string> Targets)
{
    private static readonly string[] KnownTargets = ["csharp", "cpp"];

    public static CommandLineOptions? Parse(string[] args)
    {
        string? sourcePath = null;
        var includePaths = new List<string>();
        var outputDirectory = "generated";
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        return new CommandLineOptions(sourcePath, includePaths, outputDirectory, targets);
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
        Console.Error.WriteLine("  -t, --target <list>      Comma-separated targets: csharp, cpp (default: all).");
        Console.Error.WriteLine("  -h, --help               Show this help.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("environment:");
        Console.Error.WriteLine("  PROTOLANG_PROTOC         Path to protoc. Otherwise PATH and the NuGet");
        Console.Error.WriteLine("                           Grpc.Tools package are searched.");
    }
}
