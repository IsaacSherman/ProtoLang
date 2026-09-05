using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ProtoLang.Binding;
using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// What happens to a descriptor load that outstays its budget, and to one nobody wants any more.
/// </summary>
/// <remarks>
/// <para>
/// Both kill paths are reached deliberately rather than hopefully. A protoc that sleeps for a minute
/// cannot finish inside the interval any of these tests allow, so the only way each wait can end is
/// the one being tested; asking a real protoc to be slow would be a race, and the race is one the
/// suite has already lost once.
/// </para>
/// <para>
/// The cache tests next door cover single-flight, eviction and invalidation, and those properties are
/// what #54's change of mechanism inside <see cref="DescriptorCache"/> could break. They are not
/// repeated here -- they run in the same suite, against the same cache, and a second copy of an
/// assertion is a second thing to keep true rather than a second guarantee.
/// </para>
/// </remarks>
[Collection("Timing-sensitive regressions")]
public class ProcessSupervisionTests
{
    /// <summary>Long enough that nothing here can pass by simply outrunning it.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    /// <summary>Short enough that a wait which ignored its token would fail rather than hang.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    private const string Schema =
        """
        syntax = "proto3";

        package supervision.tests;

        message Leaf {
            int64 count = 1;
        }
        """;

    private const string Source =
        """
        import proto "leaf.proto";

        method int64 Read(Leaf leaf) {
            return leaf.count;
        }
        """;

    // ------------------------------------------------------------------ cancellation

