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
public class BinderResilienceTests
{
    /// <summary>Guards against a hang. A bind this slow is a bug, not a slow machine.</summary>
    private static readonly TimeSpan BindBudget = TimeSpan.FromSeconds(60);

    private static readonly Lazy<IReadOnlyList<FileDescriptor>> Schemas = new(LoadSchemas);

    private static IrModule Bind(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "fuzz.protolang", diagnostics).Tokenize();
        var unit = new Parser(tokens, "fuzz.protolang", diagnostics).ParseCompilationUnit();

        return new Binder(Schemas.Value, diagnostics).Bind(unit);
    }

    /// <summary>Runs a sweep under a time limit, failing rather than hanging the test run.</summary>
    private static void WithinBudget(string description, Action bind)
    {
        var task = Task.Run(bind);

        if (!task.Wait(BindBudget))
        {
            Assert.Fail($"Binding did not terminate within {BindBudget.TotalSeconds:0}s: {description}");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Truncating a good file at every token boundary is the issue's own cheap approximation of a
    /// file being typed, and unlike truncating at every character it never splits a token, so what
    /// reaches the binder is a prefix a real editor could have held.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParserResilienceTests.Corpus), MemberType = typeof(ParserResilienceTests))]
    public void TruncationAtEveryTokenBoundaryBinds(string path)
    {
        var source = File.ReadAllText(path);
        var boundaries = new Lexer(source, path, new DiagnosticBag())
            .Tokenize()
            .Select(token => token.Span.End.Offset)
            .Distinct();

        WithinBudget(
            $"token-boundary truncations of {Path.GetFileName(path)}",
            () =>
            {
                foreach (var boundary in boundaries)
                {
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
    public void SingleCharacterDeletionBinds(string path)
    {
        var source = File.ReadAllText(path);

        WithinBudget(
            $"single-character deletions of {Path.GetFileName(path)}",
            () =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    Assert.NotNull(Bind(source.Remove(index, 1)));
                }
            });
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
    [InlineData("extend InvoiceItem { fn f() -> { return . } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity. } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity...; } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { var : = ; } }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { for in { } } }")]
    [InlineData("extend . { }")]
    [InlineData("test . \"x\" { }")]
    [InlineData("test InvoiceItem. \"x\" { receiver { . } expect return ; }")]
    [InlineData("test InvoiceItem.f \"x\" { receiver { = 1; } arg = 1; expect return 1; }")]
    public void MalformedInputBinds(string body)
    {
        WithinBudget($"'{body}'", () => Assert.NotNull(Bind("import proto \"invoice.proto\";\n" + body)));
    }

    /// <summary>
    /// The parser stops descending at its nesting budget; the binder walks whatever that left. The
    /// budget is what protects both, so it has to hold for a tree that reached it.
    /// </summary>
    [Fact]
    public void ATreeThatExhaustedTheNestingBudgetBinds()
    {
        const int Depth = 5_000;

        var source = "import proto \"invoice.proto\";\n"
            + "extend InvoiceItem { fn f() -> int64 { return "
            + new string('(', Depth) + "1" + new string(')', Depth) + "; } }";

        WithinBudget($"{Depth} levels of parentheses", () => Assert.NotNull(Bind(source)));
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
