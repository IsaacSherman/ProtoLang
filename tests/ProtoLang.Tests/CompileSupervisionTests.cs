using System.Diagnostics;
using System.Text.Json;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using ProtoLang.Tests.Harness;
using Xunit;
using LspFolder = ProtoLang.LanguageServer.Protocol.Lsp.WorkspaceFolder;

namespace ProtoLang.Tests;

/// <summary>
/// What a server does with work it can no longer use, and what a working day of typing costs it.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler is driven directly rather than through the JSON-RPC client, because what is being
/// measured is the backlog, the number of compiles past the gate and what the cache was asked for --
/// none of which the wire can show, and all of which the scheduler publishes precisely so that these
/// are measurements rather than arguments.
/// </para>
/// <para>
/// Its own collection, and a non-parallel one. Every assertion here is about a count settling, and a
/// count settles a good deal less predictably on a machine that is also building generated C++.
/// </para>
/// </remarks>
[Collection("Timing-sensitive regressions")]
public class CompileSupervisionTests
{
    /// <summary>How long a settling count is given before it is called stuck.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private const string Schema =
        """
        syntax = "proto3";

        package soak.tests;

        message Invoice {
            int64 total = 1;
        }
        """;

    private const string Source =
        """
        import proto "invoice.proto";

        extend Invoice {
            fn doubled() -> int64 {
                return total * 2;
            }
        }
        """;

    // ------------------------------------------------------------------ freshness

    /// <summary>
    /// The most visible failure a language server can have: an older compile finishing last and
    /// putting back the squiggle the user has just fixed. Forced rather than waited for -- the
    /// publisher holds the first compile inside the router until a newer version of the document has
    /// been registered, so the stale answer really does try to overtake the fresh one.
    /// </summary>
    [Fact]
    public async Task ASupersededCompileDoesNotPublishEvenWhenItFinishesLast()
    {
        var directory = TestPaths.CreateTempDirectory();
        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.protolang"));
        var documents = new DocumentStore();
        documents.Open(uri, "protolang", version: 1, "extend Invoice {}");

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<PublishDiagnosticsParams>();

        var router = new DiagnosticRouter(
            async message =>
            {
                lock (published)
                {
                    published.Add(message);
                }

                if (published.Count == 1)
                {
                    reached.TrySetResult();
                    await release.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
                }
            },
            document => documents.Find(document)?.Version);

        var scheduler = Scheduler(directory, documents, router, out _);

        scheduler.Schedule(uri);
        await reached.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The user types. Version 2 is what the buffer now holds, so version 1's answer describes
        // text nobody is looking at.
        documents.Open(uri, "protolang", version: 2, "extend Invoice { }");
        scheduler.Schedule(uri);

        release.TrySetResult();
        await SettleAsync(scheduler);

        var last = Assert.Single(published, message => string.Equals(message.Uri, uri.Text, StringComparison.Ordinal)
            && message.Version == 2);

        Assert.Equal(2, last.Version);
        Assert.DoesNotContain(
            published.Skip(published.IndexOf(last) + 1),
            message => message.Version == 1);
    }

    /// <summary>
    /// Ten documents edited at once must not become ten protocs. Asserted against the high-water
    /// mark, because a count sampled after the burst has passed finds nothing and agrees with
    /// everything.
    /// </summary>
    [Fact]
    public async Task ManyDocumentsEditedAtOnceNeverExceedTheConcurrencyLimit()
    {
        const int Limit = 2;
        const int Documents = 10;

        var directory = TestPaths.CreateTempDirectory();
        var documents = new DocumentStore();
        var router = new DiagnosticRouter(_ => Task.CompletedTask, document => documents.Find(document)?.Version);
        var scheduler = Scheduler(directory, documents, router, out _, concurrency: Limit);

        for (var index = 0; index < Documents; index++)
        {
            var uri = DocumentUri.FromPath(Path.Combine(directory, $"source-{index}.protolang"));
            documents.Open(uri, "protolang", version: 1, "extend Invoice {}");
            scheduler.Schedule(uri);
        }

        await SettleAsync(scheduler);

        Assert.Equal(Documents, scheduler.Compilations);
        Assert.InRange(scheduler.PeakInFlight, 1, Limit);
    }

