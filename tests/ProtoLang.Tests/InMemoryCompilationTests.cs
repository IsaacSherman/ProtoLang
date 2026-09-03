using ProtoLang.Backend;
using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Compiling text the caller already holds, which is what an editor has: the buffer is
/// authoritative, the file on disk is stale between saves and absent before the first one.
/// </summary>
/// <remarks>
/// Two properties are load-bearing across the whole set and are asserted directly rather than
/// implied. Nothing the in-memory route touches may reach the file on disk at the same path, and
/// everything the path route derives from a path -- the diagnostic label, the discovered config, the
/// import search paths -- must come out identical when it is derived from an identity instead.
/// </remarks>
public class InMemoryCompilationTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    private const string Body =
        "extend Outer { fn f() -> int64 { return count + count; } }";

    private static readonly IReadOnlyList<string> Fixtures = [TestPaths.FixtureProtoDirectory];

    // ---------------------------------------------------------------- text without a file

    [Fact]
    public void CompilesTextThatWasNeverWrittenToDisk()
    {
        var identity = UnwrittenFile();

        Assert.False(File.Exists(identity.Path!), "the point of the test is that nothing is there");

        var result = Compile(identity, Prelude + Body);

        Assert.True(result.Success, Describe(result));
        Assert.Equal("f", result.Module!.Methods.Single().Name);
    }

    [Fact]
    public void DiagnosticsFromInMemoryTextCarryTheBufferName()
    {
        var identity = UnwrittenFile("buffer.protolang");

        var result = Compile(
            identity,
            Prelude + "extend Outer { fn f() -> int64 { return no_such_field; } }");

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0037");
        Assert.Equal("buffer.protolang", diagnostic.Span.File);
    }

    /// <summary>
    /// The byte-for-byte guard. The rendered diagnostic is what the CLI prints, and the label inside
    /// it is the one thing this change could have moved without any existing test noticing.
    /// </summary>
    [Fact]
    public void TheSameTextDiagnosesIdenticallyWhicheverDoorItCameIn()
    {
        const string Source = Prelude + "extend Outer { fn f() -> int64 { return no_such_field; } }";

        var path = TestPaths.WriteTempScript(Source);

        var fromDisk = Compilation.Compile(path, Fixtures);
        var fromText = Compilation.Compile(
            new SourceDocument(SourceIdentity.FromPath(path), Source),
            Fixtures);

        Assert.Equal(Render(fromDisk), Render(fromText));
    }

    // ---------------------------------------------------------------- the text wins over the disk

    [Fact]
    public void TheSuppliedTextWinsOverTheFileOnDisk()
    {
        var path = TestPaths.WriteTempScript(
            Prelude + "extend Outer { fn on_disk() -> int64 { return count; } }");

        var result = Compile(
            SourceIdentity.FromPath(path),
            Prelude + "extend Outer { fn in_memory() -> int64 { return count; } }");

        Assert.True(result.Success, Describe(result));
        Assert.Equal("in_memory", result.Module!.Methods.Single().Name);
    }

    /// <summary>
    /// The disk copy here cannot even be lexed. If any route still reached for it -- to read, to
    /// re-read, to check -- this could not pass.
    /// </summary>
    [Fact]
    public void AFileOnDiskThatCannotEvenLexIsNeverLookedAt()
    {
        var path = TestPaths.WriteTempScript("@@@ this is not protolang @@@");

        var result = Compile(SourceIdentity.FromPath(path), Prelude + Body);

        Assert.True(result.Success, Describe(result));
    }

    // ---------------------------------------------------------------- config discovery

    [Fact]
    public void ConfigIsDiscoveredFromTheIdentityThoughNoSourceFileExists()
    {
        var identity = UnwrittenFile();
        WriteConfig(identity.Directory!, "<Arithmetic><Overflow>Saturating</Overflow></Arithmetic>");

        var result = Compile(identity, Prelude + Body);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(OverflowPolicy.Saturating, result.Config.Overflow);
        Assert.Equal(ArithmeticBehavior.Saturate, ReturnedBinary(result).Behavior);
    }

    /// <summary>
    /// A project that states a policy and is then ignored is worse off than one that states nothing.
    /// That has to hold whether the source came off disk or out of a buffer.
    /// </summary>
    [Fact]
    public void ABadConfigStopsAnInMemoryCompilationToo()
    {
        var identity = UnwrittenFile();
        WriteConfig(identity.Directory!, "<Arithmetic><Overflow>Nonsense</Overflow></Arithmetic>");

        var result = Compile(identity, Prelude + Body);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "PL2002");
    }

    [Fact]
    public void AnUnsavedBufferWithNoDirectoryRunsUnderTheDefaultPolicy()
    {
        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved(), Prelude + Body),
            Fixtures);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(ProjectConfig.Default.Overflow, result.Config.Overflow);
    }

    [Fact]
    public void AnUnsavedBufferInsideAProjectStillFindsItsConfig()
    {
        var directory = TestPaths.CreateTempDirectory();
        WriteConfig(directory, "<Arithmetic><Overflow>Checked</Overflow></Arithmetic>");

        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved("draft.protolang", directory), Prelude + Body),
            Fixtures);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(OverflowPolicy.Checked, result.Config.Overflow);
    }

    // ---------------------------------------------------------------- the import search path

    [Fact]
    public void TheIdentityDirectoryIsSearchedForImportsWithoutBeingNamed()
    {
        var identity = UnwrittenFile();
        File.Copy(
            Path.Combine(TestPaths.FixtureProtoDirectory, "fixtures.proto"),
            Path.Combine(identity.Directory!, "fixtures.proto"));

        // No include paths at all: the source's own directory is the whole search path.
        var result = Compilation.Compile(
            new SourceDocument(identity, Prelude + Body),
            []);

        Assert.True(result.Success, Describe(result));
    }

    [Fact]
    public void TheSearchPathsAreTheSameWhicheverDoorTheTextCameIn()
    {
        var path = TestPaths.WriteTempScript(Prelude + Body);

        var fromText = new Compilation(
            new SourceDocument(SourceIdentity.FromPath(path), Prelude + Body),
            new CompilationOptions { IncludePaths = Fixtures });

        Assert.Equal(Compilation.GetSearchPaths(path, Fixtures), fromText.SearchPaths);
    }

    // ---------------------------------------------------------------- a buffer with no path at all

    [Fact]
    public void AnUnsavedBufferStillReportsSyntaxErrors()
    {
        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved(), "extend { fn"),
            []);

        Assert.False(result.Success);
        Assert.True(result.Diagnostics.HasErrors);
        Assert.All(result.Diagnostics, d => Assert.Equal("<unsaved>", d.Span.File));
    }

    /// <summary>
    /// The requirement is a diagnostic, not an exception: with no path there is no source directory
    /// to fall back on, and reaching for one is how the old code would have crashed.
    /// </summary>
    [Fact]
    public void AnUnsavedBufferReportsAnUnresolvableImportAsADiagnostic()
    {
        var result = Compilation.Compile(
            new SourceDocument(
                SourceIdentity.Unsaved(),
                "import proto \"nope.proto\";\nextend Outer { fn f() -> int64 { return 1; } }"),
            []);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0002");
    }

    [Fact]
    public void AnUnsavedBufferWithNoImportsIsToldSo()
    {
        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved(), "extend Outer { fn f() -> int64 { return 1; } }"),
            []);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0001");
    }

    /// <summary>
    /// Not merely failing politely: a buffer with no path, no include paths, and nothing on disk
    /// still reaches protoc, because the loader contributes the well-known roots itself.
    /// </summary>
    [Fact]
    public void AnUnsavedBufferResolvesAWellKnownTypeWithNoIncludePathsAtAll()
    {
        var protoc = ProtocLocator.FindBundledProtoc();
        if (protoc is null)
        {
            Assert.Skip("No Grpc.Tools protoc in the NuGet cache. Restore the solution first.");
        }

        var result = Compilation.Compile(
            new SourceDocument(
                SourceIdentity.Unsaved(),
                "import proto \"google/protobuf/timestamp.proto\";\n"
                + "extend google.protobuf.Timestamp { fn f() -> int64 { return seconds; } }"),
            [],
            new DescriptorLoader(protoc));

        Assert.True(result.Success, Describe(result));
    }

    // ---------------------------------------------------------------- unusable include paths

    /// <summary>
    /// The two problems are independent, and the source's is the one the editor is waiting on. The
    /// pipeline settled search paths only after the parse gate for exactly this reason, and moving
    /// that work earlier would have let a mistyped include directory swallow every squiggle in the
    /// buffer.
    /// </summary>
    [Fact]
    public void AMalformedIncludePathDoesNotHideSyntaxDiagnostics()
    {
        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved(), "extend { fn"),
            [string.Empty]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0010" || d.Code == "PL0011");
    }

    /// <summary>
    /// A compiler an editor calls on every keystroke may not throw at its caller. An include path
    /// the file system cannot parse is a diagnostic like any other.
    /// </summary>
    [Fact]
    public void AMalformedIncludePathIsADiagnosticNotACrash()
    {
        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved(), Prelude + Body),
            [string.Empty]);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0082");

        // It has no position in the source, and must not be given a plausible-looking one.
        Assert.Equal(SourceSpan.None, diagnostic.Span);
    }

    /// <summary>One bad entry, one diagnostic -- not one for the entry and one for every import.</summary>
    [Fact]
    public void AMalformedIncludePathDoesNotAlsoBlameTheImport()
    {
        var result = Compilation.Compile(
            new SourceDocument(SourceIdentity.Unsaved(), Prelude + Body),
            [string.Empty]);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0002");
        Assert.Single(result.Diagnostics);
    }

    /// <summary>The path route settles include paths no earlier than it ever did.</summary>
    [Fact]
    public void AMalformedIncludePathDoesNotHideSyntaxDiagnosticsFromDiskEither()
    {
        var path = TestPaths.WriteTempScript("extend { fn");

        var result = Compilation.Compile(path, [string.Empty]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0010" || d.Code == "PL0011");
    }

    /// <summary>
    /// Scaffolding runs after a compilation that has already reported the bad entry, so it skips it
    /// rather than throwing at a caller who is only asking where the schemas were.
    /// </summary>
    [Fact]
    public void SearchPathsSkipAnUnusableIncludePathRatherThanThrowing()
    {
        var path = TestPaths.WriteTempScript(Prelude + Body);

        var searchPaths = Compilation.GetSearchPaths(path, [string.Empty, TestPaths.FixtureProtoDirectory]);

        Assert.Equal(
            [TestPaths.FixtureProtoDirectory, Path.GetDirectoryName(Path.GetFullPath(path))!],
            searchPaths);
    }

    /// <summary>
    /// One directory is one place to search, however the caller spelled it. A second spelling costs a
    /// redundant <c>--proto_path</c>, a "Searched:" line that names the same directory twice, and --
    /// because the roots are part of a descriptor request -- a cache entry that the identical load
    /// spelled the other way will never match.
    /// </summary>
    [Fact]
    public void TwoSpellingsOfOneIncludeDirectoryAreOneSearchPath()
    {
        var path = TestPaths.WriteTempScript(Prelude + Body);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var searchPaths = Compilation.GetSearchPaths(
            path,
            [
                TestPaths.FixtureProtoDirectory + Path.DirectorySeparatorChar,
                TestPaths.FixtureProtoDirectory,
                directory + Path.DirectorySeparatorChar,
            ]);

        Assert.Equal([TestPaths.FixtureProtoDirectory + Path.DirectorySeparatorChar, directory + Path.DirectorySeparatorChar], searchPaths);
    }

    // ---------------------------------------------------------------- scaffolding

    [Fact]
    public void ScaffoldingUsesTheSearchPathsTheCompilationActuallyUsed()
    {
        var path = TestPaths.WriteTempScript(Prelude + Body);
        var directory = Path.GetDirectoryName(path)!;

        var result = Compilation.Compile(path, Fixtures);
        Assert.True(result.Success, Describe(result));

        var behavior = Path.Combine(directory, "generated", "csharp");
        var tests = Path.Combine(directory, "generated", "tests", "csharp");

        var fromResult = ScaffoldOptions.Create(result.SearchPaths, result.Descriptors, behavior, tests, []);
        var fromPath = ScaffoldOptions.Create(path, Fixtures, result.Descriptors, behavior, tests, []);

        Assert.Equal(fromPath.ProtoFiles, fromResult.ProtoFiles);
        Assert.NotEmpty(fromResult.ProtoFiles);
    }

    // ---------------------------------------------------------------- helpers

    private static CompilationResult Compile(SourceIdentity identity, string source)
        => Compilation.Compile(new SourceDocument(identity, source), Fixtures);

    /// <summary>An identity naming a file in a directory of its own that is never written.</summary>
    private static SourceIdentity UnwrittenFile(string name = "buffer.protolang")
        => SourceIdentity.FromPath(Path.Combine(TestPaths.CreateTempDirectory(), name));

    private static void WriteConfig(string directory, string body)
        => File.WriteAllText(
            Path.Combine(directory, ProjectConfig.FileName),
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ProtoLang>\n{body}\n</ProtoLang>\n");

    private static IrBinary ReturnedBinary(CompilationResult result)
        => Assert.IsType<IrBinary>(
            result.Module!.Methods.Single(m => m.Name == "f").Body
                .Statements.OfType<IrReturn>().Single().Value!);

    private static string Render(CompilationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));

    private static string Describe(CompilationResult result) => Render(result);
}
