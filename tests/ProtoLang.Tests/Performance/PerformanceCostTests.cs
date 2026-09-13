using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// What each answer costs in work done, rather than in time taken.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half that runs in CI, and it is the half that can.</b> A millisecond is a property
/// of the machine; a compilation is a property of the code. Counting compilations, protoc
/// invocations and answers in flight gives a check that cannot flake on a loaded runner and that
/// fails the moment somebody puts a compile on a path that did not have one -- which is the
/// regression that actually happens, and the one a wall-clock threshold would notice last.
/// </para>
/// <para>
/// The milliseconds are measured too, behind <c>PROTOLANG_BENCH</c>. See
/// <c>docs/performance.md</c> for why the two are split this way rather than either being the whole
/// answer.
/// </para>
/// <para>
/// <b>Every number here is exact.</b> Not "at most a few" -- a caret moving through an unedited
/// buffer costs <em>zero</em> further compilations, and an assertion saying so fails on the first
/// one. A bound with slack in it is a bound that absorbs the regression it exists to catch.
/// </para>
/// </remarks>
[Collection("Timing-sensitive regressions")]
public class PerformanceCostTests
{
    /// <summary>
    /// Moving the caret through a buffer nobody has edited compiles nothing further.
    /// </summary>
    /// <remarks>
    /// The claim every caret-driven feature in wave three rests on, asserted rather than argued:
    /// hover, highlighting and go-to-definition all read a model the last keystroke built, so the
    /// hundredth caret move costs what the hundredth lookup costs and not what a compile costs. This
    /// is what licenses consulting the reference index directly at every caret, which is what #51
    /// does and what #57 was asked to confirm.
    /// </remarks>
    [Fact]
    public async Task MovingTheCaretThroughAnUneditedBufferCompilesNothingFurther()
    {
        var workspace = new PerformanceWorkspace(PerformanceCorpus.Stress).Warm();
        var after = workspace.Semantics.Compilations;

        foreach (var offset in Carets(workspace))
        {
            await workspace.Hover.AnswerAsync(
                workspace.Hover.Read(workspace.Ask(offset))!, TestContext.Current.CancellationToken);
            await workspace.Highlights.AnswerAsync(
                workspace.Highlights.Read(workspace.Ask(offset))!, TestContext.Current.CancellationToken);
            await workspace.Definition.AnswerAsync(
                workspace.Definition.Read(workspace.Ask(offset))!, TestContext.Current.CancellationToken);
        }

        Assert.Equal(after, workspace.Semantics.Compilations);
    }

    /// <summary>One edit costs exactly one compilation, however many answers follow it.</summary>
    /// <remarks>
    /// The other half of the same claim. A buffer that changed must be compiled once -- and then
    /// every question asked about it until the next keystroke must be answered from that one
    /// compilation rather than provoking another.
    /// </remarks>
    [Fact]
    public async Task OneEditCostsOneCompilationHoweverManyAnswersFollowIt()
    {
        var workspace = new PerformanceWorkspace(PerformanceCorpus.Stress).Warm();
        var before = workspace.Semantics.Compilations;

        workspace.Documents.Apply(
            workspace.Uri,
            2,
            [new TextDocumentContentChangeEvent { Text = workspace.Text + "\n// edited\n" }]);

        foreach (var offset in Carets(workspace))
        {
            await workspace.Hover.AnswerAsync(
                workspace.Hover.Read(workspace.Ask(offset))!, TestContext.Current.CancellationToken);
            await workspace.Highlights.AnswerAsync(
                workspace.Highlights.Read(workspace.Ask(offset))!, TestContext.Current.CancellationToken);
        }

        Assert.Equal(before + 1, workspace.Semantics.Compilations);
    }

