using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// What each operation in the budget table actually costs, measured rather than asserted about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Behind <c>PROTOLANG_BENCH</c>, and deliberately not in CI.</b> A wall-clock deadline on a
/// shared runner is a coin toss with a build attached: it flakes until somebody loosens it, and a
/// threshold loose enough never to flake no longer describes the budget. This repository has already
/// spent two commits on deadlines that were fine locally and were not fine on a runner. What CI
/// checks is <c>PerformanceCostTests</c> -- counted work, which is deterministic and cannot flake --
/// and what this checks is the milliseconds, on a machine somebody chose.
/// </para>
/// <para>
/// <b>It asserts as well as reports, because a benchmark nobody runs is worth little and a benchmark
/// that only prints is one nobody has to read.</b> The report is written either way, so a failure
/// arrives with the numbers rather than with a verdict.
/// </para>
/// <para>
/// One test rather than one per row, which is the exception to this repository's rule and is worth
/// saying why: the measurements share a warm workspace that costs a protoc invocation to build, they
/// must not run in parallel with each other, and a reader wants every row of the table at once
/// rather than the first one that was too slow. The assertion names every row that missed.
/// </para>
/// </remarks>
[Collection("Timing-sensitive regressions")]
public class PerformanceBudgetTests
{
    /// <summary>Every budgeted operation, measured on the stress corpus, warm.</summary>
    [Fact]
    public void EveryBudgetedOperationIsWithinItsBudget()
    {
        if (!Sampler.Requested)
        {
            Assert.Skip("Set PROTOLANG_BENCH=1 to measure the budgets. See docs/performance.md.");
        }

        var report = new PerformanceReport();

        foreach (var corpus in new[] { PerformanceCorpus.Normal, PerformanceCorpus.Stress })
        {
            Measure(report, corpus);
        }

        report.Note($"Descriptor cache capacity is {ProtoLang.Binding.DescriptorCache.DefaultCapacity} entries.");

        var path = report.Append();

        var over = report.Samples
            .Where(sample => sample.Corpus == PerformanceCorpus.Stress)
            .Select(sample => (sample, budget: PerformanceBudgets.All.SingleOrDefault(b => b.Operation == sample.Operation)))
            // Negated rather than `>`, so that a sample which produced no runs at all -- p95 of NaN,
            // which compares false against everything -- fails here instead of passing silently. The
            // report renders the same condition as "over", and the two must not disagree.
            .Where(pair => pair.budget is not null && !(pair.sample.P95 <= pair.budget.Milliseconds))
            .Select(pair => $"{pair.sample.Operation}: {pair.sample.P95:0.0} ms against {pair.budget!.Milliseconds:0} ms")
            .ToList();

        Assert.True(over.Count == 0, $"over budget at p95 on the stress corpus ({path}):\n  " + string.Join("\n  ", over));
    }

    /// <summary>
    /// The budget table in the documentation is the one the tests enforce.
    /// </summary>
    /// <remarks>
    /// The numbers live in <see cref="PerformanceBudgets"/> and the argument for them lives in
    /// <c>docs/performance.md</c>, which means the figures appear twice -- once where a machine reads
    /// them and once where a person does. They cannot be given one home across that boundary, so
    /// they are pinned instead: a rule written twice disagrees eventually, and the copy that would
    /// quietly stop being true is the one people read.
    /// </remarks>
    [Fact]
    public void TheDocumentedBudgetsAreTheEnforcedOnes()
    {
        var documentation = File.ReadAllText(
            Path.Combine(TestPaths.RepositoryRoot, "docs", "performance.md"));

        foreach (var budget in PerformanceBudgets.All)
        {
            var row = $"| {budget.Operation} | {budget.Milliseconds:0} ms |";

            Assert.True(
                documentation.Contains(row, StringComparison.Ordinal),
                $"docs/performance.md must carry the enforced budget as a table row: '{row}'");
        }
    }

    private static void Measure(PerformanceReport report, string corpus)
    {
        var workspace = new PerformanceWorkspace(corpus).Warm();
        var text = workspace.Text;

        // The widely referenced method, which is the worst case highlighting has and the whole
        // reason the stress file has one. On the normal corpus its nearest equivalent stands in.
        var shared = corpus == PerformanceCorpus.Stress ? StressCorpus.Shared : "line_total_cents";

        // A call and not the declaration. `At(shared)` would find `fn base_cents(`, because a
        // declaration and a call are the same shape and the declaration comes first -- which is the
        // trap five of #51's tests fell into, passing for the wrong reason until a guard exposed
        // them. The work is the same either way here, but a caret is on a call far more often than
        // on the line that introduced the name, and a measurement should describe the common case.
        var onShared = workspace.At($"= {shared}();") + "= ".Length;

        report.Add(Sampler.Time(
            PerformanceBudgets.Hover,
            corpus,
            "warm",
            () => workspace.Hover.AnswerAsync(
                workspace.Hover.Read(workspace.Ask(onShared))!, CancellationToken.None).GetAwaiter().GetResult()));

        report.Add(Sampler.Time(
            PerformanceBudgets.Highlighting,
            corpus,
            "warm",
            () => workspace.Highlights.AnswerAsync(
                workspace.Highlights.Read(workspace.Ask(onShared))!, CancellationToken.None).GetAwaiter().GetResult()));

        report.Add(Sampler.Time(
            PerformanceBudgets.Definition,
            corpus,
            "warm",
            () => workspace.Definition.AnswerAsync(
                workspace.Definition.Read(workspace.Ask(onShared))!, CancellationToken.None).GetAwaiter().GetResult()));

        var dot = text.IndexOf("item.", StringComparison.Ordinal);

        if (dot >= 0)
        {
            var afterDot = dot + "item.".Length;

            report.Add(Sampler.Time(
                PerformanceBudgets.Completion,
                corpus,
                "warm",
                () => workspace.Completion.AnswerAsync(
                    workspace.Completion.Read(new CompletionParams
                    {
                        TextDocument = new TextDocumentIdentifier { Uri = workspace.Uri.ToString() },
                        Position = EditorFixture.Ask(workspace.Uri, text, afterDot).Position,
                    })!,
                    CancellationToken.None).GetAwaiter().GetResult()));
        }

        report.Add(MeasureDiagnostics(workspace));
    }

    /// <summary>
    /// What an edit costs once the debounce has elapsed: one compilation of the whole buffer.
    /// </summary>
    /// <remarks>
    /// The buffer is changed every iteration, because a compilation the host is allowed to reuse is
    /// not a measurement of compiling. A comment is appended rather than code edited, so every
    /// iteration compiles a program that still binds and none of them measures an error path.
    /// </remarks>
    private static Sample MeasureDiagnostics(PerformanceWorkspace workspace)
    {
        var version = 1;

        return Sampler.Time(
            PerformanceBudgets.Diagnostics,
            workspace.Which,
            "warm",
            () =>
            {
                version++;
                workspace.Documents.Apply(
                    workspace.Uri,
                    version,
                    [new TextDocumentContentChangeEvent { Text = workspace.Text + $"\n// {version}\n" }]);
                workspace.Semantics.For(workspace.Document, workspace.Configuration, CancellationToken.None);
            },
            iterations: 20,
            discarded: 3);
    }
}
