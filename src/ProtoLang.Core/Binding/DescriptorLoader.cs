using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ProtoLang.Binding;

public sealed class DescriptorLoadException : Exception
{
    public DescriptorLoadException(string message) : base(message)
    {
    }
}

/// <summary>
/// Invokes protoc to produce a <c>FileDescriptorSet</c> and builds the runtime descriptor
/// objects the binder resolves names against.
/// </summary>
public sealed class DescriptorLoader
{
    private readonly string _protocPath;

    public DescriptorLoader(string protocPath)
    {
        _protocPath = protocPath;
        ImplicitIncludePaths = ProtocLocator.FindWellKnownTypeIncludePaths(protocPath);
    }

    /// <summary>
    /// Include directories this loader adds to every protoc run on top of the caller's, holding the
    /// well-known schemas that ship beside the located protoc. Empty when the install carries none.
    /// </summary>
    /// <remarks>
    /// Exposed because a caller that reports on unresolved imports has to search the same places
    /// protoc will. <see cref="Compilation"/> checks that every import exists before running protoc,
    /// and would otherwise reject an <c>import proto "google/protobuf/timestamp.proto"</c> that
    /// protoc resolves perfectly well.
    /// </remarks>
    public IReadOnlyList<string> ImplicitIncludePaths { get; }

    /// <summary>
    /// Creates a loader using <see cref="ProtocLocator"/>.
    /// </summary>
    /// <exception cref="DescriptorLoadException">No protoc executable could be found.</exception>
    public static DescriptorLoader CreateDefault()
    {
        var protoc = ProtocLocator.Locate();

        if (protoc is null)
        {
            var searched = string.Join(", ", ProtocLocator.GetNuGetPackageRoots());
            throw new DescriptorLoadException(
                "Could not find a 'protoc' executable. Install protoc and put it on PATH, set "
                + $"{ProtocLocator.OverrideEnvironmentVariable} to its full path, or restore the "
                + "Grpc.Tools NuGet package. "
                + $"Searched PATH and these package roots: {searched}");
        }

        return new DescriptorLoader(protoc);
    }

    /// <summary>
    /// Compiles <paramref name="protoFiles"/> into descriptors.
    /// </summary>
    /// <param name="protoFiles">Paths to .proto files, relative to one of the include paths.</param>
    /// <param name="includePaths">Directories passed to protoc as --proto_path.</param>
    public IReadOnlyList<FileDescriptor> Load(
        IReadOnlyList<string> protoFiles,
        IReadOnlyList<string> includePaths)
    {
        if (protoFiles.Count == 0)
        {
            return [];
        }

        var descriptorSetPath = Path.Combine(Path.GetTempPath(), $"protolang-{Guid.NewGuid():N}.desc");

        try
        {
            RunProtoc(protoFiles, includePaths, descriptorSetPath);

            var bytes = File.ReadAllBytes(descriptorSetPath);
            var set = FileDescriptorSet.Parser.ParseFrom(bytes);

            // --include_imports emits dependencies before dependents, which is exactly the order
            // BuildFromByteStrings requires.
            var serialized = set.File.Select(file => file.ToByteString()).ToList();

            try
            {
                return FileDescriptor.BuildFromByteStrings(serialized);
            }
            catch (Exception ex)
            {
                throw new DescriptorLoadException($"Failed to build protobuf descriptors: {ex.Message}");
            }
        }
        finally
        {
            if (File.Exists(descriptorSetPath))
            {
                File.Delete(descriptorSetPath);
            }
        }
    }

    private void RunProtoc(
        IReadOnlyList<string> protoFiles,
        IReadOnlyList<string> includePaths,
        string descriptorSetPath)
    {
        var startInfo = new ProcessStartInfo(_protocPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add($"--descriptor_set_out={descriptorSetPath}");
        startInfo.ArgumentList.Add("--include_imports");
        startInfo.ArgumentList.Add("--include_source_info");

        foreach (var includePath in includePaths)
        {
            startInfo.ArgumentList.Add($"--proto_path={includePath}");
        }

        // Trailing, so a project vendoring its own copy of a well-known schema still wins.
        foreach (var includePath in ImplicitIncludePaths)
        {
            startInfo.ArgumentList.Add($"--proto_path={includePath}");
        }

        foreach (var protoFile in protoFiles)
        {
            startInfo.ArgumentList.Add(protoFile);
        }

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A stale or corrupt protoc on the probe path is a configuration problem, not a crash.
            throw new DescriptorLoadException(
                $"Failed to start protoc at '{_protocPath}': {ex.Message}");
        }

        using var process = started
            ?? throw new DescriptorLoadException($"Failed to start protoc at '{_protocPath}'.");

        // Both streams are drained concurrently, and only then is the exit awaited. Reading one to
        // the end before starting the other deadlocks whenever the child fills the pipe it is not
        // being read from: it blocks on the write, so it never exits, so the stream being read
        // never reaches end. protoc writes its output to a file here and normally leaves stdout
        // empty, which is the only reason the sequential form has held up.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        var stderr = stderrTask.GetAwaiter().GetResult();
        stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new DescriptorLoadException(
                $"protoc failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr.Trim()}");
        }
    }
}
