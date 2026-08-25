using System.Globalization;
using System.Text.RegularExpressions;
using ProtoLang.Backend;

namespace ProtoLang.Tests.Harness;

/// <summary>The outcome of building and running one generated C++ test driver.</summary>
/// <param name="ExitCode">
/// The driver's own exit code, or <see cref="ProcessResult.NotRun"/> when it never got that far
/// because the compile or link step failed.
/// </param>
internal sealed record CppProgramResult(string DriverSource, int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// A throwaway C++ workspace that compiles ProtoLang's generated header and generated test driver
/// together with protoc's C++ output, then runs the driver.
/// </summary>
/// <remarks>
/// Several drivers share one protobuf schema, so the generated <c>.pb.cc</c> is compiled once and
/// its object file is linked into every driver. That compile is by far the most expensive step, and
/// repeating it per driver would dominate the suite's runtime.
/// </remarks>
internal sealed class CppTestWorkspace
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    private CppTestWorkspace(string directory) => Directory = directory;

    public string Directory { get; }

    public static CppTestWorkspace Create(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-" + label, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new CppTestWorkspace(directory);
    }

    public void Write(IEnumerable<GeneratedFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(Directory, file.RelativePath);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Contents);
        }
    }

    public ProcessResult GenerateProtobuf(string protoc, string protoPath, params string[] protoFiles)
        => Toolchain.RunProtoc(protoc, "cpp_out", protoPath, Directory, protoFiles);

    /// <summary>Parses a source file without producing an object, to check the emitted C++ is valid.</summary>
    public ProcessResult RunSyntaxOnly(CppCompiler compiler, string sourcePath, string protobufInclude)
    {
        switch (compiler.Kind)
        {
            case CppCompilerKind.ClangOrGcc:
            {
                var startInfo = ProcessRunner.Create(compiler.FileName, Directory);
                startInfo.ArgumentList.Add("-std=c++20");
                startInfo.ArgumentList.Add("-fsyntax-only");
                startInfo.ArgumentList.Add($"-I{Directory}");
                startInfo.ArgumentList.Add($"-I{protobufInclude}");
                startInfo.ArgumentList.Add(sourcePath);
                return ProcessRunner.Run(startInfo, BuildTimeout);
            }

            case CppCompilerKind.Msvc:
            {
                string[] arguments =
                [
                    "/nologo", "/std:c++20", "/Zc:__cplusplus", "/EHsc", "/Zs",
                    $"/I{Directory}", $"/I{protobufInclude}", sourcePath,
                ];

                if (compiler.VisualStudioDevCommand is null)
                {
                    var startInfo = ProcessRunner.Create(compiler.FileName, Directory);
                    foreach (var argument in arguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }

                    return ProcessRunner.Run(startInfo, BuildTimeout);
                }

                var responsePath = Path.Combine(Directory, "msvc-syntax.rsp");
                File.WriteAllLines(responsePath, arguments.Select(ProcessRunner.QuoteRspArgument));

                return ProcessRunner.RunCmdScript(
                    Path.Combine(Directory, "msvc-syntax.cmd"),
                    [
                        "@echo off",
                        $"call \"{compiler.VisualStudioDevCommand}\" -arch=x64 -host_arch=x64",
                        "if errorlevel 1 exit /b %errorlevel%",
                        $"cl.exe @{ProcessRunner.QuoteForCmd(responsePath)}",
                    ],
                    Directory,
                    BuildTimeout);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(compiler), compiler.Kind, "Unknown compiler kind.");
        }
    }

    /// <summary>
    /// Compiles the shared protobuf sources once, then builds and runs one executable per driver.
    /// </summary>
    /// <param name="driverSources">Generated <c>.tests.cc</c> file names, relative to the workspace.</param>
    /// <param name="protobufSources">protoc's <c>.pb.cc</c> file names, relative to the workspace.</param>
    public IReadOnlyList<CppProgramResult> BuildAndRun(
        CppCompiler compiler,
        ProtobufCppInstall protobuf,
        IReadOnlyList<string> driverSources,
        IReadOnlyList<string> protobufSources)
        => compiler.Kind == CppCompilerKind.Msvc
            ? BuildAndRunMsvc(compiler, protobuf, driverSources, protobufSources)
            : BuildAndRunPosix(compiler, protobuf, driverSources, protobufSources);

    private IReadOnlyList<CppProgramResult> BuildAndRunMsvc(
        CppCompiler compiler,
        ProtobufCppInstall protobuf,
        IReadOnlyList<string> driverSources,
        IReadOnlyList<string> protobufSources)
    {
        if (protobuf.LibraryDirectory is null || protobuf.BinaryDirectory is null)
        {
            throw new InvalidOperationException("Linking with MSVC needs a protobuf lib and bin directory.");
        }

        var sharedResponse = Path.Combine(Directory, "msvc-shared.rsp");
        File.WriteAllLines(
            sharedResponse,
            CompileArguments().Concat(protobufSources.Select(s => Rsp(Path.Combine(Directory, s)))));

        var lines = new List<string> { "@echo off" };
        if (compiler.VisualStudioDevCommand is not null)
        {
            lines.Add($"call \"{compiler.VisualStudioDevCommand}\" -arch=x64 -host_arch=x64");
            lines.Add("if errorlevel 1 exit /b %errorlevel%");
        }

        lines.Add($"cl.exe @{ProcessRunner.QuoteForCmd(sharedResponse)}");
        lines.Add("if errorlevel 1 exit /b %errorlevel%");
        lines.Add($"set \"PATH={protobuf.BinaryDirectory};%PATH%\"");

        var sharedObjects = protobufSources
            .Select(source => Path.Combine(Directory, Path.GetFileNameWithoutExtension(source) + ".obj"))
            .ToList();

        for (var i = 0; i < driverSources.Count; i++)
        {
            var driver = driverSources[i];
            var stem = Path.GetFileNameWithoutExtension(driver);
            var driverObject = Path.Combine(Directory, stem + ".obj");
            var executable = Path.Combine(Directory, stem + ".exe");

            var compileResponse = Path.Combine(Directory, $"msvc-compile-{i}.rsp");
            File.WriteAllLines(
                compileResponse,
                CompileArguments().Append(Rsp(Path.Combine(Directory, driver))));

            var linkResponse = Path.Combine(Directory, $"msvc-link-{i}.rsp");
            File.WriteAllLines(
                linkResponse,
                [
                    "/nologo",
                    $"/out:{Rsp(executable)}",
                    Rsp(driverObject),
                    .. sharedObjects.Select(Rsp),
                    $"/LIBPATH:{Rsp(protobuf.LibraryDirectory)}",
                    "libprotobuf.lib",
                    "abseil_dll.lib",
                    "utf8_validity.lib",
                    "utf8_range.lib",
                ]);

            var next = $"DRIVER{i + 1}";

            // No parenthesised blocks: cmd expands %errorlevel% when it reads a block, not when it
            // runs each line, so a marker inside one would report a stale value.
            lines.Add($"cl.exe @{ProcessRunner.QuoteForCmd(compileResponse)}");
            lines.Add($"if errorlevel 1 echo {Marker(i)} COMPILE-FAILED");
            lines.Add($"if errorlevel 1 goto {next}");
            lines.Add($"link.exe @{ProcessRunner.QuoteForCmd(linkResponse)}");
            lines.Add($"if errorlevel 1 echo {Marker(i)} LINK-FAILED");
            lines.Add($"if errorlevel 1 goto {next}");
            lines.Add(ProcessRunner.QuoteForCmd(executable));
            lines.Add($"echo {Marker(i)} EXIT=%errorlevel%");
            lines.Add($":{next}");
        }

        lines.Add("exit /b 0");

        var result = ProcessRunner.RunCmdScript(
            Path.Combine(Directory, "msvc-build-run.cmd"), lines, Directory, BuildTimeout);

        return SplitByMarkers(driverSources, result);

        IEnumerable<string> CompileArguments() =>
        [
            "/nologo", "/std:c++20", "/Zc:__cplusplus", "/EHsc", "/c",
            $"/I{Rsp(Directory)}",
            $"/I{Rsp(protobuf.IncludeDirectory)}",
            $"/Fo{Rsp(Directory + Path.DirectorySeparatorChar)}",
        ];

        static string Rsp(string argument) => ProcessRunner.QuoteRspArgument(argument);
    }

    private IReadOnlyList<CppProgramResult> BuildAndRunPosix(
        CppCompiler compiler,
        ProtobufCppInstall protobuf,
        IReadOnlyList<string> driverSources,
        IReadOnlyList<string> protobufSources)
    {
        var results = new List<CppProgramResult>();
        var sharedObjects = new List<string>();

        foreach (var source in protobufSources)
        {
            var objectPath = Path.Combine(Directory, Path.GetFileNameWithoutExtension(source) + ".o");
            var compile = ProcessRunner.Create(compiler.FileName, Directory);
            compile.ArgumentList.Add("-std=c++20");
            compile.ArgumentList.Add("-c");
            compile.ArgumentList.Add($"-I{Directory}");
            compile.ArgumentList.Add($"-I{protobuf.IncludeDirectory}");
            compile.ArgumentList.Add(Path.Combine(Directory, source));
            compile.ArgumentList.Add("-o");
            compile.ArgumentList.Add(objectPath);

            var compiled = ProcessRunner.Run(compile, BuildTimeout);
            if (compiled.ExitCode != 0)
            {
                // Nothing can link without the schema, so report the failure against every driver.
                return driverSources
                    .Select(driver => new CppProgramResult(driver, ProcessResult.NotRun, compiled.Output))
                    .ToList();
            }

            sharedObjects.Add(objectPath);
        }

        foreach (var driver in driverSources)
        {
            var stem = Path.GetFileNameWithoutExtension(driver);
            var executable = Path.Combine(Directory, stem);

            var link = ProcessRunner.Create(compiler.FileName, Directory);
            link.ArgumentList.Add("-std=c++20");
            link.ArgumentList.Add($"-I{Directory}");
            link.ArgumentList.Add($"-I{protobuf.IncludeDirectory}");
            link.ArgumentList.Add(Path.Combine(Directory, driver));

            foreach (var objectPath in sharedObjects)
            {
                link.ArgumentList.Add(objectPath);
            }

            if (protobuf.LibraryDirectory is not null)
            {
                link.ArgumentList.Add($"-L{protobuf.LibraryDirectory}");
            }

            link.ArgumentList.Add("-lprotobuf");
            link.ArgumentList.Add("-o");
            link.ArgumentList.Add(executable);

            var linked = ProcessRunner.Run(link, BuildTimeout);
            if (linked.ExitCode != 0)
            {
                results.Add(new CppProgramResult(driver, ProcessResult.NotRun, linked.Output));
                continue;
            }

            var run = ProcessRunner.Create(executable, Directory);
            if (protobuf.LibraryDirectory is not null)
            {
                run.Environment["LD_LIBRARY_PATH"] = protobuf.LibraryDirectory;
                run.Environment["DYLD_LIBRARY_PATH"] = protobuf.LibraryDirectory;
            }

            var ran = ProcessRunner.Run(run, BuildTimeout);
            results.Add(new CppProgramResult(driver, ran.ExitCode, ran.Output));
        }

        return results;
    }

    private static string Marker(int index) => $"###PROTOLANG-DRIVER-{index}";

    /// <summary>
    /// Attributes the batch script's combined output back to each driver. Everything printed since
    /// the previous marker belongs to the driver the current marker names.
    /// </summary>
    private static IReadOnlyList<CppProgramResult> SplitByMarkers(
        IReadOnlyList<string> driverSources,
        ProcessResult script)
    {
        var results = new List<CppProgramResult>();
        var pending = new List<string>();
        var byIndex = new Dictionary<int, CppProgramResult>();

        foreach (var line in script.Output.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            var match = Regex.Match(line, @"^###PROTOLANG-DRIVER-(\d+)\s+(.*)$");
            if (!match.Success)
            {
                pending.Add(line);
                continue;
            }

            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var status = match.Groups[2].Value.Trim();
            var exitCode = status.StartsWith("EXIT=", StringComparison.Ordinal)
                && int.TryParse(status[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : ProcessResult.NotRun;

            if (index < driverSources.Count)
            {
                byIndex[index] = new CppProgramResult(
                    driverSources[index],
                    exitCode,
                    string.Join(Environment.NewLine, pending).Trim());
            }

            pending.Clear();
        }

        for (var i = 0; i < driverSources.Count; i++)
        {
            results.Add(byIndex.TryGetValue(i, out var found)
                ? found
                // No marker at all means the script died before reaching this driver, so the whole
                // script output is the best evidence available.
                : new CppProgramResult(driverSources[i], ProcessResult.NotRun, script.Output));
        }

        return results;
    }
}
