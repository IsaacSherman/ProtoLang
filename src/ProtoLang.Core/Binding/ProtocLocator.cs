using System.Runtime.InteropServices;

namespace ProtoLang.Binding;

/// <summary>
/// Finds a <c>protoc</c> executable. Spec 21.1 requires the compiler to consume protobuf
/// descriptors rather than reparsing <c>.proto</c> files, which means we need a real protoc.
/// </summary>
public static class ProtocLocator
{
    public const string OverrideEnvironmentVariable = "PROTOLANG_PROTOC";

    /// <summary>
    /// Resolves protoc by probing, in order: the <c>PROTOLANG_PROTOC</c> environment variable,
    /// the system PATH, and finally a Grpc.Tools package in the local NuGet cache.
    /// </summary>
    /// <returns>The full path to protoc, or null if none was found.</returns>
    public static string? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var onPath = FindOnSystemPath();
        if (onPath is not null)
        {
            return onPath;
        }

        return FindBundledProtoc();
    }

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "protoc.exe" : "protoc";

    private static string? FindOnSystemPath()
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
                var candidate = Path.Combine(directory.Trim(), ExecutableName);
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

    /// <summary>
    /// Every NuGet package root to search, most specific first. <c>NUGET_PACKAGES</c> overrides the
    /// default location, and build agents and sandboxes routinely set it, so probing only the
    /// current user profile finds nothing when the restore went elsewhere.
    /// </summary>
    public static IEnumerable<string> GetNuGetPackageRoots()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".nuget", "packages");
        }

        // GetFolderPath can come back empty on some Unix configurations; HOME still works there.
        var unixHome = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(unixHome) && unixHome != home)
        {
            yield return Path.Combine(unixHome, ".nuget", "packages");
        }
    }

    /// <summary>
    /// The directories holding the well-known .proto schemas that ship alongside a protoc install,
    /// suitable for passing to protoc as additional --proto_path entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// protoc only resolves google/protobuf/*.proto from descriptors compiled into the binary from
    /// version 33 onwards. Older builds -- including the Grpc.Tools one <see cref="FindBundledProtoc"/>
    /// falls back to -- need the schemas passed on the command line, so a schema importing Timestamp
    /// fails to compile on the toolchain that requires no installation at all.
    /// </para>
    /// <para>
    /// Grpc.Tools solves this for its own protoc runs the same way, passing its bundled include
    /// directory as Protobuf_StandardImportsPath on every invocation. The package ships those
    /// schemas because its protoc needs them.
    /// </para>
    /// </remarks>
    /// <param name="protocPath">Full path to a protoc executable.</param>
    public static IReadOnlyList<string> FindWellKnownTypeIncludePaths(string protocPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(protocPath));
        if (string.IsNullOrEmpty(directory))
        {
            return [];
        }

        var candidates = new[]
        {
            // The layout of protoc's own release archives: bin/protoc beside include/.
            Path.Combine(directory, "..", "include"),

            // Grpc.Tools puts protoc at <version>/tools/<rid>/ and the schemas at
            // <version>/build/native/include.
            Path.Combine(directory, "..", "..", "build", "native", "include"),
        };

        var found = new List<string>();

        foreach (var candidate in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                continue;
            }

            // Require a schema every protobuf distribution carries rather than trusting the
            // directory name, so a wrong guess never becomes a --proto_path pointing at nothing.
            if (File.Exists(Path.Combine(full, "google", "protobuf", "descriptor.proto"))
                && !found.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(full);
            }
        }

        return found;
    }

    /// <summary>
    /// Grpc.Tools ships prebuilt protoc binaries per RID. If the package is already restored we
    /// can use it without asking the developer to install protoc separately.
    /// </summary>
    /// <remarks>
    /// Public because this is the protoc a machine with nothing installed actually gets, and it is
    /// the one that cannot resolve well-known imports on its own. A test that means to cover that
    /// path has to name it: whichever protoc happens to be first on PATH may be recent enough to
    /// resolve them unaided, which would make the test pass without proving anything.
    /// </remarks>
    public static string? FindBundledProtoc()
    {
        var rid = GetRuntimeIdentifier();
        if (rid is null)
        {
            return null;
        }

        foreach (var packageRoot in GetNuGetPackageRoots())
        {
            var toolsRoot = Path.Combine(packageRoot, "grpc.tools");
            if (!Directory.Exists(toolsRoot))
            {
                continue;
            }

            // Prefer the highest version present so the descriptor format stays current.
            var versions = Directory.GetDirectories(toolsRoot)
                .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase);

            foreach (var version in versions)
            {
                var candidate = Path.Combine(version, "tools", rid, ExecutableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return architecture switch
            {
                Architecture.X64 or Architecture.Arm64 => "windows_x64",
                Architecture.X86 => "windows_x86",
                _ => null,
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return architecture switch
            {
                Architecture.X64 => "linux_x64",
                Architecture.X86 => "linux_x86",
                Architecture.Arm64 => "linux_arm64",
                _ => null,
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return architecture switch
            {
                Architecture.X64 => "macosx_x64",
                // Grpc.Tools does not ship an arm64 macOS protoc; the x64 build runs under Rosetta.
                Architecture.Arm64 => "macosx_x64",
                _ => null,
            };
        }

        return null;
    }
}
