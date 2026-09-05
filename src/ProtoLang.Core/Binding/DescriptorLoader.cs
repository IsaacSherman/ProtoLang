using System.Collections.Concurrent;
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

    /// <inheritdoc cref="DescriptorLoadFailureKind"/>
    /// <remarks>
    /// Init-only with a default rather than a fourth constructor parameter, so that both existing
    /// constructors keep their signatures and keep meaning what they always meant: a failure nobody
    /// classified is one protoc reported.
    /// </remarks>
    public DescriptorLoadFailureKind Kind { get; init; }
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

    /// <summary>Descriptor sets this loader wrote and has not yet managed to delete.</summary>
    /// <remarks>A set; the value is unused. <see cref="Release"/> says why they are kept.</remarks>
    private readonly ConcurrentDictionary<string, byte> _abandoned = new(StringComparer.Ordinal);

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
        => LoadBundle(protoFiles, includePaths, CancellationToken.None);

    /// <inheritdoc cref="LoadBundle(IReadOnlyList{string}, IReadOnlyList{string})"/>
    /// <param name="cancellationToken">
    /// Abandons the <em>wait</em>, and stops protoc only when this caller is the only thing the load
    /// exists for.
    /// </param>
    /// <remarks>
    /// <para>
    /// The distinction is the whole of the cancellation story, and it is worth stating rather than
    /// discovering. A cached load belongs to the cache, not to whoever asked first: the keystroke
    /// that superseded this one is about to want the same schemas, and killing protoc would throw
    /// away exactly the work its successor needs and then pay for it again. So a cancelled caller
    /// stops waiting, releases whatever it was holding, and leaves the load to finish and populate
    /// the entry. An uncached load has no such successor -- nothing else can ever reach it -- so
    /// cancelling it stops protoc.
    /// </para>
    /// <para>
    /// What bounds a cached load is therefore <see cref="DescriptorLoaderOptions.Timeout"/> alone,
    /// which is why there is deliberately no way to say "wait forever".
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public DescriptorBundle LoadBundle(
        IReadOnlyList<string> protoFiles,
        IReadOnlyList<string> includePaths,
        CancellationToken cancellationToken)
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
        //
        // CancellationToken.None inside the cache, and the caller's token on the wait for it: the
        // load that runs there outlives the caller that started it.
        return Options.Cache is { } cache && request.IdentifiesItsProtoc
            ? cache.GetOrLoad(request, () => Invoke(request, CancellationToken.None), cancellationToken)
            : Invoke(request, cancellationToken);
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

    private DescriptorBundle Invoke(DescriptorRequest request, CancellationToken cancellationToken)
    {
        SweepAbandoned();

        var descriptorSetPath = Reserve();

        try
        {
            RunProtoc(request, descriptorSetPath, cancellationToken);

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
            Release(descriptorSetPath);
        }
    }

    /// <summary>A path for this run's descriptor set, in a directory that exists.</summary>
    /// <remarks>
    /// A directory that cannot be created is left to protoc to complain about. Its complaint names
    /// the file it could not write and arrives through the same channel as every other protoc
    /// failure; one thrown from here would be a different exception type escaping a method
    /// documented to report rather than throw.
    /// </remarks>
    private string Reserve()
    {
        try
        {
            Directory.CreateDirectory(Options.TemporaryDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
        }

        return Path.Combine(Options.TemporaryDirectory, $"protolang-{Guid.NewGuid():N}.desc");
    }

    /// <summary>Deletes one run's descriptor set, or remembers to try again.</summary>
    /// <remarks>
    /// <para>
    /// This runs in a <c>finally</c>, on the way out of a load that has usually already failed, and
    /// the failure it is carrying is the one worth reporting. A delete that threw would replace
    /// "protoc rejected your schema on line 12" with an <see cref="IOException"/> about a temp file
    /// nobody asked about -- and the delete is exactly the one most likely to fail, because a protoc
    /// that had to be killed can leave a plugin holding the handle for a moment longer than the
    /// grace period allows.
    /// </para>
    /// <para>
    /// Remembered rather than shrugged off, because a server runs for a working day. One undeletable
    /// file is nothing; one per keystroke is a disk. The next load sweeps them, by which time
    /// whatever held the handle has let go.
    /// </para>
    /// <para>
    /// Remembered only when something is actually there, which is what keeps the list of them from
    /// being the leak it exists to prevent. A delete can also fail because the path was never
    /// writable at all -- a temporary directory that turned out to be a file, a drive that is not
    /// mapped -- and retrying that one every load for the rest of the session would be an entry that
    /// can never come off.
    /// </para>
    /// </remarks>
    private void Release(string descriptorSetPath)
    {
        try
        {
            File.Delete(descriptorSetPath);
            _abandoned.TryRemove(descriptorSetPath, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Path.Exists(descriptorSetPath))
            {
                _abandoned[descriptorSetPath] = 0;
            }
        }
    }

    /// <inheritdoc cref="Release"/>
    private void SweepAbandoned()
    {
        foreach (var path in _abandoned.Keys)
        {
            Release(path);
        }
    }

    private void RunProtoc(DescriptorRequest request, string descriptorSetPath, CancellationToken cancellationToken)
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
        using var expiry = Expiry();
        using var supervision = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);

        try
        {
            process.WaitForExitAsync(supervision.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Terminate(process);

            var abandoned = Drain(stderrTask);
            Drain(stdoutTask);

            // The caller's own token is answered first. A load nobody wants any more is not a protoc
            // that misbehaved, and reporting it as one would put a timeout in the log every time a
            // user typed quickly.
            cancellationToken.ThrowIfCancellationRequested();

            throw new DescriptorLoadException(
                $"protoc did not finish within {Options.Timeout.TotalSeconds:0.###} seconds and was "
                + $"stopped.{Environment.NewLine}{abandoned.Trim()}".TrimEnd(),
                ProtocDiagnostic.Parse(abandoned),
                abandoned)
            {
                Kind = DescriptorLoadFailureKind.TimedOut,
            };
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

    /// <summary>A source that fires when protoc has had all the time it is going to get.</summary>
    /// <remarks>
    /// Clamped rather than validated. A budget of zero or less is a caller saying "do not wait", which
    /// is a legitimate thing to ask of a supervisor -- and clamping is what keeps it meaning that:
    /// handed straight to <see cref="CancellationTokenSource.CancelAfter(int)"/>, a negative
    /// millisecond count is <see cref="System.Threading.Timeout.Infinite"/>, so the one state
    /// <see cref="DescriptorLoaderOptions"/> says must not exist would be reachable by asking for
    /// less than none. Zero is cancelled outright rather than scheduled for zero milliseconds, so
    /// that "do not wait" is an answer rather than a race between a timer and a quick protoc.
    /// </remarks>
    private CancellationTokenSource Expiry()
    {
        var expiry = new CancellationTokenSource();

        if (Options.Timeout <= TimeSpan.Zero)
        {
            expiry.Cancel();
        }
        else
        {
            expiry.CancelAfter((int)Math.Min(Options.Timeout.TotalMilliseconds, int.MaxValue));
        }

        return expiry;
    }

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
