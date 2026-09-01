using ProtoLang.Diagnostics;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

public class ParserTests
{
    private static CompilationUnit Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, "test.protolang", diagnostics).Tokenize();
        return new Parser(tokens, "test.protolang", diagnostics).ParseCompilationUnit();
    }

    [Fact]
    public void ParsesImportAndExtendBlocks()
    {
        var unit = Parse(
            """
            import proto "invoice.proto";

            extend InvoiceItem {
                fn line_total_cents() -> int64 {
                    return quantity * unit_price_cents;
                }
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("invoice.proto", Assert.Single(unit.Imports).Path);

        var extend = Assert.Single(unit.Extends);
        Assert.Equal("InvoiceItem", extend.MessageName.Text);

        var method = Assert.Single(extend.Methods);
        Assert.Equal("line_total_cents", method.Name.Text);
        Assert.False(method.IsVirtual);
        Assert.Equal("int64", method.ReturnType?.Name.Text);
    }

    [Fact]
    public void ParsesVirtualMethods()
    {
        var unit = Parse(
            """
            import proto "x.proto";
            extend M { virtual fn f() -> double { return 1.0; } }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.True(unit.Extends[0].Methods[0].IsVirtual);
    }

    [Fact]
    public void ParsesUnitTests()
    {
        var unit = Parse(
            """
            import proto "invoice.proto";
            test Invoice.total_cents "sums line totals" {
                receiver {
                    items {
                        quantity = 2;
                        unit_price_cents = 300;
                    }

                    items {
                        quantity = 4;
                        unit_price_cents = 125;
                    }
                }

                expect return 1100;
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        var test = Assert.Single(unit.Tests);
        Assert.Equal("Invoice", test.Target.Receiver.Text);
        Assert.Equal("total_cents", test.Target.Method.Text);
        Assert.Equal("sums line totals", test.Name);
        Assert.IsType<TestReturnExpectation>(test.Expectation);

        var items = new List<TestMessageFieldInitializer>();
        Assert.Collection(
            test.Receiver.Fields,
            first => items.Add(Assert.IsType<TestMessageFieldInitializer>(first)),
            second => items.Add(Assert.IsType<TestMessageFieldInitializer>(second)));
        Assert.Equal("items", items[0].FieldName.Text);
    }

    [Fact]
    public void ParsesForInAndAssignment()
    {
        var unit = Parse(
            """
            import proto "invoice.proto";
            extend Invoice {
                fn total_cents() -> int64 {
                    var total: int64 = 0;
                    for item in items {
                        total = total + item.line_total_cents();
                    }
                    return total;
                }
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        var body = unit.Extends[0].Methods[0].Body;
        Assert.Collection(
            body.Statements,
            statement => Assert.IsType<VariableDeclarationStatement>(statement),
            statement =>
            {
                var forIn = Assert.IsType<ForInStatement>(statement);
                Assert.Equal("item", forIn.VariableName.Text);
                Assert.IsType<AssignmentStatement>(Assert.Single(forIn.Body.Statements));
            },
            statement => Assert.IsType<ReturnStatement>(statement));
    }

    [Fact]
    public void AppliesMultiplicationBeforeAddition()
    {
        var unit = Parse(
            """
            import proto "x.proto";
            extend M { fn f() -> int64 { return 1 + 2 * 3; } }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        var returnStatement = Assert.IsType<ReturnStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);
        var root = Assert.IsType<BinaryExpression>(returnStatement.Value);

        Assert.Equal(BinaryOperatorKind.Add, root.Operator);
        Assert.Equal(BinaryOperatorKind.Multiply, Assert.IsType<BinaryExpression>(root.Right).Operator);
    }

    [Fact]
    public void TreatsSubtractionAsLeftAssociative()
    {
        var unit = Parse(
            """
            import proto "x.proto";
            extend M { fn f() -> int64 { return 10 - 3 - 2; } }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        var returnStatement = Assert.IsType<ReturnStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);
        var root = Assert.IsType<BinaryExpression>(returnStatement.Value);

        // (10 - 3) - 2, not 10 - (3 - 2).
        Assert.Equal(BinaryOperatorKind.Subtract, Assert.IsType<BinaryExpression>(root.Left).Operator);
        Assert.IsType<IntegerLiteralExpression>(root.Right);
    }

    [Fact]
    public void ReportsMissingSemicolon()
    {
        Parse(
            """
            import proto "x.proto";
            extend M { fn f() -> int64 { return 1 } }
            """,
            out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0010");
    }

    [Fact]
    public void ReportsFieldDeclarationInsideExtendBlock()
    {
        // Spec 17.1 shows a field declared inside an extend block, but a ProtoLang-only field has
        // no wire representation, so this compiler rejects it.
        Parse(
            """
            import proto "x.proto";
            extend M { double real_time; }
            """,
            out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0012");
    }

    [Fact]
    public void RecoversAfterUnexpectedTopLevelToken()
    {
        var unit = Parse(
            """
            garbage here
            import proto "x.proto";
            extend M { fn f() -> int64 { return 1; } }
            """,
            out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0011");

        // Recovery must still surface the well-formed declarations that follow.
        Assert.Single(unit.Imports);
        Assert.Single(unit.Extends);
    }

    private static Statement ParseSingleStatement(string body, out DiagnosticBag diagnostics)
    {
        var unit = Parse(
            "import proto \"x.proto\";\nextend M { fn f() -> int64 {\n" + body + "\n} }",
            out diagnostics);

        return unit.Extends[0].Methods[0].Body.Statements[0];
    }

    [Fact]
    public void ParsesIfWithoutAnElseBranch()
    {
        var statement = ParseSingleStatement("if a > 1 { return 1; }", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Null(Assert.IsType<IfStatement>(statement).Else);
    }

    [Fact]
    public void ParsesElseIfAsAChainRatherThanANestedBlock()
    {
        var statement = ParseSingleStatement(
            """
            if a > 2 {
                return 1;
            } else if a > 1 {
                return 2;
            } else {
                return 3;
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        // 'else if' holds the next if directly, so the chain stays flat.
        var second = Assert.IsType<IfStatement>(Assert.IsType<IfStatement>(statement).Else);
        Assert.IsType<BlockStatement>(second.Else);
    }

    [Fact]
    public void BindsElseToTheNearestUnmatchedIf()
    {
        var statement = ParseSingleStatement(
            """
            if a > 1 {
                if a > 2 {
                    return 1;
                } else {
                    return 2;
                }
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        var outer = Assert.IsType<IfStatement>(statement);
        Assert.Null(outer.Else);

        var inner = Assert.IsType<IfStatement>(Assert.Single(outer.Then.Statements));
        Assert.NotNull(inner.Else);
    }

    [Fact]
    public void ParsesWhileLoopsWithBreakAndContinue()
    {
        var statement = ParseSingleStatement(
            """
            while a > 1 {
                if a > 2 {
                    break;
                }

                continue;
            }
            """,
            out var diagnostics);

        Assert.Empty(diagnostics);

        var loop = Assert.IsType<WhileStatement>(statement);
        Assert.Equal(2, loop.Body.Statements.Count);
        Assert.IsType<BreakStatement>(
            Assert.Single(Assert.IsType<IfStatement>(loop.Body.Statements[0]).Then.Statements));
        Assert.IsType<ContinueStatement>(loop.Body.Statements[1]);
    }

    [Fact]
    public void RequiresASemicolonAfterBreak()
    {
        ParseSingleStatement("while a > 1 { break }", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0010");
    }

    private static Expression ParseSingleReturn(string expression, out DiagnosticBag diagnostics)
    {
        var statement = ParseSingleStatement("return " + expression + ";", out diagnostics);
        return Assert.IsType<ReturnStatement>(statement).Value!;
    }

    [Fact]
    public void ParsesACastToAScalarKeywordType()
    {
        var expression = ParseSingleReturn("a as int64", out var diagnostics);

        Assert.Empty(diagnostics);

        var cast = Assert.IsType<CastExpression>(expression);
        Assert.Equal("int64", cast.TargetType.Name.Text);
        Assert.Equal("a", Assert.IsType<NameExpression>(cast.Operand).Name.Text);
    }

    [Fact]
    public void ParsesACastToAQualifiedTypeName()
    {
        var expression = ParseSingleReturn("a as pkg.Message", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("pkg.Message", Assert.IsType<CastExpression>(expression).TargetType.Name.Text);
    }

    /// <summary>
    /// A cast binds tighter than any binary operator, which is the whole point: the reason casts
    /// exist is to make the operands of an arithmetic expression agree.
    /// </summary>
    [Fact]
    public void ACastBindsTighterThanAnArithmeticOperator()
    {
        var expression = ParseSingleReturn("a as int64 * b", out var diagnostics);

        Assert.Empty(diagnostics);

        var product = Assert.IsType<BinaryExpression>(expression);
        Assert.Equal(BinaryOperatorKind.Multiply, product.Operator);
        Assert.IsType<CastExpression>(product.Left);
        Assert.IsType<NameExpression>(product.Right);
    }

    /// <summary>
    /// A cast binds looser than a prefix operator, so the negation happens in the source type and
    /// the result is converted, not the other way round.
    /// </summary>
    [Fact]
    public void ACastBindsLooserThanUnaryNegation()
    {
        var expression = ParseSingleReturn("-a as int32", out var diagnostics);

        Assert.Empty(diagnostics);

        var cast = Assert.IsType<CastExpression>(expression);
        Assert.Equal(UnaryOperatorKind.Negate, Assert.IsType<UnaryExpression>(cast.Operand).Operator);
    }

    [Fact]
    public void ParsesChainedCastsLeftToRight()
    {
        var expression = ParseSingleReturn("a as int32 as int64", out var diagnostics);

        Assert.Empty(diagnostics);

        var outer = Assert.IsType<CastExpression>(expression);
        Assert.Equal("int64", outer.TargetType.Name.Text);
        Assert.Equal("int32", Assert.IsType<CastExpression>(outer.Operand).TargetType.Name.Text);
    }

    /// <summary>
    /// The on_zero fallback parses at unary precedence, which includes casts, so a trailing cast
    /// applies to the fallback rather than to the quotient.
    /// </summary>
    [Fact]
    public void ACastAfterAnOnZeroFallbackAppliesToTheFallback()
    {
        var expression = ParseSingleReturn("a / b on_zero c as int32", out var diagnostics);

        Assert.Empty(diagnostics);

        var division = Assert.IsType<BinaryExpression>(expression);
        Assert.IsType<CastExpression>(division.OnZero!.Fallback);
    }

    [Fact]
    public void ReportsAMissingTypeAfterAs()
    {
        ParseSingleReturn("a as 1", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0013");
    }
    // ------------------------------------------------------- multi-line constructs

    /// <summary>
    /// The defect issue #37 exists to fix. A span used to keep the start position and take the
    /// closing token's length, so a block spanning four lines claimed to be one character long.
    /// </summary>
    [Fact]
    public void AMultiLineExtendBlockSpansAllOfIt()
    {
        const string Text =
            """
            import proto "invoice.proto";

            extend InvoiceItem {
                fn line_total_cents() -> int64 {
                    return quantity * unit_price_cents;
                }
            }
            """;

        var unit = Parse(Text, out var diagnostics);
        var lines = new LineMap(Text);

        Assert.Empty(diagnostics);

        var span = Assert.Single(unit.Extends).Span;
        var start = Text.IndexOf("extend", StringComparison.Ordinal);
        var end = Text.LastIndexOf('}') + 1;

        Assert.Equal(start, span.Start.Offset);
        Assert.Equal(end, span.End.Offset);
        Assert.Equal(end - start, span.Length);
        Assert.Equal(lines.PositionOf(start), span.Start);
        Assert.Equal(lines.PositionOf(end), span.End);
        Assert.True(span.End.Line > span.Start.Line, "the block ends on a later line than it starts");
    }

    [Fact]
    public void AMultiLineMethodSpansFromItsKeywordToItsClosingBrace()
    {
        const string Text =
            """
            import proto "x.proto";
            extend M {
                fn f() -> int64 {
                    var a = 1;
                    return a;
                }
            }
            """;

        var unit = Parse(Text, out var diagnostics);

        Assert.Empty(diagnostics);

        var method = unit.Extends[0].Methods[0];
        var reported = Text.Substring(method.Span.Start.Offset, method.Span.Length);

        Assert.StartsWith("fn f()", reported, StringComparison.Ordinal);
        Assert.EndsWith("}", reported, StringComparison.Ordinal);
        Assert.Contains("return a;", reported, StringComparison.Ordinal);
        Assert.Equal(method.Body.Span.End, method.Span.End);
        Assert.True(method.Span.End.Line > method.Span.Start.Line);

        var body = Text.Substring(method.Body.Span.Start.Offset, method.Body.Span.Length);
        Assert.StartsWith("{", body, StringComparison.Ordinal);
        Assert.EndsWith("}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AMultiLineIfStopsAtItsOwnEnd()
    {
        const string Text =
            """
            import proto "x.proto";
            extend M {
                fn f() -> int64 {
                    if (1 == 1) {
                        return 2;
                    }
                    return 3;
                }
            }
            """;

        var unit = Parse(Text, out var diagnostics);

        Assert.Empty(diagnostics);

        var statement = Assert.IsType<IfStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);
        var reported = Text.Substring(statement.Span.Start.Offset, statement.Span.Length);

        Assert.StartsWith("if (1 == 1)", reported, StringComparison.Ordinal);
        Assert.EndsWith("}", reported, StringComparison.Ordinal);
        Assert.Contains("return 2;", reported, StringComparison.Ordinal);
        Assert.DoesNotContain("return 3;", reported, StringComparison.Ordinal);
        Assert.True(statement.Span.End.Line > statement.Span.Start.Line);
    }
    // ------- names that have not been typed yet

    /// <summary>
    /// The motivating shape of the whole exercise: the author typed a dot and stopped, and the
    /// tree has to say so rather than record a member called the empty string.
    /// </summary>
    [Fact]
    public void ADotWithNothingAfterItIsAMissingName()
    {
        var expression = ParseSingleReturn("a.", out _);

        Assert.True(Assert.IsType<MemberAccessExpression>(expression).Name.IsMissing);
    }

    [Fact]
    public void AMemberNameThatWasTypedIsNotMissing()
    {
        var expression = ParseSingleReturn("a.b", out var diagnostics);

        Assert.Empty(diagnostics);

        var member = Assert.IsType<MemberAccessExpression>(expression);
        Assert.False(member.Name.IsMissing);
        Assert.Equal("b", member.Name.Text);
    }

    /// <summary>
    /// A missing name is not merely an empty one. Both read as empty text; only one of them means
    /// the author is mid-word, and a completion list must open for exactly that one.
    /// </summary>
    [Fact]
    public void AMissingNameAndAPresentNameAreToldApartBySomethingOtherThanTheirText()
    {
        var missing = Assert.IsType<MemberAccessExpression>(ParseSingleReturn("a.", out _)).Name;
        var present = Assert.IsType<MemberAccessExpression>(ParseSingleReturn("a.b", out _)).Name;

        Assert.NotEqual(missing.IsMissing, present.IsMissing);
        Assert.Empty(missing.Text);
    }

    /// <summary>
    /// Where the name would be written, which for a dot at the end of a line is on that line -- not
    /// on the next one, where the token that recovery tripped over happens to sit.
    /// </summary>
    [Fact]
    public void AMissingMemberNameIsAnchoredAfterTheDotAndNotAtTheNextToken()
    {
        const string Text =
            """
            import proto "x.proto";
            extend M {
                fn f() -> int64 {
                    return line.
                }
            }
            """;

        var unit = Parse(Text, out _);
        var returned = Assert.IsType<ReturnStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);
        var name = Assert.IsType<MemberAccessExpression>(returned.Value).Name;

        var afterTheDot = Text.IndexOf("line.", StringComparison.Ordinal) + "line.".Length;

        Assert.True(name.Span.IsEmpty, "a name that is not there covers no text");
        Assert.Equal(afterTheDot, name.Span.Start.Offset);
        Assert.Equal(Text[..afterTheDot].Count(c => c == '\n') + 1, name.Span.Start.Line);
    }

    /// <summary>
    /// The access stops at the dot too. Ending it at whatever token recovery reached made a
    /// two-line node out of a four-character expression.
    /// </summary>
    [Fact]
    public void AMemberAccessWithNoNameEndsAtItsDot()
    {
        const string Text =
            """
            import proto "x.proto";
            extend M {
                fn f() -> int64 {
                    return line.
                }
            }
            """;

        var unit = Parse(Text, out _);
        var returned = Assert.IsType<ReturnStatement>(unit.Extends[0].Methods[0].Body.Statements[0]);
        var member = Assert.IsType<MemberAccessExpression>(returned.Value);

        Assert.Equal(
            "line.",
            Text.Substring(member.Span.Start.Offset, member.Span.Length));
    }

    /// <summary>Modelling the name does not excuse the file; it is still a syntax error.</summary>
    [Fact]
    public void ADotWithNothingAfterItIsStillReported()
    {
        ParseSingleReturn("a.", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0010");
    }

    [Fact]
    public void AQualifiedNameEndingInADotIsAMissingName()
    {
        var unit = Parse("import proto \"x.proto\";\nextend pkg. { }", out var diagnostics);

        Assert.True(Assert.Single(unit.Extends).MessageName.IsMissing);
        Assert.Contains(diagnostics, d => d.Code == "PL0010");
    }

    [Fact]
    public void ATypePositionWithNoNameInItIsAMissingName()
    {
        var statement = ParseSingleStatement("var x: = 1;", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0013");
        Assert.True(Assert.IsType<VariableDeclarationStatement>(statement).DeclaredType!.Name.IsMissing);
    }

    [Fact]
    public void AVariableWithNoNameIsAMissingName()
    {
        var statement = ParseSingleStatement("var = 1;", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PL0010");
        Assert.True(Assert.IsType<VariableDeclarationStatement>(statement).Name.IsMissing);
    }

    /// <summary>
    /// Every name the parser hands on is either written or explicitly missing, whichever file it
    /// came from. A sweep rather than a sample, because the property is meant to hold everywhere and
    /// the cost of checking it everywhere is one walk.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParserResilienceTests.Corpus), MemberType = typeof(ParserResilienceTests))]
    public void ANameIsEitherWrittenOrMissingAndNeverQuietlyEmpty(string path)
    {
        var source = File.ReadAllText(path);

        for (var length = 0; length <= source.Length; length++)
        {
            var unit = Parse(source[..length], out _);

            foreach (var name in Names(unit))
            {
                Assert.True(
                    name.IsMissing || name.Text.Length > 0,
                    $"a name that is present must have text (truncated at {length})");
                Assert.True(
                    !name.IsMissing || name.Span.IsEmpty,
                    $"a name that is not there must cover no text (truncated at {length})");
            }
        }
    }

    /// <summary>Every <see cref="SyntaxName"/> in a unit, in no particular order.</summary>
    private static IEnumerable<SyntaxName> Names(CompilationUnit unit)
    {
        foreach (var extend in unit.Extends)
        {
            yield return extend.MessageName;

            foreach (var method in extend.Methods)
            {
                yield return method.Name;

                foreach (var parameter in method.Parameters)
                {
                    yield return parameter.Name;
                    yield return parameter.Type.Name;
                }

                if (method.ReturnType is { } returnType)
                {
                    yield return returnType.Name;
                }

                foreach (var name in Names(method.Body))
                {
                    yield return name;
                }
            }
        }

        foreach (var test in unit.Tests)
        {
            yield return test.Target.Receiver;
            yield return test.Target.Method;

            foreach (var name in Names(test.Receiver.Fields))
            {
                yield return name;
            }

            foreach (var argument in test.Arguments)
            {
                yield return argument.Name;

                foreach (var name in Names(argument.Value))
                {
                    yield return name;
                }
            }

            if (test.Expectation is TestReturnExpectation expectation)
            {
                foreach (var name in Names(expectation.Value))
                {
                    yield return name;
                }
            }
        }
    }

    /// <inheritdoc cref="Names(CompilationUnit)"/>
    private static IEnumerable<SyntaxName> Names(IReadOnlyList<TestFieldInitializer> fields)
    {
        foreach (var field in fields)
        {
            yield return field.FieldName;

            switch (field)
            {
                case TestScalarFieldInitializer scalar:
                    foreach (var name in Names(scalar.Value))
                    {
                        yield return name;
                    }

                    break;

                case TestMessageFieldInitializer message:
                    foreach (var name in Names(message.Fields))
                    {
                        yield return name;
                    }

                    break;
            }
        }
    }

    /// <inheritdoc cref="Names(CompilationUnit)"/>
    private static IEnumerable<SyntaxName> Names(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (var name in block.Statements.SelectMany(Names))
                {
                    yield return name;
                }

                break;

            case VariableDeclarationStatement declaration:
                yield return declaration.Name;

                if (declaration.DeclaredType is { } declaredType)
                {
                    yield return declaredType.Name;
                }

                foreach (var name in Names(declaration.Initializer))
                {
                    yield return name;
                }

                break;

            case ReturnStatement { Value: { } value }:
                foreach (var name in Names(value))
                {
                    yield return name;
                }

                break;

            case ForInStatement forIn:
                yield return forIn.VariableName;

                foreach (var name in Names(forIn.Collection).Concat(Names(forIn.Body)))
                {
                    yield return name;
                }

                break;

            case IfStatement branch:
                foreach (var name in Names(branch.Condition).Concat(Names(branch.Then)))
                {
                    yield return name;
                }

                if (branch.Else is { } elseBranch)
                {
                    foreach (var name in Names(elseBranch))
                    {
                        yield return name;
                    }
                }

                break;

            case WhileStatement loop:
                foreach (var name in Names(loop.Condition).Concat(Names(loop.Body)))
                {
                    yield return name;
                }

                break;

            case AssignmentStatement assignment:
                foreach (var name in Names(assignment.Target).Concat(Names(assignment.Value)))
                {
                    yield return name;
                }

                break;

            case ExpressionStatement expression:
                foreach (var name in Names(expression.Expression))
                {
                    yield return name;
                }

                break;
        }
    }

    /// <inheritdoc cref="Names(CompilationUnit)"/>
    private static IEnumerable<SyntaxName> Names(Expression expression)
    {
        switch (expression)
        {
            case NameExpression name:
                yield return name.Name;
                break;

            case MemberAccessExpression member:
                yield return member.Name;

                foreach (var name in Names(member.Receiver))
                {
                    yield return name;
                }

                break;

            case InvocationExpression invocation:
                foreach (var name in Names(invocation.Callee).Concat(invocation.Arguments.SelectMany(Names)))
                {
                    yield return name;
                }

                break;

            case BinaryExpression binary:
                foreach (var name in Names(binary.Left).Concat(Names(binary.Right)))
                {
                    yield return name;
                }

                if (binary.OnZero?.Fallback is { } fallback)
                {
                    foreach (var name in Names(fallback))
                    {
                        yield return name;
                    }
                }

                break;

            case UnaryExpression unary:
                foreach (var name in Names(unary.Operand))
                {
                    yield return name;
                }

                break;

            case HasExpression has:
                foreach (var name in Names(has.Operand))
                {
                    yield return name;
                }

                break;

            case CastExpression cast:
                yield return cast.TargetType.Name;

                foreach (var name in Names(cast.Operand))
                {
                    yield return name;
                }

                break;
        }
    }
}
