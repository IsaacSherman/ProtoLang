using ProtoLang.Ir;
using ProtoLang.Types;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Explicit numeric conversions (spec 10.3). Spec 10.3 decided there are no implicit numeric
/// conversions, which is what makes 10.1's wrapping rule well defined; until <c>as</c> existed that
/// left mixed-width arithmetic inexpressible rather than merely verbose.
/// </summary>
public class ConversionTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    private static CompilationResult CompileBody(string body)
        => Compile(Prelude + "extend Outer {\n" + body + "\n}");

    private static IrConversion SingleConversion(CompilationResult result, string methodName)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returned = result.Module!.Methods.Single(m => m.Name == methodName).Body
            .Statements.OfType<IrReturn>().Single().Value!;

        return Assert.IsType<IrConversion>(returned);
    }

    /// <summary>
    /// The case the feature exists for: an int32 field and an int64 field in one expression.
    /// </summary>
    [Fact]
    public void MixedWidthArithmeticCompilesOnceAnOperandIsConverted()
    {
        var result = CompileBody("fn f() -> int64 { return small_count as int64 * count; }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    /// <summary>Without the conversion the same expression is the error the feature answers.</summary>
    [Fact]
    public void MixedWidthArithmeticIsStillRejectedWithoutAConversion()
    {
        var result = CompileBody("fn f() -> int64 { return small_count * count; }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0048");
    }

    [Theory]
    [InlineData("count as int32", ConversionKind.IntegerToInteger)]
    [InlineData("small_count as uint32", ConversionKind.IntegerToInteger)]
    [InlineData("count as double", ConversionKind.IntegerToFloat)]
    [InlineData("amount as float", ConversionKind.FloatToFloat)]
    [InlineData("ratio as double", ConversionKind.FloatToFloat)]
    [InlineData("amount as int64", ConversionKind.FloatToInteger)]
    [InlineData("ratio as uint64", ConversionKind.FloatToInteger)]
    public void ClassifiesEachConversionFamily(string expression, ConversionKind expected)
    {
        var returnType = expression.Split(" as ")[1];
        var result = CompileBody($"fn f() -> {returnType} {{ return {expression}; }}");

        Assert.Equal(expected, SingleConversion(result, "f").Kind);
    }

    /// <summary>
    /// A conversion to the type a value already has is allowed and lowers to a no-op. It states
    /// nothing new, but it is not a mistake either, and rejecting it would make a conversion
    /// written for clarity into an error.
    /// </summary>
    [Fact]
    public void AConversionToTheSameTypeIsAnIdentity()
    {
        var result = CompileBody("fn f() -> int64 { return count as int64; }");

        Assert.Equal(ConversionKind.Identity, SingleConversion(result, "f").Kind);
    }

    /// <summary>
    /// The operand of a conversion is bound with no expected type, so a literal takes its own
    /// natural int64 and the conversion genuinely narrows it. Binding the literal against the
    /// target instead would report PL0036 for a value the author explicitly asked to narrow.
    /// </summary>
    [Fact]
    public void AnOutOfRangeLiteralNarrowsRatherThanBeingRejected()
    {
        var result = CompileBody("fn f() -> int32 { return 3000000000 as int32; }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(ConversionKind.IntegerToInteger, SingleConversion(result, "f").Kind);
    }

    [Fact]
    public void CarriesTheBehaviorTheNumericPolicyResolved()
    {
        var result = CompileBody("fn f() -> int32 { return count as int32; }");

        Assert.Equal(ConversionBehavior.WrapOrSaturate, SingleConversion(result, "f").Behavior);
    }

    [Theory]
    [InlineData("fn f() -> int64 { return label as int64; }")]
    [InlineData("fn f() -> string { return count as string; }")]
    [InlineData("fn f() -> int64 { return inner as int64; }")]
    [InlineData("fn f() -> int64 { return status as int64; }")]
    [InlineData("fn f() -> TopLevelStatus { return count as TopLevelStatus; }")]
    [InlineData("fn f() -> int64 { return count as void; }")]
    public void RejectsAConversionThatIsNotBetweenNumericScalars(string body)
    {
        var result = CompileBody(body);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0075");
    }

    [Fact]
    public void NamesBothTypesWhenRejectingAConversion()
    {
        var result = CompileBody("fn f() -> int64 { return label as int64; }");

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0075");
        Assert.Contains("string", diagnostic.ToString(), StringComparison.Ordinal);
        Assert.Contains("int64", diagnostic.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown target type is already PL0025's job, and the conversion must not pile a second
    /// complaint on top of it.
    /// </summary>
    [Fact]
    public void ReportsAnUnknownTargetTypeOnlyOnce()
    {
        var result = CompileBody("fn f() -> int64 { return count as NotAType; }");

        Assert.Single(result.Diagnostics, d => d.Code == "PL0025");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0075");
    }

    /// <summary>
    /// Conversions are expressions, so they work wherever an expression does, including the
    /// fixture and expectation positions of a test declaration.
    /// </summary>
    [Fact]
    public void WorksInsideATestDeclaration()
    {
        var result = Compile(
            Prelude +
            """
            extend Outer {
                fn f() -> int32 {
                    return small_count;
                }
            }

            test Outer.f "a conversion binds in a fixture and an expectation" {
                receiver {
                    small_count = 7 as int32;
                }

                expect return 7 as int32;
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }
}
