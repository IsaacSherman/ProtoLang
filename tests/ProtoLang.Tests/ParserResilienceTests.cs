using ProtoLang.Diagnostics;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The parser must survive arbitrary input, because an editor hands it arbitrary input constantly:
/// every file is malformed while it is being typed.
/// </summary>
/// <remarks>
/// <para>
/// The bar here is higher than "reports a sensible error". A StackOverflowException cannot be
/// caught -- it terminates the process, skipping every handler and every finally block. In the CLI
/// that is a bad crash. In a long-lived language server it kills the session, and no amount of
/// defensive handling around the call site can prevent it, so the parser itself has to be the thing
/// that holds.
/// </para>
/// <para>
/// These tests stay at the lexer-and-parser level rather than running whole compilations. They need
/// no protoc, so they are fast enough to run thousands of inputs, which is what makes the sweeps
/// below worth having.
/// </para>
/// </remarks>
public class ParserResilienceTests
{
    /// <summary>Guards against a hang. A parse this slow is a bug, not a slow machine.</summary>
    private static readonly TimeSpan ParseBudget = TimeSpan.FromSeconds(30);

    private static DiagnosticBag Parse(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "fuzz.protolang", diagnostics).Tokenize();
        new Parser(tokens, "fuzz.protolang", diagnostics).ParseCompilationUnit();
        return diagnostics;
    }

    /// <summary>Runs a parse under a time limit, failing rather than hanging the test run.</summary>
    private static void WithinBudget(string description, Action parse)
    {
        var task = Task.Run(parse);

        if (!task.Wait(ParseBudget))
        {
            Assert.Fail($"Parsing did not terminate within {ParseBudget.TotalSeconds:0}s: {description}");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Real sources, used as the seed for the mutation sweeps. Anything committed to the repository
    /// is a realistic shape, which makes its prefixes realistic half-typed shapes.
    /// </summary>
    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string> { TestPaths.SimpleScript };

        var vectors = Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "vectors");
        if (Directory.Exists(vectors))
        {
            // Sorted so a failure names the same file on every machine.
            foreach (var file in Directory
                         .GetFiles(vectors, "*.protolang", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                data.Add(file);
            }
        }

        return data;
    }

    /// <summary>
    /// Truncating a good file at every offset is the cheapest brutal approximation of "the user is
    /// halfway through typing this", and it covers every construct the file happens to contain.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void TruncationAtEveryOffsetTerminates(string path)
    {
        var source = File.ReadAllText(path);

        WithinBudget(
            $"truncations of {Path.GetFileName(path)}",
            () =>
            {
                for (var length = 0; length <= source.Length; length++)
                {
                    Parse(source[..length]);
                }
            });
    }

    /// <summary>
    /// Deleting one character models a real typo more closely than truncation, and it produces
    /// unbalanced delimiters in the middle of a file rather than only at the end.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void SingleCharacterDeletionTerminates(string path)
    {
        var source = File.ReadAllText(path);

        WithinBudget(
            $"single-character deletions of {Path.GetFileName(path)}",
            () =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    Parse(source.Remove(index, 1));
                }
            });
    }

    /// <summary>
    /// The specific shape that used to recurse forever: a token inside a fixture that is neither a
    /// field name nor a closing brace. Every Expect failed without consuming, and the recursive call
    /// then re-entered on an unchanged position.
    /// </summary>
    [Theory]
    [InlineData(";")]
    [InlineData("=")]
    [InlineData("42")]
    [InlineData("return")]
    [InlineData("+ + +")]
    [InlineData("a = ;")]
    public void StrayTokenInTestFixtureTerminates(string stray)
    {
        WithinBudget(
            $"stray '{stray}' in a receiver fixture",
            () =>
            {
                var diagnostics = Parse(
                    $$"""
                      import proto "invoice.proto";

                      test InvoiceItem.f "stray" {
                          receiver { {{stray}} }
                          expect return 1;
                      }
                      """);

                Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            });
    }

    /// <summary>
    /// Each construct that recurses carries its own nesting budget, so each needs its own check that
    /// the budget is what gets reached rather than the stack.
    /// </summary>
    [Theory]
    [InlineData("parentheses")]
    [InlineData("prefix operators")]
    [InlineData("blocks")]
    [InlineData("call arguments")]
    [InlineData("message fixtures")]
    public void DeepNestingIsReportedRatherThanOverflowing(string construct)
    {
        // Comfortably past the parser's budget, and roughly ten times the depth that used to
        // overflow the stack.
        const int Depth = 5_000;

        var source = construct switch
        {
            "parentheses" =>
                Body($"return {new string('(', Depth)}1{new string(')', Depth)};"),
            "prefix operators" =>
                Body($"return {string.Concat(Enumerable.Repeat("not ", Depth))}true;"),
            "blocks" =>
                Body(new string('{', Depth) + new string('}', Depth)),
            "call arguments" =>
                Body($"return {string.Concat(Enumerable.Repeat("f(", Depth))}1{new string(')', Depth)};"),
            "message fixtures" =>
                $$"""
                  import proto "invoice.proto";

                  test InvoiceItem.f "deep" {
                      receiver { {{string.Concat(Enumerable.Repeat("a {", Depth))}}{{new string('}', Depth)}} }
                      expect return 1;
                  }
                  """,
            _ => throw new ArgumentOutOfRangeException(nameof(construct), construct, "Unknown construct."),
        };

        WithinBudget(
            $"{Depth} levels of {construct}",
            () =>
            {
                var diagnostics = Parse(source);

                Assert.Contains(
                    diagnostics,
                    d => d.Code == "PL0081" && d.Severity == DiagnosticSeverity.Error);

                // Reported once, however deep it went. One diagnostic per enclosing level would
                // bury every other error in the file.
                Assert.Equal(1, diagnostics.Count(d => d.Code == "PL0081"));
            });
    }

    /// <summary>Nesting a real program could plausibly contain must still parse cleanly.</summary>
    [Fact]
    public void OrdinaryNestingIsWellWithinTheBudget()
    {
        const int Depth = 40;

        var diagnostics = Parse(Body($"return {new string('(', Depth)}1{new string(')', Depth)};"));

        Assert.Empty(diagnostics);
    }

    private static string Body(string statements)
        => $$"""
             import proto "invoice.proto";

             extend InvoiceItem {
                 fn f() -> int64 {
                     {{statements}}
                 }
             }
             """;
}
