using System.Diagnostics;
using ProtoLang.Binding;
using Xunit;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// What a descriptor load costs, what several at once cost, and what keeping them costs in memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured and reported, never budgeted.</b> A cold load is <c>protoc</c> starting, reading a
/// schema closure and writing a descriptor set, and this project does not get to choose how long
/// that takes. #57 says so explicitly: the budget on a cold load is that it happens once. What the
/// number is for is everything downstream of it -- whether the cache capacity is right, whether the
/// supervision timeout is a backstop or a limit, and whether the concurrency limit is safe to raise.
/// </para>
/// <para>
/// <b>The concurrent case is here because the budget table would miss it.</b> Every row of that
/// table measures one operation. #54 changed <see cref="DescriptorCache"/> to hold each load as a
/// <c>Task</c> rather than a <c>Lazy</c>, so a superseded compile can abandon its wait -- at the cost
/// of a second blocked thread per load, since the load runs on the pool while its caller waits on
/// it. Four concurrent compiles therefore want eight blocked threads on a machine whose default
/// minimum worker count is one per processor. That only shows up with several cold loads at once,
/// which is the moment a workspace opens with a handful of files already open, and is both the first
/// thing a user does and the moment they are most obviously waiting.
/// </para>
/// </remarks>
[Collection("Timing-sensitive regressions")]
public class DescriptorLoadMeasurementTests
{
    /// <summary>How long one cold load takes, and how much a warm one saves.</summary>
    [Fact]
    public void ADescriptorLoadIsMeasuredColdAndWarm()
    {
        if (!Sampler.Requested)
        {
            Assert.Skip("Set PROTOLANG_BENCH=1 to measure descriptor loads. See docs/performance.md.");
        }

        var protoc = RequireProtoc();
        var report = new PerformanceReport();

        var cold = new List<double>();

        for (var run = 0; run < 5; run++)
        {
            var loader = new DescriptorLoader(
                protoc,
                new DescriptorLoaderOptions { Cache = new DescriptorCache() });

            var clock = Stopwatch.StartNew();
            loader.LoadBundle(["invoice.proto"], [TestPaths.ExampleProtoDirectory]);
            clock.Stop();

            cold.Add(clock.Elapsed.TotalMilliseconds);
        }

        report.Add(new Sample("descriptor load", "examples/protos", "cold", cold));

        var shared = new DescriptorLoader(
            protoc,
            new DescriptorLoaderOptions { Cache = new DescriptorCache() });

        report.Add(Sampler.Time(
            "descriptor load",
            "examples/protos",
            "warm",
            () => shared.LoadBundle(["invoice.proto"], [TestPaths.ExampleProtoDirectory])));

        report.Note(
            $"A cold load is {cold.Min():0} to {cold.Max():0} ms of protoc. The supervision timeout is "
                + $"{DescriptorLoaderOptions.DefaultTimeout.TotalSeconds:0} s, which is a backstop for a hung "
                + "protoc rather than a bound anything normal approaches.");

        report.Append();
    }

    /// <summary>
    /// What several cold loads at once do to the thread pool, which is the scenario #54 left open.
    /// </summary>
    /// <remarks>
    /// Each load is given its own schema directory so that single-flight cannot collapse them into
    /// one: the question is what <em>n</em> genuinely concurrent loads cost, and a measurement where
    /// four callers shared one protoc invocation would answer a much happier question.
    /// </remarks>
    [Fact]
    public async Task ConcurrentColdLoadsAreMeasuredAgainstTheConcurrencyLimit()
    {
        if (!Sampler.Requested)
        {
            Assert.Skip("Set PROTOLANG_BENCH=1 to measure concurrent loads. See docs/performance.md.");
        }

        var protoc = RequireProtoc();
        var report = new PerformanceReport();
        var limit = ProtoLang.LanguageServer.Hosting.CompileScheduler.DefaultConcurrency;

        foreach (var concurrent in new[] { 1, limit, limit * 2 })
        {
            var directories = Enumerable.Range(0, concurrent).Select(_ => CopySchemas()).ToArray();
            var loader = new DescriptorLoader(
                protoc,
                new DescriptorLoaderOptions { Cache = new DescriptorCache() });

            var before = ThreadPool.ThreadCount;
            var peak = before;
            var watching = true;

            var watcher = Task.Run(() =>
            {
                while (Volatile.Read(ref watching))
                {
                    peak = Math.Max(peak, ThreadPool.ThreadCount);
                    Thread.Sleep(2);
                }
            });

            var clock = Stopwatch.StartNew();

            await Task.WhenAll(
                directories.Select(directory => Task.Run(
                    () => loader.LoadBundle(["invoice.proto"], [directory]))));

            clock.Stop();
            Volatile.Write(ref watching, false);
            await watcher;

            report.Add(new Sample(
                $"{concurrent} concurrent cold load(s)",
                "examples/protos",
                "cold",
                [clock.Elapsed.TotalMilliseconds]));

            report.Note(
                $"{concurrent} concurrent: {clock.Elapsed.TotalMilliseconds:0} ms wall clock, "
                    + $"thread pool {before} -> {peak}.");
        }

        report.Note(
            $"The compile concurrency limit is {limit} and the answer limit is "
                + $"{ProtoLang.LanguageServer.Hosting.DeferredAnswers.DefaultConcurrency}.");

        report.Append();
    }

    /// <summary>What a full descriptor cache costs in memory, which #48 asked and could not answer.</summary>
    /// <remarks>
    /// Measured rather than reasoned about, because a descriptor set carrying source info is not
    /// small and "not small" is not a number anybody can size a cache against. Taken as the
    /// difference across a settled heap: a collection before and after, with the bundles held live
    /// so nothing measured can have been collected out from under the reading.
    /// </remarks>
    [Fact]
    public void TheDescriptorCacheFootprintIsAStatedNumber()
    {
        if (!Sampler.Requested)
        {
            Assert.Skip("Set PROTOLANG_BENCH=1 to measure the cache footprint. See docs/performance.md.");
        }

        var protoc = RequireProtoc();
        var report = new PerformanceReport();
        var held = new List<DescriptorBundle>();

        var settled = Settle();

        for (var entry = 0; entry < DescriptorCache.DefaultCapacity; entry++)
        {
            var loader = new DescriptorLoader(
                protoc,
                new DescriptorLoaderOptions { Cache = new DescriptorCache() });

            held.Add(loader.LoadBundle(["invoice.proto"], [CopySchemas()]));
        }

        var full = Settle();
        var perEntry = (full - settled) / (double)DescriptorCache.DefaultCapacity;

        Assert.Equal(DescriptorCache.DefaultCapacity, held.Count);

        report.Note(
            $"A retained bundle for the examples' schema closure is about {perEntry / 1024:0} KiB, so a full "
                + $"cache of {DescriptorCache.DefaultCapacity} is about "
                + $"{perEntry * DescriptorCache.DefaultCapacity / (1024 * 1024):0.0} MiB for that closure.");

        report.Append();
    }

    private static long Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>A directory of its own holding the examples' schemas, so a load cannot be shared.</summary>
    private static string CopySchemas()
    {
        var directory = TestPaths.CreateTempDirectory();

        foreach (var schema in Directory.EnumerateFiles(TestPaths.ExampleProtoDirectory, "*.proto"))
        {
            File.Copy(schema, Path.Combine(directory, Path.GetFileName(schema)));
        }

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

}
