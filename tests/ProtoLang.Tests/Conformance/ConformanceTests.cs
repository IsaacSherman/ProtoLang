using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests.Conformance;

/// <summary>
/// Static checks over the conformance corpus. These need only protoc, so they always run and give a
/// clear diagnosis when a vector itself is broken, rather than leaving it to be inferred from a
/// failed build in one of the execution tests.
/// </summary>
public class ConformanceVectorTests
{
    [Fact]
    public void TheCorpusIsNotEmpty()
    {
        Assert.True(
            ConformanceVectors.All.Count > 0,
            $"No conformance vectors were found under {ConformanceVectors.VectorDirectory}.");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryVectorCompilesAndDeclaresTests(string name)
    {
        var vector = ConformanceVectors.ByName(name);
        var result = ConformanceVectors.Compile(vector);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        // A vector with no test blocks would compile, generate, build, and prove nothing.
        Assert.True(
            result.Module!.Tests.Count > 0,
            $"'{name}' declares no test blocks, so running it would assert nothing.");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryVectorExtendsItsOwnMessage(string name)
    {
        // Every vector is compiled into one C# assembly, and the backend names its extension class
        // after the receiver. Two vectors sharing a receiver would emit that class twice.
        var receivers = ConformanceVectors.Compile(ConformanceVectors.ByName(name))
            .Module!.Methods
            .Select(method => method.Receiver.FullName)
            .Distinct(StringComparer.Ordinal);

        var others = ConformanceVectors.All
            .Where(vector => !string.Equals(vector.Name, name, StringComparison.Ordinal))
            .SelectMany(vector => ConformanceVectors.Compile(vector).Module!.Methods)
            .Select(method => method.Receiver.FullName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var receiver in receivers)
        {
            Assert.False(
                others.Contains(receiver),
                $"'{name}' extends '{receiver}', which another vector also extends. Give each vector "
                + "its own message in conformance.proto.");
        }
    }

    public static TheoryData<string> Names => ConformanceVectors.Names;
}

/// <summary>
/// Executes the conformance corpus in every backend and requires them to agree.
/// </summary>
/// <remarks>
/// This is the assertion the project exists to make. Golden tests over emitted source only say that
/// a backend emits what it emitted last time; these compile and run the generated code, in both
/// languages, against expectations written once in ProtoLang.
/// </remarks>
public class ConformanceTests : IClassFixture<ConformanceFixture>
{
    private readonly ConformanceFixture _fixture;

    public ConformanceTests(ConformanceFixture fixture) => _fixture = fixture;

    [Fact]
    public void CSharpRunsEveryConformanceVector() => AssertBackendAgrees(_fixture.CSharp);

    [Fact]
    public void CppRunsEveryConformanceVector() => AssertBackendAgrees(_fixture.Cpp);

    /// <summary>
    /// The cross-language check. Each backend passing on its own is not enough: a backend that
    /// silently ran a smaller set of vectors would also pass, and comparing the two observed sets
    /// against the declared one is what rules that out.
    /// </summary>
    [Fact]
    public void BothBackendsRunTheSameVectors()
    {
        SkipIfUnavailable(_fixture.CSharp);
        SkipIfUnavailable(_fixture.Cpp);

        var declared = _fixture.DeclaredIdentities.ToHashSet(StringComparer.Ordinal);
        var csharp = _fixture.CSharp.Identities.ToHashSet(StringComparer.Ordinal);
        var cpp = _fixture.Cpp.Identities.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.Count, _fixture.DeclaredIdentities.Count);
        AssertSameSet("declared", declared, "csharp", csharp);
        AssertSameSet("declared", declared, "cpp", cpp);
        AssertSameSet("csharp", csharp, "cpp", cpp);
    }

    private void AssertBackendAgrees(ConformanceRun run)
    {
        SkipIfUnavailable(run);

        Assert.True(
            run.Results.Count == _fixture.DeclaredIdentities.Count,
            run.Describe(
                $"reported {run.Results.Count} result(s) for {_fixture.DeclaredIdentities.Count} "
                + "declared test(s)"));

        Assert.True(run.NotPassed.Count == 0, run.Describe($"{run.NotPassed.Count} vector(s) did not pass"));
    }

    private static void SkipIfUnavailable(ConformanceRun run)
    {
        if (run.SkipReason is not null)
        {
            Assert.Skip($"Conformance vectors skipped for the {run.Backend} backend. {run.SkipReason}");
        }
    }

    private static void AssertSameSet(
        string leftName,
        IReadOnlySet<string> left,
        string rightName,
        IReadOnlySet<string> right)
    {
        var missing = left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var extra = right.Except(left, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"{leftName} and {rightName} ran different tests."
            + (missing.Count > 0
                ? $"{Environment.NewLine}  only in {leftName}: {string.Join(", ", missing)}"
                : string.Empty)
            + (extra.Count > 0
                ? $"{Environment.NewLine}  only in {rightName}: {string.Join(", ", extra)}"
                : string.Empty));
    }
}
