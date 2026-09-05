using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;
using LspFolder = ProtoLang.LanguageServer.Protocol.Lsp.WorkspaceFolder;

namespace ProtoLang.Tests;

[CollectionDefinition("Compile scheduler races", DisableParallelization = true)]
public sealed class CompileSchedulerRaceCollection;

[Collection("Compile scheduler races")]
public class CompileSchedulerTests
{
    [Fact]
    public async Task ANewerScheduledCompileStaysCancellableWhileItRuns()
    {
        var directory = TestPaths.CreateTempDirectory();
        var missingProtoc = Path.Combine(directory, "protoc-that-is-not-there.exe");
        var documents = new DocumentStore();

        var log = new ServerLog { Mirror = TextWriter.Null };
        var configuration = new ConfigurationSync(new JsonRpcConnection(Stream.Null, Stream.Null, log), log);
        configuration.SetFolders([new LspFolder { Uri = new Uri(directory).AbsoluteUri, Name = "workspace" }]);

        ApplyProtocPath(configuration, missingProtoc);
        var blocker = DocumentUri.FromPath(Path.Combine(directory, "blocker.protolang"));
        documents.Open(blocker, "protolang", version: 1, "extend InvoiceItem {}");

        var blockerReachedPublisher = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishes = 0;
        var router = new DiagnosticRouter(
            async _ =>
            {
                if (Interlocked.Increment(ref publishes) == 1)
                {
                    blockerReachedPublisher.TrySetResult();
                    await releaseBlocker.Task.WaitAsync(TestContext.Current.CancellationToken);
                }
            },
            document => documents.Find(document)?.Version);
        var scheduler = new CompileScheduler(
            documents,
            configuration,
            new LoaderPool(log),
            router,
            () => new DiagnosticMapper(relatedInformationSupported: false),
            log,
            debounce: TimeSpan.Zero,
            concurrency: 1);

        scheduler.Schedule(blocker);
        await blockerReachedPublisher.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // Keep earlier runs inside WaitAsync while their cancellation callbacks and Schedule race to
        // replace their entries. More workers make that otherwise microscopic interleaving reproducible.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 256), completionPorts);
        var replacements = new List<DocumentUri>();
        try
        {
            for (var attempt = 0; attempt < 10_000; attempt++)
            {
                var uri = DocumentUri.FromPath(Path.Combine(directory, $"source-{attempt}.protolang"));
                documents.Open(uri, "protolang", version: 1, "extend InvoiceItem {}");

                scheduler.Schedule(uri);
                scheduler.Schedule(uri);
                replacements.Add(uri);
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);

            foreach (var uri in replacements)
            {
                Assert.True(IsPending(scheduler, uri), "a completed compile removed the cancellation source for a newer compile");
            }
        }
        finally
        {
            foreach (var uri in replacements)
            {
                await scheduler.ForgetAsync(uri);
            }

            ThreadPool.SetMinThreads(workers, completionPorts);
            releaseBlocker.TrySetResult();
        }
    }

    private static void ApplyProtocPath(ConfigurationSync configuration, string path)
    {
        using var settings = JsonDocument.Parse(JsonSerializer.Serialize(new { protocPath = path }));
        configuration.ApplyPush(settings.RootElement);
    }

    private static bool IsPending(CompileScheduler scheduler, DocumentUri document)
    {
        var field = typeof(CompileScheduler).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CompileScheduler no longer keeps pending work by document.");
        var pending = (ConcurrentDictionary<string, CancellationTokenSource>)field.GetValue(scheduler)!;

        return pending.ContainsKey(document.Key);
    }
}
