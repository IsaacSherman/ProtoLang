using System.Xml.Linq;
using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Covers the build files emitted by <c>--scaffold</c>: the project that turns generated test
/// source into something a developer can actually run.
/// </summary>
/// <remarks>
/// The assertions on emitted content are guards, not the point. A build file is only correct if a
/// build system accepts it, so <see cref="ScaffoldExecutionTests"/> configures, builds, and runs
/// the emitted projects for real. Everything here exists to name a specific failure quickly when
/// that end-to-end test breaks.
/// </remarks>
public class ScaffoldTests
{
    private static ScaffoldOptions Options() => new(
        BehaviorDirectory: "../../csharp",
        ProtoFiles: [new ProtoFileReference("../../../examples/protos", "invoice.proto")],
        TestSourceFileNames: ["simpleScript.tests.g.cs"]);

    private static ScaffoldOptions CppOptions() => new(
        BehaviorDirectory: "../../cpp",
        ProtoFiles: [new ProtoFileReference("../../../examples/protos", "invoice.proto")],
        TestSourceFileNames: ["simpleScript.tests.cc"]);

    /// <summary>
    /// The emitted project must be well-formed XML. This is not a formality: an XML comment cannot
    /// contain a double hyphen, so mentioning a command-line flag inside one produces a project
    /// MSBuild refuses to load with MSB4025, and no string assertion would notice.
    /// </summary>
    [Fact]
    public void TheEmittedCSharpProjectIsWellFormedXml()
    {
        var project = CSharpTestProject.Build(Options());

        var parsed = Record.Exception(() => XDocument.Parse(project));
        Assert.True(parsed is null, $"the emitted project is not valid XML: {parsed?.Message}");
    }

    /// <summary>
    /// A consumer has no copy of this repository's central package versions, so the scaffold states
    /// them. That makes the pinned versions a second copy of something already declared, and this
    /// is what stops the two drifting apart.
    /// </summary>
    [Fact]
    public void EveryPinnedVersionMatchesCentralPackageManagement()
    {
        var central = XDocument.Load(TestPaths.DirectoryPackagesProps)
            .Descendants("PackageVersion")
            .ToDictionary(
                element => (string)element.Attribute("Include")!,
                element => (string)element.Attribute("Version")!,
                StringComparer.Ordinal);

        foreach (var (package, version) in CSharpTestProject.PackageVersions)
        {
            Assert.True(
                central.TryGetValue(package, out var expected),
                $"The scaffold pins '{package}', which Directory.Packages.props does not declare. "
                + "Either the repository stopped using it or the scaffold gained a package the "
                + "repository never builds against.");

            Assert.True(
                expected == version,
                $"The scaffold pins {package} {version}, but Directory.Packages.props declares "
                + $"{expected}. The emitted project would resolve a version this repository does "
                + "not test against.");
        }
    }

