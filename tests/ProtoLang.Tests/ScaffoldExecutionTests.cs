using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Builds and runs the projects emitted by <c>--scaffold</c>, using nothing but the emitted files.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the scaffolding exists to pass. Asserting on emitted content proves a build
/// file says what was intended; only handing it to a real build system proves it works. Both
/// mistakes found while writing this feature were invisible to content assertions: an XML comment
/// containing a double hyphen, which MSBuild rejects outright, and a missing
/// <c>/Zc:__cplusplus</c>, without which MSVC fails a C++20 static assertion while compiling as
/// C++20.
/// </para>
/// <para>
/// Neither run is given a protoc invocation or a hand-written project. That is the claim: a
/// consumer runs the compiler once, then their language's normal build command.
/// </para>
/// </remarks>
public class ScaffoldExecutionTests
{
    [Fact]
    public void TheEmittedCSharpProjectBuildsAndPassesItsTests()
    {
        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var layout = ScaffoldLayout.Emit(new CSharpBackend(), "scaffold-csharp");

        var run = CSharpTestWorkspace.At(layout.TestDirectory).RunTests(dotnet);

        Assert.True(
            run.Process.ExitCode == 0,
            $"The scaffolded project failed to build or run.{Environment.NewLine}{run.Process.Output}");

        Assert.True(
            run.PassedCount == layout.ExpectedTestCount,
            $"Expected {layout.ExpectedTestCount} passing tests, saw {run.PassedCount?.ToString() ?? "none"}. "
            + "A run that discovers nothing also exits 0."
            + Environment.NewLine + run.Process.Output);
    }

