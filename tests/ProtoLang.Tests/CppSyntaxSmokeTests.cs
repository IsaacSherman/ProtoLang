using System.Runtime.InteropServices;
using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Diagnostics;
using ProtoLang.Tests.Harness;
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
        var compiler = Toolchain.LocateCppCompiler();
        if (compiler is null)
        {
            Assert.Skip(
                "No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools "
                + "to run C++ smoke tests.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ headers found. Install protobuf headers or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to their include directory.");
        }

        var protoc = protobuf.ProtocPath ?? Toolchain.LocateProtoc();
        if (protoc is null)
        {
            Assert.Skip("No protoc executable found. Restore Grpc.Tools or install protoc to run C++ smoke tests.");
        }

        var workspace = PrepareSmokeWorkspace(protoc, out var driver);

        var compileResult = workspace.RunSyntaxOnly(
            compiler,
            Path.Combine(workspace.Directory, driver),
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

        var compiler = Toolchain.LocateMsvc();
        if (compiler is null)
        {
            Assert.Skip("No Visual Studio C++ Build Tools installation found.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            Assert.Skip(
                "No protobuf C++ headers found. Install protobuf with vcpkg or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to their include directory.");
        }

        if (!protobuf.CanLink)
        {
            Assert.Skip(
                "The link-and-run C++ smoke test needs more than headers. "
                + protobuf.DescribeMissingLinkInputs());
        }

        var workspace = PrepareSmokeWorkspace(protobuf.ProtocPath!, out var driver);

        var runResult = Assert.Single(workspace.BuildAndRun(compiler, protobuf, [driver], ["invoice.pb.cc"]));

        Assert.True(
            runResult.Succeeded,
            $"C++ link-and-run smoke test failed with {compiler.DisplayName}.{Environment.NewLine}"
            + $"exit code {runResult.ExitCode}{Environment.NewLine}{runResult.Output}");
    }

    private static CppTestWorkspace PrepareSmokeWorkspace(string protoc, out string driverSource)
    {
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

        var workspace = CppTestWorkspace.Create("cpp-smoke");
        workspace.Write(files.Concat(testFiles));

        var protocResult = workspace.GenerateProtobuf(protoc, TestPaths.ExampleProtoDirectory, "invoice.proto");
        Assert.True(
            protocResult.ExitCode == 0,
            $"protoc C++ generation failed.{Environment.NewLine}{protocResult.Output}");

        driverSource = Assert.Single(
            testFiles,
            file => file.RelativePath.EndsWith(".cc", StringComparison.Ordinal)).RelativePath;

        return workspace;
    }
}
