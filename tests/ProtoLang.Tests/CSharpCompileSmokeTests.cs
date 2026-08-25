using System.Diagnostics;
using System.Runtime.InteropServices;
using ProtoLang.Backend;
using ProtoLang.Backend.CSharp;
using ProtoLang.Binding;
using ProtoLang.Diagnostics;
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
    /// <summary>How long a nested dotnet invocation may run before it is treated as hung.</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void GeneratedCSharpCompilesAgainstProtocOutput()
    {
        var workspace = PrepareWorkspace(out var dotnet);

        var result = RunDotnet(dotnet, workspace.Directory, "build", "--nologo", "-v", "quiet");

        Assert.True(
            result.ExitCode == 0,
            $"Generated C# failed to compile.{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public void GeneratedCSharpTestsPassWhenExecuted()
    {
        var workspace = PrepareWorkspace(out var dotnet);

        var result = RunDotnet(dotnet, workspace.Directory, "test", "--nologo", "-v", "quiet");

        Assert.True(
            result.ExitCode == 0,
            $"Generated C# unit tests failed.{Environment.NewLine}{result.Output}");

        // A run that discovers nothing also exits 0 and prints "Passed!", so check the count
        // rather than the word.
        var passed = System.Text.RegularExpressions.Regex.Match(result.Output, @"Passed:\s*(\d+)");
        Assert.True(
            passed.Success,
            $"Could not read a passed count from the test output.{Environment.NewLine}{result.Output}");
        Assert.True(
            int.Parse(passed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0,
            $"The generated test project ran no tests.{Environment.NewLine}{result.Output}");
    }

    private static CSharpSmokeWorkspace PrepareWorkspace(out string dotnet)
    {
        dotnet = LocateDotnet() ?? string.Empty;
        if (string.IsNullOrEmpty(dotnet))
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var protoc = ProtocLocator.Locate();
        if (protoc is null)
        {
            Assert.Skip("No protoc executable found. Restore Grpc.Tools or install protoc.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "protolang-csharp-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

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

        foreach (var file in files.Concat(testFiles))
        {
            File.WriteAllText(Path.Combine(directory, file.RelativePath), file.Contents);
        }

        var protocResult = RunProtocCSharp(protoc, directory);
        Assert.True(
            protocResult.ExitCode == 0,
            $"protoc C# generation failed.{Environment.NewLine}{protocResult.Output}");

        WriteProjectFiles(directory);

        return new CSharpSmokeWorkspace(directory);
    }

    private static void WriteProjectFiles(string directory)
    {
        // An empty Directory.Build.props stops MSBuild walking further up the temp directory tree
        // and picking up settings that have nothing to do with this project.
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />" + Environment.NewLine);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />" + Environment.NewLine);

        // Take the repository's central package versions rather than restating them here, so the
        // generated project cannot drift from what the repository actually builds against. This
        // also keeps the test working offline: those versions are the ones already restored.
        File.Copy(
            TestPaths.DirectoryPackagesProps,
            Path.Combine(directory, "Directory.Packages.props"),
            overwrite: true);

        File.WriteAllText(
            Path.Combine(directory, "GeneratedSmoke.csproj"),
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

    private static ProcessResult RunProtocCSharp(string protoc, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo(protoc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add($"--proto_path={TestPaths.ExampleProtoDirectory}");
        startInfo.ArgumentList.Add($"--csharp_out={outputDirectory}");
        startInfo.ArgumentList.Add("invoice.proto");

        return Run(startInfo);
    }

    private static ProcessResult RunDotnet(string dotnet, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The outer test run sets MSBuild and test-platform variables that confuse a nested
        // invocation, so clear the ones that leak.
        startInfo.Environment.Remove("MSBuildSDKsPath");
        startInfo.Environment.Remove("MSBuildExtensionsPath");
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("VSTEST_HOST_DEBUG");
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        return Run(startInfo);
    }

    private static string? LocateDotnet()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH");

        foreach (var entry in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(entry.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

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

        // Drain both pipes concurrently; reading them in sequence deadlocks once either fills.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            return new ProcessResult(-1, $"Timed out after {ProcessTimeout.TotalMinutes:0} minutes.");
        }

        var output = string.Join(Environment.NewLine, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        return new ProcessResult(process.ExitCode, output);
    }

    private sealed record CSharpSmokeWorkspace(string Directory);

    private sealed record ProcessResult(int ExitCode, string Output);
}