    /// <summary>
    /// A load with nothing but its caller behind it is that caller's, so giving up on it stops
    /// protoc rather than leaving a process on the machine for the length of its budget.
    /// </summary>
    [Fact]
    public void ACancelledUncachedLoadStopsWaitingAndLeavesNoProcessBehind()
    {
        var directory = WriteSchema();
        var loader = new DescriptorLoader(
            StandInProtoc.Sleeping(),
            new DescriptorLoaderOptions { Timeout = Generous, TemporaryDirectory = TestPaths.CreateTempDirectory() });

        var before = FixtureProcesses();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        var elapsed = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => loader.LoadBundle(["leaf.proto"], [directory], cancellation.Token));
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < Prompt,
            $"a cancelled load must stop waiting rather than serve out protoc's {Generous.TotalSeconds:0}s budget");
        Assert.Empty(FixtureProcesses().Except(before));
    }

    /// <summary>
    /// A cancelled caller's descriptor set is deleted like any other, because the process holding it
    /// was killed before the load returned.
    /// </summary>
    [Fact]
    public void ACancelledLoadLeavesNoTemporaryFile()
    {
        var directory = WriteSchema();
        var temporary = TestPaths.CreateTempDirectory();
        var loader = new DescriptorLoader(
            StandInProtoc.Sleeping(),
            new DescriptorLoaderOptions { Timeout = Generous, TemporaryDirectory = temporary });

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        Assert.ThrowsAny<OperationCanceledException>(
            () => loader.LoadBundle(["leaf.proto"], [directory], cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(temporary));
    }

    /// <summary>
    /// A load reached through the cache belongs to the cache. One caller giving up must not fail the
    /// other -- two documents importing one schema share an entry, and a keystroke in the first must
    /// not break the compile of the second.
    /// </summary>
    [Fact]
    public async Task ACancelledWaiterDoesNotCancelALoadAnotherCallerIsSharing()
    {
        var cache = new DescriptorCache();
        var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;

        DescriptorBundle Load()
        {
            Interlocked.Increment(ref loads);
            started.TrySetResult();
            release.Task.Wait(Prompt);

            return DescriptorBundle.Empty;
        }

        using var cancellation = new CancellationTokenSource();

        var abandoning = Task.Run(() => cache.GetOrLoad(request, Load, cancellation.Token));
        await started.Task.WaitAsync(Prompt, TestContext.Current.CancellationToken);

        var waiting = Task.Run(() => cache.GetOrLoad(request, Load, CancellationToken.None));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoning);

        release.TrySetResult();

        Assert.NotNull(await waiting.WaitAsync(Prompt, TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref loads));
    }

    /// <summary>
    /// The reason a shared load is not killed: the keystroke that superseded this one wants the same
    /// schemas, and finds them already loaded rather than paying for protoc again.
    /// </summary>
    [Fact]
    public async Task ALoadEveryCallerAbandonedStillPopulatesTheCache()
    {
        var cache = new DescriptorCache();
        var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;

        DescriptorBundle Load()
        {
            Interlocked.Increment(ref loads);
            started.TrySetResult();
            release.Task.Wait(Prompt);

            return DescriptorBundle.Empty;
        }

        using var cancellation = new CancellationTokenSource();

        var abandoning = Task.Run(() => cache.GetOrLoad(request, Load, cancellation.Token));
        await started.Task.WaitAsync(Prompt, TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoning);

        release.TrySetResult();

        // The successor, arriving after the load nobody was waiting for has finished.
        Assert.NotNull(
            await Task.Run(() => cache.GetOrLoad(request, Load, CancellationToken.None))
                .WaitAsync(Prompt, TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref loads));
        Assert.Equal(1, cache.Statistics.Hits);
    }

    /// <summary>
    /// A caller that gave up leaves the entry alone. Dropping it would be the same mistake as
    /// killing the load: the next caller finds nothing and starts protoc over schemas that are
    /// already being compiled.
    /// </summary>
    [Fact]
    public async Task ACancelledWaitDoesNotDropTheEntryItWasWaitingOn()
    {
        var cache = new DescriptorCache();
        var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        DescriptorBundle Load()
        {
            started.TrySetResult();
            release.Task.Wait(Prompt);

            return DescriptorBundle.Empty;
        }

        using var cancellation = new CancellationTokenSource();

        var abandoning = Task.Run(() => cache.GetOrLoad(request, Load, cancellation.Token));
        await started.Task.WaitAsync(Prompt, TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoning);

        Assert.Equal(1, cache.Count);

        release.TrySetResult();
    }

    // ------------------------------------------------------------------ the budget

    /// <summary>
    /// A schema protoc never finished reading is not a schema protoc rejected, and the codes say so.
    /// Told under one code, the first reads as the second and sends a reader hunting for a fault in
    /// a file that has none.
    /// </summary>
    [Fact]
    public void AProtocStoppedByItsBudgetIsReportedApartFromASchemaItRejected()
    {
        var directory = WriteSchema();
        var source = Path.Combine(directory, "read.protolang");
        File.WriteAllText(source, Source);

        var result = Compilation.Compile(
            source,
            [directory],
            new DescriptorLoader(
                StandInProtoc.Sleeping(),
                new DescriptorLoaderOptions { Timeout = TimeSpan.FromMilliseconds(250) }));

        var expired = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "PL0083");

        Assert.Equal(DescriptorLoadFailureKind.TimedOut, result.SchemaFailure?.Kind);
        Assert.Contains("did not finish within", expired.Message);
        Assert.False(string.IsNullOrWhiteSpace(expired.Help), "a reader has to be told what to do about it");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "PL0003");
    }

    /// <summary>The counterpart: a schema protoc read and refused keeps the code it always had.</summary>
    [Fact]
    public void ASchemaProtocRejectsIsStillReportedAsAFailedLoad()
    {
        var directory = WriteSchema();
        File.WriteAllText(Path.Combine(directory, "leaf.proto"), "syntax = \"proto3\"; message {");
        var source = Path.Combine(directory, "read.protolang");
        File.WriteAllText(source, Source);

        var result = Compilation.Compile(source, [directory], new DescriptorLoader(RequireProtoc()));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PL0003");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "PL0083");
        Assert.Equal(DescriptorLoadFailureKind.Failed, result.SchemaFailure?.Kind);
    }

    // ------------------------------------------------------------------ cleaning up

    /// <summary>
    /// The failure worth reporting is protoc's. A descriptor set that cannot be deleted on the way
    /// out is a footnote, and a footnote thrown from a <c>finally</c> replaces the report entirely --
    /// leaving a reader an IO error about a temporary file where the reason their build failed
    /// should be.
    /// </summary>
    [Fact]
    public void ADescriptorSetThatCannotBeDeletedDoesNotReplaceWhatProtocSaid()
    {
        var directory = WriteSchema();
        var temporary = TestPaths.CreateTempDirectory();
        var loader = new DescriptorLoader(
            StandInProtoc.Obstructive(),
            new DescriptorLoaderOptions { TemporaryDirectory = temporary });

        try
        {
            var failure = Assert.Throws<DescriptorLoadException>(
                () => loader.LoadBundle(["leaf.proto"], [directory]));

            Assert.Contains("refused", failure.Message);
            Assert.Single(Directory.GetFileSystemEntries(temporary));
        }
        finally
        {
            StandInProtoc.Unlock(temporary);
        }
    }

    /// <summary>
    /// A file that could not be deleted is deleted later rather than forgotten. One is nothing; one
    /// per keystroke over a working day is a disk.
    /// </summary>
    [Fact]
    public void ADescriptorSetLeftBehindIsSweptByTheNextLoad()
    {
        var directory = WriteSchema();
        var temporary = TestPaths.CreateTempDirectory();
        var loader = new DescriptorLoader(
            StandInProtoc.Obstructive(),
            new DescriptorLoaderOptions { TemporaryDirectory = temporary });

        try
        {
            Assert.Throws<DescriptorLoadException>(() => loader.LoadBundle(["leaf.proto"], [directory]));

            var stranded = Assert.Single(Directory.GetFileSystemEntries(temporary));

            // Whatever was holding it has let go, which is the case this exists for: the file that
            // could not be deleted a moment ago can be deleted now, and something has to notice.
            StandInProtoc.Unlock(temporary);

            Assert.Throws<DescriptorLoadException>(() => loader.LoadBundle(["leaf.proto"], [directory]));

            Assert.DoesNotContain(stranded, Directory.GetFileSystemEntries(temporary));
        }
        finally
        {
            StandInProtoc.Unlock(temporary);
        }
    }

    /// <summary>
    /// The list of descriptor sets to come back to is bounded. Remembering one file whose handle a
    /// dying plugin has not released is the point of it; remembering one per compile for the rest of
    /// the session is the leak it was written to prevent, reached by another road.
    /// </summary>
    /// <remarks>
    /// A directory that refuses every delete rather than one that refuses a single file, because
    /// that is the shape that grows: each load strands a descriptor set of its own, each one really
    /// is on disk, so the "only remember what is really there" guard admits all of them. The count
    /// is read by reflection, the way <c>CompileSchedulerTests</c> reads the scheduler's pending
    /// work: it is a bound on an internal, and a property published only so a test could see it
    /// would be a worse answer than a test that reaches in.
    /// </remarks>
    [Fact]
    public void TheDescriptorSetsToComeBackToAreBounded()
    {
        const int Loads = 60;

        var directory = WriteSchema();
        var temporary = TestPaths.CreateTempDirectory();
        var loader = new DescriptorLoader(
            StandInProtoc.Obstructive(),
            new DescriptorLoaderOptions { TemporaryDirectory = temporary });

        void Strand(int times)
        {
            for (var load = 0; load < times; load++)
            {
                Assert.Throws<DescriptorLoadException>(() => loader.LoadBundle(["leaf.proto"], [directory]));
            }
        }

        try
        {
            Strand(Loads);
            var afterOneSession = Remembered(loader);

            Strand(Loads);

            // Twice the loads, twice the files on disk, and the same number to come back to. Asserted
            // as "it stopped growing" rather than against the cap itself, because the number is a
            // judgement that may be revised and the property is what must not be.
            Assert.Equal(Loads * 2, Directory.GetFileSystemEntries(temporary).Length);
            Assert.Equal(afterOneSession, Remembered(loader));
            Assert.InRange(afterOneSession, 1, Loads);
        }
        finally
        {
            StandInProtoc.Unlock(temporary);
        }
    }

    /// <summary>
    /// A protoc that says it succeeded and wrote nothing is reported, not thrown out of the
    /// pipeline. Compilation catches a descriptor-load failure and nothing else, so an IO exception
    /// escaping here becomes a crash on input rather than a diagnostic.
    /// </summary>
    [Fact]
    public void ADescriptorSetThatCannotBeReadIsReportedRatherThanThrown()
    {
        var directory = WriteSchema();

        // A temporary directory that is really a file, so nothing can be created inside it and the
        // descriptor set protoc claims to have written is not there to read.
        var temporary = Path.Combine(TestPaths.CreateTempDirectory(), "not-a-directory");
        File.WriteAllText(temporary, string.Empty);

        var loader = new DescriptorLoader(
            StandInProtoc.Silent(),
            new DescriptorLoaderOptions { TemporaryDirectory = temporary });

        var failure = Assert.Throws<DescriptorLoadException>(
            () => loader.LoadBundle(["leaf.proto"], [directory]));

        Assert.Contains("could not be read", failure.Message);
        Assert.Equal(DescriptorLoadFailureKind.Failed, failure.Kind);
    }

    /// <summary>A temporary directory that is not there yet is made, not complained about.</summary>
    [Fact]
    public void ATemporaryDirectoryThatDoesNotExistYetIsCreated()
    {
        var directory = WriteSchema();
        var temporary = Path.Combine(TestPaths.CreateTempDirectory(), "not", "there", "yet");
        var loader = new DescriptorLoader(
            RequireProtoc(),
            new DescriptorLoaderOptions { TemporaryDirectory = temporary });

        Assert.NotEmpty(loader.LoadBundle(["leaf.proto"], [directory]).Descriptors);
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
    }

    // ------------------------------------------------------------------ fixtures

    private static DescriptorRequest Request()
        => new("protoc", 1, DateTime.UnixEpoch, ["roots"], [], ["leaf.proto"]);

    private static string WriteSchema()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "leaf.proto"), Schema);

        return directory;
    }

    private static string RequireProtoc()
    {
        var protoc = ProtocLocator.Locate();
        if (protoc is null)
        {
            Assert.Skip("No protoc on PATH and none in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }

    /// <summary>How many undeletable descriptor sets a loader is still meaning to come back to.</summary>
    private static int Remembered(DescriptorLoader loader)
    {
        var field = typeof(DescriptorLoader).GetField("_abandoned", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DescriptorLoader no longer keeps the sets it could not delete.");

        return ((ConcurrentDictionary<string, byte>)field.GetValue(loader)!).Count;
    }

    /// <summary>Every process this suite's stand-in protocs are made of, by id.</summary>
    /// <remarks>
    /// Compared as a set difference rather than a count, so that a <c>ping</c> or a <c>sleep</c> some
    /// other program on the machine happened to be running cannot decide whether this passes. What is
    /// asserted is only that nothing which was not there before is there afterwards.
    /// </remarks>
    private static HashSet<int> FixtureProcesses()
    {
        string[] names = OperatingSystem.IsWindows() ? ["cmd", "PING"] : ["sh", "sleep"];

        return [.. names.SelectMany(name => Process.GetProcessesByName(name)).Select(process => process.Id)];
    }
}
