using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Covers the mapping from protobuf names and ProtoLang literals into each target language.
/// These are the cases where emitting something plausible-looking still fails to compile.
/// </summary>
public class NameMappingTests
{
    private static string Emit(IBackend backend, string source, string fileSuffix)
    {
        var path = TestPaths.WriteTempScript(source);
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = backend.Emit(result.Module!, new BackendOptions(Path.GetFileName(path)), diagnostics);
        Assert.Empty(diagnostics);

        return files.Single(f => f.RelativePath.EndsWith(fileSuffix, StringComparison.Ordinal)).Contents;
    }

    private const string FixturePrelude = "import proto \"fixtures.proto\";\n";
    private const string BarePrelude = "import proto \"nopackage.proto\";\n";
    private const string CrossNamespacePrelude =
        "import proto \"cross_target.proto\";\nimport proto \"cross_caller.proto\";\n";

    [Fact]
    public void CSharpQualifiesTopLevelAndNestedEnums()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var top = status;
                    var inner = nested;
                    return count;
                }
            }
            """,
            "test.g.cs");

        Assert.Contains("global::Protolang.Tests.TopLevelStatus top", source, StringComparison.Ordinal);

        // protoc nests enums inside the containing message's Types class.
        Assert.Contains("global::Protolang.Tests.Outer.Types.Nested inner", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The explicit-return-type path, which only became reachable once the binder learned to
    /// resolve enum type references (issue #1).
    /// </summary>
    [Fact]
    public void CSharpQualifiesAnExplicitlyDeclaredEnumReturnType()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> TopLevelStatus {
                    var s: TopLevelStatus = status;
                    return s;
                }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "public static global::Protolang.Tests.TopLevelStatus F(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("global::Protolang.Tests.TopLevelStatus s = self.Status;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppQualifiesAnExplicitlyDeclaredEnumReturnType()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> protolang.tests.Outer.Nested {
                    var n: Nested = nested;
                    return n;
                }
            }
            """,
            "test.pl.h");

        Assert.Contains("inline ::protolang::tests::Outer_Nested f(", source, StringComparison.Ordinal);
        Assert.Contains("::protolang::tests::Outer_Nested n = self.nested();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppFlattensNestedEnumsWithUnderscores()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var top = status;
                    var inner = nested;
                    return count;
                }
            }
            """,
            "test.pl.h");

        Assert.Contains("::protolang::tests::TopLevelStatus top", source, StringComparison.Ordinal);
        Assert.Contains("::protolang::tests::Outer_Nested inner", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpHandlesEnumsInAFileWithNoPackage()
    {
        var source = Emit(
            new CSharpBackend(),
            BarePrelude +
            """
            extend BareMessage {
                fn f() -> int64 {
                    var s = status;
                    return value;
                }
            }
            """,
            "test.g.cs");

        // Not "global::.BareStatus".
        Assert.Contains("global::BareStatus s", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppHandlesEnumsInAFileWithNoPackage()
    {
        var source = Emit(
            new CppBackend(),
            BarePrelude +
            """
            extend BareMessage {
                fn f() -> int64 {
                    var s = status;
                    return value;
                }
            }
            """,
            "test.pl.h");

        Assert.Contains("::BareStatus s", source, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpSuffixesFloatLiteralsWithF()
    {
        // 'return 1.5d;' would not compile: C# does not implicitly narrow double to float.
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn f() -> float { return 1.5; } }",
            "test.g.cs");

        Assert.Contains("return 1.5f;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpSuffixesDoubleLiteralsWithD()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn f() -> double { return 1.5; } }",
            "test.g.cs");

        Assert.Contains("return 1.5d;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppSuffixesFloatLiteralsWithF()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude + "extend Outer { fn f() -> float { return 1.5; } }",
            "test.pl.h");

        Assert.Contains("return 1.5f;", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void EscapesControlCharactersInStringLiterals(string target)
    {
        // The lexer decodes escapes, so the IR holds a real newline and tab here. Emitting them
        // raw would produce a literal spanning three lines, which neither language accepts.
        const string Script = """
            extend Outer {
                fn f() -> string { return "a\nb\tc\"d\\e"; }
            }
            """;

        var source = target == "csharp"
            ? Emit(new CSharpBackend(), FixturePrelude + Script, "test.g.cs")
            : Emit(new CppBackend(), FixturePrelude + Script, "test.pl.h");

        Assert.Contains("\"a\\nb\\tc\\\"d\\\\e\"", source, StringComparison.Ordinal);

        // The emitted literal must not have been split across lines.
        var literalLine = source.Split('\n').Single(line => line.Contains("return \"a", StringComparison.Ordinal));
        Assert.EndsWith(";", literalLine.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void CppEscapesMethodNamesThatAreKeywords()
    {
        var source = Emit(
            new CppBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn operator() -> int64 { return count; }
                fn caller() -> int64 { return operator(); }
            }
            """,
            "test.pl.h");

        // Declaration, definition, and call site must agree on the escaped spelling.
        Assert.Contains("inline ::std::int64_t operator_(", source, StringComparison.Ordinal);
        Assert.Contains("::protolang::tests::operator_(self)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("int64_t operator(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpPascalCaseAvoidsKeywordCollisions()
    {
        // Every C# reserved word is lowercase, so PascalCasing is already sufficient escaping.
        // 'class' is not a ProtoLang keyword, so it reaches the backend as an ordinary identifier.
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude + "extend Outer { fn class() -> int64 { return count; } }",
            "test.g.cs");

        Assert.Contains("public static long Class(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesCrossNamespaceMethodCalls()
    {
        var source = Emit(
            new CSharpBackend(),
            CrossNamespacePrelude +
            """
            extend protolang.target.Target {
                fn adjusted_value() -> int64 { return value + 1; }
            }

            extend protolang.caller.Caller {
                fn total() -> int64 {
                    if not has target {
                        return 0;
                    }

                    return target.adjusted_value();
                }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "return global::ProtoLang.Target.TargetProtoLangExtensions.AdjustedValue(self.Target);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".Target.AdjustedValue()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpIncludesContainingMessagesInNestedReceiverExtensionClassNames()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend protolang.tests.Outer.Inner {
                fn f() -> int64 { return 1; }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "public static class Outer_InnerProtoLangExtensions",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "this global::Protolang.Tests.Outer.Types.Inner self",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpMethodCallsDoNotBindToProtobufInstanceMembers()
    {
        var source = Emit(
            new CSharpBackend(),
            FixturePrelude +
            """
            extend Outer {
                fn clone() -> int64 { return count; }
                fn caller() -> int64 { return clone(); }
            }
            """,
            "test.g.cs");

        Assert.Contains(
            "return global::Protolang.Tests.OuterProtoLangExtensions.Clone(self);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("return self.Clone();", source, StringComparison.Ordinal);
    }

    // --- enum values ---
    //
    // protoc names an enum value completely differently in the two targets: C# strips the enum name
    // off the front and PascalCases the rest, while C++ keeps the .proto spelling and prefixes
    // nested enums with the flattened type name. Neither is derivable from the other, and a
    // near-miss emits an identifier that does not exist.

    private static string EmitEnumValue(
        IBackend backend,
        string prelude,
        string receiver,
        string returnType,
        string value,
        string suffix)
        => Emit(backend, prelude + $"extend {receiver} {{ fn f() -> {returnType} {{ return {value}; }} }}", suffix);

    [Fact]
    public void CSharpStripsTheEnumPrefixAndPascalCasesTheValue()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_OK",
            "test.g.cs");

        Assert.Contains("return global::Protolang.Tests.TopLevelStatus.Ok;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesANestedEnumValueThroughTheTypesClass()
    {
        var source = EmitEnumValue(
            new CSharpBackend(), FixturePrelude, "Outer", "Nested", "Nested.NESTED_SOME", "test.g.cs");

        Assert.Contains(
            "return global::Protolang.Tests.Outer.Types.Nested.Some;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpQualifiesADeeplyNestedEnumValue()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "protolang.tests.Outer.Inner.Deep",
            "protolang.tests.Outer.Inner.Deep.DEEP_NONE",
            "test.g.cs");

        Assert.Contains(
            "return global::Protolang.Tests.Outer.Types.Inner.Types.Deep.None;",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// protoc only strips a prefix the value actually carries, so a value named independently of
    /// its enum keeps every part of its name.
    /// </summary>
    [Fact]
    public void CSharpKeepsTheWholeNameOfAValueWithoutTheEnumPrefix()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.OTHER_RESULT",
            "test.g.cs");

        Assert.Contains(
            "return global::Protolang.Tests.TopLevelStatus.OtherResult;",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Stripping the enum name from TOP_LEVEL_STATUS_2 leaves "2", which is not an identifier, so
    /// protoc prefixes an underscore. Emitting the bare digit would not compile.
    /// </summary>
    [Fact]
    public void CSharpUnderscoresAValueThatStripsToALeadingDigit()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_2",
            "test.g.cs");

        Assert.Contains("return global::Protolang.Tests.TopLevelStatus._2;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppLeavesATopLevelEnumValueUnprefixed()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            FixturePrelude,
            "Outer",
            "TopLevelStatus",
            "TopLevelStatus.TOP_LEVEL_STATUS_OK",
            "test.pl.h");

        Assert.Contains("return ::protolang::tests::TOP_LEVEL_STATUS_OK;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested enum has its values at namespace scope prefixed with the flattened enum name,
    /// rather than as members of the enum, so the qualification goes on the value and not the type.
    /// </summary>
    [Fact]
    public void CppPrefixesANestedEnumValueWithTheFlattenedEnumName()
    {
        var source = EmitEnumValue(
            new CppBackend(), FixturePrelude, "Outer", "Nested", "Nested.NESTED_SOME", "test.pl.h");

        Assert.Contains(
            "return ::protolang::tests::Outer_Nested_NESTED_SOME;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppFlattensADeeplyNestedEnumValue()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            FixturePrelude,
            "Outer",
            "protolang.tests.Outer.Inner.Deep",
            "protolang.tests.Outer.Inner.Deep.DEEP_NONE",
            "test.pl.h");

        Assert.Contains(
            "return ::protolang::tests::Outer_Inner_Deep_DEEP_NONE;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpHandlesEnumValuesInAFileWithNoPackage()
    {
        var source = EmitEnumValue(
            new CSharpBackend(),
            BarePrelude,
            "BareMessage",
            "BareStatus",
            "BareStatus.BARE_STATUS_SET",
            "test.g.cs");

        Assert.Contains("return global::BareStatus.Set;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("global::.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppHandlesEnumValuesInAFileWithNoPackage()
    {
        var source = EmitEnumValue(
            new CppBackend(),
            BarePrelude,
            "BareMessage",
            "BareStatus",
            "BareStatus.BARE_STATUS_SET",
            "test.pl.h");

        Assert.Contains("return ::BARE_STATUS_SET;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", source, StringComparison.Ordinal);
    }

    // --- explicit conversions ---

    private static string EmitConversion(IBackend backend, string returnType, string expression, string suffix)
        => Emit(
            backend,
            FixturePrelude + $"extend Outer {{ fn f() -> {returnType} {{ return {expression}; }} }}",
            suffix);

    /// <summary>
    /// A narrowing conversion has to be unchecked. The conformance harness builds generated code
    /// with CheckForOverflowUnderflow, where a bare cast throws instead of wrapping.
    /// </summary>
    [Fact]
    public void CSharpNarrowsIntegersInsideUnchecked()
    {
        var source = EmitConversion(new CSharpBackend(), "int32", "count as int32", "test.g.cs");

        Assert.Contains("return unchecked((int)self.Count);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpConvertsToFloatingPointWithAPlainCast()
    {
        var source = EmitConversion(new CSharpBackend(), "double", "count as double", "test.g.cs");

        Assert.Contains("return (double)self.Count;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Floating point to integer is the one conversion C# leaves unspecified when out of range, and
    /// throws on under a checked build, so it cannot be a cast in either context.
    /// </summary>
    [Fact]
    public void CSharpRoutesFloatToIntegerThroughTheRuntime()
    {
        var source = EmitConversion(new CSharpBackend(), "int32", "amount as int32", "test.g.cs");

        Assert.Contains(
            "return global::ProtoLang.Runtime.ProtoLangArithmetic.ToInt32((double)self.Amount);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("(int)self.Amount", source, StringComparison.Ordinal);
    }

    /// <summary>A float source widens to double first, so one helper serves both widths.</summary>
    [Fact]
    public void CSharpWidensAFloatSourceBeforeConvertingToAnInteger()
    {
        var source = EmitConversion(new CSharpBackend(), "int64", "ratio as int64", "test.g.cs");

        Assert.Contains(
            "return global::ProtoLang.Runtime.ProtoLangArithmetic.ToInt64((double)self.Ratio);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppNarrowsIntegersWithStaticCast()
    {
        var source = EmitConversion(new CppBackend(), "int32", "count as int32", "test.pl.h");

        Assert.Contains("return static_cast<::std::int32_t>(self.count());", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppRoutesFloatToIntegerThroughTheRuntime()
    {
        var source = EmitConversion(new CppBackend(), "int32", "amount as int32", "test.pl.h");

        Assert.Contains(
            "return ::protolang_runtime::trunc_sat_f64_to_i32(static_cast<double>(self.amount()));",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Narrowing a double past the range of float is undefined behavior in C++, so that direction
    /// needs a helper even though widening does not.
    /// </summary>
    [Fact]
    public void CppRoutesDoubleToFloatThroughTheRuntimeButNotTheReverse()
    {
        var narrowing = EmitConversion(new CppBackend(), "float", "amount as float", "test.pl.h");
        var widening = EmitConversion(new CppBackend(), "double", "ratio as double", "test.pl.h");

        Assert.Contains(
            "return ::protolang_runtime::narrow_f64_to_f32(self.amount());",
            narrowing,
            StringComparison.Ordinal);
        Assert.Contains("return static_cast<double>(self.ratio());", widening, StringComparison.Ordinal);
    }

    /// <summary>A conversion to the type a value already has emits nothing at all.</summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public void AnIdentityConversionEmitsTheOperandUnchanged(string target)
    {
        var isCSharp = target == "csharp";
        var source = EmitConversion(
            isCSharp ? new CSharpBackend() : new CppBackend(),
            "int64",
            "count as int64",
            isCSharp ? "test.g.cs" : "test.pl.h");

        Assert.Contains(
            isCSharp ? "return self.Count;" : "return self.count();",
            source,
            StringComparison.Ordinal);
    }
}
