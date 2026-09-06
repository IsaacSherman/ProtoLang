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
/// <b>What cancellation reaches, and what it only discards.</b> A superseded compile stops waiting
/// and gives back the worker it was holding: the token cancels the debounce, the wait for a slot, and
/// the wait on protoc. What it does not do is stop protoc, and that is deliberate rather than a gap.
/// A descriptor load belongs to the shared cache rather than to whichever keystroke asked first, and
/// the keystroke that superseded this one is about to want the same schemas -- so killing that load
/// would throw away exactly the work its successor needs and then pay for it again. Everything after
/// the load is milliseconds and simply runs to completion, and its answer is discarded. What bounds a
/// protoc nobody is waiting for any more is its budget, which is why there is no way to switch that
/// off.
/// </para>
/// <para>
/// <b>How deep the queue goes.</b> One entry per document, and a new request replaces the entry the
/// previous one left rather than joining it -- so the queue cannot outgrow the number of open
/// documents however fast anybody types, and superseding is what "the queue is full" means here.
/// Dropping anything else would be worse: every entry is the newest thing known about its document,
/// and discarding one leaves that document showing squiggles for text it no longer contains, with
/// nothing scheduled that would correct them.
/// </para>
/// <para>
/// The numbers below -- the interval, the concurrency limit -- are still #57's, which measures rather
/// than guesses.
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
    /// guess; #57 pins it against measured latency, and <see cref="PeakInFlight"/> is what shows
    /// whether whatever it is pinned to is being honoured.
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

    /// <summary>Documents whose scheduled compile is still live.</summary>
    /// <remarks>
    /// <para>
    /// The queue depth, and the whole of it, because the queue is keyed by document. Published so
    /// that "sustained editing does not grow the backlog" is a measurement rather than an argument
    /// about a dictionary, and for the status report in #58, where a server that feels stuck should
    /// be able to say whether it is holding work or merely idle.
    /// </para>
    /// <para>
    /// Live means supersedable: a later request for that document would replace this one, and a
    /// close would cancel it. It is deliberately not "how much work is running", because a compile
    /// abandoned by a close leaves this the instant it is abandoned and may take a moment longer to
    /// notice. <see cref="InFlight"/> is the other question, and the two are only equal when nothing
    /// has been given up on.
    /// </para>
    /// </remarks>
    public int Pending => _pending.Count;

    /// <summary>Compiles that are past the gate and have not yet returned.</summary>
    /// <remarks>
    /// What is actually occupying a worker, including one whose answer is already known to be
    /// unwanted. That gap is the whole subject of cancellation here: the number that matters is how
    /// long a compile keeps a worker after it has been abandoned, and it cannot be measured from
    /// <see cref="Pending"/>, which has already forgotten it.
    /// </remarks>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The most compiles that have ever been past the gate at one moment.</summary>
    /// <remarks>
    /// A high-water mark rather than a current count, because the property worth testing is that the
    /// limit was never exceeded, and a current count only ever shows that it is not being exceeded
    /// right now. Ten documents edited at once is a burst that lasts milliseconds; a sample taken
    /// afterwards would find nothing and pass.
    /// </remarks>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    private int _compilations;
    private int _inFlight;
    private int _peakInFlight;

    /// <summary>Recompiles one document once the typing settles.</summary>
    /// <remarks>
    /// The run is handed to the pool rather than started here, and that is not a preference. An
    /// <c>await</c> on an interval of zero completes synchronously, and so does a wait on a
    /// semaphore with a slot free -- so started inline, this method runs the whole compilation,
    /// protoc and all, on whichever thread called it before it returns. The thread that calls it is
    /// the one worker reading every notification the client sends, which would stop the server dead
    /// for the length of a schema load. The default interval hides it, because a real delay does
    /// yield; a shorter one, which is exactly what #57 might choose, would not.
    /// </remarks>
    public void Schedule(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var cancellation = new CancellationTokenSource();

        _pending.AddOrUpdate(document.Key, cancellation, (_, previous) => Supersede(previous, cancellation));

        _ = Task.Run(() => RunAsync(document, cancellation), CancellationToken.None);
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
                Enter();

                await CompileAsync(document, token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Either a keystroke superseded this compile or the document closed. Both are ordinary,
            // and both leave the descriptor load that was under way to finish into the cache.
            _log.Trace($"Abandoned a compilation of '{document}' that was superseded before it finished.");
        }
        catch (Exception ex)
        {
            // The compiler is written not to throw on bad input, and a server that dies when it does
            // anyway is worse than one that says so and keeps answering about every other file.
            _log.Error($"Compiling '{document}' failed.", ex);
        }
        finally
        {
            // Both halves of "remove it only if it is still mine" in one operation. Looking first and
            // removing afterwards is two, and a keystroke lands between them: Schedule replaces this
            // entry with the compile it just superseded this one for, and this line then removes that
            // one instead. What is left is a compile nothing holds a handle to -- the next edit cannot
            // supersede it and closing the document cannot cancel it, so it runs to completion holding
            // a concurrency slot to publish an answer about text that has already moved on.
            _pending.TryRemove(KeyValuePair.Create(document.Key, cancellation));
        }
    }

    /// <summary>Records that one more compile is past the gate, and how high that has ever been.</summary>
    /// <remarks>
    /// Read back and raised in a loop rather than compared once, because two compiles entering
    /// together can each read the old peak and each write a value the other has already beaten.
    /// </remarks>
    private void Enter()
    {
        var current = Interlocked.Increment(ref _inFlight);

        var peak = Volatile.Read(ref _peakInFlight);
        while (current > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peakInFlight, current, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }

    private async Task CompileAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        // Asked before the work rather than only after it. A compile that waited for a slot behind
        // three others has usually been superseded by the time it gets one, and running it anyway
        // spends a protoc on text nobody is looking at.
        cancellationToken.ThrowIfCancellationRequested();

        if (_documents.Find(uri) is not { } document)
        {
            return;
        }

        Interlocked.Increment(ref _compilations);

        var configuration = _configuration.Current;
        var settings = configuration.Resolve(uri);
        var mapper = _mapper();

        var contribution = Diagnose(document, settings, mapper, cancellationToken);

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
        DiagnosticMapper mapper,
        CancellationToken cancellationToken)
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
        var result = compilation.Compile(cancellationToken);

        ReportExpiry(result, uri, compilation.Loader);

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

    /// <summary>Says in the log that protoc was stopped, and which protoc it was.</summary>
    /// <remarks>
    /// The user already sees <c>PL0083</c> on the import line, which is the half of this that belongs
    /// on their screen. The half that belongs in a log is the executable, because the diagnostic
    /// cannot carry it without naming a path in every message and the first question a support
    /// request has to answer is which protoc was in effect. #58 reads the same fact from the same
    /// place.
    /// </remarks>
    private void ReportExpiry(CompilationResult result, DocumentUri uri, DescriptorLoader? loader)
    {
        if (result.SchemaFailure is not { Kind: DescriptorLoadFailureKind.TimedOut })
        {
            return;
        }

        _log.Warning(
            $"protoc was stopped for outrunning its budget while compiling '{uri}'"
                + $"{(loader is null ? string.Empty : $", running '{loader.ProtocPath}'")}.");
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

        var configuration = settings.ConfigPath is { } path && DocumentUri.TryParse(path, out var file) ? file : null;

        foreach (var diagnostic in settings.Diagnostics)
        {
            Attribute(contribution, uri, configuration, diagnostic, mapper);
        }

        foreach (var diagnostic in _configuration.SettingsDiagnostics)
        {
            Attribute(contribution, uri, configuration, diagnostic, mapper);
        }

        return contribution;
    }

    /// <summary>Files one configuration diagnostic against the document its position is a position in.</summary>
    /// <remarks>
    /// <para>
    /// A configuration diagnostic is not always about the document being compiled, and the two kinds
    /// look nothing alike. <c>ProjectConfig.Load</c> reports a line and a column inside
    /// <c>protolang.config.xml</c>: published against the source buffer, an invalid value on line 4 of
    /// the configuration file draws a squiggle on line 4 of the source, which is a different file
    /// saying a different thing -- or past the end of it, on a source shorter than the configuration.
    /// The file it belongs to is <see cref="DocumentConfiguration.ConfigPath"/>, the same file
    /// <c>PL2106</c> names.
    /// </para>
    /// <para>
    /// A diagnostic with no position is the other kind: a setting being ignored, a path that would not
    /// resolve, the refusal summary itself. Those belong on the document, at its start, because what
    /// they are about is this document not compiling. They stay there.
    /// </para>
    /// <para>
    /// The fallback is the document at its start rather than the document at the position, for a
    /// located diagnostic whose file cannot be turned into a URI. A range that is honestly wrong is
    /// worse than one that admits it knows nothing: the message already names the file.
    /// </para>
    /// </remarks>
    private static void Attribute(
        DiagnosticContribution contribution,
        DocumentUri document,
        DocumentUri? configuration,
        Diagnostics.Diagnostic diagnostic,
        DiagnosticMapper mapper)
    {
        if (!diagnostic.Span.IsNone && configuration is not null)
        {
            contribution.Add(configuration, mapper.Map(diagnostic, configuration.Text));
            return;
        }

        contribution.Add(document, mapper.Map(diagnostic, document.Text, DiagnosticMapper.WholeDocumentStart));
    }
}
