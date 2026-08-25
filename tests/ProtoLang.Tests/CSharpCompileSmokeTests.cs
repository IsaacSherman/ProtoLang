using ProtoLang.Backend;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Compiles the generated C# together with protoc's own C# output, then executes the unit tests
/// ProtoLang generated from the <c>test</c> blocks in the source.
/// </summary>
/// <remarks>
/// The mirror of <see cref="CppSyntaxSmokeTests"/> for the C# backend. Everything fed to the
/// compiler here is compiler output: the behavior comes from <see cref="CSharpBackend.Emit"/> and
/// the test driver from <see cref="CSharpBackend.EmitTests"/>, so a change to the emitted namespace,
/// method naming, or integration shape is followed automatically rather than needing C# in this
/// file to be edited to match.
/// </remarks>
public class CSharpCompileSmokeTests
{
    [Fact]
    public void GeneratedCSharpCompilesAgainstProtocOutput()
    {
        var workspace = PrepareWorkspace(out var dotnet);

        var result = workspace.Build(dotnet);

        Assert.True(
            result.ExitCode == 0,
            $"Generated C# failed to compile.{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public void GeneratedCSharpTestsPassWhenExecuted()
    {
        var workspace = PrepareWorkspace(out var dotnet);

        var run = workspace.RunTests(dotnet);

        Assert.True(
            run.Process.ExitCode == 0,
            $"Generated C# unit tests failed.{Environment.NewLine}{run.Process.Output}");

        // A run that discovers nothing also exits 0 and prints "Passed!", so check the count rather
        // than the word.
        var executed = run.Executed.Count > 0 ? run.Executed.Count : run.PassedCount;
        Assert.True(
            executed > 0,
            $"The generated test project ran no tests.{Environment.NewLine}{run.Process.Output}");
        Assert.All(run.Executed, test => Assert.True(test.Passed, $"'{test.Name}' was {test.Outcome}."));
    }

    private static CSharpTestWorkspace PrepareWorkspace(out string dotnet)
    {
        dotnet = Toolchain.LocateDotnet() ?? string.Empty;
        if (string.IsNullOrEmpty(dotnet))
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var protoc = Toolchain.LocateProtoc();
        if (protoc is null)
        {
            Assert.Skip("No protoc executable found. Restore Grpc.Tools or install protoc.");
        }

        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        // Guard against a vacuous pass: EmitTests returns nothing when the source declares no
        // tests, which would leave a project that builds and reports success without asserting.
        Assert.NotEmpty(result.Module!.Tests);

        var backend = new CSharpBackend();
        var options = new BackendOptions(Path.GetFileName(TestPaths.SimpleScript));
        var diagnostics = new DiagnosticBag();

        var files = backend.Emit(result.Module!, options, diagnostics);
        var testFiles = backend.EmitTests(result.Module!, options, diagnostics);

        Assert.Empty(diagnostics);
        Assert.NotEmpty(testFiles);
        GeneratedSourceGuards.AssertExercisesControlFlow("C#", "foreach (", files);

        var workspace = CSharpTestWorkspace.Create("csharp-smoke");
        workspace.Write(files.Concat(testFiles));

        var protocResult = workspace.GenerateProtobuf(protoc, TestPaths.ExampleProtoDirectory, "invoice.proto");
        Assert.True(
            protocResult.ExitCode == 0,
            $"protoc C# generation failed.{Environment.NewLine}{protocResult.Output}");

        workspace.WriteProjectFiles();

        return workspace;
    }
}
