using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ProtoLang.Binding;

public sealed class DescriptorLoadException : Exception
{
    public DescriptorLoadException(string message) : this(message, [], string.Empty)
    {
    }

    /// <param name="output">protoc's own report, split into the lines it wrote.</param>
    /// <param name="rawOutput">Its standard error exactly as it arrived.</param>
    public DescriptorLoadException(
        string message,
        IReadOnlyList<ProtocDiagnostic> output,
        string rawOutput)
        : base(message)
    {
        Output = output;
        RawOutput = rawOutput;
    }

    /// <summary>What protoc said, with the file and position it said it about kept separate.</summary>
    /// <remarks>
    /// The message alone is what this exception used to carry, and it is prose: it names the schema
    /// and the line inside an English sentence, where the only way to act on either is to parse the
    /// sentence back apart. An editor has to publish these against the <c>.proto</c> they blame, and
    /// #41 has to resolve them into that file -- so the structure is recovered once, here, rather
    /// than by every reader that needs it. Empty when the failure was not protoc reporting on a
    /// schema: a protoc that could not be started, or descriptors that would not build.
    /// </remarks>
    public IReadOnlyList<ProtocDiagnostic> Output { get; }

    /// <inheritdoc cref="Output"/>
    public string RawOutput { get; }
}

/// <summary>
/// Invokes protoc to produce a <c>FileDescriptorSet</c> and builds the runtime descriptor
/// objects the binder resolves names against.
/// </summary>
public sealed class DescriptorLoader
{
    /// <summary>How long a killed protoc is given to actually die before it stops being waited on.</summary>
    private const int GraceMilliseconds = 5_000;

    private readonly string _protocPath;

    private int _protocInvocations;

    public DescriptorLoader(string protocPath)
        : this(protocPath, new DescriptorLoaderOptions())
    {
    }

    public DescriptorLoader(string protocPath, DescriptorLoaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Resolved once, so that the executable this loader reports, measures, looks for bundled
        // schemas beside, and finally runs are all the same file. A caller naming a bare 'protoc'
        // otherwise leaves each of those asking a different question of a different thing.
        _protocPath = ProtocLocator.Resolve(protocPath);
        Options = options;
        ImplicitIncludePaths = ProtocLocator.FindWellKnownTypeIncludePaths(_protocPath);
    }

    /// <summary>The protoc this loader runs.</summary>
    /// <remarks>
    /// Published because "which protoc was in effect?" is the first question a support request has to
    /// answer, and #58 exists to answer it. A caller that had to recompute it would be reporting on
    /// the protoc it thinks would be located rather than on the one that ran.
    /// </remarks>
    public string ProtocPath => _protocPath;

    /// <inheritdoc cref="DescriptorLoaderOptions"/>
    public DescriptorLoaderOptions Options { get; }

    /// <summary>Where this loader keeps its loads, or null when it keeps none.</summary>
    public DescriptorCache? Cache => Options.Cache;

    /// <summary>How many times this loader has started protoc.</summary>
    /// <remarks>
    /// The supported way to assert that a compilation did not invoke protoc. Without it, every
    /// requirement about caching is a claim rather than a test: a cache that silently did nothing
    /// would produce identical descriptors and pass every other assertion that could be written about
    /// it. Counted at the point the process is started, so a protoc that fails to start still counts
    /// -- the question is whether this loader reached for the executable, not whether it succeeded.
    /// </remarks>
    public int ProtocInvocations => Volatile.Read(ref _protocInvocations);

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
    public static DescriptorLoader CreateDefault() => CreateDefault(new DescriptorLoaderOptions());

    /// <inheritdoc cref="CreateDefault()"/>
    public static DescriptorLoader CreateDefault(DescriptorLoaderOptions options)
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

