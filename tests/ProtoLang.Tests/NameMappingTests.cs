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
                fn total() -> int64 { return target.adjusted_value(); }
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
}
