using System.Globalization;
using System.Text;
using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

/// <summary>
/// Converts ProtoLang source text into a token stream. Comments and whitespace are discarded;
/// both line (<c>//</c>) and block (<c>/* */</c>) comments are accepted, though spec 6.2 still
/// lists block comments as an open question for version 1.
/// </summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["and"] = TokenKind.And,
        ["arg"] = TokenKind.Arg,
        ["bool"] = TokenKind.Bool,
        ["break"] = TokenKind.Break,
        ["bytes"] = TokenKind.Bytes,
        ["case"] = TokenKind.Case,
        ["continue"] = TokenKind.Continue,
        ["double"] = TokenKind.Double,
        ["else"] = TokenKind.Else,
        ["enum"] = TokenKind.Enum,
        ["extend"] = TokenKind.Extend,
        ["expect"] = TokenKind.Expect,
        ["fail"] = TokenKind.Fail,
        ["false"] = TokenKind.False,
        ["float"] = TokenKind.Float,
        ["fn"] = TokenKind.Fn,
        ["for"] = TokenKind.For,
        ["if"] = TokenKind.If,
        ["import"] = TokenKind.Import,
        ["in"] = TokenKind.In,
        ["int32"] = TokenKind.Int32,
        ["int64"] = TokenKind.Int64,
        ["message"] = TokenKind.Message,
        ["not"] = TokenKind.Not,
        ["on_zero"] = TokenKind.OnZero,
        ["or"] = TokenKind.Or,
        ["proto"] = TokenKind.Proto,
        ["receiver"] = TokenKind.Receiver,
        ["return"] = TokenKind.Return,
        ["string"] = TokenKind.String,
        ["switch"] = TokenKind.Switch,
        ["test"] = TokenKind.Test,
        ["true"] = TokenKind.True,
        ["uint32"] = TokenKind.UInt32,
        ["uint64"] = TokenKind.UInt64,
        ["var"] = TokenKind.Var,
        ["virtual"] = TokenKind.Virtual,
        ["void"] = TokenKind.Void,
        ["while"] = TokenKind.While,
    };

    private readonly string _text;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;

    private int _position;
    private int _line = 1;
    private int _lineStart;

    public Lexer(string text, string file, DiagnosticBag diagnostics)
    {
        _text = text;
        _file = file;
        _diagnostics = diagnostics;
    }

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    private char Peek(int offset)
    {
        var index = _position + offset;
        return index >= _text.Length ? '\0' : _text[index];
    }

    private int Column => _position - _lineStart + 1;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
            {
                return tokens;
            }
        }
    }

    private Token NextToken()
    {
        SkipTrivia();

        var start = _position;
        var startLine = _line;
        var startColumn = Column;

        if (_position >= _text.Length)
        {
            return new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(_file, startLine, startColumn, 0));
        }

        var current = Current;

        if (char.IsLetter(current) || current == '_')
        {
            return LexIdentifierOrKeyword(start, startLine, startColumn);
        }

        if (char.IsDigit(current))
        {
            return LexNumber(start, startLine, startColumn);
        }

        if (current == '"')
        {
            return LexString(start, startLine, startColumn);
        }

        return LexOperator(start, startLine, startColumn);
    }

    private void SkipTrivia()
    {
        while (_position < _text.Length)
        {
            var current = Current;

            if (current == '\n')
            {
                Advance();
                _line++;
                _lineStart = _position;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                Advance();
                continue;
            }

            if (current == '/' && Lookahead == '/')
            {
                while (_position < _text.Length && Current != '\n')
                {
                    Advance();
                }

                continue;
            }

            if (current == '/' && Lookahead == '*')
            {
                var commentLine = _line;
                var commentColumn = Column;
                Advance();
                Advance();

                var closed = false;
                while (_position < _text.Length)
                {
                    if (Current == '*' && Lookahead == '/')
                    {
                        Advance();
                        Advance();
                        closed = true;
                        break;
                    }

                    if (Current == '\n')
                    {
                        Advance();
                        _line++;
                        _lineStart = _position;
                        continue;
                    }

                    Advance();
                }

                if (!closed)
                {
                    _diagnostics.Error(
                        "PL0004",
                        "unterminated block comment",
                        "Reached end of file while scanning a block comment.",
                        new SourceSpan(_file, commentLine, commentColumn, 2),
                        "Close the comment with '*/'.");
                }

                continue;
            }

            return;
        }
    }

    private void Advance() => _position++;

    private Token LexIdentifierOrKeyword(int start, int line, int column)
    {
        while (_position < _text.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
        {
            Advance();
        }

        var text = _text[start.._position];
        var kind = Keywords.TryGetValue(text, out var keyword) ? keyword : TokenKind.Identifier;
        return new Token(kind, text, new SourceSpan(_file, line, column, text.Length));
    }

    private Token LexNumber(int start, int line, int column)
    {
        while (_position < _text.Length && char.IsDigit(Current))
        {
            Advance();
        }

        var isFloat = false;

        // A '.' only begins a fractional part when a digit follows it; otherwise it is member
        // access on an integer-looking expression and belongs to the next token.
        if (Current == '.' && char.IsDigit(Lookahead))
        {
            isFloat = true;
            Advance();
            while (_position < _text.Length && char.IsDigit(Current))
            {
                Advance();
            }
        }

        var text = _text[start.._position];
        var span = new SourceSpan(_file, line, column, text.Length);

        if (isFloat)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
            {
                _diagnostics.Error(
                    "PL0005",
                    "invalid float literal",
                    $"'{text}' is not a valid floating-point literal.",
                    span);
                floatValue = 0d;
            }

            return new Token(TokenKind.FloatLiteral, text, span, floatValue);
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            _diagnostics.Error(
                "PL0006",
                "integer literal out of range",
                $"'{text}' does not fit in a 64-bit signed integer.",
                span);
            intValue = 0L;
        }

        return new Token(TokenKind.IntegerLiteral, text, span, intValue);
    }

    private Token LexString(int start, int line, int column)
    {
        Advance(); // opening quote

        var builder = new StringBuilder();
        var terminated = false;

        while (_position < _text.Length)
        {
            var current = Current;

            if (current == '"')
            {
                Advance();
                terminated = true;
                break;
            }

            if (current == '\n')
            {
                break;
            }

            if (current == '\\')
            {
                Advance();
                var escape = Current;
                switch (escape)
                {
                    case 'n': builder.Append('\n'); Advance(); break;
                    case 't': builder.Append('\t'); Advance(); break;
                    case 'r': builder.Append('\r'); Advance(); break;
                    case '\\': builder.Append('\\'); Advance(); break;
                    case '"': builder.Append('"'); Advance(); break;
                    default:
                        _diagnostics.Error(
                            "PL0007",
                            "unrecognized escape sequence",
                            $"'\\{escape}' is not a recognized escape sequence.",
                            new SourceSpan(_file, line, Column, 2));
                        Advance();
                        break;
                }

                continue;
            }

            builder.Append(current);
            Advance();
        }

        var text = _text[start.._position];
        var span = new SourceSpan(_file, line, column, text.Length);

        if (!terminated)
        {
            _diagnostics.Error(
                "PL0008",
                "unterminated string literal",
                "String literals must be closed before the end of the line.",
                span);
        }

        return new Token(TokenKind.StringLiteral, text, span, builder.ToString());
    }

    private Token LexOperator(int start, int line, int column)
    {
        var current = Current;
        TokenKind kind;

        switch (current)
        {
            case '{': Advance(); kind = TokenKind.OpenBrace; break;
            case '}': Advance(); kind = TokenKind.CloseBrace; break;
            case '(': Advance(); kind = TokenKind.OpenParen; break;
            case ')': Advance(); kind = TokenKind.CloseParen; break;
            case ';': Advance(); kind = TokenKind.Semicolon; break;
            case ',': Advance(); kind = TokenKind.Comma; break;
            case ':': Advance(); kind = TokenKind.Colon; break;
            case '.': Advance(); kind = TokenKind.Dot; break;
            case '+': Advance(); kind = TokenKind.Plus; break;
            case '*': Advance(); kind = TokenKind.Star; break;
            case '/': Advance(); kind = TokenKind.Slash; break;
            case '%': Advance(); kind = TokenKind.Percent; break;

            case '-':
                Advance();
                if (Current == '>')
                {
                    Advance();
                    kind = TokenKind.Arrow;
                }
                else
                {
                    kind = TokenKind.Minus;
                }

                break;

            case '=':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.EqualsEquals;
                }
                else
                {
                    kind = TokenKind.Equals;
                }

                break;

            case '!':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.BangEquals;
                }
                else
                {
                    kind = TokenKind.Bang;
                }

                break;

            case '<':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.LessEquals;
                }
                else
                {
                    kind = TokenKind.Less;
                }

                break;

            case '>':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    kind = TokenKind.GreaterEquals;
                }
                else
                {
                    kind = TokenKind.Greater;
                }

                break;

            case '&':
                Advance();
                if (Current == '&')
                {
                    Advance();
                    kind = TokenKind.AmpersandAmpersand;
                }
                else
                {
                    kind = TokenKind.Unknown;
                }

                break;

            case '|':
                Advance();
                if (Current == '|')
                {
                    Advance();
                    kind = TokenKind.PipePipe;
                }
                else
                {
                    kind = TokenKind.Unknown;
                }

                break;

            default:
                Advance();
                kind = TokenKind.Unknown;
                break;
        }

        var text = _text[start.._position];
        var span = new SourceSpan(_file, line, column, text.Length);

        if (kind == TokenKind.Unknown)
        {
            _diagnostics.Error(
                "PL0009",
                "unexpected character",
                $"'{text}' is not valid ProtoLang syntax.",
                span);
        }

        return new Token(kind, text, span);
    }
}
