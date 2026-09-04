using System.Collections.Concurrent;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Decides when a document is compiled, and refuses to publish an answer about text nobody is looking
/// at any more.
/// </summary>
/// <remarks>
/// <para>
/// <b>Debounced and coalesced.</b> A keystroke schedules a compile and cancels the one the previous
/// keystroke scheduled, so a burst of typing produces one compilation of the settled text rather than
/// one per character. Without it every keystroke reaches protoc, which is tens to hundreds of
/// milliseconds each and all but one of them wasted.
/// </para>
/// <para>
/// <b>The latest buffer wins.</b> Every run carries the document version and the configuration
/// generation it started under, and its result is thrown away rather than published if either has
/// moved. This is the rule worth being strictest about: the most visible failure a language server can
/// have is the user fixing an error, watching the squiggle vanish, and then watching an older compile
/// finish and put it back.
/// </para>
/// <para>
/// <b>What is here and what is #54's.</b> Debounce, coalescing, the version stamp and the staleness
/// rule are #42's own requirements and are implemented. Genuine cancellation of a compile already
/// running, a bounded queue, and the numbers below -- the interval, the concurrency limit, protoc's
/// timeout -- belong to #54 and #57, which measure rather than guess. A stale run here is discarded
/// rather than stopped; that is stated plainly because it is exactly the kind of thing a later reader
/// would otherwise assume was already handled.
/// </para>
/// </remarks>
public sealed class CompileScheduler
{
    /// <summary>How long typing has to pause before a compile starts.</summary>
    /// <remarks>
    /// A quarter of a second: long enough that a fluent typist produces one compile per pause rather
    /// than one per word, short enough that the squiggles still feel attached to the typing. #57 pins
    /// it against a measured budget; it is a value here rather than a constant threaded through the
    /// code so that pinning it is a one-line change.
    /// </remarks>
    public static TimeSpan DefaultDebounce => TimeSpan.FromMilliseconds(250);

    /// <summary>How many documents may be compiling at once.</summary>
    /// <remarks>
    /// Each compile can start a protoc, so ten open files must not mean ten processes. Four is a
    /// guess, and #54 owns the real bound.
    /// </remarks>
    public const int DefaultConcurrency = 4;

    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly LoaderPool _loaders;
    private readonly DiagnosticRouter _router;
    private readonly Func<DiagnosticMapper> _mapper;
    private readonly ServerLog _log;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _concurrency;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    public CompileScheduler(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        DiagnosticRouter router,
        Func<DiagnosticMapper> mapper,
        ServerLog log,
        TimeSpan? debounce = null,
        int concurrency = DefaultConcurrency)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _debounce = debounce ?? DefaultDebounce;
        _concurrency = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <summary>How many compilations have actually run. For tests and for #58.</summary>
    /// <remarks>
    /// Coalescing is otherwise unverifiable: a scheduler that ignored the debounce entirely would
    /// publish the same diagnostics and pass every other assertion.
    /// </remarks>
    public int Compilations => Volatile.Read(ref _compilations);

    private int _compilations;

    /// <summary>Recompiles one document once the typing settles.</summary>
    public void Schedule(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var cancellation = new CancellationTokenSource();

        _pending.AddOrUpdate(document.Key, cancellation, (_, previous) => Supersede(previous, cancellation));

        _ = RunAsync(document, cancellation);
    }

    /// <summary>Recompiles everything, because something that affects every document changed.</summary>
    public void ScheduleAll()
    {
        foreach (var document in _documents.All)
        {
            Schedule(document.Uri);
        }
    }

    /// <summary>Abandons a document's outstanding work and clears what it published.</summary>
    public Task ForgetAsync(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_pending.TryRemove(document.Key, out var pending))
        {
            Cancel(pending);
        }

