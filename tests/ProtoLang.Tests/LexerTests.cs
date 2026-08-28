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
}
