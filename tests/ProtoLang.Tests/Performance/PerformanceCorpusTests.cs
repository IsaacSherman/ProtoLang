using Xunit;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// That the corpus the budgets are measured against is the corpus in the repository, and that both
/// halves of it are what they are claimed to be.
/// </summary>
/// <remarks>
/// <para>
/// A performance number means nothing without the input it was taken on, and an input nobody can
/// reproduce turns a budget into an anecdote. These are the cheap, always-on checks that keep
/// <c>docs/performance.md</c> honest: the stress file is what its generator produces, both files
/// compile without diagnostics, and the shapes the measurements depend on -- a widely referenced
/// method, a deep scope -- are actually present.
/// </para>
/// <para>
/// The last of those is the one worth having. A generated corpus can drift into being merely long:
/// rename <see cref="StressCorpus.Shared"/> in half the bodies and the file still parses, still
/// binds, still has three thousand lines, and quietly stops measuring what occurrence highlighting
/// costs. So the properties are asserted against the compiled model rather than against the text.
/// </para>
/// </remarks>
public class PerformanceCorpusTests
{
    /// <summary>
    /// The committed stress file is exactly what <see cref="StressCorpus.Generate"/> produces.
    /// </summary>
    /// <remarks>
    /// Without this the file and the generator drift the first time either is edited, and a run
    /// taken today stops being comparable with one taken last month -- which is the only reason to
    /// commit a generated file at all.
    /// </remarks>
    [Fact]
    public void TheCommittedStressFileIsWhatTheGeneratorProduces()
    {
        Assert.Equal(StressCorpus.Generate(), StressCorpus.Text);
    }

    /// <summary>Both halves of the corpus compile clean, so nothing measures an error path.</summary>
    /// <remarks>
    /// A file with diagnostics in it takes a different route through the binder and a different
    /// route through everything downstream of it. Measuring one would produce numbers that are
    /// stable, repeatable and about the wrong thing.
    /// </remarks>
    [Theory]
    [InlineData(PerformanceCorpus.Normal)]
    [InlineData(PerformanceCorpus.Stress)]
    public void EveryCorpusFileCompilesWithoutDiagnostics(string which)
    {
        var compiled = PerformanceCorpus.Compile(which);

        Assert.True(
            compiled.Success,
            $"{which} must compile clean to be measured: "
                + string.Join("\n", compiled.Diagnostics.Select(diagnostic => diagnostic.ToString())));
    }

    /// <summary>
    /// The stress file is larger than the normal one by an order of magnitude, which is what makes
    /// the pair say anything about scale.
    /// </summary>
    [Fact]
    public void TheStressFileIsAnOrderOfMagnitudeLargerThanTheNormalOne()
    {
        var normal = PerformanceCorpus.Lines(PerformanceCorpus.Normal);
        var stress = PerformanceCorpus.Lines(PerformanceCorpus.Stress);

        Assert.True(
            stress > normal * 8,
            $"the stress case must dwarf the normal one to mean anything: {stress} against {normal}");
    }

    /// <summary>
    /// One method in the stress file is referenced from every generated body, so the reference list
    /// occurrence highlighting walks is long rather than typical.
    /// </summary>
    /// <remarks>
    /// Asserted through the semantic model rather than by counting the text, because what
    /// highlighting walks is the reference index and not the file. A count taken from the source
    /// would keep passing if the binder stopped recording half of them, which is the failure this
    /// is here to notice.
    /// </remarks>
    [Fact]
    public void TheStressFileGivesOneMethodAReferenceFromEveryGeneratedBody()
    {
        var references = PerformanceCorpus.ReferencesTo(PerformanceCorpus.Stress, StressCorpus.Shared);

        Assert.True(
            references >= StressCorpus.Steps * 3,
            $"'{StressCorpus.Shared}' must be referenced from every step body: {references} references "
                + $"for {StressCorpus.Steps} steps");
    }

    /// <summary>
    /// The stress file has one body whose scope is deep in locals rather than wide in members.
    /// </summary>
    [Fact]
    public void TheStressFileHasOneBodyWithAScopeDeepInLocals()
    {
        var text = StressCorpus.Text;
        var last = $"return held_{StressCorpus.Locals - 1};";
        var deepest = text.IndexOf(last, StringComparison.Ordinal);

        Assert.True(deepest >= 0, $"the deep body must declare and then read {StressCorpus.Locals} locals");

        var visible = PerformanceCorpus.NamesInScopeAt(PerformanceCorpus.Stress, deepest + "return ".Length);

        Assert.True(
            visible >= StressCorpus.Locals,
            $"a caret at the end of the deep body must see every local above it: {visible} names in scope");
    }
}
