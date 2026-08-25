using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Tests.Harness;

namespace ProtoLang.Tests.Conformance;

/// <summary>
/// Compiles the whole conformance corpus for every backend, builds the generated code, and runs it
/// once. The results are shared by the assertions in <see cref="ConformanceTests"/>, because
/// building and executing real C# and C++ is far too expensive to repeat per assertion.
/// </summary>
public sealed class ConformanceFixture
{
    internal IReadOnlyList<string> DeclaredIdentities { get; }

    internal ConformanceRun CSharp { get; }

    internal ConformanceRun Cpp { get; }

    public ConformanceFixture()
    {
        var modules = new List<(ConformanceVector Vector, IrModule Module)>();
        var failures = new List<string>();

        foreach (var vector in ConformanceVectors.All)
        {
            var result = ConformanceVectors.Compile(vector);
            if (result.Success)
            {
                modules.Add((vector, result.Module!));
                continue;
            }

            failures.Add($"{vector.Name}: " + string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        }

        DeclaredIdentities = modules
            .SelectMany(entry => ConformanceVectors.DeclaredIdentities(entry.Module.Tests))
            .ToList();

        if (failures.Count > 0)
        {
            // A corpus that does not compile is reported by ConformanceVectorTests, which runs
            // without this fixture. Reporting it again from every execution test would bury it.
            var reason = "the corpus did not compile: " + string.Join(" | ", failures);
            CSharp = ConformanceRun.Skipped("csharp", reason);
            Cpp = ConformanceRun.Skipped("cpp", reason);
            return;
        }

        CSharp = RunCSharp(modules);
        Cpp = RunCpp(modules);
    }

    private static ConformanceRun RunCSharp(IReadOnlyList<(ConformanceVector Vector, IrModule Module)> modules)
    {
        const string Backend = "csharp";

        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            return ConformanceRun.Skipped(
                Backend, "No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var protoc = Toolchain.LocateProtoc();
        if (protoc is null)
        {
            return ConformanceRun.Skipped(
                Backend, "No protoc executable found. Restore Grpc.Tools or install protoc.");
        }

        var backend = new CSharpBackend();
        var diagnostics = new DiagnosticBag();
        var files = EmitAll(modules, backend, diagnostics);

        if (diagnostics.HasErrors)
        {
            return ConformanceRun.Skipped(
                Backend, "code generation failed: " + string.Join("; ", diagnostics.Select(d => d.ToString())));
        }

        var workspace = CSharpTestWorkspace.Create("conformance-csharp");
        workspace.Write(files);

        var generated = workspace.GenerateProtobuf(
            protoc, ConformanceVectors.ProtoDirectory, ConformanceVectors.SchemaFileName);

        if (generated.ExitCode != 0)
        {
            return new ConformanceRun(
                Backend, null, [], workspace.Directory, "protoc C# generation failed." + generated.Output);
        }

        workspace.WriteProjectFiles();

        var run = workspace.RunTests(dotnet);
        var byName = run.Executed.ToDictionary(test => test.Name, StringComparer.Ordinal);

        var results = modules
            .SelectMany(entry => entry.Module.Tests)
            .Select(test => byName.TryGetValue(test.Identity, out var executed)
                ? new ConformanceResult(
                    test.Identity,
                    executed.Passed ? ConformanceOutcome.Passed : ConformanceOutcome.Failed,
                    executed.Outcome)
                : new ConformanceResult(test.Identity, ConformanceOutcome.Missing, "not present in the test log"))
            .ToList();

        return new ConformanceRun(Backend, null, results, workspace.Directory, run.Process.Output);
    }

    private static ConformanceRun RunCpp(IReadOnlyList<(ConformanceVector Vector, IrModule Module)> modules)
    {
        const string Backend = "cpp";

        var compiler = Toolchain.LocateCppCompiler();
        if (compiler is null)
        {
            return ConformanceRun.Skipped(
                Backend,
                "No C++ compiler found. Install clang++, g++, or Visual Studio C++ Build Tools.");
        }

        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf is null)
        {
            return ConformanceRun.Skipped(
                Backend,
                "No protobuf C++ install found. Run 'vcpkg install' or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to the include directory.");
        }

        if (!protobuf.CanLink)
        {
            return ConformanceRun.Skipped(
                Backend,
                "Running the conformance vectors needs a protobuf C++ install with include, lib, "
                + "bin, and tools/protobuf/protoc directories.");
        }

        var backend = new CppBackend();
        var diagnostics = new DiagnosticBag();
        var files = EmitAll(modules, backend, diagnostics);

        if (diagnostics.HasErrors)
        {
            return ConformanceRun.Skipped(
                Backend, "code generation failed: " + string.Join("; ", diagnostics.Select(d => d.ToString())));
        }

        var workspace = CppTestWorkspace.Create("conformance-cpp");
        workspace.Write(files);

        var generated = workspace.GenerateProtobuf(
            protobuf.ProtocPath!, ConformanceVectors.ProtoDirectory, ConformanceVectors.SchemaFileName);

        if (generated.ExitCode != 0)
        {
            return new ConformanceRun(
                Backend, null, [], workspace.Directory, "protoc C++ generation failed." + generated.Output);
        }

        var drivers = modules.Select(entry => entry.Vector.Name + ".tests.cc").ToList();
        var programs = workspace.BuildAndRun(compiler, protobuf, drivers, ["conformance.pb.cc"]);

        var results = new List<ConformanceResult>();
        var output = new List<string>();

        for (var i = 0; i < modules.Count; i++)
        {
            var program = programs[i];
            output.Add($"--- {drivers[i]} (exit code {program.ExitCode}) ---");
            output.Add(program.Output);

            results.AddRange(ReadDriverResults(modules[i].Module.Tests, program.Output));
        }

        return new ConformanceRun(
            Backend, null, results, workspace.Directory, string.Join(Environment.NewLine, output));
    }

    /// <summary>
    /// Reads one driver's output. Each declared test is looked up by its own identity rather than
    /// by parsing identities out of the driver's lines, so a test name containing bracket or
    /// parenthesis characters cannot confuse the match.
    /// </summary>
    private static IEnumerable<ConformanceResult> ReadDriverResults(
        IReadOnlyList<IrTest> tests,
        string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).ToList();
        var reported = lines.ToHashSet(StringComparer.Ordinal);

        foreach (var test in tests)
        {
            if (reported.Contains("[ok] " + test.Identity))
            {
                yield return new ConformanceResult(test.Identity, ConformanceOutcome.Passed, string.Empty);
                continue;
            }

            var failure = lines.FirstOrDefault(
                line => line.StartsWith("[FAIL] " + test.Identity, StringComparison.Ordinal));

            yield return failure is not null
                ? new ConformanceResult(test.Identity, ConformanceOutcome.Failed, failure)
                : new ConformanceResult(
                    test.Identity, ConformanceOutcome.Missing, "the driver never reported this test");
        }
    }

    /// <summary>
    /// Emits behavior and tests for every vector into one file set. The arithmetic and test support
    /// files are emitted once per vector and are identical every time, so they are deduplicated
    /// here: that is exactly the case their fixed file names exist to allow.
    /// </summary>
    private static IReadOnlyList<GeneratedFile> EmitAll(
        IReadOnlyList<(ConformanceVector Vector, IrModule Module)> modules,
        ITestBackend backend,
        DiagnosticBag diagnostics)
    {
        var byPath = new Dictionary<string, GeneratedFile>(StringComparer.Ordinal);

        foreach (var (vector, module) in modules)
        {
            var options = new BackendOptions(Path.GetFileName(vector.SourcePath));

            foreach (var file in backend.Emit(module, options, diagnostics)
                .Concat(backend.EmitTests(module, options, diagnostics)))
            {
                byPath[file.RelativePath] = file;
            }
        }

        return byPath.Values.ToList();
    }
}
