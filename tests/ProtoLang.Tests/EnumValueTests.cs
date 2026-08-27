using ProtoLang.Ir;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Named enum constants. Enum <em>types</em> became nameable first; until values followed, an enum
/// could be declared, passed, and returned but never compared against anything meaningful, and no
/// test fixture could give an enum field a value other than its default.
/// </summary>
public class EnumValueTests
{
    private const string FixturePrelude = "import proto \"fixtures.proto\";\n";
    private const string AmbiguousPrelude = "import proto \"ambiguous_enums.proto\";\n";

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    private static IrEnumValue SingleEnumValue(CompilationResult result, string methodName)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returned = result.Module!.Methods.Single(m => m.Name == methodName).Body
            .Statements.OfType<IrReturn>().Single().Value!;

        return Assert.IsType<IrEnumValue>(returned);
    }

    [Fact]
    public void ResolvesAValueOfATopLevelEnumBySimpleName()
    {
        var result = Compile(
            FixturePrelude +
            "extend Outer { fn f() -> TopLevelStatus { return TopLevelStatus.TOP_LEVEL_STATUS_OK; } }");

        var value = SingleEnumValue(result, "f");
        Assert.Equal("TOP_LEVEL_STATUS_OK", value.Value.Name);
        Assert.Equal("protolang.tests.TopLevelStatus", value.EnumType.Descriptor.FullName);
    }

    [Fact]
    public void ResolvesAValueOfANestedEnumBySimpleName()
    {
        var result = Compile(
            FixturePrelude + "extend Outer { fn f() -> Nested { return Nested.NESTED_SOME; } }");

        Assert.Equal("protolang.tests.Outer.Nested", SingleEnumValue(result, "f").EnumType.Descriptor.FullName);
    }

    [Fact]
    public void ResolvesAValueByFullyQualifiedEnumName()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> protolang.tests.Outer.Inner.Deep {
                    return protolang.tests.Outer.Inner.Deep.DEEP_NONE;
                }
            }
            """);

        Assert.Equal("DEEP_NONE", SingleEnumValue(result, "f").Value.Name);
    }

    [Fact]
    public void ComparesAFieldAgainstANamedValue()
    {
        var result = Compile(
            FixturePrelude +
            "extend Outer { fn f() -> bool { return status == TopLevelStatus.TOP_LEVEL_STATUS_OK; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void RejectsAValueTheEnumDoesNotDeclare()
    {
        var result = Compile(
            FixturePrelude + "extend Outer { fn f() -> TopLevelStatus { return TopLevelStatus.NOPE; } }");

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0076");
        Assert.Contains("NOPE", diagnostic.ToString(), StringComparison.Ordinal);
        Assert.Contains("protolang.tests.TopLevelStatus", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAValueNamedThroughAnAmbiguousSimpleName()
    {
        var result = Compile(
            AmbiguousPrelude + "extend First { fn f() -> bool { return kind == Kind.FIRST_KIND_NONE; } }");

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0074");
        Assert.Contains("protolang.tests.ambiguous.First.Kind", diagnostic.ToString(), StringComparison.Ordinal);
        Assert.Contains("protolang.tests.ambiguous.Second.Kind", diagnostic.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A name that is in scope as a value wins over an enum type spelled the same way, so the
    /// expression is a field access. Otherwise adding an enum to a schema could silently change
    /// what an existing expression means.
    /// </summary>
    [Fact]
    public void AValueInScopeWinsOverAnEnumTypeOfTheSameName()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> Deep {
                    if not has inner {
                        return Deep.DEEP_NONE;
                    }

                    var TopLevelStatus: Inner = inner;
                    return TopLevelStatus.deep;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    /// <summary>
    /// Member access on something that is neither an enum type nor a value still reports the
    /// original unknown-name diagnostic, and only once.
    /// </summary>
    [Fact]
    public void ReportsAnUnknownReceiverNameOnlyOnce()
    {
        var result = Compile(
            FixturePrelude + "extend Outer { fn f() -> int64 { return nothing.here; } }");

        Assert.Single(result.Diagnostics, d => d.Code == "PL0037");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0076");
    }

    /// <summary>
    /// The second half of the feature. An enum field is set from a named constant, which is an
    /// ordinary expression, so the fixture rule that demands a nested block applies to messages
    /// only.
    /// </summary>
    [Fact]
    public void AFixtureCanSetAnEnumFieldToANamedValue()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> bool {
                    return status == TopLevelStatus.TOP_LEVEL_STATUS_OK;
                }
            }

            test Outer.f "an enum field takes a named value" {
                receiver {
                    status = TopLevelStatus.TOP_LEVEL_STATUS_OK;
                }

                expect return true;
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void RejectsAFixtureValueFromAnotherEnum()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    return count;
                }
            }

            test Outer.f "an enum field rejects a value from another enum" {
                receiver {
                    status = Nested.NESTED_SOME;
                }

                expect return 0;
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0063");
    }

    /// <summary>A message field still demands a nested block rather than an expression.</summary>
    [Fact]
    public void StillRequiresANestedBlockForAMessageFixtureField()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    return count;
                }
            }

            test Outer.f "a message field cannot be set from an expression" {
                receiver {
                    inner = 1;
                }

                expect return 0;
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0062");
    }

    [Fact]
    public void AnExpectationCanBeANamedValue()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> TopLevelStatus {
                    return status;
                }
            }

            test Outer.f "a named value is a valid expectation" {
                receiver {
                    status = TopLevelStatus.OTHER_RESULT;
                }

                expect return TopLevelStatus.OTHER_RESULT;
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }
}
