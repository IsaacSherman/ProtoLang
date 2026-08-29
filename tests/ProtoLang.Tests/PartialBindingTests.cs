using ProtoLang.Ir;
using ProtoLang.Types;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// A file that does not parse is still bound, so an editor has types for the parts that do.
/// </summary>
/// <remarks>
/// These run the whole pipeline against the real example schema, because the property under test is
/// a property of the pipeline: the binder was always willing to do this and was never asked.
/// </remarks>
public class PartialBindingTests
{
    private static CompilationResult CompileSource(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.ExampleProtoDirectory]);

    private const string Prelude = "import proto \"invoice.proto\";\n";

    /// <summary>The motivating case: the caret sits after a dot and a completion list is due.</summary>
    private const string TrailingDot =
        """
        import proto "invoice.proto";
        extend Invoice {
            fn f() -> int64 {
                for line in items {
                    return line.
                }

                return 0;
            }
        }
        """;

    // ------- the trailing dot

    [Fact]
    public void ATrailingDotBindsItsReceiverAndExposesItsType()
    {
        var awaiting = MissingMemberAccessIn(CompileSource(TrailingDot));

        Assert.Equal(
            "protolang.examples.InvoiceItem",
            Assert.IsType<MessageType>(awaiting.Receiver.Type).Descriptor.FullName);
    }

    [Fact]
    public void ATrailingDotIsAnchoredWhereTheMemberNameWouldGo()
    {
        var awaiting = MissingMemberAccessIn(CompileSource(TrailingDot));

        var afterTheDot = TrailingDot.IndexOf("line.", StringComparison.Ordinal) + "line.".Length;

        Assert.True(awaiting.Span.IsEmpty, "an insertion point covers no text");
        Assert.Equal(afterTheDot, awaiting.Span.Start.Offset);
    }

    /// <summary>
    /// A dot awaiting a name is a different thing in the IR from a member access that failed for
    /// any other reason, all of which collapse to an error-typed literal.
    /// </summary>
    [Fact]
    public void AnAccessAwaitingANameIsNotTheSameAsAnAccessThatFailed()
    {
        var unknownField = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return nosuchfield.x; } }");

        Assert.False(unknownField.Success);
        Assert.Empty(Walk(unknownField.Module!).OfType<IrMissingMemberAccess>());
    }

    // ------- what survives a parse error

    [Fact]
    public void AFileWithAParseErrorStillHasTypesForWhatParsed()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem {\n"
            + "    fn broken() -> int64 { return name. }\n"
            + "    fn whole() -> int64 { return quantity; }\n"
            + "}");

        Assert.True(result.Diagnostics.HasErrors);

        var whole = result.Module!.Methods.Single(m => m.Name == "whole");
        var returned = Assert.IsType<IrReturn>(Assert.Single(whole.Body.Statements));

        Assert.Equal(ScalarType.Int64Type, whole.ReturnType);
        Assert.Equal("quantity", Assert.IsType<IrFieldAccess>(returned.Value).Field.Name);
    }

    [Fact]
    public void AModuleIsCarriedOutOfACompilationThatFailedToParse()
    {
        Assert.NotNull(CompileSource(TrailingDot).Module);
    }

    /// <summary>
    /// The safety property the whole change rests on. Every consumer in the repository asks this
    /// before touching the module, so a module that now exists where none did must not make it true.
    /// </summary>
    [Theory]
    [InlineData("extend Invoice { fn f() -> int64 { return items. } }")]
    [InlineData("extend { }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity }")]
    [InlineData("}{")]
    public void SuccessIsStillFalseWhenTheFileDidNotParse(string body)
    {
        var result = CompileSource(Prelude + body);

        Assert.True(result.Diagnostics.HasErrors);
        Assert.False(result.Success);
    }

    // ------- one mistake, one diagnostic

    /// <summary>
    /// The cascade the error-typed placeholder exists to stop: a name that resolves to nothing must
    /// not also make every expression derived from it complain about its type.
    /// </summary>
    [Fact]
    public void OneUnresolvableNameProducesOneDiagnostic()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { var x: int64 = nope; return x + x * x; } }");

        Assert.Equal("PL0037", Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>
    /// A name that is missing has already been reported by the parser, at the position it is missing
    /// from. Resolving it anyway would say the same thing a second time in different words.
    /// </summary>
    [Theory]
    [InlineData("extend { }", "PL0010", "PL0021")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { var x: = 1; return 1; } }", "PL0013", "PL0025")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return name. } }", "PL0010", "PL0041")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity.(); } }", "PL0010", "PL0044")]
    [InlineData("extend InvoiceItem { fn f() -> bool { return has name.; } }", "PL0010", "PL0041")]
    public void AMissingNameIsNotDiagnosedASecondTimeByTheBinder(
        string body,
        string reportedByTheParser,
        string notReportedAgain)
    {
        var result = CompileSource(Prelude + body);

        Assert.Contains(result.Diagnostics, d => d.Code == reportedByTheParser);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == notReportedAgain);
    }

    /// <summary>
    /// Two methods being typed at once are two unnamed methods, not one method declared twice.
    /// </summary>
    [Fact]
    public void HalfTypedMethodsDoNotCollideWithEachOther()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn () -> int64 { return 1; } fn () -> int64 { return 2; } }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0022");
    }

    [Fact]
    public void HalfTypedParametersDoNotCollideWithEachOther()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f(: int64, : int64) -> int64 { return 1; } }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0026");
    }

    [Fact]
    public void HalfTypedVariablesDoNotCollideWithEachOther()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { var = 1; var = 2; return 1; } }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0029");
    }

    // ------- helpers

    private static IrMissingMemberAccess MissingMemberAccessIn(CompilationResult result)
    {
        Assert.False(result.Success, "the motivating case is a file with a syntax error in it");

        return Assert.Single(Walk(result.Module!).OfType<IrMissingMemberAccess>());
    }

    /// <summary>Every expression in a module, however deeply nested.</summary>
    private static IEnumerable<IrExpression> Walk(IrModule module)
        => module.Methods.SelectMany(method => Walk(method.Body));

    /// <inheritdoc cref="Walk(IrModule)"/>
    private static IEnumerable<IrExpression> Walk(IrStatement statement)
        => statement switch
        {
            IrBlock block => block.Statements.SelectMany(Walk),
            IrVariableDeclaration declaration => Walk(declaration.Initializer),
            IrAssignment assignment => Walk(assignment.Value),
            IrReturn { Value: { } value } => Walk(value),
            IrForEach loop => Walk(loop.Collection).Concat(Walk(loop.Body)),
            IrIf branch => Walk(branch.Condition)
                .Concat(Walk(branch.Then))
                .Concat(branch.Else is { } otherwise ? Walk(otherwise) : []),
            IrWhile loop => Walk(loop.Condition).Concat(Walk(loop.Body)),
            IrExpressionStatement expression => Walk(expression.Expression),
            _ => [],
        };

    /// <inheritdoc cref="Walk(IrModule)"/>
    private static IEnumerable<IrExpression> Walk(IrExpression expression)
    {
        yield return expression;

        var operands = expression switch
        {
            IrFieldAccess field => (IEnumerable<IrExpression>)[field.Receiver],
            IrFieldPresence presence => [presence.Receiver],
            IrMethodCall call => [call.Receiver, .. call.Arguments],
            IrBinary binary => [binary.Left, binary.Right],
            IrIntegerDivision division => division.OnZero is { } onZero
                ? [division.Left, division.Right, onZero]
                : [division.Left, division.Right],
            IrUnary unary => [unary.Operand],
            IrConversion conversion => [conversion.Operand],
            IrMissingMemberAccess awaiting => [awaiting.Receiver],
            _ => [],
        };

        foreach (var operand in operands.SelectMany(Walk))
        {
            yield return operand;
        }
    }
}
