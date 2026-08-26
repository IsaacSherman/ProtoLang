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
        var tests = new List<TestDeclaration>();

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

                case TokenKind.Test:
                    tests.Add(ParseTestDeclaration());
                    break;

                default:
                    _diagnostics.Error(
                        "PL0011",
                        "unexpected top-level declaration",
                        $"Expected 'import', 'extend', or 'test' but found {Current.Kind.Describe()}.",
                        Current.Span,
                        "A ProtoLang file contains proto imports, extend blocks, and test declarations.");
                    SkipToNextTopLevelDeclaration();
                    break;
            }
        }

        return new CompilationUnit(imports, extends, tests, Spanning(start, Current.Span));
    }

    private void SkipToNextTopLevelDeclaration()
    {
        while (Current.Kind is not (TokenKind.EndOfFile or TokenKind.Import or TokenKind.Extend or TokenKind.Test))
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

    private TestDeclaration ParseTestDeclaration()
    {
        var start = Expect(TokenKind.Test).Span;
        var targetName = ParseQualifiedName();
        var name = Expect(TokenKind.StringLiteral);
        Expect(TokenKind.OpenBrace);

        TestReceiverFixture? receiver = null;
        var arguments = new List<TestArgumentDeclaration>();
        TestExpectation? expectation = null;

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            switch (Current.Kind)
            {
                case TokenKind.Receiver:
                    receiver = ParseTestReceiver();
                    break;

                case TokenKind.Arg:
                    arguments.Add(ParseTestArgument());
                    break;

                case TokenKind.Expect:
                    expectation = ParseTestExpectation();
                    break;

                default:
                    _diagnostics.Error(
                        "PL0016",
                        "unexpected member in test block",
                        $"Expected 'receiver', 'arg', or 'expect' but found {Current.Kind.Describe()}.",
                        Current.Span);

                    while (Current.Kind is not (
                        TokenKind.CloseBrace or TokenKind.EndOfFile or TokenKind.Receiver
                        or TokenKind.Arg or TokenKind.Expect))
                    {
                        Advance();
                    }

                    break;
            }
        }

        var end = Expect(TokenKind.CloseBrace).Span;

        if (receiver is null)
        {
            _diagnostics.Error(
                "PL0017",
                "test is missing a receiver",
                "A ProtoLang unit test must declare the protobuf receiver fixture.",
                Spanning(start, end),
                "Add a 'receiver { ... }' block.");
            receiver = new TestReceiverFixture([], Spanning(start, end));
        }

        if (expectation is null)
        {
            _diagnostics.Error(
                "PL0018",
                "test is missing an expectation",
                "A ProtoLang unit test must declare 'expect return <value>;' or 'expect fail;'.",
                Spanning(start, end));
            expectation = new TestFailExpectation(Spanning(start, end));
        }

        return new TestDeclaration(
            targetName,
            (string?)name.Value ?? string.Empty,
            receiver,
            arguments,
            expectation,
            Spanning(start, end));
    }

    private TestReceiverFixture ParseTestReceiver()
    {
        var start = Expect(TokenKind.Receiver).Span;
        Expect(TokenKind.OpenBrace);
        var fields = ParseTestFieldInitializers();
        var end = Expect(TokenKind.CloseBrace).Span;
        return new TestReceiverFixture(fields, Spanning(start, end));
    }

    private IReadOnlyList<TestFieldInitializer> ParseTestFieldInitializers()
    {
        var fields = new List<TestFieldInitializer>();

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var start = Current.Span;
            var fieldName = Expect(TokenKind.Identifier).Text;

            if (Match(TokenKind.Equals))
            {
                var value = ParseExpression();
                var end = Expect(TokenKind.Semicolon).Span;
                fields.Add(new TestScalarFieldInitializer(fieldName, value, Spanning(start, end)));
                continue;
            }

            Expect(TokenKind.OpenBrace);
            var nested = ParseTestFieldInitializers();
            var nestedEnd = Expect(TokenKind.CloseBrace).Span;
            fields.Add(new TestMessageFieldInitializer(fieldName, nested, Spanning(start, nestedEnd)));
        }

        return fields;
    }

    private TestArgumentDeclaration ParseTestArgument()
    {
        var start = Expect(TokenKind.Arg).Span;
        var name = Expect(TokenKind.Identifier).Text;
        Expect(TokenKind.Equals);
        var value = ParseExpression();
        var end = Expect(TokenKind.Semicolon).Span;
        return new TestArgumentDeclaration(name, value, Spanning(start, end));
    }

    private TestExpectation ParseTestExpectation()
    {
        var start = Expect(TokenKind.Expect).Span;

        if (Match(TokenKind.Return))
        {
            var value = ParseExpression();
            var end = Expect(TokenKind.Semicolon).Span;
            return new TestReturnExpectation(value, Spanning(start, end));
        }

        if (Match(TokenKind.Fail))
        {
            var end = Expect(TokenKind.Semicolon).Span;
            return new TestFailExpectation(Spanning(start, end));
        }

        _diagnostics.Error(
            "PL0019",
            "expected a test expectation",
            $"Expected 'return' or 'fail' but found {Current.Kind.Describe()}.",
            Current.Span);
        Advance();
        var recoveredEnd = Current.Span;
        Match(TokenKind.Semicolon);
        return new TestFailExpectation(Spanning(start, recoveredEnd));
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
            TokenKind.If => ParseIfStatement(),
            TokenKind.While => ParseWhileStatement(),
            TokenKind.Break => ParseBreakStatement(),
            TokenKind.Continue => ParseContinueStatement(),
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

    /// <summary>Parses <c>if &lt;condition&gt; { ... }</c> with an optional else branch (spec 15.1).</summary>
    /// <remarks>
    /// The condition is unparenthesized, so the '{' that opens the body is what ends it. That is
    /// unambiguous only because no ProtoLang expression can contain a brace; if message
    /// construction literals (spec 13.2) are ever added, the condition will have to be parsed at a
    /// restricted precedence to keep <c>if m { }</c> from reading as a construction. An 'else'
    /// binds to the nearest unmatched 'if', which recursive descent gives for free.
    /// </remarks>
    private Statement ParseIfStatement()
    {
        var start = Expect(TokenKind.If).Span;
        var condition = ParseExpression();
        var then = ParseBlock();

        // 'else if' nests another if statement rather than wrapping one in a block, so the tree
        // records the chain the author wrote.
        Statement? elseBranch = null;
        if (Match(TokenKind.Else))
        {
            elseBranch = Current.Kind == TokenKind.If ? ParseIfStatement() : ParseBlock();
        }

        return new IfStatement(condition, then, elseBranch, Spanning(start, elseBranch?.Span ?? then.Span));
    }

    private Statement ParseWhileStatement()
    {
        var start = Expect(TokenKind.While).Span;
        var condition = ParseExpression();
        var body = ParseBlock();

        return new WhileStatement(condition, body, Spanning(start, body.Span));
    }

    private Statement ParseBreakStatement()
    {
        var start = Expect(TokenKind.Break).Span;
        var end = Expect(TokenKind.Semicolon).Span;
        return new BreakStatement(Spanning(start, end));
    }

    private Statement ParseContinueStatement()
    {
        var start = Expect(TokenKind.Continue).Span;
        var end = Expect(TokenKind.Semicolon).Span;
        return new ContinueStatement(Spanning(start, end));
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
    /// <c>a / b on_zero 0 + 1</c> from being ambiguous. Unary precedence includes <c>as</c>, so
    /// <c>a / b on_zero 0 as int32</c> converts the fallback rather than the quotient; parenthesize
    /// the division to convert its result.
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

    /// <summary>
    /// Parses a prefix expression and any <c>as</c> conversions applied to it.
    /// </summary>
    /// <remarks>
    /// <c>as</c> binds tighter than every binary operator and looser than a prefix operator, so
    /// <c>a as int64 * b</c> is <c>(a as int64) * b</c> and <c>-x as int32</c> negates first and
    /// converts the result. Chaining is allowed and left-associative, so a conversion through an
    /// intermediate width reads left to right.
    /// </remarks>
    private Expression ParseUnaryExpression()
    {
        var expression = ParsePrefixExpression();

        while (Current.Kind == TokenKind.As)
        {
            Advance();
            var target = ParseTypeReference();
            expression = new CastExpression(expression, target, Spanning(expression.Span, target.Span));
        }

        return expression;
    }

    private Expression ParsePrefixExpression()
    {
        var token = Current;

        if (token.Kind is TokenKind.Minus or TokenKind.Bang or TokenKind.Not)
        {
            Advance();
            var operand = ParsePrefixExpression();
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
