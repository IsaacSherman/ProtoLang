using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Every <c>import proto</c> declaration comes out of a compilation saying what became of it, so a
/// caller can branch on the answer instead of inferring one from the diagnostics.
/// </summary>
public class ImportResolutionTests
{
    private static CompilationResult CompileSource(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.ExampleProtoDirectory]);

    private const string Body = "extend InvoiceItem { fn f() -> int64 { return quantity; } }";

    [Fact]
    public void AResolvedImportSaysWhichFileBacksIt()
    {
        var result = CompileSource("import proto \"invoice.proto\";\n" + Body);

        var import = Assert.Single(result.Imports);

        Assert.Equal(ImportOutcome.Resolved, import.Outcome);
        Assert.True(import.IsResolved);
        Assert.Equal("invoice.proto", import.Path);
        Assert.Equal(
            Path.Combine(TestPaths.ExampleProtoDirectory, "invoice.proto"),
            import.ResolvedPath);
    }

    /// <summary>
    /// The directories searched travel with the import rather than being recomputed by whoever asks
    /// later, because protoc contributes directories the caller never named and could not guess.
    /// </summary>
    [Fact]
    public void AResolvedImportSaysWhereItWasSearchedFor()
    {
        var result = CompileSource("import proto \"invoice.proto\";\n" + Body);

        var import = Assert.Single(result.Imports);

        Assert.Contains(TestPaths.ExampleProtoDirectory, import.SearchedPaths);
        Assert.Contains(
            import.SearchedPaths,
            searched => import.ResolvedPath!.StartsWith(searched, StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportThatIsNotThereIsMarkedNotFoundAndBacksNoFile()
    {
        var result = CompileSource("import proto \"nosuch.proto\";\n" + Body);

        var import = Assert.Single(result.Imports);

        Assert.Equal(ImportOutcome.NotFound, import.Outcome);
        Assert.False(import.IsResolved);
        Assert.Null(import.ResolvedPath);
        Assert.NotEmpty(import.SearchedPaths);
    }

    /// <summary>
    /// The distinction the flag it replaced could not draw: a path that is absent has not been
    /// searched for and has already been reported, and a path that is present and wrong has not.
    /// </summary>
    [Fact]
    public void AnImportWithNoPathIsUnwrittenRatherThanNotFound()
    {
        var result = CompileSource("import proto ;\n" + Body);

        var import = Assert.Single(result.Imports);

        Assert.Equal(ImportOutcome.Unwritten, import.Outcome);
        Assert.True(import.Declaration.PathIsMissing);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0002");
    }

    [Fact]
    public void EveryImportIsAccountedForInTheOrderItWasWritten()
    {
        var result = CompileSource(
            "import proto \"invoice.proto\";\nimport proto \"nosuch.proto\";\nimport proto ;\n" + Body);

        Assert.Equal(
            [ImportOutcome.Resolved, ImportOutcome.NotFound, ImportOutcome.Unwritten],
            result.Imports.Select(import => import.Outcome));
    }

    /// <summary>
    /// The declaration travels with the outcome, so a caller reporting against an import uses the
    /// span the parser recorded rather than one it reconstructs.
    /// </summary>
    [Fact]
    public void AnImportCarriesTheDeclarationItCameFrom()
    {
        const string Source = "import proto \"nosuch.proto\";\n" + Body;

        var result = CompileSource(Source);
        var import = Assert.Single(result.Imports);

        Assert.IsType<ImportDeclaration>(import.Declaration);
        Assert.Equal(import.Declaration.Span, import.Span);
        Assert.Equal(Source.IndexOf("import", StringComparison.Ordinal), import.Span.Start.Offset);
    }

    /// <summary>
    /// A compilation that never reached the imports reports none, rather than reporting them as
    /// having failed. Nothing was asked, so nothing is claimed.
    /// </summary>
    [Fact]
    public void ACompilationThatStoppedBeforeTheImportsReportsNone()
    {
        var result = CompileSource(Body);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0001");
        Assert.Empty(result.Imports);
    }
}