    /// <summary>
    /// The emitted project pins its own versions, which is an error under central package
    /// management. A consumer repository with a Directory.Packages.props above the output directory
    /// would otherwise fail restore with NU1008.
    /// </summary>
    [Fact]
    public void TheEmittedCSharpProjectOptsOutOfCentralPackageManagement()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options()));

        var value = project.Descendants("ManagePackageVersionsCentrally").SingleOrDefault()?.Value;
        Assert.Equal("false", value);

        // Every reference must carry a version, or opting out leaves them unresolvable.
        foreach (var reference in project.Descendants("PackageReference"))
        {
            Assert.True(
                reference.Attribute("Version") is not null,
                $"'{(string?)reference.Attribute("Include")}' has no version, and this project has "
                + "opted out of central package management.");
        }
    }

    /// <summary>
    /// An 'expect fail' test relaunches the test assembly to observe a process that terminates, so
    /// the project has to produce an executable.
    /// </summary>
    [Fact]
    public void TheEmittedCSharpProjectIsExecutable()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options()));
        Assert.Equal("Exe", project.Descendants("OutputType").SingleOrDefault()?.Value);
    }

    /// <summary>
    /// The harness project clears every NuGet source so a sandboxed conformance run cannot reach
    /// the network. That is right for the harness and wrong here: a consumer has to be able to
    /// restore. Nothing in the scaffold should reintroduce it.
    /// </summary>
    [Fact]
    public void TheScaffoldEmitsNoNuGetConfiguration()
    {
        var files = new CSharpBackend().EmitTestProject(Options(), new DiagnosticBag());

        Assert.DoesNotContain(
            files,
            file => file.RelativePath.Contains("nuget.config", StringComparison.OrdinalIgnoreCase));

        var project = string.Join("\n", files.Select(file => file.Contents));
        Assert.DoesNotContain("<packageSources>", project, StringComparison.Ordinal);
    }

    /// <summary>
    /// The harness builds generated code the hostile way -- warnings as errors, checked arithmetic
    /// -- to prove the compiler's output survives it. Imposing either on a consumer's project is a
    /// different thing, and not the scaffold's call to make.
    /// </summary>
    [Fact]
    public void TheEmittedCSharpProjectDoesNotImposeHarnessBuildSettings()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options()));

        Assert.Empty(project.Descendants("TreatWarningsAsErrors"));
        Assert.Empty(project.Descendants("CheckForOverflowUnderflow"));
    }

    /// <summary>
    /// Grpc.Tools runs protoc during the build, which is what removes the separate protoc step. It
    /// contributes nothing at runtime, so it must not flow to anything referencing this project.
    /// </summary>
    [Fact]
    public void TheEmittedCSharpProjectGeneratesProtobufClassesAtBuildTime()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options()));

        var protobuf = Assert.Single(project.Descendants("Protobuf"));
        Assert.Equal("../../../examples/protos/invoice.proto", (string?)protobuf.Attribute("Include"));
        Assert.Equal("../../../examples/protos", (string?)protobuf.Attribute("ProtoRoot"));
        Assert.Equal("None", (string?)protobuf.Attribute("GrpcServices"));

        var tools = Assert.Single(
            project.Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == "Grpc.Tools");
        Assert.Equal("all", (string?)tools.Attribute("PrivateAssets"));
    }

    /// <summary>The generated tests call into the behavior, so the project has to compile it.</summary>
    [Fact]
    public void TheEmittedCSharpProjectCompilesTheBehaviorDirectory()
    {
        var project = XDocument.Parse(CSharpTestProject.Build(Options()));

        var compile = Assert.Single(project.Descendants("Compile"));
        Assert.Equal("../../csharp/**/*.cs", (string?)compile.Attribute("Include"));
    }

    /// <summary>
    /// Emitted paths use forward slashes whatever platform the compiler ran on, so a project
    /// generated on Windows is not broken everywhere else.
    /// </summary>
    [Fact]
    public void EmittedPathsUseForwardSlashes()
    {
        var options = new ScaffoldOptions(
            BehaviorDirectory: @"..\..\csharp",
            ProtoFiles: [new ProtoFileReference(@"..\..\..\examples\protos", "invoice.proto")],
            TestSourceFileNames: ["simpleScript.tests.g.cs"]);

        Assert.DoesNotContain(@"\", CSharpTestProject.Build(options), StringComparison.Ordinal);

        var cpp = CppTestProject.Build(options with { BehaviorDirectory = @"..\..\cpp" });
        Assert.DoesNotContain(@"\", cpp, StringComparison.Ordinal);
    }

    /// <summary>
    /// MSVC reports __cplusplus as 199711L unless told otherwise, and the generated runtime header
    /// static_asserts on it. Without this flag the build fails claiming C++20 is unavailable while
    /// compiling as C++20.
    /// </summary>
    [Fact]
    public void TheEmittedCMakeProjectAsksMsvcToReportCplusplusCorrectly()
    {
        var cmake = CppTestProject.Build(CppOptions());

        Assert.Contains("if(MSVC)", cmake, StringComparison.Ordinal);
        Assert.Contains("/Zc:__cplusplus", cmake, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three include roots the generated driver needs. It includes its ProtoLang header
    /// unqualified, and that header includes protoc's output, so none of them is optional.
    /// </summary>
    [Fact]
    public void TheEmittedCMakeProjectSuppliesEveryIncludeRoot()
    {
        var cmake = CppTestProject.Build(CppOptions());

        Assert.Contains("\"${CMAKE_CURRENT_SOURCE_DIR}\"", cmake, StringComparison.Ordinal);
        Assert.Contains("\"${CMAKE_CURRENT_SOURCE_DIR}/../../cpp\"", cmake, StringComparison.Ordinal);
        Assert.Contains("\"${CMAKE_CURRENT_BINARY_DIR}\"", cmake, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONFIG mode, because protobuf::libprotobuf carries the Abseil and utf8_range dependencies
    /// that protobuf 4.x split out. Naming them by hand is the problem this avoids.
    /// </summary>
    [Fact]
    public void TheEmittedCMakeProjectFindsProtobufInConfigMode()
    {
        var cmake = CppTestProject.Build(CppOptions());

        Assert.Contains("find_package(Protobuf CONFIG REQUIRED)", cmake, StringComparison.Ordinal);
        Assert.Contains("protobuf::libprotobuf", cmake, StringComparison.Ordinal);
        Assert.Contains("protobuf_generate(", cmake, StringComparison.Ordinal);
    }

    /// <summary>Every driver needs a target and a ctest entry, or it is built and never run.</summary>
    [Fact]
    public void TheEmittedCMakeProjectRunsEveryDriver()
    {
        var options = CppOptions() with
        {
            TestSourceFileNames = ["simpleScript.tests.cc", "other.tests.cc"],
        };

        var cmake = CppTestProject.Build(options);

        Assert.Contains("enable_testing()", cmake, StringComparison.Ordinal);

        foreach (var target in new[] { "simpleScript_tests", "other_tests" })
        {
            Assert.Contains($"add_executable({target} ", cmake, StringComparison.Ordinal);
            Assert.Contains($"add_test(NAME {target} COMMAND {target})", cmake, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A schema imported by another schema still has code generated against it, so the build file
    /// has to name it too. Listing only the source's own <c>import proto</c> declarations produces a
    /// project that generates a type it never defines, which fails at build time rather than here.
    /// </summary>
    [Fact]
    public void TransitivelyImportedSchemasAreListed()
    {
        // cross_caller.proto imports cross_target.proto, which the ProtoLang source never names.
        var options = ScaffoldFor(
            """
            import proto "cross_caller.proto";

            extend Caller {
                fn target_value() -> int64 {
                    return target.value;
                }
            }
            """);

        Assert.Contains(options.ProtoFiles, file => file.RelativePath == "cross_caller.proto");
        Assert.Contains(options.ProtoFiles, file => file.RelativePath == "cross_target.proto");
    }

    /// <summary>
    /// The closure includes protobuf's own schemas when something imports one. Both targets'
    /// runtimes ship those already compiled, so regenerating them would define the same types twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The well-known descriptor is supplied directly rather than through a fixture that imports
    /// one, because protoc as located by <c>ProtocLocator</c> cannot currently resolve
    /// <c>google/protobuf/*.proto</c> without the caller passing its include directory. Injecting
    /// the descriptor tests the filter itself and does not depend on that being fixed.
    /// </para>
    /// <para>
    /// The include paths deliberately contain a directory holding a <c>google/protobuf</c> schema,
    /// which is what a project vendoring its own copies looks like. Without that, the descriptor
    /// resolves under no search path and is dropped for that reason instead, so the test would pass
    /// with the well-known-type filter deleted.
    /// </para>
    /// </remarks>
    [Fact]
    public void WellKnownTypesAreNotRegenerated()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "cross_caller.proto";

            extend Caller {
                fn target_value() -> int64 {
                    return target.value;
                }
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var directory = Path.GetDirectoryName(path)!;

        // A proto root that really does contain the well-known schema, so only the filter keeps it
        // out of the emitted project.
        var vendored = Path.Combine(directory, "vendored");
        Directory.CreateDirectory(Path.Combine(vendored, "google", "protobuf"));
        File.WriteAllText(Path.Combine(vendored, "google", "protobuf", "timestamp.proto"), string.Empty);

        var options = ScaffoldOptions.Create(
            path,
            [vendored, TestPaths.FixtureProtoDirectory],
            [Google.Protobuf.WellKnownTypes.Timestamp.Descriptor.File, .. result.Descriptors],
            Path.Combine(directory, "generated", "csharp"),
            Path.Combine(directory, "generated", "tests", "csharp"),
            []);

        Assert.Contains(options.ProtoFiles, file => file.RelativePath == "cross_caller.proto");
        Assert.DoesNotContain(
            options.ProtoFiles,
            file => file.RelativePath.StartsWith("google/protobuf/", StringComparison.Ordinal));
    }

    /// <summary>Compiles a source against the fixture schemas and builds its scaffold options.</summary>
    private static ScaffoldOptions ScaffoldFor(string source)
    {
        var path = TestPaths.WriteTempScript(source);
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var directory = Path.GetDirectoryName(path)!;
        return ScaffoldOptions.Create(
            path,
            [TestPaths.FixtureProtoDirectory],
            result.Descriptors,
            Path.Combine(directory, "generated", "csharp"),
            Path.Combine(directory, "generated", "tests", "csharp"),
            []);
    }

    /// <summary>
    /// A C# test backend emits a support file beside its drivers. CMake must not try to build an
    /// executable out of one.
    /// </summary>
    [Fact]
    public void TheEmittedCMakeProjectIgnoresNonCppSources()
    {
        var options = CppOptions() with
        {
            TestSourceFileNames = ["simpleScript.tests.cc", "ProtoLangTestSupport.g.cs"],
        };

        var cmake = CppTestProject.Build(options);

        Assert.Contains("add_executable(simpleScript_tests ", cmake, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtoLangTestSupport", cmake, StringComparison.Ordinal);
    }
}
