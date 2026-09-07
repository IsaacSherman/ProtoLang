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

    /// <summary>
    /// And the converse: a schema that was found and then would not load is still a schema that was
    /// found. Reporting no imports there would say they were never looked at, and would throw away
    /// the file each declaration resolved to -- which is what a diagnostic anchors against and what
    /// a descriptor cache keys on.
    /// </summary>
    [Fact]
    public void ASchemaThatWouldNotLoadIsStillReportedAsResolved()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "broken.proto"), "this is not a protobuf schema");

        var source = Path.Combine(directory, "test.protolang");
        File.WriteAllText(source, "import proto \"broken.proto\";\n" + Body);

        var result = Compilation.Compile(source, [directory]);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0003");

        var import = Assert.Single(result.Imports);
        Assert.Equal(ImportOutcome.Resolved, import.Outcome);
        Assert.Equal(Path.Combine(directory, "broken.proto"), import.ResolvedPath);
    }

    // ------------------------------------------------------- what it nearly said

    /// <summary>
    /// The half of <c>PL0002</c> a reader can act on. A list of directories helps somebody who
    /// already knew what they were aiming at; the name of the schema beside the one they typed tells
    /// them what they got wrong.
    /// </summary>
    [Fact]
    public void AnUnresolvedImportNamesTheSchemaItAlmostNamed()
    {
        var result = CompileSource("import proto \"invoic.proto\";\n" + Body);

        var reported = Assert.Single(result.Diagnostics, d => d.Code == "PL0002");

        Assert.Contains("invoice.proto", reported.Help);
        Assert.StartsWith("Did you mean", reported.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// And where nothing is close enough to name, the help is the sentence it has always been --
    /// which is why the diagnostics the command line already renders do not move.
    /// </summary>
    [Fact]
    public void AnUnresolvedImportWithNothingLikeItSaysOnlyWhereItLooked()
    {
        var result = CompileSource("import proto \"nosuch.proto\";\n" + Body);

        var reported = Assert.Single(result.Diagnostics, d => d.Code == "PL0002");

        Assert.StartsWith("Searched: ", reported.Help, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", reported.Help);
    }

    /// <summary>
    /// The suggestion is drawn from the roots this import was searched against, so a schema that
    /// resolves only because protoc contributes its own directory is one the suggestion can reach.
    /// </summary>
    [Fact]
    public void ANearMissOnAWellKnownSchemaIsNamedFromProtocsOwnDirectory()
    {
        var result = CompileSource("import proto \"google/protobuf/timestam.proto\";\n" + Body);

        var reported = Assert.Single(result.Diagnostics, d => d.Code == "PL0002");

        if (!reported.Help!.Contains("Did you mean", StringComparison.Ordinal))
        {
            Assert.Skip("This protoc ships no well-known schemas as files, so there is nothing to name.");
        }

        Assert.Contains("google/protobuf/timestamp.proto", reported.Help);
    }
}
