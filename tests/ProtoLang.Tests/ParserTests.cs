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
        Assert.Equal("InvoiceItem", extend.MessageName);

        var method = Assert.Single(extend.Methods);
        Assert.Equal("line_total_cents", method.Name);
        Assert.False(method.IsVirtual);
        Assert.Equal("int64", method.ReturnType?.Name);
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
        Assert.Equal("Invoice.total_cents", test.TargetName);
        Assert.Equal("sums line totals", test.Name);
        Assert.IsType<TestReturnExpectation>(test.Expectation);

        var items = new List<TestMessageFieldInitializer>();
        Assert.Collection(
            test.Receiver.Fields,
            first => items.Add(Assert.IsType<TestMessageFieldInitializer>(first)),
            second => items.Add(Assert.IsType<TestMessageFieldInitializer>(second)));
        Assert.Equal("items", items[0].FieldName);
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
                Assert.Equal("item", forIn.VariableName);
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
        Assert.Equal("int64", cast.TargetType.Name);
        Assert.Equal("a", Assert.IsType<NameExpression>(cast.Operand).Name);
    }

    [Fact]
    public void ParsesACastToAQualifiedTypeName()
    {
        var expression = ParseSingleReturn("a as pkg.Message", out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("pkg.Message", Assert.IsType<CastExpression>(expression).TargetType.Name);
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
        Assert.Equal("int64", outer.TargetType.Name);
        Assert.Equal("int32", Assert.IsType<CastExpression>(outer.Operand).TargetType.Name);
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
}
