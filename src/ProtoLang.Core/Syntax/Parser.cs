using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar sketched in spec 7.1. Semicolons are mandatory
/// after statements; spec 7.1 lists that as an open question, and this is the decision.
/// </summary>
public sealed class Parser
{
    /// <summary>
    /// How deeply nested constructs may be before the parser gives up on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recursive descent costs stack per nesting level, and a <see cref="StackOverflowException"/>
    /// cannot be caught: it terminates the process immediately, skipping every <c>finally</c> and
    /// every handler. In the CLI that is an ugly crash. In a long-lived host it takes the whole
    /// session down, which is why this is a budget rather than a matter of taste.
    /// </para>
    /// <para>
    /// The limit is far above anything hand-written -- real code nests single digits deep -- and far
    /// below where the stack runs out, with room to spare for the binder and the backends, which
    /// walk the same tree with larger frames.
    /// </para>
    /// </remarks>
    private const int MaxNestingDepth = 128;

    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _file;
    private int _position;
    private int _nestingDepth;
    private bool _reportedNesting;

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
        TryExpect(kind, out var token);
        return token;
    }

    /// <summary>
    /// Consumes the expected token, or reports that it is missing and synthesizes one.
    /// </summary>
    /// <returns>
    /// True when the token was really there. False when it was not, in which case
    /// <paramref name="token"/> is a stand-in and the diagnostic has already been reported.
    /// </returns>
    /// <remarks>
    /// The answer is published rather than inferred from the stand-in, because the stand-in is
    /// indistinguishable from a real token of the same kind carrying no text. Callers that build a
    /// name out of the result need to know which they got; see <see cref="SyntaxName"/>.
    /// </remarks>
    private bool TryExpect(TokenKind kind, out Token token)
    {
        if (Current.Kind == kind)
        {
            token = Advance();
            return true;
        }

        _diagnostics.Error(
            "PL0010",
            "unexpected token",
            $"Expected {kind.Describe()} but found {Current.Kind.Describe()}.",
            Current.Span);

        // A synthetic token so callers can continue building a tree.
        token = new Token(kind, string.Empty, Current.Span);
        return false;
    }

    /// <summary>
    /// Parses an identifier into a <see cref="SyntaxName"/>, modelling its absence rather than
    /// standing in for it.
    /// </summary>
    private SyntaxName ExpectName()
    {
        // Taken before the attempt, because a failed Expect does not consume and the token it
        // failed on is the wrong anchor -- for a trailing dot at the end of a line, that token is
        // on the next line.
        var insertionPoint = InsertionPointAfterPreviousToken();

        return TryExpect(TokenKind.Identifier, out var token)
            ? new SyntaxName(token.Text, token.Span)
            : SyntaxName.Missing(insertionPoint);
    }

    /// <summary>The empty range immediately after the last token consumed.</summary>
    /// <remarks>
    /// Where a name would be typed next, which is where an editor opens a completion list. Before
    /// anything has been consumed this degenerates to the end of the current token; no name is
    /// expected at the start of a file, so nothing reaches that case.
    /// </remarks>
    private SourceSpan InsertionPointAfterPreviousToken() => InsertionPointAfter(Peek(-1));

    /// <inheritdoc cref="InsertionPointAfterPreviousToken"/>
    private SourceSpan InsertionPointAfter(Token token) => new(_file, token.Span.End, token.Span.End);

    /// <summary>
    /// Takes one level of nesting budget, or reports that the budget is exhausted.
    /// </summary>
    /// <returns>
    /// True when the caller may recurse, in which case it must call <see cref="ExitNesting"/>.
    /// False when it must not, in which case the diagnostic has already been reported.
    /// </returns>
    /// <remarks>
    /// Reported once per file. A construct deep enough to exhaust the budget produces one
    /// diagnostic per enclosing level otherwise, and the hundredth copy tells the reader nothing
    /// the first did not.
    /// </remarks>
    private bool TryEnterNesting()
    {
        if (_nestingDepth < MaxNestingDepth)
        {
            _nestingDepth++;
            return true;
        }

        if (!_reportedNesting)
        {
            _reportedNesting = true;
            _diagnostics.Error(
                "PL0081",
                "nesting is too deep",
                $"This construct nests more than {MaxNestingDepth} levels deep, which the compiler "
                + "does not parse.",
                Current.Span,
                "This is nearly always a malformed or generated file. Reduce the nesting, or split "
                + "the expression across intermediate variables.");
        }

        return false;
    }

    private void ExitNesting() => _nestingDepth--;

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
        var written = TryExpect(TokenKind.StringLiteral, out var path);
        var end = Expect(TokenKind.Semicolon).Span;

        return new ImportDeclaration(
            (string?)path.Value ?? string.Empty,
            Spanning(start, end),
            !written);
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
        var target = ParseTestTarget();
        var name = Expect(TokenKind.StringLiteral);
        Expect(TokenKind.OpenBrace);

        // Where a part nobody wrote would be written: just inside the brace. Taken before the body
        // is parsed, because that is the only moment the position is at hand.
        var insertionPoint = InsertionPointAfterPreviousToken();

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

            // The diagnostic is about the whole test; the node stands for a block that is not there,
            // and spans the empty point one would be typed at -- the rule SyntaxName.Missing and
            // IrMissingMemberAccess already follow. Spanning the test instead made a fixture nobody
            // wrote the innermost thing at every offset of the declaration, its header included, so a
            // position query on 'test Outer.f' answered with a receiver fixture.
            receiver = new TestReceiverFixture([], insertionPoint);
        }

        if (expectation is null)
        {
            _diagnostics.Error(
                "PL0018",
                "test is missing an expectation",
                "A ProtoLang unit test must declare 'expect return <value>;' or 'expect fail;'.",
                Spanning(start, end));

            // Same rule, same point. Both stand-ins share it when both are absent, which is what a
            // caret between the braces of an empty test should find: two things that are not there.
            expectation = new TestFailExpectation(insertionPoint);
        }

        return new TestDeclaration(
            target,
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
            var before = _position;
            var start = Current.Span;
            var fieldName = ExpectName();

            if (Match(TokenKind.Equals))
            {
                var value = ParseExpression();
                var end = Expect(TokenKind.Semicolon).Span;
                fields.Add(new TestScalarFieldInitializer(fieldName, value, Spanning(start, end)));
            }
            else if (!TryEnterNesting())
            {
                // Message fixtures nest, so they carry the same budget as blocks and expressions.
                // Whether the closer was found does not change a fixture: it declares no name, so
                // nothing's visibility ends at its brace.
                var abandonedEnd = Current.Span;
                if (Match(TokenKind.OpenBrace))
                {
                    TrySkipBalancedBlock(out abandonedEnd);
                }

                fields.Add(new TestMessageFieldInitializer(fieldName, [], Spanning(start, abandonedEnd)));
            }
            else
            {
                try
                {
                    Expect(TokenKind.OpenBrace);
                    var nested = ParseTestFieldInitializers();
                    var nestedEnd = Expect(TokenKind.CloseBrace).Span;
                    fields.Add(
                        new TestMessageFieldInitializer(fieldName, nested, Spanning(start, nestedEnd)));
                }
                finally
                {
                    ExitNesting();
                }
            }

            // Guarantee forward progress, as ParseBlock does. Nothing above is obliged to consume a
            // token: on a stray token every Expect fails without advancing, and the recursive call
            // then re-enters on an unchanged position. That recursion has no base case, and the
            // resulting StackOverflowException cannot be caught -- it takes the process with it.
            if (_position == before)
            {
                Advance();
            }
        }

        return fields;
    }

    private TestArgumentDeclaration ParseTestArgument()
    {
        var start = Expect(TokenKind.Arg).Span;
        var name = ExpectName();
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

    /// <summary>Parses <c>Invoice.total_cents</c> into the message and the method it names.</summary>
    /// <remarks>
    /// The last dot separates them, which is the rule the binder used to apply to the joined string.
    /// Applied here instead, because only the parser holds the tokens and therefore the range of each
    /// half; see <see cref="TestTarget"/> for what a missing half means and why the binder reads the
    /// shape rather than the text.
    /// </remarks>
    private TestTarget ParseTestTarget()
    {
        // Taken before the attempt, for the reason ExpectName gives.
        var insertionPoint = InsertionPointAfterPreviousToken();

        if (!TryExpect(TokenKind.Identifier, out var first))
        {
            return new TestTarget(
                SyntaxName.Missing(insertionPoint),
                SyntaxName.Missing(insertionPoint),
                insertionPoint);
        }

        var parts = new List<Token> { first };

        while (Current.Kind == TokenKind.Dot)
        {
            var dot = Advance();
            if (!TryExpect(TokenKind.Identifier, out var part))
            {
                // The name stops here. What has been written names a receiver; the method is the
                // hole after the dot, which TryExpect has already reported.
                var hole = InsertionPointAfter(dot);
                return new TestTarget(Joined(parts), SyntaxName.Missing(hole), Spanning(first.Span, hole));
            }

            parts.Add(part);
        }

        var method = new SyntaxName(parts[^1].Text, parts[^1].Span);

        return parts.Count == 1
            ? new TestTarget(SyntaxName.Missing(insertionPoint), method, method.Span)
            : new TestTarget(
                Joined(parts.GetRange(0, parts.Count - 1)),
                method,
                Spanning(first.Span, method.Span));

        SyntaxName Joined(IReadOnlyList<Token> tokens) => new(
            string.Join('.', tokens.Select(token => token.Text)),
            Spanning(tokens[0].Span, tokens[^1].Span));
    }

    /// <summary>Parses <c>Foo</c> or <c>pkg.Foo</c> into a single dotted name.</summary>
    /// <remarks>
    /// A dot with no identifier after it is consumed and modelled as a missing name rather than
    /// left in the stream. Leaving it made the caller's next <c>Expect</c> report the dot as the
    /// unexpected token, which blamed the wrong thing and left nothing in the tree to anchor a
    /// completion list to. It is still an error, reported here against the token that should have
    /// been the name.
    /// </remarks>
    private SyntaxName ParseQualifiedName()
    {
        var insertionPoint = InsertionPointAfterPreviousToken();
        if (!TryExpect(TokenKind.Identifier, out var first))
        {
            return SyntaxName.Missing(insertionPoint);
        }

        var parts = new List<string> { first.Text };
        var span = first.Span;

        while (Current.Kind == TokenKind.Dot)
        {
            var dot = Advance();
            if (!TryExpect(TokenKind.Identifier, out var part))
            {
                return SyntaxName.Missing(InsertionPointAfter(dot));
            }

            parts.Add(part.Text);
            span = Spanning(span, part.Span);
        }

        return new SyntaxName(string.Join('.', parts), span);
    }

    private MethodDeclaration ParseMethodDeclaration()
    {
        var start = Current.Span;
        var isVirtual = Match(TokenKind.Virtual);

        Expect(TokenKind.Fn);
        var name = ExpectName();

        Expect(TokenKind.OpenParen);
        var parameters = new List<ParameterDeclaration>();
        if (Current.Kind != TokenKind.CloseParen)
        {
            do
            {
                var parameterStart = Current.Span;
                var parameterName = ExpectName();
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
            return new TypeReference(new SyntaxName(token.Text, token.Span), token.Span);
        }

        if (token.Kind == TokenKind.Identifier)
        {
            var name = ParseQualifiedName();
            return new TypeReference(name, Spanning(token.Span, name.Span));
        }

        var insertionPoint = InsertionPointAfterPreviousToken();

        _diagnostics.Error(
            "PL0013",
            "expected a type",
            $"Expected a type name but found {token.Kind.Describe()}.",
            token.Span);
        Advance();

        // A missing name rather than a sentinel spelled like one. The old placeholder was the
        // string "<error>", which the binder then looked up and failed to find, reporting an
        // unknown type on top of the syntax error already reported here.
        return new TypeReference(SyntaxName.Missing(insertionPoint), token.Span);
    }

    private BlockStatement ParseBlock()
    {
        var start = Expect(TokenKind.OpenBrace).Span;

        // Blocks nest through if/while/for bodies, so they need the same budget expressions do.
        if (!TryEnterNesting())
        {
            var skipped = TrySkipBalancedBlock(out var closer);
            return new BlockStatement([], Spanning(start, closer)) { IsClosed = skipped };
        }

        try
        {
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

            var closed = TryExpect(TokenKind.CloseBrace, out var end);
            return new BlockStatement(statements, Spanning(start, end.Span)) { IsClosed = closed };
        }
        finally
        {
            ExitNesting();
        }
    }

    /// <summary>
    /// Consumes tokens through the closer matching an already-consumed <c>{</c>. Used to step over a
    /// construct too deeply nested to descend into.
    /// </summary>
    /// <param name="closer">
    /// The closing brace's range, or the empty range at the end of the file when the tokens ran out
    /// before it did.
    /// </param>
    /// <returns>True when a matching closer was really there.</returns>
    /// <remarks>
    /// The answer is published rather than inferred from <paramref name="closer"/>, for the reason
    /// <see cref="TryExpect"/> gives: a stand-in is indistinguishable from a real token, and here
    /// what turns on the difference is where the block's names stop. See
    /// <see cref="BlockStatement.IsClosed"/>.
    /// </remarks>
    private bool TrySkipBalancedBlock(out SourceSpan closer)
    {
        var depth = 1;

        while (Current.Kind != TokenKind.EndOfFile)
        {
            if (Current.Kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (Current.Kind == TokenKind.CloseBrace)
            {
                depth--;
                if (depth == 0)
                {
                    closer = Advance().Span;
                    return true;
                }
            }

            Advance();
        }

        closer = Current.Span;
        return false;
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
        var name = ExpectName();

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
        var variable = ExpectName();
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

    private Expression ParseExpression()
    {
        if (!TryEnterNesting())
        {
            return AbandonExpression();
        }

        try
        {
            return ParseBinaryExpression(0);
        }
        finally
        {
            ExitNesting();
        }
    }

    /// <summary>
    /// Gives up on an expression that is nested too deeply, consuming a token so the enclosing loop
    /// still makes progress.
    /// </summary>
    private Expression AbandonExpression()
    {
        var span = Current.Span;

        if (Current.Kind != TokenKind.EndOfFile)
        {
            Advance();
        }

        return new ErrorExpression(span);
    }

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

        // 'has' sits at prefix precedence beside 'not', so 'has a.b' takes the whole path and
        // 'has a and has a.b' groups the way it reads. Its operand is parsed as a postfix
        // expression rather than a prefix one: 'has not x' is nonsense, and letting it parse would
        // only move the diagnostic further from the mistake.
        if (token.Kind == TokenKind.Has)
        {
            Advance();
            var target = ParsePostfixExpression();
            return new HasExpression(target, Spanning(token.Span, target.Span));
        }

        if (token.Kind is TokenKind.Minus or TokenKind.Bang or TokenKind.Not)
        {
            Advance();

            // A chain of prefix operators recurses without passing through ParseExpression, so it
            // needs its own budget rather than inheriting that one.
            if (!TryEnterNesting())
            {
                return AbandonExpression();
            }

            Expression operand;
            try
            {
                operand = ParsePrefixExpression();
            }
            finally
            {
                ExitNesting();
            }

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
                var name = ExpectName();

                // The access ends where the name is, and a missing name is the empty range just
                // after the dot. Taking the end from the token Expect happened to fail on instead
                // stretched the access to wherever recovery landed -- for a dot at the end of a
                // line, the brace on the next one.
                expression = new MemberAccessExpression(expression, name, Spanning(expression.Span, name.Span));
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
                return new NameExpression(new SyntaxName(token.Text, token.Span), token.Span);

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

    /// <summary>The span covering both operands and everything between them.</summary>
    /// <remarks>
    /// Order-insensitive, because error recovery reaches here with an <c>end</c> that precedes its
    /// <c>start</c>. Stamped with the file being parsed rather than with whichever file an
    /// operand carries, because the parser is the authority on that and some of what it
    /// combines is synthesized.
    /// </remarks>
    private SourceSpan Spanning(SourceSpan start, SourceSpan end)
        => SourceSpan.Union(_file, start, end);
}
