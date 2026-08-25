using System.Diagnostics;
using System.Runtime.InteropServices;
using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Optional smoke coverage that asks a real C++ compiler to parse and execute generated C++ code
/// together with protoc's generated C++ output.
/// </summary>
public class CppSyntaxSmokeTests
{
    [Fact]
    public void GeneratedCppParsesWithARealCompiler()
    {
        var compiler = CppCompiler.Locate();
        if (compiler is null)
        {
            Assert.Skip(
                "No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools "
                + "to run C++ smoke tests.");
        }

        var protobuf = LocateProtobufCppInstall();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ headers found. Install protobuf headers or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to their include directory.");
        }

        var protoc = protobuf.ProtocPath ?? ProtocLocator.Locate();
        if (protoc is null)
        {
            Assert.Skip("No protoc executable found. Restore Grpc.Tools or install protoc to run C++ smoke tests.");
        }

        var workspace = PrepareSmokeWorkspace(protoc);

        var compileResult = compiler.RunSyntaxOnly(
            workspace.DriverSourcePath,
            workspace.Directory,
            protobuf.IncludeDirectory);
        Assert.True(
            compileResult.ExitCode == 0,
            $"C++ syntax check failed with {compiler.DisplayName}.{Environment.NewLine}{compileResult.Output}");
    }

    [Fact]
    public void GeneratedCppLinksAndRunsWithVcpkgProtobuf()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Skip("The link-and-run C++ smoke test currently targets MSVC with vcpkg protobuf.");
        }

        var compiler = CppCompiler.LocateMsvc();
        if (compiler is null)
        {
            Assert.Skip("No Visual Studio C++ Build Tools installation found.");
        }

        var protobuf = LocateProtobufCppInstall();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ headers found. Install protobuf with vcpkg or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to their include directory.");
        }

        if (protobuf.ProtocPath is null
            || protobuf.LibraryDirectory is null
            || protobuf.BinaryDirectory is null)
        {
            Assert.Skip(
                "The link-and-run C++ smoke test requires a vcpkg protobuf install with "
                + "include, lib, bin, and tools/protobuf/protoc.exe directories.");
        }

        var workspace = PrepareSmokeWorkspace(protobuf.ProtocPath);
        var runResult = compiler.BuildAndRunWithVcpkgProtobuf(workspace, protobuf);

        Assert.True(
            runResult.ExitCode == 0,
            $"C++ link-and-run smoke test failed with {compiler.DisplayName}.{Environment.NewLine}{runResult.Output}");
    }

    private static CppSmokeWorkspace PrepareSmokeWorkspace(string protoc)
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-cpp-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        // Guard against a vacuous pass: EmitTests returns nothing when the source declares no
        // tests, which would leave a driver that compiles and exits 0 without calling anything.
        Assert.NotEmpty(result.Module!.Tests);

        var backend = new CppBackend();
        var options = new BackendOptions(Path.GetFileName(TestPaths.SimpleScript));
        var diagnostics = new DiagnosticBag();

        var files = backend.Emit(result.Module!, options, diagnostics);

        // The driver is generated from the `test` blocks in the ProtoLang source rather than
        // hand-written here, so everything this test compiles is compiler output. That matters for
        // more than tidiness: a hand-written driver hardcodes the emitted namespace, the method
        // naming, and the free-function calling convention, so changing any of those would break
        // the driver and have to be fixed by editing C++ in this file. Generating it means the
        // driver follows the backend, and a failure here is a real regression rather than a stale
        // copy of what the backend used to emit.
        var testFiles = backend.EmitTests(result.Module!, options, diagnostics);

        Assert.Empty(diagnostics);
        GeneratedSourceGuards.AssertExercisesControlFlow("C++", "for (", files);

        foreach (var file in files.Concat(testFiles))
        {
            File.WriteAllText(Path.Combine(directory, file.RelativePath), file.Contents);
        }

        var protocResult = RunProtocCpp(protoc, directory);
        Assert.True(
            protocResult.ExitCode == 0,
            $"protoc C++ generation failed.{Environment.NewLine}{protocResult.Output}");

        var driver = Assert.Single(
            testFiles,
            file => file.RelativePath.EndsWith(".cc", StringComparison.Ordinal));

        return new CppSmokeWorkspace(
            directory,
            Path.Combine(directory, driver.RelativePath),
            Path.Combine(directory, "invoice.pb.cc"));
    }

    private static ProcessResult RunProtocCpp(string protoc, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo(protoc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add($"--proto_path={TestPaths.ExampleProtoDirectory}");
        startInfo.ArgumentList.Add($"--cpp_out={outputDirectory}");
        startInfo.ArgumentList.Add("invoice.proto");

        return Run(startInfo);
    }

    private static ProtobufCppInstall? LocateProtobufCppInstall()
    {
        var configured = Environment.GetEnvironmentVariable("PROTOLANG_PROTOBUF_CPP_INCLUDE");
        if (!string.IsNullOrWhiteSpace(configured) && ContainsProtobufCppHeaders(configured))
        {
            return new ProtobufCppInstall(configured, null, null, null);
        }

        IReadOnlyList<string> candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ?
            [
                .. VcpkgInstalledIncludeCandidates(),
                @"C:\vcpkg\installed\x64-windows\include",
                @"C:\tools\vcpkg\installed\x64-windows\include",
                @"C:\Program Files\protobuf\include",
                @"C:\Program Files (x86)\protobuf\include",
            ]
            :
            [
                .. VcpkgInstalledIncludeCandidates(),
                "/usr/local/include",
                "/usr/include",
                "/opt/homebrew/include",
            ];

        foreach (var candidate in candidates)
        {
            if (ContainsProtobufCppHeaders(candidate))
            {
                return CreateProtobufCppInstall(candidate);
            }
        }

        return null;
    }

    private static ProtobufCppInstall CreateProtobufCppInstall(string includeDirectory)
    {
        var tripletDirectory = Directory.GetParent(includeDirectory);
        if (tripletDirectory is null)
        {
            return new ProtobufCppInstall(includeDirectory, null, null, null);
        }

        var lib = Path.Combine(tripletDirectory.FullName, "lib");
        var bin = Path.Combine(tripletDirectory.FullName, "bin");

        return new ProtobufCppInstall(
            includeDirectory,
            LocateProtocBesideVcpkgInclude(includeDirectory),
            File.Exists(Path.Combine(lib, "libprotobuf.lib")) ? lib : null,
            File.Exists(Path.Combine(bin, "libprotobuf.dll")) ? bin : null);
    }

    private static bool ContainsProtobufCppHeaders(string includeDirectory)
        => File.Exists(Path.Combine(includeDirectory, "google", "protobuf", "message.h"));

    private static string? LocateProtocBesideVcpkgInclude(string includeDirectory)
    {
        var tripletDirectory = Directory.GetParent(includeDirectory);
        if (tripletDirectory is null)
        {
            return null;
        }

        var protoc = Path.Combine(tripletDirectory.FullName, "tools", "protobuf", ProtocExecutableName);
        return File.Exists(protoc) ? protoc : null;
    }

    private static string ProtocExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "protoc.exe" : "protoc";

    private static IEnumerable<string> VcpkgInstalledIncludeCandidates()
    {
        var roots = new List<string>();

        AddIfSet(roots, "VCPKG_INSTALLED_DIR");

        var repoLocal = Path.Combine(TestPaths.RepositoryRoot, "vcpkg_installed");
        if (Directory.Exists(repoLocal))
        {
            roots.Add(repoLocal);
        }

        var vcpkgRoot = Environment.GetEnvironmentVariable("VCPKG_ROOT");
        if (!string.IsNullOrWhiteSpace(vcpkgRoot))
        {
            roots.Add(Path.Combine(vcpkgRoot, "installed"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var triplet in CandidateVcpkgTriplets())
            {
                yield return Path.Combine(root, triplet, "include");
            }
        }
    }

    private static IEnumerable<string> CandidateVcpkgTriplets()
    {
        var configured = Environment.GetEnvironmentVariable("VCPKG_DEFAULT_TRIPLET");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return "x64-windows";
            yield return "x64-windows-static";
            yield return "x64-windows-static-md";
            yield return "x86-windows";
            yield return "arm64-windows";
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "arm64-osx"
                : "x64-osx";
            yield break;
        }

        yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "arm64-linux"
            : "x64-linux";
    }

    private static void AddIfSet(List<string> values, string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private sealed record ProtobufCppInstall(
        string IncludeDirectory,
        string? ProtocPath,
        string? LibraryDirectory,
        string? BinaryDirectory);

    private sealed record CppSmokeWorkspace(
        string Directory,
        string DriverSourcePath,
        string ProtobufSourcePath);

    private static ProcessResult Run(ProcessStartInfo startInfo)
    {
        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, ex.Message);
        }

        using var process = started ?? throw new InvalidOperationException("Process.Start returned null.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout + stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed record CppCompiler(
        string FileName,
        CppCompilerKind Kind,
        string DisplayName,
        string? VisualStudioDevCommand = null)
    {
        public static CppCompiler? Locate()
        {
            if (FindOnPath("clang++") is { } clang)
            {
                return new CppCompiler(clang, CppCompilerKind.ClangOrGcc, "clang++");
            }

            if (FindOnPath("g++") is { } gcc)
            {
                return new CppCompiler(gcc, CppCompilerKind.ClangOrGcc, "g++");
            }

            return LocateMsvc();
        }

        public static CppCompiler? LocateMsvc()
        {
            var msvc = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? FindOnPath("cl.exe")
                : FindOnPath("cl");

            if (msvc is not null)
            {
                return new CppCompiler(msvc, CppCompilerKind.Msvc, "cl.exe");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && FindVisualStudioDevCommand() is { } devCommand)
            {
                return new CppCompiler("cl.exe", CppCompilerKind.Msvc, "MSVC via VsDevCmd.bat", devCommand);
            }

            return null;
        }

        public ProcessResult RunSyntaxOnly(string sourcePath, string generatedInclude, string protobufInclude)
        {
            switch (Kind)
            {
                case CppCompilerKind.ClangOrGcc:
                {
                    var startInfo = CreateStartInfo(FileName);
                    startInfo.ArgumentList.Add("-std=c++20");
                    startInfo.ArgumentList.Add("-fsyntax-only");
                    startInfo.ArgumentList.Add($"-I{generatedInclude}");
                    startInfo.ArgumentList.Add($"-I{protobufInclude}");
                    startInfo.ArgumentList.Add(sourcePath);
                    return Run(startInfo);
                }

                case CppCompilerKind.Msvc:
                {
                    if (VisualStudioDevCommand is not null)
                    {
                        return RunMsvcViaDevCommand(sourcePath, generatedInclude, protobufInclude);
                    }

                    var msvcStartInfo = CreateStartInfo(FileName);
                    msvcStartInfo.ArgumentList.Add("/nologo");
                    msvcStartInfo.ArgumentList.Add("/std:c++20");
                    msvcStartInfo.ArgumentList.Add("/Zc:__cplusplus");
                    msvcStartInfo.ArgumentList.Add("/EHsc");
                    msvcStartInfo.ArgumentList.Add("/Zs");
                    msvcStartInfo.ArgumentList.Add($"/I{generatedInclude}");
                    msvcStartInfo.ArgumentList.Add($"/I{protobufInclude}");
                    msvcStartInfo.ArgumentList.Add(sourcePath);
                    return Run(msvcStartInfo);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown compiler kind.");
            }
        }

        public ProcessResult BuildAndRunWithVcpkgProtobuf(
            CppSmokeWorkspace workspace,
            ProtobufCppInstall protobuf)
        {
            if (Kind != CppCompilerKind.Msvc
                || protobuf.LibraryDirectory is null
                || protobuf.BinaryDirectory is null)
            {
                throw new InvalidOperationException("Only MSVC with vcpkg protobuf is supported here.");
            }

            return VisualStudioDevCommand is null
                ? RunMsvcLinkAndRun(workspace, protobuf, null)
                : RunMsvcLinkAndRun(workspace, protobuf, VisualStudioDevCommand);
        }

        private ProcessResult RunMsvcViaDevCommand(
            string sourcePath,
            string generatedInclude,
            string protobufInclude)
        {
            var responsePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "msvc-syntax.rsp");
            File.WriteAllLines(
                responsePath,
                [
                    "/nologo",
                    "/std:c++20",
                    "/Zc:__cplusplus",
                    "/EHsc",
                    "/Zs",
                    $"/I{generatedInclude}",
                    $"/I{protobufInclude}",
                    sourcePath,
                ]);

            var scriptPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "msvc-syntax.cmd");
            File.WriteAllLines(
                scriptPath,
                [
                    "@echo off",
                    $"call \"{VisualStudioDevCommand}\" -arch=x64 -host_arch=x64",
                    "if errorlevel 1 exit /b %errorlevel%",
                    $"cl.exe @{QuoteForCmd(responsePath)}",
                ]);

            var startInfo = CreateStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(scriptPath);

            return Run(startInfo);
        }

        private ProcessResult RunMsvcLinkAndRun(
            CppSmokeWorkspace workspace,
            ProtobufCppInstall protobuf,
            string? visualStudioDevCommand)
        {
            var compileResponsePath = Path.Combine(workspace.Directory, "msvc-compile.rsp");
            File.WriteAllLines(
                compileResponsePath,
                [
                    "/nologo",
                    "/std:c++20",
                    "/Zc:__cplusplus",
                    "/EHsc",
                    "/c",
                    $"/I{QuoteRspArgument(workspace.Directory)}",
                    $"/I{QuoteRspArgument(protobuf.IncludeDirectory)}",
                    $"/Fo{QuoteRspArgument(workspace.Directory + Path.DirectorySeparatorChar)}",
                    QuoteRspArgument(workspace.DriverSourcePath),
                    QuoteRspArgument(workspace.ProtobufSourcePath),
                ]);

            // MSVC names each object after its source basename. Derive both rather than hardcoding
            // them: the driver filename comes from the backend now, not from this file.
            var driverObject = Path.Combine(
                workspace.Directory,
                Path.GetFileNameWithoutExtension(workspace.DriverSourcePath) + ".obj");
            var protobufObject = Path.Combine(
                workspace.Directory,
                Path.GetFileNameWithoutExtension(workspace.ProtobufSourcePath) + ".obj");

            var executablePath = Path.Combine(workspace.Directory, "protolang-cpp-smoke.exe");
            var linkResponsePath = Path.Combine(workspace.Directory, "msvc-link.rsp");
            File.WriteAllLines(
                linkResponsePath,
                [
                    "/nologo",
                    $"/out:{QuoteRspArgument(executablePath)}",
                    QuoteRspArgument(driverObject),
                    QuoteRspArgument(protobufObject),
                    $"/LIBPATH:{QuoteRspArgument(protobuf.LibraryDirectory!)}",
                    "libprotobuf.lib",
                    "abseil_dll.lib",
                    "utf8_validity.lib",
                    "utf8_range.lib",
                ]);

            var scriptPath = Path.Combine(workspace.Directory, "msvc-link-run.cmd");
            var lines = new List<string> { "@echo off" };
            if (visualStudioDevCommand is not null)
            {
                lines.Add($"call \"{visualStudioDevCommand}\" -arch=x64 -host_arch=x64");
                lines.Add("if errorlevel 1 exit /b %errorlevel%");
            }

            lines.Add($"cl.exe @{QuoteForCmd(compileResponsePath)}");
            lines.Add("if errorlevel 1 exit /b %errorlevel%");
            lines.Add($"link.exe @{QuoteForCmd(linkResponsePath)}");
            lines.Add("if errorlevel 1 exit /b %errorlevel%");
            lines.Add($"set \"PATH={protobuf.BinaryDirectory};%PATH%\"");
            lines.Add($"\"{executablePath}\"");
            lines.Add("exit /b %errorlevel%");
            File.WriteAllLines(scriptPath, lines);

            var startInfo = CreateStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(scriptPath);

            return Run(startInfo);
        }

        private static ProcessStartInfo CreateStartInfo(string fileName)
            => new(fileName)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

        private static string QuoteForCmd(string path) => "\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        private static string QuoteRspArgument(string argument)
            => argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument;

        private static string? FindOnPath(string executable)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), executable);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }

            return null;
        }

        private static string? FindVisualStudioDevCommand()
        {
            foreach (var candidate in VisualStudioDevCommandCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> VisualStudioDevCommandCandidates()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] versions = ["18", "2022", "17"];
            string[] editions = ["Community", "BuildTools", "Professional", "Enterprise"];

            foreach (var root in new[] { programFiles, programFilesX86 }.Where(root => !string.IsNullOrEmpty(root)))
            {
                foreach (var version in versions)
                {
                    foreach (var edition in editions)
                    {
                        yield return Path.Combine(
                            root,
                            "Microsoft Visual Studio",
                            version,
                            edition,
                            "Common7",
                            "Tools",
                            "VsDevCmd.bat");
                    }
                }
            }
        }
    }

    private enum CppCompilerKind
    {
        ClangOrGcc,
        Msvc,
    }
}