    /// <summary>
    /// Each provider's work is accounted for by the compilation count the host publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One provider per case, which is the whole point of the shape.</b> Seven providers share one
    /// <see cref="ProtoLang.LanguageServer.Hosting.DocumentSemantics"/> in the host, and a change
    /// that gave any of them its own would multiply the cost of a keystroke and pass every
    /// behavioural test in the repository. Asking all of them together would not catch it either --
    /// the count would still reach one, off the others -- so each is asked alone against a fresh
    /// workspace, where a provider that compiled somewhere else leaves the shared counter at
    /// <em>zero</em> while still answering perfectly.
    /// </para>
    /// <para>
    /// It is also what makes <see cref="ProtoLang.LanguageServer.Hosting.DocumentSemantics.Compilations"/>
    /// worth publishing at all, which #58 reports: a counter that some of the work goes around is
    /// worse than no counter, because it reads as an answer.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("hover")]
    [InlineData("highlights")]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("completion")]
    public async Task EachProvidersWorkIsCountedByTheSharedCompilation(string provider)
    {
        var workspace = new PerformanceWorkspace(PerformanceCorpus.Normal);
        var offset = workspace.At("line_total_cents()") + 1;
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(0, workspace.Semantics.Compilations);

        object? answer = provider switch
        {
            "hover" => await workspace.Hover.AnswerAsync(workspace.Hover.Read(workspace.Ask(offset))!, token),
            "highlights" => await workspace.Highlights.AnswerAsync(
                workspace.Highlights.Read(workspace.Ask(offset))!, token),
            "definition" => await workspace.Definition.AnswerAsync(
                workspace.Definition.Read(workspace.Ask(offset))!, token),
            "references" => await workspace.References.AnswerAsync(
                workspace.References.Read(new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = workspace.Uri.ToString() },
                    Position = workspace.Ask(offset).Position,
                    Context = new ReferenceContext { IncludeDeclaration = true },
                })!,
                includeDeclaration: true,
                token),
            "completion" => await workspace.Completion.AnswerAsync(
                workspace.Completion.Read(new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = workspace.Uri.ToString() },
                    Position = workspace.Ask(workspace.At("item.") + "item.".Length).Position,
                })!,
                token),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "not a provider"),
        };

        Assert.NotNull(answer);
        Assert.Equal(1, workspace.Semantics.Compilations);
    }

    /// <summary>
    /// The descriptor cache is bounded, and the bound is a number rather than an emergent one.
    /// </summary>
    /// <remarks>
    /// #48 asked for this and could not answer it without measurement; the measurement is in
    /// <c>docs/performance.md</c> and the bound is here. What this pins is that a bound exists and is
    /// stated -- a session lasting all day must not grow an entry per file it ever touched, and a
    /// descriptor set carrying source info is not small.
    /// </remarks>
    [Fact]
    public void TheDescriptorCacheBoundIsAStatedNumber()
    {
        Assert.True(
            ProtoLang.Binding.DescriptorCache.DefaultCapacity > 0,
            "an unbounded descriptor cache is a leak with a good reputation");

        var documented = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "docs", "performance.md"));

        Assert.True(
            documented.Contains(
                $"cache capacity of {ProtoLang.Binding.DescriptorCache.DefaultCapacity}",
                StringComparison.Ordinal),
            "docs/performance.md must state the capacity it measured the footprint of");
    }

    /// <summary>A handful of carets spread across the file, including the expensive one.</summary>
    /// <remarks>
    /// Spread rather than repeated, because a single caret asked twenty times would pass this test
    /// against a provider that cached one answer and compiled for every other position.
    /// </remarks>
    private static IEnumerable<int> Carets(PerformanceWorkspace workspace)
    {
        var text = workspace.Text;
        var shared = workspace.Which == PerformanceCorpus.Stress
            ? StressCorpus.Shared
            : "line_total_cents";

        var found = 0;

        for (var offset = text.IndexOf(shared, StringComparison.Ordinal);
             offset >= 0 && found < 12;
             offset = text.IndexOf(shared, offset + 1, StringComparison.Ordinal))
        {
            found++;
            yield return offset + 1;
        }
    }
}
