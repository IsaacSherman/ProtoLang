using System.Text.RegularExpressions;
using System.Xml.Linq;
using ProtoLang.Backend;

namespace ProtoLang.Tests.Harness;

/// <summary>One test the generated C# project actually executed, as reported by the test run.</summary>
internal sealed record ExecutedTest(string Name, string Outcome)
{
    public bool Passed => string.Equals(Outcome, "Passed", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Executed">
/// Per-test results parsed from the TRX log. Empty when no TRX was produced, in which case only
/// <paramref name="PassedCount"/> is available.
/// </param>
internal sealed record CSharpTestRun(
    ProcessResult Process,
    IReadOnlyList<ExecutedTest> Executed,
    int? PassedCount);

/// <summary>
/// A throwaway C# project that compiles ProtoLang's generated behavior and generated tests together
/// with protoc's C# output, then runs them.
/// </summary>
/// <remarks>
/// Everything the project compiles is compiler output. That matters for more than tidiness: a
/// hand-written driver would hardcode the emitted namespace, the method naming, and the integration
/// shape, so changing any of those would break the driver and have to be fixed by editing C# here.
/// Generating it means the driver follows the backend.
/// </remarks>
internal sealed class CSharpTestWorkspace
{
    private const string ProjectFileName = "GeneratedProtoLang.csproj";

    private CSharpTestWorkspace(string directory) => Directory = directory;

    public string Directory { get; }

    public string ResultsDirectory => Path.Combine(Directory, "TestResults");

    /// <summary>
    /// Adopts an existing directory instead of creating one, for a project the compiler's own
    /// scaffolding wrote rather than <see cref="WriteProjectFiles"/>. Only <see cref="RunTests"/>
    /// and <see cref="Build"/> are meaningful in that mode.
    /// </summary>
    public static CSharpTestWorkspace At(string directory) => new(directory);

    public static CSharpTestWorkspace Create(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-" + label, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new CSharpTestWorkspace(directory);
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
        => Toolchain.RunProtoc(protoc, "csharp_out", protoPath, Directory, protoFiles);

    public void WriteProjectFiles()
    {
        // Empty stubs stop MSBuild walking further up the temp directory tree and picking up
        // settings that have nothing to do with this project.
        File.WriteAllText(Path.Combine(Directory, "Directory.Build.props"), "<Project />" + Environment.NewLine);
        File.WriteAllText(Path.Combine(Directory, "Directory.Build.targets"), "<Project />" + Environment.NewLine);

        // Take the repository's central package versions rather than restating them here, so the
        // generated project cannot drift from what the repository actually builds against.
        File.Copy(
            TestPaths.DirectoryPackagesProps,
            Path.Combine(Directory, "Directory.Packages.props"),
            overwrite: true);

        // No package sources at all. Every package this project references is one the repository
        // already references at the same version, so the global packages folder has all of them and
        // restore has nothing to fetch. Clearing the sources makes that a guarantee rather than a
        // hope: the restore cannot reach the network, so it cannot fail because a machine is
        // offline, behind a proxy, or in a sandbox, and it cannot be slowed by nuget.org.
        //
        // Without this, restore contacts the configured source for vulnerability data, which fails
        // in a restricted environment. TreatWarningsAsErrors below then turns that NU1900 warning
        // into a hard error and the whole conformance run reads as a branch failure.
        File.WriteAllText(
            Path.Combine(Directory, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);

        File.WriteAllText(
            Path.Combine(Directory, ProjectFileName),
            """
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
                <OutputType>Exe</OutputType>
                <!--
                  Consumers may opt into checked arithmetic project-wide. ProtoLang states wrapping
                  per operation with unchecked(...) precisely so that setting cannot change the
                  emitted semantics, so build the generated code the hostile way.
                -->
                <CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>
                <!--
                  Hostile in the other direction too: generated code has to be droppable into a
                  project that treats warnings as errors, which this repository does. This escalates
                  NuGet warnings as well, which is why nuget.config clears the package sources:
                  otherwise an unreachable source turns an audit warning into a build failure.
                -->
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <!--
                  Nothing to audit against with no sources configured, and the repository already
                  audits these same packages at these same versions.
                -->
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Google.Protobuf" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <PackageReference Include="xunit.v3" />
                <PackageReference Include="xunit.runner.visualstudio" />
              </ItemGroup>

            </Project>
            """);
    }

    public ProcessResult Build(string dotnet) => RunDotnet(dotnet, "build", "--nologo", "-v", "quiet");

    public CSharpTestRun RunTests(string dotnet)
    {
        var result = RunDotnet(
            dotnet,
            "test",
            "--nologo",
            "-v",
            "quiet",
            "--results-directory",
            ResultsDirectory,
            "--logger",
            "trx;LogFileName=protolang.trx");

        return new CSharpTestRun(result, ReadTrx(), ReadPassedCount(result.Output));
    }

    private ProcessResult RunDotnet(string dotnet, params string[] arguments)
    {
        var startInfo = ProcessRunner.Create(dotnet, Directory);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ProcessRunner.ScrubMsBuildEnvironment(startInfo);

        // Belt and braces. ProtoLang's fail path exits cleanly rather than crashing, so nothing
        // here should reach the crash reporter, but a genuinely faulting child would otherwise stall
        // the run while a dump is collected.
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";

        return ProcessRunner.Run(startInfo);
    }

    /// <summary>
    /// Per-test outcomes from the TRX log. Returns an empty list when no TRX was written, which
    /// leaves callers on the coarser passed-count check rather than failing outright.
    /// </summary>
    private IReadOnlyList<ExecutedTest> ReadTrx()
    {
        if (!System.IO.Directory.Exists(ResultsDirectory))
        {
            return [];
        }

        var trx = System.IO.Directory.GetFiles(ResultsDirectory, "*.trx", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (trx is null)
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Load(trx);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        return document.Descendants(ns + "UnitTestResult")
            .Select(element => new ExecutedTest(
                (string?)element.Attribute("testName") ?? string.Empty,
                (string?)element.Attribute("outcome") ?? string.Empty))
            .ToList();
    }

    /// <summary>
    /// Fallback for a run that produced no TRX. A run that discovers nothing also exits 0 and
    /// prints "Passed!", so the count is the only part of that output worth reading.
    /// </summary>
    private static int? ReadPassedCount(string output)
    {
        var match = Regex.Match(output, @"Passed:\s*(\d+)");
        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
