using ProtoLang.Ir;
using ProtoLang.Types;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// End-to-end tests over the real example schema. These invoke protoc, so they exercise the
/// descriptor-binding path described in spec 21.1 rather than a stub.
/// </summary>
public class CompilationTests
{
    private static CompilationResult CompileSource(string source)
    {
        var path = TestPaths.WriteTempScript(source);
        return Compilation.Compile(path, [TestPaths.ExampleProtoDirectory]);
    }

    private const string Prelude = "import proto \"invoice.proto\";\n";

    [Fact]
    public void CompilesTheInvoiceExample()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var module = result.Module!;
        Assert.Equal(8, module.Methods.Count);
        Assert.Equal(12, module.Tests.Count);

        var lineTotal = module.Methods.Single(m => m.Name == "line_total_cents");
        Assert.Equal("protolang.examples.InvoiceItem", lineTotal.Receiver.FullName);
        Assert.Equal(ScalarType.Int64Type, lineTotal.ReturnType);

        var totalCents = module.Methods.Single(m => m.Name == "total_cents");
        Assert.Equal("protolang.examples.Invoice", totalCents.Receiver.FullName);

        var test = module.Tests.Single(t => t.Name == "sums line totals");
        Assert.Equal(totalCents.Signature, test.Target);
        Assert.IsType<IrTestReturnExpectation>(test.Expectation);
    }

    [Fact]
    public void TypeChecksUnitTestFixtures()
    {
        var result = CompileSource(
            Prelude +
            """
            extend InvoiceItem {
                fn line_total_cents() -> int64 {
                    return quantity * unit_price_cents;
                }
            }

            test InvoiceItem.line_total_cents "line total" {
                receiver {
                    quantity = 2;
                    unit_price_cents = "oops";
                }

                expect return 600;
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0063");
    }

    [Fact]
    public void RejectsReceiverFieldReferencesInsideUnitTestFixtures()
    {
        var result = CompileSource(
            Prelude +
            """
            extend InvoiceItem {
                fn line_total_cents() -> int64 {
                    return quantity * unit_price_cents;
                }
            }

            test InvoiceItem.line_total_cents "line total" {
                receiver {
                    quantity = unit_price_cents;
                    unit_price_cents = 300;
                }

                expect return 600;
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0037");
    }

    [Fact]
    public void MarksIntegerArithmeticAsWrapping()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success);

        var lineTotal = result.Module!.Methods.Single(m => m.Name == "line_total_cents");
        var returnStatement = Assert.IsType<IrReturn>(lineTotal.Body.Statements[0]);
        var multiply = Assert.IsType<IrBinary>(returnStatement.Value);

        Assert.Equal(IrBinaryOperator.Multiply, multiply.Operator);
        Assert.Equal(ArithmeticBehavior.Wrap, multiply.Behavior);
        Assert.Equal(ScalarType.Int64Type, multiply.ResultType);
    }

    [Fact]
    public void ResolvesBareIdentifiersToReceiverFields()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success);

        var lineTotal = result.Module!.Methods.Single(m => m.Name == "line_total_cents");
        var returnStatement = Assert.IsType<IrReturn>(lineTotal.Body.Statements[0]);
        var multiply = Assert.IsType<IrBinary>(returnStatement.Value);

        var left = Assert.IsType<IrFieldAccess>(multiply.Left);
        Assert.Equal("quantity", left.Field.Name);
        Assert.IsType<IrThis>(left.Receiver);
    }

    [Fact]
    public void ResolvesForwardReferencesBetweenExtendBlocks()
    {
        // total_cents is declared before line_total_cents here, so binding must be two-pass.
        var result = CompileSource(
            Prelude +
            """
            extend Invoice {
                fn total_cents() -> int64 {
                    var total: int64 = 0;
                    for item in items {
                        total = total + item.line_total_cents();
                    }
                    return total;
                }
            }

            extend InvoiceItem {
                fn line_total_cents() -> int64 {
                    return quantity * unit_price_cents;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void ReportsUnknownMessage()
    {
        var result = CompileSource(Prelude + "extend NoSuchMessage { fn f() -> int64 { return 1; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0021");
    }

    [Fact]
    public void ReportsUnknownField()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return no_such_field; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0037");
    }

    [Fact]
    public void ReportsMissingReturn()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { var x: int64 = 1; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void RejectsImplicitNumericConversion()
    {
        // 'name' is a string; multiplying it by an int64 must not be coerced into anything.
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity * name; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0048");
    }

    [Fact]
    public void RejectsReturningTheWrongType()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return name; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0032");
    }

    [Fact]
    public void RejectsIteratingANonRepeatedField()
    {
        var result = CompileSource(
            Prelude +
            "extend InvoiceItem { fn f() -> int64 { for q in quantity { } return 1; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0033");
    }

    [Fact]
    public void RejectsCallingAnUndefinedMethod()
    {
        var result = CompileSource(
            Prelude + "extend Invoice { fn f() -> int64 { return items_count(); } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0044");
    }

    [Fact]
    public void RejectsAssigningToAProtobufField()
    {
        // Whether methods may mutate the receiver is still open (spec 16.1); until it is decided,
        // the compiler refuses rather than picking a semantics.
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { quantity = 1; return quantity; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0034");
    }

    [Fact]
    public void RejectsOverloadedMethodNames()
    {
        var result = CompileSource(
            Prelude +
            """
            extend InvoiceItem {
                fn f() -> int64 { return 1; }
                fn f() -> int64 { return 2; }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0022");
    }

    [Fact]
    public void RejectsOverloadedMethodNamesWithDifferentParameterCountsWithoutThrowing()
    {
        CompilationResult? result = null;
        var exception = Record.Exception(
            () =>
            {
                result = CompileSource(
                    Prelude +
                    """
                    extend InvoiceItem {
                        fn f() -> int64 { return 1; }
                        fn f(x: int64) -> int64 { return x; }
                    }
                    """);
            });

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0022");
    }

    [Fact]
    public void RejectsMethodNameCollidingWithField()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn quantity() -> int64 { return 1; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0023");
    }

    [Fact]
    public void ReportsMissingProtoFile()
    {
        var result = CompileSource("import proto \"nope.proto\";\n");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0002");
    }

    [Fact]
    public void RequiresOnZeroForIntegerDivision()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity / unit_price_cents; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0054");
    }

    [Fact]
    public void RequiresOnZeroForIntegerModulo()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity % unit_price_cents; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0054");
    }

    [Fact]
    public void AcceptsIntegerDivisionWithOnZero()
    {
        var result = CompileSource(
            Prelude +
            "extend InvoiceItem { fn f() -> int64 { return quantity / unit_price_cents on_zero 0; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returnStatement = Assert.IsType<IrReturn>(result.Module!.Methods.Single().Body.Statements[0]);
        var division = Assert.IsType<IrIntegerDivision>(returnStatement.Value);

        Assert.Equal(IrBinaryOperator.Divide, division.Operator);
        Assert.NotNull(division.OnZero);
    }

    [Fact]
    public void AllowsBareDivisionByANonZeroLiteral()
    {
        // Division by zero is unreachable here, so no clause is required and no check is emitted.
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity / 2; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returnStatement = Assert.IsType<IrReturn>(result.Module!.Methods.Single().Body.Statements[0]);
        Assert.Null(Assert.IsType<IrIntegerDivision>(returnStatement.Value).OnZero);
    }

    [Fact]
    public void StillRequiresOnZeroWhenDivisorIsALiteralZero()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity / 0; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0054");
    }

    [Fact]
    public void WarnsWhenOnZeroIsUnreachable()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity / 2 on_zero 7; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0056");
    }

    [Fact]
    public void AcceptsOnZeroFail()
    {
        var result = CompileSource(
            Prelude +
            "extend InvoiceItem { fn f() -> int64 { return quantity / unit_price_cents on_zero fail; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returnStatement = Assert.IsType<IrReturn>(result.Module!.Methods.Single().Body.Statements[0]);
        var division = Assert.IsType<IrIntegerDivision>(returnStatement.Value);

        Assert.Equal(ZeroDivisorBehavior.Fail, division.ZeroBehavior);
        Assert.Null(division.OnZero);
    }

    [Fact]
    public void AcceptsOnZeroFailForModulo()
    {
        var result = CompileSource(
            Prelude +
            "extend InvoiceItem { fn f() -> int64 { return quantity % unit_price_cents on_zero fail; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returnStatement = Assert.IsType<IrReturn>(result.Module!.Methods.Single().Body.Statements[0]);
        Assert.Equal(ZeroDivisorBehavior.Fail, Assert.IsType<IrIntegerDivision>(returnStatement.Value).ZeroBehavior);
    }

    [Fact]
    public void MarksFallbackDivisionDistinctlyFromFail()
    {
        var result = CompileSource(
            Prelude +
            "extend InvoiceItem { fn f() -> int64 { return quantity / unit_price_cents on_zero 0; } }");

        Assert.True(result.Success);

        var returnStatement = Assert.IsType<IrReturn>(result.Module!.Methods.Single().Body.Statements[0]);
        var division = Assert.IsType<IrIntegerDivision>(returnStatement.Value);

        Assert.Equal(ZeroDivisorBehavior.Fallback, division.ZeroBehavior);
        Assert.NotNull(division.OnZero);
    }

    [Fact]
    public void WarnsWhenOnZeroFailIsUnreachable()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity / 2 on_zero fail; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0056");
    }

    [Fact]
    public void RejectsOnZeroWithAMismatchedType()
    {
        var result = CompileSource(
            Prelude +
            "extend InvoiceItem { fn f() -> int64 { return quantity / unit_price_cents on_zero name; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0055");
    }

    [Fact]
    public void DoesNotRequireOnZeroForFloatDivision()
    {
        // Float division follows IEEE 754 and yields infinity or NaN rather than failing.
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> double { return 1.0 / 0.0; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void RejectsOnZeroOnFloatDivision()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> double { return 1.0 / 0.0 on_zero 0.0; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0015");
    }

    [Fact]
    public void RejectsOnZeroOnNonDivisionOperators()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity + 1 on_zero 0; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0015");
    }

    [Fact]
    public void BindsOnZeroClauseToItsOwnDivisionOnly()
    {
        // 'a + b / c on_zero 0' must mean 'a + (b / c on_zero 0)'.
        var result = CompileSource(
            Prelude +
            """
            extend InvoiceItem {
                fn f() -> int64 { return quantity + unit_price_cents / quantity on_zero 0; }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returnStatement = Assert.IsType<IrReturn>(result.Module!.Methods.Single().Body.Statements[0]);
        var addition = Assert.IsType<IrBinary>(returnStatement.Value);

        Assert.Equal(IrBinaryOperator.Add, addition.Operator);
        Assert.NotNull(Assert.IsType<IrIntegerDivision>(addition.Right).OnZero);
    }

    [Fact]
    public void AdaptsIntegerLiteralToDeclaredType()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { var x: int64 = 0; return x; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var method = result.Module!.Methods.Single();
        var declaration = Assert.IsType<IrVariableDeclaration>(method.Body.Statements[0]);
        Assert.Equal(ScalarType.Int64Type, declaration.Initializer.Type);
    }

    [Fact]
    public void AcceptsIfElseWhereBothBranchesReturn()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { if quantity > 1 { return 1; } else { return 2; } } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void AcceptsAnElseIfChainThatReturnsEverywhere()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { if quantity > 2 { return 1; } "
            + "else if quantity > 1 { return 2; } else { return 3; } } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void ReportsMissingReturnWhenOnlyOneBranchReturns()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { if quantity > 1 { return 1; } "
            + "else { var x: int64 = 2; } } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void ReportsMissingReturnWhenTheElseBranchIsAbsent()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { if quantity > 1 { return 1; } } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void ReportsMissingReturnWhenAnElseIfChainHasNoElse()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { if quantity > 2 { return 1; } "
            + "else if quantity > 1 { return 2; } } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void TreatsAnUnconditionalLoopAsNeverFallingThrough()
    {
        // Nothing follows the loop because nothing can: the only way out is the return.
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { while true { return 1; } } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void ReportsMissingReturnWhenAnUnconditionalLoopCanBreak()
    {
        // The 'break' escapes the loop, so the end of the method is reachable after all.
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { while true { if quantity > 1 { break; } return 1; } } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void DoesNotTreatAConditionalLoopAsAlwaysReturning()
    {
        // A 'while' with a real condition may run zero times.
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { while quantity > 1 { return 1; } } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void DoesNotTreatAForLoopAsAlwaysReturning()
    {
        // The repeated field may be empty.
        var result = CompileSource(
            Prelude + "extend Invoice { fn f() -> int64 { for item in items { return 1; } } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0027");
    }

    [Fact]
    public void RejectsANonBoolIfCondition()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { if quantity { return 1; } return 0; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0071");
    }

    [Fact]
    public void RejectsANonBoolWhileCondition()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { while name { return 1; } return 0; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0071");
    }

    [Fact]
    public void RejectsBreakOutsideALoop()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { if quantity > 1 { break; } return 0; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0072");
    }

    [Fact]
    public void RejectsContinueOutsideALoop()
    {
        var result = CompileSource(Prelude + "extend InvoiceItem { fn f() -> int64 { continue; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0073");
    }

    [Fact]
    public void RejectsBreakAfterTheLoopItLooksLikeItBelongsTo()
    {
        var result = CompileSource(
            Prelude
            + "extend Invoice { fn f() -> int64 { for item in items { var x: int64 = 1; } break; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0072");
    }

    [Fact]
    public void AcceptsBreakAndContinueInsideAForLoop()
    {
        var result = CompileSource(
            Prelude
            + "extend Invoice { fn f() -> int64 { for item in items { if item.quantity > 1 { break; } "
            + "continue; } return 0; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void AcceptsBreakAndContinueInsideAWhileLoop()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { var n: int64 = 0; while n < quantity { "
            + "n = n + 1; if n > 2 { break; } continue; } return n; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void LowersBranchesAndLoopsIntoTheIr()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { while true { if quantity > 1 { break; } "
            + "else { continue; } } return 0; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var body = result.Module!.Methods.Single(m => m.Name == "f").Body;
        var loop = Assert.IsType<IrWhile>(body.Statements[0]);
        var branch = Assert.IsType<IrIf>(Assert.Single(loop.Body.Statements));

        Assert.IsType<IrBreak>(Assert.Single(branch.Then.Statements));
        Assert.IsType<IrContinue>(Assert.Single(Assert.IsType<IrBlock>(branch.Else).Statements));
    }
}