    /// <summary>
    /// Closing a document takes its outstanding work with it. A compile left scheduled for a buffer
    /// the editor has shut spends a worker to produce an answer that must then be thrown away.
    /// </summary>
    /// <remarks>
    /// The other half of closing -- that what the document had already published is withdrawn -- is
    /// asserted end to end over the wire in <c>LanguageServerTests</c>, where a client can watch it
    /// happen. This is the half that leaves no trace on the wire at all: the compile that never ran.
    /// </remarks>
    [Fact]
    public async Task ClosingADocumentDropsTheWorkOutstandingForIt()
    {
        var directory = TestPaths.CreateTempDirectory();
        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.protolang"));
        var documents = new DocumentStore();
        documents.Open(uri, "protolang", version: 1, "extend Invoice {}");

        var router = new DiagnosticRouter(_ => Task.CompletedTask, document => documents.Find(document)?.Version);

        // A debounce long enough that the compile is certainly still waiting when the close arrives.
        var scheduler = Scheduler(directory, documents, router, out _, debounce: TimeSpan.FromSeconds(30));

        scheduler.Schedule(uri);
        Assert.Equal(1, scheduler.Pending);

        documents.Close(uri);
        await scheduler.ForgetAsync(uri).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(0, scheduler.Pending);
        Assert.Equal(0, scheduler.Compilations);
    }

    /// <summary>
    /// A second request for the same document replaces the first rather than joining it, which is
    /// what keeps the backlog at one entry per document however fast anybody types.
    /// </summary>
    [Fact]
    public async Task TypingIntoOneDocumentNeverQueuesMoreThanOneCompileForIt()
    {
        var directory = TestPaths.CreateTempDirectory();
        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.protolang"));
        var documents = new DocumentStore();
        var router = new DiagnosticRouter(_ => Task.CompletedTask, document => documents.Find(document)?.Version);
        var scheduler = Scheduler(directory, documents, router, out _, debounce: TimeSpan.FromMilliseconds(50));

        for (var version = 1; version <= 200; version++)
        {
            documents.Open(uri, "protolang", version, $"extend Invoice {{ {new string(' ', version)} }}");
            scheduler.Schedule(uri);

            Assert.Equal(1, scheduler.Pending);
        }

        await SettleAsync(scheduler);

        Assert.InRange(scheduler.Compilations, 1, 20);
    }

