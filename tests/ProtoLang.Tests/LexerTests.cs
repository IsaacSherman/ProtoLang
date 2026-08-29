using ProtoLang.Diagnostics;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

public class LexerTests
{
    private static List<Token> Tokenize(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        return new Lexer(text, "test.protolang", diagnostics).Tokenize();
    }

    [Fact]
    public void RecognizesKeywordsAndIdentifiers()
    {
        var tokens = Tokenize("extend fn var for in return test receiver arg expect InvoiceItem", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(
            [
                TokenKind.Extend, TokenKind.Fn, TokenKind.Var, TokenKind.For, TokenKind.In,
                TokenKind.Return, TokenKind.Test, TokenKind.Receiver, TokenKind.Arg, TokenKind.Expect,
                TokenKind.Identifier, TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void RecognizesArrowSeparatelyFromMinus()
    {
        var tokens = Tokenize("-> - >", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(
            [TokenKind.Arrow, TokenKind.Minus, TokenKind.Greater, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void ParsesIntegerLiteralValue()
    {
        var tokens = Tokenize("1234", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(1234L, tokens[0].Value);
    }

    [Fact]
    public void TreatsTrailingDotAsMemberAccessRatherThanFloat()
    {
        // '1.foo' is not a float; the '.' belongs to the member access that follows.
        var tokens = Tokenize("1.foo", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(
            [TokenKind.IntegerLiteral, TokenKind.Dot, TokenKind.Identifier, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void ParsesFloatLiteral()
    {
        var tokens = Tokenize("3.5", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].Kind);
        Assert.Equal(3.5d, tokens[0].Value);
    }

    [Fact]
    public void SkipsLineAndBlockComments()
    {
        var tokens = Tokenize("// comment\n/* block\n spanning lines */ fn", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal([TokenKind.Fn, TokenKind.EndOfFile], tokens.Select(t => t.Kind));
    }

    [Fact]
    public void TracksLineNumbersAcrossBlockComments()
    {
        var tokens = Tokenize("/* one\ntwo */\nfn", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(3, tokens[0].Span.Line);
    }

    [Fact]
    public void ReportsUnterminatedString()
    {
        Tokenize("\"abc", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0008");
    }

    [Fact]
    public void ReportsUnterminatedStringEndingInBackslash()
    {
        var tokens = Tokenize("\"abc\\", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0008");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("abc", tokens[0].Value);
    }

    [Fact]
    public void ReportsUnterminatedBlockComment()
    {
        Tokenize("/* never closed", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0004");
    }

    [Fact]
    public void ReportsIntegerLiteralOutOfRange()
    {
        Tokenize("99999999999999999999", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0006");
    }

    [Fact]
    public void DecodesStringEscapes()
    {
        var tokens = Tokenize("\"a\\tb\\\"c\"", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("a\tb\"c", tokens[0].Value);
    }

    [Fact]
    public void RecognizesControlFlowKeywords()
    {
        var tokens = Tokenize("if else while break continue", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(
            [
                TokenKind.If, TokenKind.Else, TokenKind.While, TokenKind.Break, TokenKind.Continue,
                TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
    }
    // ------------------------------------------------------- where a token says it is

    /// <summary>
    /// The consistency sweep. Every token has to be findable by its offset and describable by its
    /// line and column, and the two have to name the same character. Asserting it over a whole file
    /// costs one loop and covers every path through the lexer at once.
    /// </summary>
    [Fact]
    public void EveryTokenSpanIndexesTheTextItCameFrom()
    {
        const string Text =
            """
            import proto "invoice.proto";

            extend InvoiceItem {
                // a line comment
                fn total(rate float) -> int64 {
                    /* a block
                       comment */
                    var s = "quoted \n text";
                    return quantity * unit_price_cents + 1.5;
                }
            }
            """;

        var tokens = Tokenize(Text, out var diagnostics);
        var lines = new LineMap(Text);

        Assert.Empty(diagnostics);
        Assert.All(
            tokens,
            token =>
            {
                Assert.Equal(token.Text, Text.Substring(token.Span.Start.Offset, token.Span.Length));
                Assert.Equal(token.Span.Start, lines.PositionOf(token.Span.Start.Offset));
                Assert.Equal(token.Span.End, lines.PositionOf(token.Span.End.Offset));
            });
    }

    [Fact]
    public void ColumnsAndOffsetsCountUtf16CodeUnits()
    {
        // U+1D11E MUSICAL SYMBOL G CLEF is one rune and two UTF-16 code units. The lexer indexes a
        // .NET string, so it counts code units, which is what the default LSP position encoding
        // wants. That agreement is worth a test rather than a coincidence: counting runes instead
        // would silently shift every squiggle after the first astral character on the line.
        const string Clef = "\U0001D11E";
        var text = $"/* {Clef} */ var a = \"{Clef}\"; var b = 1;";

        var tokens = Tokenize(text, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(2, Clef.Length);
        Assert.All(
            tokens,
            token =>
            {
                // Everything is on line 1, so a column counting anything else would drift from the
                // offset at the first clef.
                Assert.Equal(token.Span.Start.Offset + 1, token.Span.Start.Column);
                Assert.Equal(token.Text, text.Substring(token.Span.Start.Offset, token.Span.Length));
            });
    }

    [Fact]
    public void ACarriageReturnBeforeANewlineDoesNotShiftTheLineAfterIt()
    {
        const string Text = "var a = 1;\r\nvar b = 2;";

        var tokens = Tokenize(Text, out var diagnostics);

        Assert.Empty(diagnostics);

        var second = tokens.First(t => t.Span.Line == 2);
        Assert.Equal(1, second.Span.Start.Column);
        Assert.Equal(Text.IndexOf("var b", StringComparison.Ordinal), second.Span.Start.Offset);
    }

    [Fact]
    public void AnUnterminatedBlockCommentIsReportedOverItsOpeningDelimiter()
    {
        const string Text = "fn f() {\n    /* never closed\n";

        Tokenize(Text, out var diagnostics);
        var lines = new LineMap(Text);

        var span = Assert.Single(diagnostics, d => d.Code == "PL0004").Span;
        Assert.Equal("/*", Text.Substring(span.Start.Offset, span.Length));
        Assert.Equal(span.Start, lines.PositionOf(span.Start.Offset));
    }

    [Fact]
    public void AnUnrecognizedEscapeIsReportedOverTheBackslashAndWhatItEscapes()
    {
        const string Text = "var s = \"a\\qb\";";

        Tokenize(Text, out var diagnostics);

        var span = Assert.Single(diagnostics, d => d.Code == "PL0007").Span;
        Assert.Equal(Text.IndexOf('\\'), span.Start.Offset);
        Assert.Equal(2, span.Length);
        Assert.Equal("\\q", Text.Substring(span.Start.Offset, span.Length));
    }

    /// <summary>
    /// A range that ends past the end of the buffer is one no editor can render and no position
    /// query can answer, so the escape span has to stay inside the text even when the text stops
    /// in the middle of the escape.
    /// </summary>
    [Fact]
    public void AnEscapeAtTheVeryEndOfTheTextDoesNotRunPastIt()
    {
        const string Text = "var s = \"a\\q";

        Tokenize(Text, out var diagnostics);

        var span = Assert.Single(diagnostics, d => d.Code == "PL0007").Span;
        Assert.True(span.End.Offset <= Text.Length, "the span must not end past the end of the text");
        Assert.Equal("\\q", Text.Substring(span.Start.Offset, span.Length));
    }
}
