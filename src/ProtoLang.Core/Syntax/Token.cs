using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

/// <summary>
/// A lexed token. <paramref name="Text"/> is the raw source text; <paramref name="Value"/>
/// carries the decoded value for literals (a <see cref="long"/>, <see cref="double"/>, or
/// <see cref="string"/>) and is null otherwise.
/// </summary>
public sealed record Token(TokenKind Kind, string Text, SourceSpan Span, object? Value = null)
{
    public override string ToString() => $"{Kind} '{Text}'";
}

public static class TokenKindExtensions
{
    private static readonly Dictionary<TokenKind, string> DisplayText = new()
    {
        [TokenKind.OpenBrace] = "{",
        [TokenKind.CloseBrace] = "}",
        [TokenKind.OpenParen] = "(",
        [TokenKind.CloseParen] = ")",
        [TokenKind.Semicolon] = ";",
        [TokenKind.Comma] = ",",
        [TokenKind.Colon] = ":",
        [TokenKind.Dot] = ".",
        [TokenKind.Arrow] = "->",
        [TokenKind.Plus] = "+",
        [TokenKind.Minus] = "-",
        [TokenKind.Star] = "*",
        [TokenKind.Slash] = "/",
        [TokenKind.Percent] = "%",
        [TokenKind.Equals] = "=",
        [TokenKind.EqualsEquals] = "==",
        [TokenKind.BangEquals] = "!=",
        [TokenKind.Bang] = "!",
        [TokenKind.Less] = "<",
        [TokenKind.LessEquals] = "<=",
        [TokenKind.Greater] = ">",
        [TokenKind.GreaterEquals] = ">=",
        [TokenKind.AmpersandAmpersand] = "&&",
        [TokenKind.PipePipe] = "||",
        [TokenKind.OnZero] = "on_zero",
        [TokenKind.Identifier] = "identifier",
        [TokenKind.IntegerLiteral] = "integer literal",
        [TokenKind.FloatLiteral] = "float literal",
        [TokenKind.StringLiteral] = "string literal",
        [TokenKind.EndOfFile] = "end of file",
    };

    /// <summary>Human-readable spelling used in "expected X" diagnostics.</summary>
    public static string Describe(this TokenKind kind)
        => DisplayText.TryGetValue(kind, out var text) ? text : kind.ToString().ToLowerInvariant();
}