        return new DescriptorLoader(protoc, options);
    }

    /// <summary>
    /// Compiles <paramref name="protoFiles"/> into descriptors.
    /// </summary>
    /// <param name="protoFiles">Paths to .proto files, relative to one of the include paths.</param>
    /// <param name="includePaths">Directories passed to protoc as --proto_path.</param>
    public IReadOnlyList<FileDescriptor> Load(
        IReadOnlyList<string> protoFiles,
        IReadOnlyList<string> includePaths)
        => LoadBundle(protoFiles, includePaths).Descriptors;

    /// <summary>
    /// Compiles <paramref name="protoFiles"/> and returns everything the run produced, not only the
    /// descriptors built from it.
    /// </summary>
    /// <remarks>
    /// The door <see cref="Load"/> now goes through. Callers wanting the descriptor list alone are
    /// left exactly as they were, and a caller that needs source info -- a doc comment, the line a
    /// message is declared on -- asks for the bundle instead of asking for another protoc run.
    /// </remarks>
    /// <param name="protoFiles">Paths to .proto files, relative to one of the include paths.</param>
    /// <param name="includePaths">Directories passed to protoc as --proto_path.</param>
    public DescriptorBundle LoadBundle(
        IReadOnlyList<string> protoFiles,
        IReadOnlyList<string> includePaths)
    {
        ArgumentNullException.ThrowIfNull(protoFiles);
        ArgumentNullException.ThrowIfNull(includePaths);

        if (protoFiles.Count == 0)
        {
            return DescriptorBundle.Empty;
        }

        var request = Describe(protoFiles, includePaths);

        // A request that could not identify its own protoc does not go in the cache. Its key would
        // claim to account for which executable ran while knowing nothing about it, so two loads
        // under two different protocs of the same name would share an entry -- and a cache that is
        // wrong is worth less than one that is absent.
        return Options.Cache is { } cache && request.IdentifiesItsProtoc
            ? cache.GetOrLoad(request, () => Invoke(request))
            : Invoke(request);
    }

    /// <summary>Everything this load is, in the one object that decides what protoc would produce.</summary>
    /// <remarks>
    /// protoc is measured here rather than when the loader was built, so that an install upgraded in
    /// place partway through a long editing session invalidates instead of being believed. A loader
    /// outlives many loads, and the executable underneath it is not required to hold still.
    /// </remarks>
    private DescriptorRequest Describe(IReadOnlyList<string> protoFiles, IReadOnlyList<string> includePaths)
    {
        var length = 0L;
        var lastWrite = default(DateTime);

        try
        {
            var protoc = new FileInfo(_protocPath);
            if (protoc.Exists)
            {
                length = protoc.Length;
                lastWrite = protoc.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A protoc the file system will not describe is still a protoc worth trying to run. The
            // load either works or reports why; refusing here would turn a readable failure into a
            // crash before the executable was ever reached.
        }

        // Copied, because the request is held for as long as its entry is and a caller is free to
        // reuse the array it passed.
        return new DescriptorRequest(
            _protocPath,
            length,
            lastWrite,
            [.. includePaths],
            ImplicitIncludePaths,
            [.. protoFiles]);
    }

    private DescriptorBundle Invoke(DescriptorRequest request)
    {
        var descriptorSetPath = Path.Combine(Path.GetTempPath(), $"protolang-{Guid.NewGuid():N}.desc");

        try
        {
            RunProtoc(request, descriptorSetPath);

            var bytes = File.ReadAllBytes(descriptorSetPath);
            var set = FileDescriptorSet.Parser.ParseFrom(bytes);

            // --include_imports emits dependencies before dependents, which is exactly the order
            // BuildFromByteStrings requires.
            var serialized = set.File.Select(file => file.ToByteString()).ToList();

            IReadOnlyList<FileDescriptor> descriptors;
            try
            {
                descriptors = FileDescriptor.BuildFromByteStrings(serialized);
            }
            catch (Exception ex)
            {
                throw new DescriptorLoadException($"Failed to build protobuf descriptors: {ex.Message}");
            }

            // The set is kept, not read and dropped. It carries every FileDescriptorProto and the
            // source info --include_source_info was asked for, which is where a schema's declaration
            // sites and doc comments live.
            return new DescriptorBundle(
                descriptors,
                set,
                SchemaClosure.Describe(set.File.Select(file => file.Name), request.SearchRoots));
        }
        finally
        {
            if (File.Exists(descriptorSetPath))
            {
                File.Delete(descriptorSetPath);
            }
        }
    }

    private void RunProtoc(DescriptorRequest request, string descriptorSetPath)
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

        // The caller's directories first and the implicit ones behind them, so a project vendoring
        // its own copy of a well-known schema still wins. The order is the request's, because it is
        // also the order a cached closure is re-resolved against, and the two must be the same order.
        foreach (var includePath in request.SearchRoots)
        {
            startInfo.ArgumentList.Add($"--proto_path={includePath}");
        }

        foreach (var protoFile in request.ProtoFiles)
        {
            startInfo.ArgumentList.Add(protoFile);
        }

        Process? started;
        try
        {
            Interlocked.Increment(ref _protocInvocations);
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

        // Waiting on exit before waiting on the reads, rather than the other way around: a protoc
        // that hangs without writing leaves both reads outstanding forever, so a budget applied to
        // them would never be reached. Starting the reads first is what keeps the pipes drained
        // while this wait runs.
        if (!process.WaitForExit(Budget()))
        {
            Terminate(process);

            var abandoned = Drain(stderrTask);
            Drain(stdoutTask);

            throw new DescriptorLoadException(
                $"protoc did not finish within {Options.Timeout.TotalSeconds:0.###} seconds and was "
                + $"stopped.{Environment.NewLine}{abandoned.Trim()}".TrimEnd(),
                ProtocDiagnostic.Parse(abandoned),
                abandoned);
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        stdoutTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new DescriptorLoadException(
                $"protoc failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr.Trim()}",
                ProtocDiagnostic.Parse(stderr),
                stderr);
        }
    }

    /// <remarks>
    /// Clamped rather than validated. A budget of zero or less is a caller saying "do not wait", which
    /// is a legitimate thing to ask of a supervisor and the only way to exercise this path in a test
    /// without a fixture process to babysit.
    /// </remarks>
    private int Budget()
        => Options.Timeout <= TimeSpan.Zero
            ? 0
            : (int)Math.Min(Options.Timeout.TotalMilliseconds, int.MaxValue);

    /// <summary>Stops a protoc that outstayed its budget, and the children it started.</summary>
    /// <remarks>
    /// The whole tree, because protoc can spawn plugins and a killed parent leaves those holding the
    /// pipes this process is still reading. Every failure here is swallowed: the process may have
    /// exited between the timeout and the kill, and the caller is already being told the load failed
    /// -- reporting a second, worse error about the cleanup would bury the first.
    /// </remarks>
    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(GraceMilliseconds);
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or NotSupportedException or AggregateException)
        {
        }
    }

    /// <remarks>
    /// Bounded, and never rethrows. This runs only when a load has already failed, and its whole
    /// purpose is to salvage whatever protoc managed to say; a read that will not complete must not
    /// be able to hold the compiler open on the way out.
    /// </remarks>
    private static string Drain(Task<string> read)
    {
        try
        {
            return read.Wait(GraceMilliseconds) ? read.Result : string.Empty;
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}