    /// <summary>
    /// Scheduling a compile does not perform one. The caller is the single worker reading every
    /// notification the client sends, so a <c>Schedule</c> that ran the compilation before returning
    /// would stop the server for as long as protoc took.
    /// </summary>
    /// <remarks>
    /// Zero, because that is the interval at which the trap springs: awaiting a delay of nothing
    /// completes synchronously, as does taking a semaphore slot that is free, so an inline run
    /// reaches protoc without ever yielding. The default interval of a quarter of a second hides
    /// this completely, and #57 is entitled to choose a shorter one.
    /// </remarks>
    [Fact]
    public async Task SchedulingACompileReturnsBeforeTheCompileRunsHoweverShortTheInterval()
    {
        var budget = TimeSpan.FromSeconds(5);
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "invoice.proto"), Schema);
        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.protolang"));

        var documents = new DocumentStore();
        documents.Open(uri, "protolang", version: 1, Source);

        var router = new DiagnosticRouter(_ => Task.CompletedTask, document => documents.Find(document)?.Version);
        var scheduler = Scheduler(
            directory,
            documents,
            router,
            out _,
            protocPath: StandInProtoc.Sleeping(),
            scratch: TestPaths.CreateTempDirectory(),
            budget: budget,
            debounce: TimeSpan.Zero);

        var elapsed = Stopwatch.StartNew();
        scheduler.Schedule(uri);
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < budget,
            $"scheduling took {elapsed.Elapsed.TotalSeconds:0.#}s, which is a compilation rather than a schedule");

        documents.Close(uri);
        await scheduler.ForgetAsync(uri).WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A compile that is waiting on protoc gives back its worker when the document closes, rather
    /// than holding it for the rest of protoc's budget. This is the whole point of cancellation
    /// reaching the compiler at all: without it a handful of slow schemas can occupy every worker
    /// the server has, and no other document is answered until they finish.
    /// </summary>
    /// <remarks>
    /// A protoc that sleeps for a minute, so the only two things that can end the wait are the close
    /// and the budget, and a budget of five seconds so that the difference between them is plain
    /// while the cost of finding out is not. The load itself is not cancelled and does run out that
    /// budget in the background -- which is the design, and is why the number is small.
    /// </remarks>
    [Fact]
    public async Task ACompileWaitingOnProtocGivesBackItsWorkerWhenTheDocumentCloses()
    {
        var budget = TimeSpan.FromSeconds(5);
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "invoice.proto"), Schema);
        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.protolang"));

        var documents = new DocumentStore();
        documents.Open(uri, "protolang", version: 1, Source);

        var router = new DiagnosticRouter(_ => Task.CompletedTask, document => documents.Find(document)?.Version);
        var scheduler = Scheduler(
            directory,
            documents,
            router,
            out _,
            protocPath: StandInProtoc.Sleeping(),
            scratch: TestPaths.CreateTempDirectory(),
            budget: budget);

        scheduler.Schedule(uri);

        // Wait until the compile is genuinely running rather than merely scheduled.
        var waited = Stopwatch.StartNew();
        while (scheduler.InFlight == 0)
        {
            Assert.True(waited.Elapsed < Patience, "the compile never started");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        documents.Close(uri);
        await scheduler.ForgetAsync(uri).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // How long the abandoned compile goes on occupying its worker, which is the whole question.
        var held = Stopwatch.StartNew();
        await SettleAsync(scheduler);
        held.Stop();

        Assert.True(
            held.Elapsed < budget,
            $"the worker was held for {held.Elapsed.TotalSeconds:0.#}s after the close, which is protoc's "
                + $"{budget.TotalSeconds:0}s budget rather than the close");
    }

    /// <summary>
    /// What makes the freshness rule implementable at all: a handler holding a document holds one
    /// version of it, and the edits arriving behind it cannot reach the text it is reading.
    /// </summary>
    /// <remarks>
    /// The rule every request type obeys is that an answer describes the version it read, and refuses
    /// rather than describing one the buffer has moved past. Diagnostics enforce it through
    /// <c>IsStale</c>; the synchronous handlers -- semantic tokens today, and whatever #43 and #44 can
    /// answer without a compile -- get it for nothing, but only because of this. A store that handed
    /// out a mutable document would leave a request describing half of one version and half of
    /// another, with nothing anywhere able to tell that it had.
    /// </remarks>
    [Fact]
    public void ARequestHoldingADocumentHoldsTheVersionItRead()
    {
        var uri = DocumentUri.FromPath(Path.Combine(TestPaths.CreateTempDirectory(), "source.protolang"));
        var documents = new DocumentStore();

        var read = documents.Open(uri, "protolang", version: 1, "extend Invoice {}");

        var edited = documents.Apply(
            uri,
            version: 2,
            [new TextDocumentContentChangeEvent { Text = "extend Invoice { fn n() -> int64 { return 1; } }" }]);

        Assert.Equal(1, read.Version);
        Assert.Equal("extend Invoice {}", read.Text);
        Assert.NotSame(read, edited);
        Assert.Equal(2, documents.Find(uri)?.Version);
    }

    // ------------------------------------------------------------------ soak

    /// <summary>
    /// Sustained editing leaves nothing behind: no backlog, no descriptor sets, no protoc processes,
    /// and no cache larger than it is allowed to be.
    /// </summary>
    /// <remarks>
    /// Small enough to belong in the unfiltered run, which is the only run there is -- a soak behind
    /// a switch is a soak nobody performs, and this repository has no CI to perform it. Its longer
    /// sibling is the one that runs deliberately.
    /// </remarks>
    [Fact]
    public Task SustainedEditingLeavesNoBacklogProcessesOrTemporaryFiles() => SoakAsync(documents: 3, edits: 50);

    /// <inheritdoc cref="SustainedEditingLeavesNoBacklogProcessesOrTemporaryFiles"/>
    /// <remarks>
    /// The same properties over a session long enough for a slow leak to show. Gated, because minutes
    /// of typing is not what a person waiting on a build wants from the suite, and run before a
    /// release rather than before a commit.
    /// </remarks>
    [Fact]
    public Task ALongSessionOfEditingLeavesNoBacklogProcessesOrTemporaryFiles()
    {
        if (Environment.GetEnvironmentVariable("PROTOLANG_SOAK") is not { Length: > 0 })
        {
            Assert.Skip("Set PROTOLANG_SOAK=1 to run the long soak. Its short sibling runs every time.");
        }

        return SoakAsync(documents: 8, edits: 400);
    }

    private static async Task SoakAsync(int documents, int edits)
    {
        var protoc = RequireBundledProtoc();
        var directory = TestPaths.CreateTempDirectory();
        var scratch = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "invoice.proto"), Schema);

        var store = new DocumentStore();
        var router = new DiagnosticRouter(_ => Task.CompletedTask, document => store.Find(document)?.Version);
        var scheduler = Scheduler(
            directory,
            store,
            router,
            out var loaders,
            protocPath: protoc,
            scratch: scratch,
            debounce: TimeSpan.FromMilliseconds(20));

        var uris = Enumerable
            .Range(0, documents)
            .Select(index => DocumentUri.FromPath(Path.Combine(directory, $"source-{index}.protolang")))
            .ToList();

        var protocsBefore = ProtocProcesses(protoc);

        foreach (var uri in uris)
        {
            store.Open(uri, "protolang", version: 1, Source);
            scheduler.Schedule(uri);
        }

        await SettleAsync(scheduler);

        for (var edit = 0; edit < edits; edit++)
        {
            var uri = uris[edit % documents];
            store.Open(uri, "protolang", edit + 2, Source + new string(' ', edit % 8));
            scheduler.Schedule(uri);
        }

        await SettleAsync(scheduler);

        Assert.Equal(0, scheduler.Pending);
        Assert.InRange(scheduler.PeakInFlight, 1, CompileScheduler.DefaultConcurrency);
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
        Assert.Empty(ProtocProcesses(protoc).Except(protocsBefore));

        // Unchanged schemas, so every compile after the first of them is a hit however many
        // keystrokes reached the compiler.
        Assert.InRange(loaders.Cache.Count, 1, loaders.Cache.Capacity);
        Assert.Equal(1, loaders.Cache.Statistics.Misses);

        // And coalescing did its job: far fewer compiles than edits, and at least one.
        Assert.InRange(scheduler.Compilations, documents, documents + edits);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A scheduler wired to a workspace, with everything a test wants to vary exposed.</summary>
    private static CompileScheduler Scheduler(
        string directory,
        DocumentStore documents,
        DiagnosticRouter router,
        out LoaderPool loaders,
        string? protocPath = null,
        string? scratch = null,
        TimeSpan? budget = null,
        TimeSpan? debounce = null,
        int concurrency = CompileScheduler.DefaultConcurrency)
    {
        var log = new ServerLog { Mirror = TextWriter.Null };
        var configuration = new ConfigurationSync(new JsonRpcConnection(Stream.Null, Stream.Null, log), log);
        configuration.SetFolders([new LspFolder { Uri = new Uri(directory).AbsoluteUri, Name = "workspace" }]);

        // A protoc that is not there when a test does not name one: the compile then stops at
        // PL2107 without a toolchain, which is all a test about scheduling needs of it.
        using (var settings = JsonDocument.Parse(
            JsonSerializer.Serialize(
                new { protocPath = protocPath ?? Path.Combine(directory, "protoc-that-is-not-there.exe") })))
        {
            configuration.ApplyPush(settings.RootElement);
        }

        var options = new DescriptorLoaderOptions();
        if (scratch is not null)
        {
            options = options with { TemporaryDirectory = scratch };
        }

        if (budget is { } limit)
        {
            options = options with { Timeout = limit };
        }

        loaders = new LoaderPool(log) { Options = options };

        return new CompileScheduler(
            documents,
            configuration,
            loaders,
            router,
            () => new DiagnosticMapper(relatedInformationSupported: false),
            log,
            debounce ?? TimeSpan.Zero,
            concurrency);
    }

    /// <summary>Waits until nothing is scheduled, waiting for a worker, or compiling.</summary>
    /// <remarks>
    /// Polled rather than signalled, because the property being waited for is the published backlog
    /// itself, and a scheduler that offered an event saying "the backlog is empty" would be
    /// answering the question with itself.
    /// </remarks>
    private static async Task SettleAsync(CompileScheduler scheduler)
    {
        var elapsed = Stopwatch.StartNew();

        while (scheduler.Pending > 0 || scheduler.InFlight > 0)
        {
            if (elapsed.Elapsed > Patience)
            {
                Assert.Fail(
                    $"{scheduler.Pending} scheduled and {scheduler.InFlight} running compilations were "
                        + $"still outstanding after {Patience.TotalSeconds:0}s.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Pending falls as the last compile leaves its finally block, a moment before the publication
        // it queued is written. One more pass lets that land.
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    /// <remarks>
    /// A set of ids rather than a count, so that a protoc some other part of the suite is running
    /// cannot decide whether this passes: the only thing asserted is that nothing which was not
    /// running before is running afterwards.
    /// </remarks>
    private static HashSet<int> ProtocProcesses(string protoc)
        => [.. Process.GetProcessesByName(Path.GetFileNameWithoutExtension(protoc)).Select(process => process.Id)];

    private static string RequireBundledProtoc()
    {
        var protoc = ProtocLocator.FindBundledProtoc();
        if (protoc is null)
        {
            Assert.Skip("No Grpc.Tools protoc in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }
}