    [Fact]
    public void TheEmittedCMakeProjectBuildsAndPassesItsTests()
    {
        var cmake = Toolchain.LocateCMake();
        if (cmake is null)
        {
            Assert.Skip("No cmake found. Install CMake or Visual Studio's C++ workload.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ install found. Run 'vcpkg install' or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to the include directory.");
        }

        if (Toolchain.LocateCppCompiler() is null)
        {
            Assert.Skip("No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
        }

        var layout = ScaffoldLayout.Emit(new CppBackend(), "scaffold-cpp");

        // The include directory's parent is the install prefix: find_package looks for the config
        // package under <prefix>/share, beside <prefix>/include.
        var prefix = Directory.GetParent(protobuf.IncludeDirectory)!.FullName;
        var buildDirectory = Path.Combine(layout.TestDirectory, "build");

        var configure = RunCMake(
            cmake, layout.TestDirectory,
            "-S", layout.TestDirectory, "-B", buildDirectory, "-DCMAKE_PREFIX_PATH=" + prefix);

        Assert.True(configure.ExitCode == 0, $"cmake configure failed.{Environment.NewLine}{configure.Output}");

        var build = RunCMake(cmake, layout.TestDirectory, "--build", buildDirectory, "--config", "Debug");
        Assert.True(build.ExitCode == 0, $"cmake build failed.{Environment.NewLine}{build.Output}");

        // -V rather than --output-on-failure, which a human would use: the driver's own summary is
        // asserted on below, and --output-on-failure prints nothing for a test that passed.
        var ctest = RunTool(
            CTestPath(cmake), layout.TestDirectory,
            "--test-dir", buildDirectory, "-C", "Debug", "-V");

        Assert.True(ctest.ExitCode == 0, $"ctest failed.{Environment.NewLine}{ctest.Output}");

        // ctest reports one entry per driver, and a driver that ran nothing also exits 0, so the
        // driver's own summary is what says the tests actually ran.
        Assert.True(
            ctest.Output.Contains($"protolang: {layout.ExpectedTestCount} test(s), 0 failed", StringComparison.Ordinal),
            $"The driver did not report {layout.ExpectedTestCount} passing tests."
            + Environment.NewLine + ctest.Output);
    }

    /// <summary>
    /// A project whose ProtoLang source extends only well-known types has no schemas to generate,
    /// and must still configure. Content assertions cannot show this: the failure is protobuf's own
    /// CMake helper rejecting a target with no .proto files, which only cmake itself reports.
    /// </summary>
    /// <remarks>
    /// Built from a stub driver rather than from a real compilation, so it does not depend on protoc
    /// being able to resolve <c>google/protobuf/*.proto</c> in the first place -- which is a separate
    /// open problem, and would otherwise make this test skip on exactly the toolchain it targets.
    /// </remarks>
    [Fact]
    public void TheEmittedCMakeProjectConfiguresWithNoSchemasOfItsOwn()
    {
        var cmake = Toolchain.LocateCMake();
        if (cmake is null)
        {
            Assert.Skip("No cmake found. Install CMake or Visual Studio's C++ workload.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ install found. Run 'vcpkg install' or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to the include directory.");
        }

        if (Toolchain.LocateCppCompiler() is null)
        {
            Assert.Skip("No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
        }

        var directory = Path.Combine(
            Path.GetTempPath(), "protolang-scaffold-noschema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "behavior"));

        File.WriteAllText(Path.Combine(directory, "stub.tests.cc"), "int main() { return 0; }" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(directory, CppTestProject.FileName),
            CppTestProject.Build(new ScaffoldOptions("behavior", [], ["stub.tests.cc"])));

        var configure = RunCMake(
            cmake, directory,
            "-S", directory,
            "-B", Path.Combine(directory, "build"),
            "-DCMAKE_PREFIX_PATH=" + Directory.GetParent(protobuf.IncludeDirectory)!.FullName);

        Assert.True(
            configure.ExitCode == 0,
            $"cmake rejected a project with no schemas of its own.{Environment.NewLine}{configure.Output}");
    }

    private static ProcessResult RunCMake(string cmake, string workingDirectory, params string[] arguments)
        => RunTool(cmake, workingDirectory, arguments);

    private static ProcessResult RunTool(string tool, string workingDirectory, params string[] arguments)
    {
        var startInfo = ProcessRunner.Create(tool, workingDirectory);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ProcessRunner.ScrubMsBuildEnvironment(startInfo);
        return ProcessRunner.Run(startInfo, TimeSpan.FromMinutes(10));
    }

    /// <summary>ctest ships beside cmake, so the located cmake also settles which ctest to use.</summary>
    private static string CTestPath(string cmake)
        => Path.Combine(
            Path.GetDirectoryName(cmake)!,
            Path.GetFileName(cmake).Replace("cmake", "ctest", StringComparison.Ordinal));
}

/// <summary>
/// Compiles the example and writes behavior, tests, and the emitted build file into the same
/// directory layout the CLI produces, so the relative paths under test are the real ones.
/// </summary>
/// <param name="ExpectedTestCount">
/// How many <c>test</c> declarations the compiled module holds. Taken from the module rather than
/// written down, so the assertion keeps its meaning -- a run that discovered nothing also exits 0 --
/// without pinning the suite to how many tests the example happens to declare today.
/// </param>
internal sealed record ScaffoldLayout(
    string Root,
    string BehaviorDirectory,
    string TestDirectory,
    int ExpectedTestCount)
{
    public static ScaffoldLayout Emit(ITestProjectScaffold backend, string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "protolang-" + label, Guid.NewGuid().ToString("N"));

        // Mirrors 'protolangc -o <root>/generated --test-out <root>/generated/tests'.
        var behaviorDirectory = Path.Combine(root, "generated", backend.Name);
        var testDirectory = Path.Combine(root, "generated", "tests", backend.Name);
        Directory.CreateDirectory(behaviorDirectory);
        Directory.CreateDirectory(testDirectory);

        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(
            result.Success,
            "the example did not compile: " + string.Join("; ", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var options = new BackendOptions(Path.GetFileName(TestPaths.SimpleScript));

        Write(behaviorDirectory, backend.Emit(result.Module!, options, diagnostics));

        var testFiles = backend.EmitTests(result.Module!, options, diagnostics);
        Write(testDirectory, testFiles);

        var scaffold = ScaffoldOptions.Create(
            TestPaths.SimpleScript,
            [TestPaths.ExampleProtoDirectory],
            result.Descriptors,
            behaviorDirectory,
            testDirectory,
            testFiles.Select(file => file.RelativePath).ToList());

        Write(testDirectory, backend.EmitTestProject(scaffold, diagnostics));

        Assert.False(
            diagnostics.HasErrors,
            "generation failed: " + string.Join("; ", diagnostics.Select(d => d.ToString())));

        return new ScaffoldLayout(
            root, behaviorDirectory, testDirectory, result.Module!.Tests.Count);
    }

    private static void Write(string directory, IEnumerable<GeneratedFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Contents);
        }
    }
}
