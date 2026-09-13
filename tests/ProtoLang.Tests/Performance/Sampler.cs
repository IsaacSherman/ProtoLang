using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ProtoLang.Tests.Performance;

/// <summary>What one operation cost, over enough runs for the number to mean something.</summary>
/// <param name="Milliseconds">Every sample, in the order taken.</param>
internal sealed record Sample(string Operation, string Corpus, string Warmth, IReadOnlyList<double> Milliseconds)
{
    public double Median => Percentile(50);

    public double P95 => Percentile(95);

    public double Min => Milliseconds.Count == 0 ? double.NaN : Milliseconds.Min();

    public double Max => Milliseconds.Count == 0 ? double.NaN : Milliseconds.Max();

    /// <remarks>
    /// Nearest-rank on the sorted samples: no interpolation, so every figure reported is a run that
    /// actually happened rather than an average of two that did. With twenty samples the 95th is the
    /// second slowest, which is the intent -- one outlier does not set the number, and two do.
    /// </remarks>
    private double Percentile(int percentile)
    {
        // A sample with no runs in it is a measurement that did not happen, and reporting 0.00 ms for
        // it would read as the fastest row in the table.
        if (Milliseconds.Count == 0)
        {
            return double.NaN;
        }

        var sorted = Milliseconds.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Runs an operation enough times to report a percentile, and throws the first runs away.
/// </summary>
/// <remarks>
/// <para>
/// <b>The discarded runs are not a courtesy.</b> The first call through any of these paths pays for
/// JIT, for a type initializer, and for a cache that has not been touched yet, and including it
/// would put a one-off cost into a per-keystroke number. What the budgets describe is the hundredth
/// hover of a session, not the first.
/// </para>
/// <para>
/// <b>No timing assertion lives outside <c>PROTOLANG_BENCH</c>.</b> A wall-clock deadline on a
/// shared runner is a coin toss with a build attached, and this repository has already spent two
/// commits on deadlines that were fine locally. What CI checks instead is counted work, which is
/// deterministic -- see <c>PerformanceCostTests</c>.
/// </para>
/// </remarks>
internal static class Sampler
{
    /// <summary>Whether the measurement suite was asked for.</summary>
    public static bool Requested
        => Environment.GetEnvironmentVariable("PROTOLANG_BENCH") is { Length: > 0 };

    /// <summary>Runs <paramref name="operation"/> and reports what it cost.</summary>
    public static Sample Time(
        string name,
        string corpus,
        string warmth,
        Action operation,
        int iterations = 20,
        int discarded = 5)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var run = 0; run < discarded; run++)
        {
            operation();
        }

        var samples = new List<double>(iterations);
        var clock = new Stopwatch();

        for (var run = 0; run < iterations; run++)
        {
            clock.Restart();
            operation();
            clock.Stop();

            samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        return new Sample(name, corpus, warmth, samples);
    }
}

/// <summary>
/// Everything one measurement run found, written where a person can read it afterwards.
/// </summary>
/// <remarks>
/// A run that only asserted would say "too slow" and nothing else, and the next question is always
/// "by how much, and was it always?". The report answers that without anybody re-running anything,
/// and it is what gets pasted into the dated results section of <c>docs/performance.md</c>.
/// </remarks>
internal sealed class PerformanceReport
{
    private readonly List<Sample> _samples = [];
    private readonly List<string> _notes = [];

    /// <summary>Whether this section carries the run's heading, which only the first one does.</summary>
    private bool _first;

    public IReadOnlyList<Sample> Samples => _samples;

    public void Add(Sample sample) => _samples.Add(sample);

    public void Note(string note) => _notes.Add(note);

    /// <summary>Where a run leaves its report.</summary>
    public static string Directory
        => System.IO.Path.Combine(TestPaths.RepositoryRoot, "artifacts", "perf");

    /// <summary>The one report file this process is writing.</summary>
    public static string Path => System.IO.Path.Combine(Directory, "report.md");

    private static readonly Lock Gate = new();
    private static bool _started;

    /// <summary>
    /// Adds this report's section to the run's one report file.
    /// </summary>
    /// <remarks>
    /// <b>Append, with the first writer truncating.</b> The measurements are several tests, and
    /// xUnit does not promise which runs first -- so a report that each test wrote in full would
    /// hold whichever section happened to finish last and silently lose the rest. That is not
    /// hypothetical: it is what the first version of this did, and the descriptor numbers vanished
    /// behind the budget table. Truncating once per process, under a lock, makes the file the whole
    /// run regardless of order.
    /// </remarks>
    public string Append()
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (!_started)
            {
                File.WriteAllText(Path, string.Empty);
                _started = true;
                _first = true;
            }

            File.AppendAllText(Path, Render() + "\n");
        }

        return Path;
    }

    public string Render()
    {
        var report = new StringBuilder();

        if (_first)
        {
            report.Append("# Performance measurement\n\n");
            report.Append($"Taken {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName}, ");
            report.Append($"{Environment.ProcessorCount} processors, {RuntimeName()}.\n\n");
            report.Append("Normal corpus is `examples/simpleScript.protolang` at ");
            report.Append(PerformanceCorpus.Lines(PerformanceCorpus.Normal).ToString(CultureInfo.InvariantCulture));
            report.Append(" lines; stress is `tests/perf/corpus/wide.protolang` at ");
            report.Append(PerformanceCorpus.Lines(PerformanceCorpus.Stress).ToString(CultureInfo.InvariantCulture));
            report.Append(" lines.\n\n");
        }

        // A section with only notes gets no table. An empty one with a header row reads as a
        // measurement that found nothing rather than as a measurement that is not a table.
        if (_samples.Count > 0)
        {
            report.Append("| Operation | Corpus | Warmth | Median | p95 | Min | Max | Budget | |\n");
            report.Append("|---|---|---|---:|---:|---:|---:|---:|---|\n");
        }

        foreach (var sample in _samples)
        {
            var budget = PerformanceBudgets.All.SingleOrDefault(entry => entry.Operation == sample.Operation);
            var ceiling = budget is null ? "-" : Milliseconds(budget.Milliseconds);
            var verdict = budget is null
                ? "measured"
                : sample.P95 <= budget.Milliseconds ? "within" : "**over**";

            report.Append($"| {sample.Operation} | {sample.Corpus} | {sample.Warmth} ");
            report.Append($"| {Milliseconds(sample.Median)} | {Milliseconds(sample.P95)} ");
            report.Append($"| {Milliseconds(sample.Min)} | {Milliseconds(sample.Max)} | {ceiling} | {verdict} |\n");
        }

        if (_notes.Count > 0)
        {
            report.Append("\n## Notes\n\n");

            foreach (var note in _notes)
            {
                report.Append($"- {note}\n");
            }
        }

        return report.ToString();
    }

    private static string Milliseconds(double value)
        => value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " ms";

    private static string RuntimeName()
        => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}
