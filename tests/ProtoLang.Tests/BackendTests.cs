using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using Xunit;

namespace ProtoLang.Tests;

public class BackendTests
{
    private static IReadOnlyList<GeneratedFile> Emit(IBackend backend, string sourcePath, out DiagnosticBag diagnostics)
    {
        var result = Compilation.Compile(sourcePath, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        diagnostics = new DiagnosticBag();
        return backend.Emit(result.Module!, new BackendOptions(Path.GetFileName(sourcePath)), diagnostics);
    }

    private static string EmitSingle(IBackend backend, string fileSuffix)
    {
        var files = Emit(backend, TestPaths.SimpleScript, out var diagnostics);
        Assert.Empty(diagnostics);
        return files.Single(f => f.RelativePath.EndsWith(fileSuffix, StringComparison.Ordinal)).Contents;
    }

    [Fact]
    public void CSharpEmitsExtensionMethodsOverGeneratedMessages()
    {
        var source = EmitSingle(new CSharpBackend(), "simpleScript.g.cs");

        Assert.Contains(
            "public static long LineTotalCents(this global::Protolang.Examples.InvoiceItem self)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static long TotalCents(this global::Protolang.Examples.Invoice self)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpStatesWrappingExplicitly()
    {
        var source = EmitSingle(new CSharpBackend(), "simpleScript.g.cs");

        // C# already wraps by default, but a consumer setting CheckForOverflowUnderflow would
        // change that silently. The generated code must not depend on their build settings.
        Assert.Contains("unchecked(self.Quantity * self.UnitPriceCents)", source, StringComparison.Ordinal);
        Assert.Contains(
            "unchecked(total + global::Protolang.Examples.InvoiceItemProtoLangExtensions.LineTotalCents(item))",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpMapsSnakeCaseToProtobufPascalCase()
    {
        var source = EmitSingle(new CSharpBackend(), "simpleScript.g.cs");

        Assert.Contains("self.UnitPriceCents", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var item in self.Items)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpEmitsArithmeticRuntimeAlongsideGeneratedCode()
    {
        var files = Emit(new CSharpBackend(), TestPaths.SimpleScript, out var diagnostics);

        Assert.Empty(diagnostics);
        var runtime = files.Single(f => f.RelativePath == CSharpRuntime.FileName);

        // unchecked does not suppress the MIN / -1 trap, so division needs a helper.
        Assert.Contains("WrapDivide", runtime.Contents, StringComparison.Ordinal);
        Assert.Contains("WrapModulo", runtime.Contents, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpEmitsXUnitTestsForProtoLangUnitTests()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = new CSharpBackend().EmitTests(
            result.Module!,
            new BackendOptions("simpleScript.protolang"),
            diagnostics);

        Assert.Empty(diagnostics);
        var source = Assert.Single(files).Contents;

        // The display name is the backend-independent identity, so a conformance harness reading
        // the test log sees the same string the C++ driver prints.
        Assert.Contains(
            "[global::Xunit.Fact(DisplayName = \"protolang.examples.Invoice.total_cents: sums line totals\")]",
            source,
            StringComparison.Ordinal);
        Assert.Contains("var receiver = new global::Protolang.Examples.Invoice", source, StringComparison.Ordinal);
        Assert.Contains("Items =", source, StringComparison.Ordinal);
        Assert.Contains("Quantity = 2L", source, StringComparison.Ordinal);
        Assert.Contains("UnitPriceCents = 300L", source, StringComparison.Ordinal);
        Assert.Contains(
            "global::Xunit.Assert.Equal(1100L, global::Protolang.Examples.InvoiceProtoLangExtensions.TotalCents(receiver));",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CppEmitsStandaloneTestsForProtoLangUnitTests()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = new CppBackend().EmitTests(
            result.Module!,
            new BackendOptions("simpleScript.protolang"),
            diagnostics);

        Assert.Empty(diagnostics);
        var source = Assert.Single(files).Contents;

        Assert.Contains("#include \"simpleScript.pl.h\"", source, StringComparison.Ordinal);
        Assert.Contains("int main(int argc, char** argv)", source, StringComparison.Ordinal);
        Assert.Contains("::protolang::examples::Invoice receiver;", source, StringComparison.Ordinal);
        Assert.Contains("auto* items = receiver.add_items();", source, StringComparison.Ordinal);
        Assert.Contains("items->set_quantity(2LL);", source, StringComparison.Ordinal);
        Assert.Contains("items->set_unit_price_cents(300LL);", source, StringComparison.Ordinal);
        Assert.Contains(
            "const auto actual = ::protolang::examples::total_cents(receiver);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("const auto expected = 1100LL;", source, StringComparison.Ordinal);

        // The driver reports each test by its backend-independent identity, and prints how many it
        // ran: a driver that ran none also exits 0, so the count is what makes the exit code mean
        // something.
        Assert.Contains(
            "::std::cout << \"[ok] protolang.examples.Invoice.total_cents: sums line totals\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("::std::cout << \"protolang: 12 test(s), \"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source with no <c>expect fail</c> test must not gain the child-process machinery, so the
    /// common case emits exactly what it emitted before the feature existed.
    /// </summary>
    [Fact]
    public void CSharpOmitsTestSupportWhenNoTestExpectsFailure()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var files = new CSharpBackend().EmitTests(
            result.Module!, new BackendOptions("simpleScript.protolang"), new DiagnosticBag());

        Assert.DoesNotContain(files, file => file.RelativePath == CSharpTestRuntime.FileName);
        Assert.DoesNotContain(
            "ModuleInitializer",
            Assert.Single(files).Contents,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothBackendsGenerateOutOfProcessTestsForExpectedFailure()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "invoice.proto";

            extend InvoiceItem {
                fn strict_ratio() -> int64 {
                    return unit_price_cents / quantity on_zero fail;
                }
            }

            test InvoiceItem.strict_ratio "a zero divisor stops the program" {
                receiver {
                    quantity = 0;
                    unit_price_cents = 100;
                }

                expect fail;
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var options = new BackendOptions(Path.GetFileName(path));

        var csharpDiagnostics = new DiagnosticBag();
        var csharpFiles = new CSharpBackend().EmitTests(result.Module!, options, csharpDiagnostics);
        Assert.Empty(csharpDiagnostics);

        // The support file carries the child-process launcher; the per-source file carries the
        // module initializer the child lands in.
        Assert.Contains(csharpFiles, file => file.RelativePath == CSharpTestRuntime.FileName);
        var csharpTests = csharpFiles.Single(file => file.RelativePath.EndsWith(".tests.g.cs", StringComparison.Ordinal));
        Assert.Contains("ModuleInitializer", csharpTests.Contents, StringComparison.Ordinal);
        Assert.Contains(
            "global::ProtoLang.Runtime.ProtoLangTestSupport.DescribeExpectFail(",
            csharpTests.Contents,
            StringComparison.Ordinal);

        var cppDiagnostics = new DiagnosticBag();
        var cppSource = Assert.Single(new CppBackend().EmitTests(result.Module!, options, cppDiagnostics)).Contents;
        Assert.Empty(cppDiagnostics);

        Assert.Contains("protolang_expect_fail(argv[0]", cppSource, StringComparison.Ordinal);
        Assert.Contains("::std::system(command.c_str())", cppSource, StringComparison.Ordinal);
        Assert.Contains("return kProtoLangDidNotTerminate;", cppSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CppRoutesArithmeticThroughTheRuntime()
    {
        var source = EmitSingle(new CppBackend(), ".pl.h");

        // Bare 'a * b' on int64 would be undefined behavior on overflow.
        Assert.Contains(
            "::protolang_runtime::wrap_mul_i64(self.quantity(), self.unit_price_cents())",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("self.quantity() * self.unit_price_cents()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppDeclaresEveryMethodBeforeDefiningAny()
    {
        var source = EmitSingle(new CppBackend(), ".pl.h");

        const string LineTotalSignature =
            "inline ::std::int64_t line_total_cents(const ::protolang::examples::InvoiceItem& self)";
        const string TotalSignature =
            "inline ::std::int64_t total_cents(const ::protolang::examples::Invoice& self)";

        // Each signature appears twice: the declaration (terminated by ';') then the definition.
        var lineTotalDeclaration = source.IndexOf(LineTotalSignature + ";", StringComparison.Ordinal);
        var totalDeclaration = source.IndexOf(TotalSignature + ";", StringComparison.Ordinal);
        var lineTotalDefinition = source.LastIndexOf(LineTotalSignature, StringComparison.Ordinal);
        var totalDefinition = source.LastIndexOf(TotalSignature, StringComparison.Ordinal);

        Assert.True(lineTotalDeclaration >= 0, "expected a forward declaration for line_total_cents");
        Assert.True(totalDeclaration >= 0, "expected a forward declaration for total_cents");

        // total_cents calls line_total_cents, so every declaration must precede every definition.
        Assert.True(
            Math.Max(lineTotalDeclaration, totalDeclaration) < Math.Min(lineTotalDefinition, totalDefinition),
            "declarations must all appear before the first definition");
    }

    [Fact]
    public void CppIncludesTheGeneratedProtobufHeader()
    {
        var source = EmitSingle(new CppBackend(), ".pl.h");

        Assert.Contains("#include \"invoice.pb.h\"", source, StringComparison.Ordinal);
        Assert.Contains("#include \"protolang_runtime.h\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppRuntimeWrapsThroughTheUnsignedDomain()
    {
        var files = Emit(new CppBackend(), TestPaths.SimpleScript, out _);
        var runtime = files.Single(f => f.RelativePath == CppRuntime.FileName);

        Assert.Contains(
            "return static_cast<::std::int64_t>(static_cast<::std::uint64_t>(a) * static_cast<::std::uint64_t>(b));",
            runtime.Contents,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothBackendsRejectVirtualMethods()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "invoice.proto";
            extend InvoiceItem {
                virtual fn line_total_cents() -> int64 {
                    return quantity * unit_price_cents;
                }
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, "the binder accepts virtual; only backends reject it");

        var options = new BackendOptions("test.protolang");

        var csharpDiagnostics = new DiagnosticBag();
        new CSharpBackend().Emit(result.Module!, options, csharpDiagnostics);
        Assert.Contains(csharpDiagnostics, d => d.Code == "PL1001");

        var cppDiagnostics = new DiagnosticBag();
        new CppBackend().Emit(result.Module!, options, cppDiagnostics);
        Assert.Contains(cppDiagnostics, d => d.Code == "PL1101");
    }

    private static string EmitDivisionSample(IBackend backend, string fileSuffix)
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "invoice.proto";
            extend InvoiceItem {
                fn checked_ratio() -> int64 { return quantity / unit_price_cents on_zero 0; }
                fn literal_ratio() -> int64 { return quantity / 2; }
            }
            """);

        var files = Emit(backend, path, out var diagnostics);
        Assert.Empty(diagnostics);
        return files.Single(f => f.RelativePath.EndsWith(fileSuffix, StringComparison.Ordinal)).Contents;
    }

    [Fact]
    public void CSharpRoutesCheckedDivisionThroughTheFallbackHelper()
    {
        var source = EmitDivisionSample(new CSharpBackend(), "test.g.cs");

        Assert.Contains(
            "WrapDivideOr(self.Quantity, self.UnitPriceCents, 0L)",
            source,
            StringComparison.Ordinal);

        // A non-zero literal divisor cannot fail, so no fallback is threaded through.
        Assert.Contains("WrapDivide(self.Quantity, 2L)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppRoutesCheckedDivisionThroughTheFallbackHelper()
    {
        var source = EmitDivisionSample(new CppBackend(), "test.pl.h");

        Assert.Contains(
            "wrap_div_or_i64(self.quantity(), self.unit_price_cents(), 0LL)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("wrap_div_i64(self.quantity(), 2LL)", source, StringComparison.Ordinal);

        // Never the bare operator: integer division by zero is undefined behavior in C++.
        Assert.DoesNotContain("self.quantity() / ", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBackendsRouteOnZeroFailThroughTheFailHelper()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "invoice.proto";
            extend InvoiceItem {
                fn strict_ratio() -> int64 { return quantity / unit_price_cents on_zero fail; }
            }
            """);

        var csharp = Emit(new CSharpBackend(), path, out var csharpDiagnostics)
            .Single(f => f.RelativePath.EndsWith("test.g.cs", StringComparison.Ordinal)).Contents;
        var cpp = Emit(new CppBackend(), path, out var cppDiagnostics)
            .Single(f => f.RelativePath.EndsWith("test.pl.h", StringComparison.Ordinal)).Contents;

        Assert.Empty(csharpDiagnostics);
        Assert.Empty(cppDiagnostics);

        Assert.Contains(
            "WrapDivideOrFail(self.Quantity, self.UnitPriceCents)", csharp, StringComparison.Ordinal);
        Assert.Contains(
            "wrap_div_or_fail_i64(self.quantity(), self.unit_price_cents())", cpp, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRuntimesTerminateRatherThanThrowOnFail()
    {
        var csharp = Emit(new CSharpBackend(), TestPaths.SimpleScript, out _)
            .Single(f => f.RelativePath == CSharpRuntime.FileName).Contents;
        var cpp = Emit(new CppBackend(), TestPaths.SimpleScript, out _)
            .Single(f => f.RelativePath == CppRuntime.FileName).Contents;

        // A catchable exception would let a consumer resume from a state the author declared has
        // no valid result, and C++ has no equivalent under this design.
        Assert.Contains("Environment.FailFast", csharp, StringComparison.Ordinal);
        Assert.Contains("WrapDivideOrFail", csharp, StringComparison.Ordinal);
        Assert.Contains("[[noreturn]] inline void fail", cpp, StringComparison.Ordinal);
        Assert.Contains("::std::abort();", cpp, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRuntimesExposeTheFallbackHelpers()
    {
        var csharp = Emit(new CSharpBackend(), TestPaths.SimpleScript, out _)
            .Single(f => f.RelativePath == CSharpRuntime.FileName).Contents;
        var cpp = Emit(new CppBackend(), TestPaths.SimpleScript, out _)
            .Single(f => f.RelativePath == CppRuntime.FileName).Contents;

        Assert.Contains("WrapDivideOr", csharp, StringComparison.Ordinal);
        Assert.Contains("WrapModuloOr", csharp, StringComparison.Ordinal);
        Assert.Contains("wrap_div_or_i64", cpp, StringComparison.Ordinal);
        Assert.Contains("wrap_mod_or_i64", cpp, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputUsesLineFeedsOnlySoGoldenTestsAreStable()
    {
        var source = EmitSingle(new CSharpBackend(), "simpleScript.g.cs");

        Assert.DoesNotContain('\r', source);
    }

    [Fact]
    public void CSharpFlattensElseIfChains()
    {
        var source = EmitSingle(new CSharpBackend(), "simpleScript.g.cs");

        // 'else if' rather than an 'else' wrapping a nested 'if': one brace level per branch is
        // what the author wrote, and what a reader of the generated code should see.
        Assert.Contains("if ((self.Quantity >= 100L))", source, StringComparison.Ordinal);
        Assert.Contains("else if ((self.Quantity >= 10L))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpEmitsLoopsWithBreakAndContinue()
    {
        var source = EmitSingle(new CSharpBackend(), "simpleScript.g.cs");

        Assert.Contains("while ((remaining >= case_size))", source, StringComparison.Ordinal);
        Assert.Contains("while (true)", source, StringComparison.Ordinal);
        Assert.Contains("break;", source, StringComparison.Ordinal);
        Assert.Contains("continue;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppFlattensElseIfChains()
    {
        var source = EmitSingle(new CppBackend(), "simpleScript.pl.h");

        Assert.Contains("if ((self.quantity() >= 100LL))", source, StringComparison.Ordinal);
        Assert.Contains("else if ((self.quantity() >= 10LL))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CppEmitsLoopsWithBreakAndContinue()
    {
        var source = EmitSingle(new CppBackend(), "simpleScript.pl.h");

        Assert.Contains("while ((remaining >= case_size))", source, StringComparison.Ordinal);
        Assert.Contains("while (true)", source, StringComparison.Ordinal);
        Assert.Contains("break;", source, StringComparison.Ordinal);
        Assert.Contains("continue;", source, StringComparison.Ordinal);
    }
}
