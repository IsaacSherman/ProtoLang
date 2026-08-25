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
}
