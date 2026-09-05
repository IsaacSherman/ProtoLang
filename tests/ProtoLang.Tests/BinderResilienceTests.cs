using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The binder must survive whatever the parser hands it, because the parser now hands it everything.
/// </summary>
/// <remarks>
/// <para>
/// The companion to <see cref="ParserResilienceTests"/>, one stage further down. Until binding ran
/// unconditionally the binder only ever saw trees from files that parsed cleanly, so every shape
/// recovery produces -- a method with no name, a type reference to nothing, an expression that
/// abandoned itself at the nesting budget -- is new to it. A long-lived host cannot be taken down by
/// any of them.
/// </para>
/// <para>
/// Descriptors are loaded once and shared, which is what makes a sweep affordable here at all:
/// <c>protoc</c> costs more than every bind in this file put together. A <see cref="Binder"/> is
/// cheap and holds the diagnostics it was given, so each input gets its own.
/// </para>
/// </remarks>
[Collection("Timing-sensitive regressions")]
public class BinderResilienceTests
{
    /// <summary>A hang guard for one bounded batch, not a throughput target for an entire file.</summary>
    private static readonly TimeSpan BindBudget = TimeSpan.FromSeconds(60);
    private const int DeletionBatchSize = 128;

    private static readonly Lazy<IReadOnlyList<FileDescriptor>> Schemas = new(LoadSchemas);

    private static IrModule Bind(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "fuzz.protolang", diagnostics).Tokenize();
        var unit = new Parser(tokens, "fuzz.protolang", diagnostics).ParseCompilationUnit();

        return new Binder(Schemas.Value, diagnostics).Bind(unit);
    }

    /// <summary>Runs a sweep under a time limit, failing rather than hanging the test run.</summary>
    /// <remarks>
    /// The sweep is told to stop before the failure is reported, so a bind that is merely slow does
    /// not go on grinding through the rest of the corpus, on a core the remaining tests want, long
    /// after this one has already failed. A single bind that never returns at all is past the reach
    /// of anything here: the binder takes no cancellation token, and adding one to the compiler for
    /// a test to hold is what <c>#54</c> -- process supervision, cancellation, and timeouts -- is
    /// for. The failure is still reported, which is what this exists to do.
    /// </remarks>
    private static async Task WithinBudget(string description, Action<CancellationToken> sweep)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var token = stop.Token;
        var task = Task.Run(() => sweep(token), token);

        try
        {
            await task.WaitAsync(BindBudget, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException) when (!task.IsFaulted)
        {
            Assert.Fail($"Binding did not terminate within {BindBudget.TotalSeconds:0}s: {description}");
        }
        finally
        {
            stop.Cancel();
        }
    }

    /// <summary>
    /// Truncating a good file at every token boundary is the issue's own cheap approximation of a
    /// file being typed, and unlike truncating at every character it never splits a token, so what
    /// reaches the binder is a prefix a real editor could have held.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParserResilienceTests.Corpus), MemberType = typeof(ParserResilienceTests))]
    public async Task TruncationAtEveryTokenBoundaryBinds(string path)
    {
        var source = File.ReadAllText(path);
        var boundaries = new Lexer(source, path, new DiagnosticBag())
            .Tokenize()
            .Select(token => token.Span.End.Offset)
            .Distinct();

        await WithinBudget(
            $"token-boundary truncations of {Path.GetFileName(path)}",
            stop =>
            {
                foreach (var boundary in boundaries)
                {
                    if (stop.IsCancellationRequested)
                    {
                        return;
                    }

                    Assert.NotNull(Bind(source[..boundary]));
                }
            });
    }

    /// <summary>
    /// Deleting one character models a typo more closely than truncation, and it is how unbalanced
    /// braces appear in the middle of a file rather than only at its end.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParserResilienceTests.Corpus), MemberType = typeof(ParserResilienceTests))]
    public async Task SingleCharacterDeletionBinds(string path)
    {
        var source = File.ReadAllText(path);

        // Every deletion still runs. Batching keeps a larger corpus from exhausting a deadline
        // merely by making progress, while a stuck bind still fails within one batch's budget.
        for (var start = 0; start < source.Length; start += DeletionBatchSize)
        {
            var first = start;
            var end = Math.Min(first + DeletionBatchSize, source.Length);
            await WithinBudget(
                $"single-character deletions of {Path.GetFileName(path)}, offsets {first} through {end - 1}",
                stop =>
                {
                    for (var index = first; index < end && !stop.IsCancellationRequested; index++)
                    {
                        Assert.NotNull(Bind(source.Remove(index, 1)));
                    }
                });
        }
    }

    /// <summary>
    /// The shapes the issue calls out by name, plus the ones where recovery leaves a name behind
    /// that no lookup should ever be asked about.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData(".")]
    [InlineData(";")]
    [InlineData("extend")]
    [InlineData("extend {")]
    [InlineData("extend InvoiceItem {")]
    [InlineData("extend InvoiceItem { fn")]
    [InlineData("extend InvoiceItem { fn f(")]
    [InlineData("extend InvoiceItem { fn (x: void) -> int64 { return 1; } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return 1; } fn f(a: int64) -> int64 { return a; } }")]
    [InlineData("extend InvoiceItem { fn f() -> { return . } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity. } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity...; } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { var : = ; } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { for in { } } }")]
    [InlineData("extend . { }")]
    [InlineData("test . \"x\" { }")]
    [InlineData("test InvoiceItem. \"x\" { receiver { . } expect return ; }")]
    [InlineData("test InvoiceItem.f \"x\" { receiver { = 1; } arg = 1; expect return 1; }")]
    public async Task MalformedInputBinds(string body)
    {
        await WithinBudget($"'{body}'", _ => Assert.NotNull(Bind("import proto \"invoice.proto\";\n" + body)));
    }

    /// <summary>
    /// The parser stops descending at its nesting budget; the binder walks whatever that left. The
    /// budget is what protects both, so it has to hold for a tree that reached it.
    /// </summary>
    [Fact]
    public async Task ATreeThatExhaustedTheNestingBudgetBinds()
    {
        const int Depth = 5_000;

        var source = "import proto \"invoice.proto\";\n"
            + "extend InvoiceItem { fn f() -> int64 { return "
            + new string('(', Depth) + "1" + new string(')', Depth) + "; } }";

        await WithinBudget($"{Depth} levels of parentheses", _ => Assert.NotNull(Bind(source)));
    }

    /// <remarks>
    /// The two schemas the corpus imports. Loaded together in one <c>protoc</c> run, because the
    /// sweeps above bind tens of thousands of trees and must not pay for a process each.
    /// </remarks>
    private static IReadOnlyList<FileDescriptor> LoadSchemas()
        => DescriptorLoader.CreateDefault().Load(
            ["invoice.proto", "conformance.proto"],
            [
                TestPaths.ExampleProtoDirectory,
                Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos"),
            ]);
}
