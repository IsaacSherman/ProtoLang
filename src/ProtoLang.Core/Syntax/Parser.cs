using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar sketched in spec 7.1. Semicolons are mandatory
/// after statements; spec 7.1 lists that as an open question, and this is the decision.
/// </summary>
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _file;
    private int _position;

    public Parser(IReadOnlyList<Token> tokens, string file, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _file = file;
        _diagnostics = diagnostics;
    }

    private Token Current => Peek(0);

    private Token Peek(int offset)
    {
        var index = Math.Clamp(_position + offset, 0, _tokens.Count - 1);
        return _tokens[index];
    }

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        _diagnostics.Error(
            "PL0010",
            "unexpected token",
            $"Expected {kind.Describe()} but found {Current.Kind.Describe()}.",
            Current.Span);

        // Return a synthetic token so callers can continue building a tree.
        return new Token(kind, string.Empty, Current.Span);
    }

    public CompilationUnit ParseCompilationUnit()
    {
        var start = Current.Span;
        var imports = new List<ImportDeclaration>();
        var extends = new List<ExtendDeclaration>();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            switch (Current.Kind)
            {
                case TokenKind.Import:
                    imports.Add(ParseImportDeclaration());
                    break;

                case TokenKind.Extend:
                    extends.Add(ParseExtendDeclaration());
                    break;

                default:
                    _diagnostics.Error(
                        "PL0011",
                        "unexpected top-level declaration",
                        $"Expected 'import' or 'extend' but found {Current.Kind.Describe()}.",
                        Current.Span,
                        "A ProtoLang file contains only proto imports and extend blocks.");
                    SkipToNextTopLevelDeclaration();
                    break;
            }
        }

        return new CompilationUnit(imports, extends, Spanning(start, Current.Span));
    }

    private void SkipToNextTopLevelDeclaration()
    {
        while (Current.Kind is not (TokenKind.EndOfFile or TokenKind.Import or TokenKind.Extend))
        {
            Advance();
        }
    }

    private ImportDeclaration ParseImportDeclaration()
    {
        var start = Expect(TokenKind.Import).Span;
        Expect(TokenKind.Proto);
        var path = Expect(TokenKind.StringLiteral);
        var end = Expect(TokenKind.Semicolon).Span;

        return new ImportDeclaration((string?)path.Value ?? string.Empty, Spanning(start, end));
    }

    private ExtendDeclaration ParseExtendDeclaration()
    {
        var start = Expect(TokenKind.Extend).Span;
        var name = ParseQualifiedName();
        Expect(TokenKind.OpenBrace);

        var methods = new List<MethodDeclaration>();
        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (Current.Kind is TokenKind.Fn or TokenKind.Virtual)
            {
                methods.Add(ParseMethodDeclaration());
                continue;
            }

            _diagnostics.Error(
                "PL0012",
                "unexpected member in extend block",
                $"Expected 'fn' or 'virtual' but found {Current.Kind.Describe()}.",
                Current.Span,
                "Extend blocks contain methods. Fields belong in the .proto schema.");

            while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile or TokenKind.Fn or TokenKind.Virtual))
            {
                Advance();
            }
        }

        var end = Expect(TokenKind.CloseBrace).Span;
        return new ExtendDeclaration(name, methods, Spanning(start, end));
    }

    /// <summary>Parses <c>Foo</c> or <c>pkg.Foo</c> into a single dotted name.</summary>
    private string ParseQualifiedName()
    {
        var parts = new List<string> { Expect(TokenKind.Identifier).Text };
        while (Current.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance();
            parts.Add(Advance().Text);
        }

        return string.Join('.', parts);
    }

    private MethodDeclaration ParseMethodDeclaration()
    {
        var start = Current.Span;
        var isVirtual = Match(TokenKind.Virtual);

        Expect(TokenKind.Fn);
        var name = Expect(TokenKind.Identifier).Text;

        Expect(TokenKind.OpenParen);
        var parameters = new List<ParameterDeclaration>();
        if (Current.Kind != TokenKind.CloseParen)
        {
            do
            {
                var parameterStart = Current.Span;
                var parameterName = Expect(TokenKind.Identifier).Text;
                Expect(TokenKind.Colon);
                var parameterType = ParseTypeReference();
                parameters.Add(new ParameterDeclaration(
                    parameterName,
                    parameterType,
                    Spanning(parameterStart, parameterType.Span)));
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.CloseParen);

        TypeReference? returnType = null;
        if (Match(TokenKind.Arrow))
        {
            returnType = ParseTypeReference();
        }

        var body = ParseBlock();
        return new MethodDeclaration(name, isVirtual, parameters, returnType, body, Spanning(start, body.Span));
    }

    private TypeReference ParseTypeReference()
    {
        var token = Current;

        // Scalar type keywords and message/enum names both land here; the binder decides which
        // is which by asking the protobuf descriptor pool.
        if (token.Kind is TokenKind.Int32 or TokenKind.Int64 or TokenKind.UInt32 or TokenKind.UInt64
            or TokenKind.Double or TokenKind.Float or TokenKind.Bool or TokenKind.String
            or TokenKind.Bytes or TokenKind.Void)
        {
            Advance();
            return new TypeReference(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Identifier)
        {
            var name = ParseQualifiedName();
            return new TypeReference(name, Spanning(token.Span, Peek(-1).Span));
        }

        _diagnostics.Error(
            "PL0013",
            "expected a type",
            $"Expected a type name but found {token.Kind.Describe()}.",
            token.Span);
        Advance();
        return new TypeReference("<error>", token.Span);
    }

    private BlockStatement ParseBlock()
    {
        var start = Expect(TokenKind.OpenBrace).Span;
        var statements = new List<Statement>();

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var before = _position;
            statements.Add(ParseStatement());

            // Guarantee forward progress even if a statement parser bailed without consuming.
            if (_position == before)
            {
                Advance();
            }
        }

        var end = Expect(TokenKind.CloseBrace).Span;
        return new BlockStatement(statements, Spanning(start, end));
    }

    private Statement ParseStatement()
    {
        return Current.Kind switch
        {
            TokenKind.Var => ParseVariableDeclaration(),
            TokenKind.Return => ParseReturnStatement(),
            TokenKind.For => ParseForInStatement(),
            TokenKind.OpenBrace => ParseBlock(),
            _ => ParseExpressionOrAssignmentStatement(),
        };
    }

    private Statement ParseVariableDeclaration()
    {
        var start = Expect(TokenKind.Var).Span;
        var name = Expect(TokenKind.Identifier).Text;

        TypeReference? declaredType = null;
        if (Match(TokenKind.Colon))
        {
            declaredType = ParseTypeReference();
        }

        Expect(TokenKind.Equals);
        var initializer = ParseExpression();
        var end = Expect(TokenKind.Semicolon).Span;

        return new VariableDeclarationStatement(name, declaredType, initializer, Spanning(start, end));
    }

    private Statement ParseReturnStatement()
    {
        var start = Expect(TokenKind.Return).Span;

        Expression? value = null;
        if (Current.Kind != TokenKind.Semicolon)
        {
            value = ParseExpression();
        }

        var end = Expect(TokenKind.Semicolon).Span;
        return new ReturnStatement(value, Spanning(start, end));
    }

    private Statement ParseForInStatement()
    {
        var start = Expect(TokenKind.For).Span;
        var variable = Expect(TokenKind.Identifier).Text;
        Expect(TokenKind.In);
        var collection = ParseExpression();
        var body = ParseBlock();

        return new ForInStatement(variable, collection, body, Spanning(start, body.Span));
    }

    private Statement ParseExpressionOrAssignmentStatement()
    {
        var start = Current.Span;
        var expression = ParseExpression();

        if (Match(TokenKind.Equals))
        {
            var value = ParseExpression();
            var assignEnd = Expect(TokenKind.Semicolon).Span;
            return new AssignmentStatement(expression, value, Spanning(start, assignEnd));
        }

        var end = Expect(TokenKind.Semicolon).Span;
        return new ExpressionStatement(expression, Spanning(start, end));
    }

    private Expression ParseExpression() => ParseBinaryExpression(0);

    /// <summary>Binding power for infix operators; higher binds tighter.</summary>
    private static int GetBinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 5,
        TokenKind.Plus or TokenKind.Minus => 4,
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals => 3,
        TokenKind.EqualsEquals or TokenKind.BangEquals => 2,
        TokenKind.AmpersandAmpersand or TokenKind.And => 1,
        TokenKind.PipePipe or TokenKind.Or => 0,
        _ => -1,
    };

    private static BinaryOperatorKind ToBinaryOperator(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BinaryOperatorKind.Add,
        TokenKind.Minus => BinaryOperatorKind.Subtract,
        TokenKind.Star => BinaryOperatorKind.Multiply,
        TokenKind.Slash => BinaryOperatorKind.Divide,
        TokenKind.Percent => BinaryOperatorKind.Modulo,
        TokenKind.EqualsEquals => BinaryOperatorKind.Equal,
        TokenKind.BangEquals => BinaryOperatorKind.NotEqual,
        TokenKind.Less => BinaryOperatorKind.LessThan,
        TokenKind.LessEquals => BinaryOperatorKind.LessThanOrEqual,
        TokenKind.Greater => BinaryOperatorKind.GreaterThan,
        TokenKind.GreaterEquals => BinaryOperatorKind.GreaterThanOrEqual,
        TokenKind.AmpersandAmpersand or TokenKind.And => BinaryOperatorKind.LogicalAnd,
        TokenKind.PipePipe or TokenKind.Or => BinaryOperatorKind.LogicalOr,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a binary operator."),
    };

    private Expression ParseBinaryExpression(int minPrecedence)
    {
        var left = ParseUnaryExpression();

        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence < minPrecedence)
            {
                return left;
            }

            var operatorToken = Advance();

            // All binary operators are left-associative, so the right operand must bind strictly
            // tighter to be absorbed into this level.
            var right = ParseBinaryExpression(precedence + 1);
            var op = ToBinaryOperator(operatorToken.Kind);
            var onZero = ParseOnZeroClause(op, operatorToken);

            left = new BinaryExpression(
                op,
                left,
                right,
                Spanning(left.Span, onZero?.Span ?? right.Span),
                onZero);
        }
    }

    /// <summary>
    /// Parses the <c>on_zero &lt;fallback&gt;</c> suffix that integer division requires.
    /// </summary>
    /// <remarks>
    /// The clause binds to the single division it follows, so <c>x + a / b on_zero 0</c> means
    /// <c>x + (a / b on_zero 0)</c>. The fallback itself is parsed at unary precedence, so anything
    /// more involved than a literal, name, or call must be parenthesized. That keeps
    /// <c>a / b on_zero 0 + 1</c> from being ambiguous.
    /// </remarks>
    private OnZeroClause? ParseOnZeroClause(BinaryOperatorKind op, Token operatorToken)
    {
        if (Current.Kind != TokenKind.OnZero)
        {
            return null;
        }

        var onZeroToken = Advance();

        if (op is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Modulo))
        {
            _diagnostics.Error(
                "PL0015",
                "on_zero is only valid on division",
                $"'on_zero' cannot be applied to '{operatorToken.Text}'.",
                onZeroToken.Span,
                "Only '/' and '%' can fail on a zero operand.");
        }

        // 'on_zero fail' says there is no correct value to substitute, so the program stops.
        if (Current.Kind == TokenKind.Fail)
        {
            var failToken = Advance();
            return new OnZeroClause(null, Spanning(onZeroToken.Span, failToken.Span));
        }

        var fallback = ParseUnaryExpression();
        return new OnZeroClause(fallback, Spanning(onZeroToken.Span, fallback.Span));
    }

    private Expression ParseUnaryExpression()
    {
        var token = Current;

        if (token.Kind is TokenKind.Minus or TokenKind.Bang or TokenKind.Not)
        {
            Advance();
            var operand = ParseUnaryExpression();
            var op = token.Kind == TokenKind.Minus ? UnaryOperatorKind.Negate : UnaryOperatorKind.LogicalNot;
            return new UnaryExpression(op, operand, Spanning(token.Span, operand.Span));
        }

        return ParsePostfixExpression();
    }

    private Expression ParsePostfixExpression()
    {
        var expression = ParsePrimaryExpression();

        while (true)
        {
            if (Current.Kind == TokenKind.Dot)
            {
                Advance();
                var name = Expect(TokenKind.Identifier);
                expression = new MemberAccessExpression(expression, name.Text, Spanning(expression.Span, name.Span));
                continue;
            }

            if (Current.Kind == TokenKind.OpenParen)
            {
                Advance();
                var arguments = new List<Expression>();
                if (Current.Kind != TokenKind.CloseParen)
                {
                    do
                    {
                        arguments.Add(ParseExpression());
                    }
                    while (Match(TokenKind.Comma));
                }

                var end = Expect(TokenKind.CloseParen).Span;
                expression = new InvocationExpression(expression, arguments, Spanning(expression.Span, end));
                continue;
            }

            return expression;
        }
    }

    private Expression ParsePrimaryExpression()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.IntegerLiteral:
                Advance();
                return new IntegerLiteralExpression((long)(token.Value ?? 0L), token.Span);

            case TokenKind.FloatLiteral:
                Advance();
                return new FloatLiteralExpression((double)(token.Value ?? 0d), token.Span);

            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpression((string?)token.Value ?? string.Empty, token.Span);

            case TokenKind.True:
                Advance();
                return new BooleanLiteralExpression(true, token.Span);

            case TokenKind.False:
                Advance();
                return new BooleanLiteralExpression(false, token.Span);

            case TokenKind.Identifier:
                Advance();
                return new NameExpression(token.Text, token.Span);

            case TokenKind.OpenParen:
            {
                Advance();
                var inner = ParseExpression();
                Expect(TokenKind.CloseParen);
                return inner;
            }

            default:
                _diagnostics.Error(
                    "PL0014",
                    "expected an expression",
                    $"Expected an expression but found {token.Kind.Describe()}.",
                    token.Span);
                Advance();
                return new ErrorExpression(token.Span);
        }
    }

    private SourceSpan Spanning(SourceSpan start, SourceSpan end)
    {
        var length = Math.Max(end.Length, 1);
        if (start.Line == end.Line && end.Column >= start.Column)
        {
            length = end.Column - start.Column + end.Length;
        }

        return new SourceSpan(_file, start.Line, start.Column, length);
    }
}