        return _router.ClearAsync(document);
    }

    /// <remarks>
    /// Cancellation sources are not disposed. One is created per scheduled compile and holds nothing
    /// but its registrations, which the cancelled delay releases; disposing it here instead would race
    /// the task that is still watching it, and trading a collectable object for an
    /// <see cref="ObjectDisposedException"/> on a keystroke is a poor bargain.
    /// </remarks>
    private static CancellationTokenSource Supersede(CancellationTokenSource previous, CancellationTokenSource replacement)
    {
        Cancel(previous);

        return replacement;
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(DocumentUri document, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;

        try
        {
            await Task.Delay(_debounce, token).ConfigureAwait(false);
            await _concurrency.WaitAsync(token).ConfigureAwait(false);

            try
            {
                await CompileAsync(document, token).ConfigureAwait(false);
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The compiler is written not to throw on bad input, and a server that dies when it does
            // anyway is worse than one that says so and keeps answering about every other file.
            _log.Error($"Compiling '{document}' failed.", ex);
        }
        finally
        {
            if (_pending.TryGetValue(document.Key, out var current) && ReferenceEquals(current, cancellation))
            {
                _pending.TryRemove(document.Key, out _);
            }
        }
    }

    private async Task CompileAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        if (_documents.Find(uri) is not { } document)
        {
            return;
        }

        Interlocked.Increment(ref _compilations);

        var configuration = _configuration.Current;
        var settings = configuration.Resolve(uri);
        var mapper = _mapper();

        var contribution = Diagnose(document, settings, mapper);

        cancellationToken.ThrowIfCancellationRequested();

        // The staleness question is handed to the router rather than asked here, because the router's
        // lock is the only thing that orders this publication against the withdrawal a close performs.
        // Asked here, it could be answered "still fresh" and then overtaken by the close, leaving
        // diagnostics on a document the editor has shut and nothing left that would ever clear them.
        if (!await _router.PublishAsync(uri, contribution, () => IsStale(document, configuration)).ConfigureAwait(false))
        {
            _log.Trace($"Discarding a compilation of '{uri}' that no longer describes the buffer.");
        }
    }

    /// <summary>
    /// Whether what was just computed describes text, or settings, that have since moved on.
    /// </summary>
    private bool IsStale(OpenDocument document, WorkspaceConfiguration configuration)
    {
        if (_documents.Find(document.Uri) is not { } current)
        {
            // Closed while this ran. ForgetAsync has already cleared it, and publishing now would put
            // diagnostics on a document the editor is no longer showing.
            return true;
        }

        return current.Version != document.Version
            || _configuration.Current.Generation != configuration.Generation;
    }

    /// <summary>Everything wrong with one document, under one settled configuration.</summary>
    private DiagnosticContribution Diagnose(
        OpenDocument document,
        DocumentConfiguration settings,
        DiagnosticMapper mapper)
    {
        var uri = document.Uri;

        // A protoc that was named and cannot be built into a loader stops this document. Falling back
        // to a located one would compile against a different executable than the settings state, while
        // this object went on reporting that the setting was in force.
        //
        // PL2107 rather than PL2105: 10.4.1 gives PL2105 to a named protoc that is not there, which is
        // a warning and falls through to the next source. This one exists and still cannot be used, it
        // is an error, and it stops the document -- a second meaning behind one code would leave a
        // reader looking up a severity the code is documented never to have.
        if (!_loaders.TryGet(settings.ProtocPath, out var loader, out var failure) && settings.ProtocPath is not null)
        {
            var refused = new DiagnosticContribution();
            refused.Add(
                uri,
                new Diagnostic
                {
                    Range = DiagnosticMapper.WholeDocumentStart,
                    Severity = DiagnosticSeverity.Error,
                    Code = "PL2107",
                    Source = DiagnosticMapper.Source,
                    Message = $"protoc could not be used: '{settings.ProtocPath}', from "
                        + $"{settings.ProtocPathSource.Describe()}, exists but could not be prepared to run. "
                        + $"{failure?.Message} Nothing is compiled for this document until it can be.",
                });

            return WithConfiguration(refused, uri, settings, mapper);
        }

        if (!settings.TryCreateCompilationOptions(loader, out var options))
        {
            // A configuration file was found and refused. PL2106 is already in the settings
            // diagnostics, and nothing compiles until it is fixed.
            return WithConfiguration(new DiagnosticContribution(), uri, settings, mapper);
        }

        var compilation = new Compilation(document.ToSource(settings.Folder?.Path), options!);
        var result = compilation.Compile();

        // The roots protoc's own error messages are resolved against: what the compilation searched,
        // then what the loader adds of its own. Taken from the compilation that ran rather than rebuilt,
        // so a well-known schema resolves to the file protoc actually read.
        IReadOnlyList<string> resolvePaths =
            [.. result.SearchPaths, .. compilation.Loader?.ImplicitIncludePaths ?? []];

        return WithConfiguration(
            CompilationDiagnostics.Build(result, uri, resolvePaths, mapper),
            uri,
            settings,
            mapper);
    }

    /// <summary>
    /// Adds what is wrong with the configuration to what is wrong with the document.
    /// </summary>
    /// <remarks>
    /// Two sources, both belonging here rather than in a log. The per-document ones come from
    /// resolving spec 10.4.1's precedence for this file -- a relative path with nothing to resolve
    /// against, a named config file that is not there, a config file that was refused. The per-scope
    /// ones are about the settings themselves and are the same for every document, which is true of
    /// their effect as well. A setting silently ignored is the failure the whole configuration model
    /// was built to prevent, and a warning the user never sees is a setting silently ignored.
    /// </remarks>
    private DiagnosticContribution WithConfiguration(
        DiagnosticContribution contribution,
        DocumentUri uri,
        DocumentConfiguration settings,
        DiagnosticMapper mapper)
    {
        contribution.Claim(uri);

        foreach (var diagnostic in settings.Diagnostics)
        {
            contribution.Add(uri, mapper.Map(diagnostic, uri.Text));
        }

        foreach (var diagnostic in _configuration.SettingsDiagnostics)
        {
            contribution.Add(uri, mapper.Map(diagnostic, uri.Text));
        }

        return contribution;
    }
}
