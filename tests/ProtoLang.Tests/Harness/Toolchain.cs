using System.Runtime.InteropServices;
using ProtoLang.Binding;

namespace ProtoLang.Tests.Harness;

internal enum CppCompilerKind
{
    ClangOrGcc,
    Msvc,
}

/// <summary>A protobuf C++ install: headers, and for linking, libraries and runtime binaries.</summary>
internal sealed record ProtobufCppInstall(
    string IncludeDirectory,
    string? ProtocPath,
    string? LibraryDirectory,
    string? BinaryDirectory)
{
    /// <summary>Whether this install has everything needed to link and run, not just to parse.</summary>
    public bool CanLink => ProtocPath is not null && LibraryDirectory is not null && BinaryDirectory is not null;
}

internal sealed record CppCompiler(
    string FileName,
    CppCompilerKind Kind,
    string DisplayName,
    string? VisualStudioDevCommand = null);

/// <summary>
/// Locates the external toolchain the C# and C++ backend suites need. Every lookup returns null
/// rather than throwing, so callers can skip with an explanatory message instead of failing on a
/// machine that simply does not have the tool.
/// </summary>
internal static class Toolchain
{
    public static string ProtocExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "protoc.exe" : "protoc";

    public static string? LocateProtoc() => ProtocLocator.Locate();

    public static string? LocateDotnet()
    {
        // The SDK exports this for child processes of a test run, and it is the only entry that is
        // guaranteed to be the host actually running us.
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        return ProcessRunner.FindOnPath(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
    }

    public static CppCompiler? LocateCppCompiler()
    {
        if (ProcessRunner.FindOnPath("clang++") is { } clang)
        {
            return new CppCompiler(clang, CppCompilerKind.ClangOrGcc, "clang++");
        }

        if (ProcessRunner.FindOnPath("g++") is { } gcc)
        {
            return new CppCompiler(gcc, CppCompilerKind.ClangOrGcc, "g++");
        }

        return LocateMsvc();
    }

    public static CppCompiler? LocateMsvc()
    {
        var msvc = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProcessRunner.FindOnPath("cl.exe")
            : ProcessRunner.FindOnPath("cl");

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

    public static ProtobufCppInstall? LocateProtobufCpp()
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

    /// <summary>Runs protoc for one language over one or more schemas below a proto path.</summary>
    public static ProcessResult RunProtoc(
        string protoc,
        string outputFlag,
        string protoPath,
        string outputDirectory,
        params string[] protoFiles)
    {
        var startInfo = ProcessRunner.Create(protoc);
        startInfo.ArgumentList.Add($"--proto_path={protoPath}");
        startInfo.ArgumentList.Add($"--{outputFlag}={outputDirectory}");

        foreach (var file in protoFiles)
        {
            startInfo.ArgumentList.Add(file);
        }

        return ProcessRunner.Run(startInfo);
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

    private static IEnumerable<string> VcpkgInstalledIncludeCandidates()
    {
        var roots = new List<string>();

        var installedDir = Environment.GetEnvironmentVariable("VCPKG_INSTALLED_DIR");
        if (!string.IsNullOrWhiteSpace(installedDir))
        {
            roots.Add(installedDir);
        }

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
            yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-osx" : "x64-osx";
            yield break;
        }

        yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-linux" : "x64-linux";
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
                        root, "Microsoft Visual Studio", version, edition, "Common7", "Tools", "VsDevCmd.bat");
                }
            }
        }
    }
}
