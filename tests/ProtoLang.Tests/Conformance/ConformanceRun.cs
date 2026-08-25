namespace ProtoLang.Tests.Conformance;

internal enum ConformanceOutcome
{
    /// <summary>The backend ran the test and it passed.</summary>
    Passed,

    /// <summary>The backend ran the test and it failed.</summary>
    Failed,

    /// <summary>The backend never reported this test at all.</summary>
    Missing,
}

/// <param name="Identity">The backend-independent test name, from <c>IrTest.Identity</c>.</param>
internal sealed record ConformanceResult(string Identity, ConformanceOutcome Outcome, string Detail);

/// <summary>
/// What one backend did with the whole corpus.
/// </summary>
/// <param name="SkipReason">
/// Non-null when this backend could not run at all, because a tool it needs is not installed.
/// </param>
/// <param name="Workspace">
/// Where the generated sources were written. Reported in failure messages so a failing run can be
/// inspected and rebuilt by hand.
/// </param>
internal sealed record ConformanceRun(
    string Backend,
    string? SkipReason,
    IReadOnlyList<ConformanceResult> Results,
    string Workspace,
    string Output)
{
    public static ConformanceRun Skipped(string backend, string reason)
        => new(backend, reason, [], string.Empty, string.Empty);

    public IReadOnlyList<string> Identities => Results.Select(result => result.Identity).ToList();

    public IReadOnlyList<ConformanceResult> NotPassed
        => Results.Where(result => result.Outcome != ConformanceOutcome.Passed).ToList();

    /// <summary>A failure message that names the backend, what went wrong, and where to look.</summary>
    public string Describe(string problem)
    {
        var lines = new List<string> { $"{Backend}: {problem}" };

        foreach (var result in NotPassed)
        {
            lines.Add($"  {result.Outcome}: {result.Identity}");
            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                lines.Add($"    {result.Detail}");
            }
        }

        if (!string.IsNullOrEmpty(Workspace))
        {
            lines.Add($"  generated sources: {Workspace}");
        }

        if (!string.IsNullOrWhiteSpace(Output))
        {
            lines.Add(string.Empty);
            lines.Add(Output);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
