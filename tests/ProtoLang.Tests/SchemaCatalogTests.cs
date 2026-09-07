using ProtoLang.Binding;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// What is importable from a set of include roots: which roots those are, what they hold one
/// directory at a time, and what a path that resolved to nothing came closest to naming.
/// </summary>
/// <remarks>
/// The property under nearly every test here is the same one, asked from different angles: a
/// candidate this offers is a path <see cref="SchemaLookup"/> would find, in the root it would find
/// it in. An editor offering something the compiler then rejects is worse than one offering nothing,
/// because it invites the user to type it and then blames them for it.
/// </remarks>
public class SchemaCatalogTests
{
    /// <summary>
    /// Two roots that overlap, so first-match has something to decide and shadowing has something to
    /// report.
    /// </summary>
    /// <remarks>
    /// <c>billing/invoice.proto</c> exists in both and is the case the whole shadowing requirement is
    /// about. <c>notes.txt</c> and <c>reading.protolang</c> are there so "non-.proto files are never
    /// offered" is tested against files that plausibly sit beside schemas rather than against nothing.
    /// </remarks>
    private static (string First, string Second) Roots()
    {
        var first = TestPaths.CreateTempDirectory();
        var second = TestPaths.CreateTempDirectory();

        Write(first, "shared.proto");
        Write(first, "notes.txt");
        Write(first, "reading.protolang");
        Write(first, "billing/invoice.proto");
        Write(first, "billing/invoice_item.proto");
        Write(first, "billing/tax/rates.proto");

        Write(second, "extra.proto");
        Write(second, "billing/invoice.proto");
        Write(second, "billing/credit.proto");

        return (first, second);
    }

    private static void Write(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "syntax = \"proto3\";\n");
    }

    private static IReadOnlyList<SchemaCandidate> Enumerate(string directory, params string[] roots)
        => SchemaCatalog.Enumerate(directory, roots).Candidates;

    // ------------------------------------------------------- agreeing with the compiler

    [Fact]
    public void EveryCandidateOfferedIsOneTheCompilerWouldResolve()
    {
        var (first, second) = Roots();
        string[] roots = [first, second];

        foreach (var directory in new[] { string.Empty, "billing/", "billing/tax/" })
        {
            foreach (var candidate in SchemaCatalog.Enumerate(directory, roots).Candidates)
            {
                if (candidate.IsDirectory)
                {
                    continue;
                }

                Assert.NotNull(SchemaLookup.Find(candidate.Path, roots));
            }
        }
    }

    /// <summary>
    /// The root a candidate names is the root the compiler would take it from, which is the whole
    /// content of first-match resolution and the only thing shadowing information is good for.
    /// </summary>
    [Fact]
    public void TheRootACandidateNamesIsTheRootThatWouldWin()
    {
        var (first, second) = Roots();
        string[] roots = [first, second];

        foreach (var candidate in SchemaCatalog.Enumerate("billing/", roots).Candidates)
        {
            if (candidate.IsDirectory)
            {
                continue;
            }

            var resolved = SchemaLookup.Find(candidate.Path, roots);

            Assert.True(
                resolved!.StartsWith(candidate.Root, StringComparison.Ordinal),
                $"'{candidate.Path}' says it comes from '{candidate.Root}' and resolves to '{resolved}'");
        }
    }

    [Fact]
    public void AShadowedPathNamesEveryRootThatHoldsIt()
    {
        var (first, second) = Roots();

        var shadowed = Single(Enumerate("billing/", first, second), "billing/invoice.proto");

        Assert.Equal(first, shadowed.Root);
        Assert.Equal([second], shadowed.ShadowedRoots);
    }

    [Fact]
    public void APathHeldByOneRootAloneShadowsNothing()
    {
        var (first, second) = Roots();

        Assert.Empty(Single(Enumerate("billing/", first, second), "billing/credit.proto").ShadowedRoots);
    }

    /// <summary>The order of the roots is the order of the argument, not of the file system.</summary>
    [Fact]
    public void ReversingTheRootsReversesWhichCopyWins()
    {
        var (first, second) = Roots();

        Assert.Equal(second, Single(Enumerate("billing/", second, first), "billing/invoice.proto").Root);
    }

    /// <summary>
    /// The roots really are the compilation's, compared against the list one that ran actually
    /// searched -- the only witness that is not this implementation restating itself.
    /// </summary>
    [Fact]
    public void TheRootsAreTheOnesAResolvedImportWasSearchedAgainst()
    {
        var compilation = new Compilation(
            SourceDocument.ReadFrom(TestPaths.SimpleScript),
            new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory] });

        var result = compilation.Compile();
        var import = Assert.Single(result.Imports);

        Assert.Equal(
            import.SearchedPaths,
            SchemaCatalog.RootsFor(compilation.SearchPaths, compilation.Loader));
    }

    [Fact]
    public void RootsWithNoLoaderAreJustTheSearchPaths()
    {
        Assert.Equal(["a", "b"], SchemaCatalog.RootsFor(["a", "b"], loader: null));
    }

    // ------------------------------------------------------- what is offered

    [Fact]
    public void AnEmptyPrefixOffersTheTopLevelOfEveryRoot()
    {
        var (first, second) = Roots();

        Assert.Equal(
            ["billing/", "extra.proto", "shared.proto"],
            Enumerate(string.Empty, first, second).Select(candidate => candidate.Path));
    }

    [Fact]
    public void ADirectoryPrefixOffersThatDirectoryAndNothingElse()
    {
        var (first, second) = Roots();

        Assert.Equal(
            ["billing/tax/", "billing/credit.proto", "billing/invoice.proto", "billing/invoice_item.proto"],
            Enumerate("billing/", first, second).Select(candidate => candidate.Path));
    }

    [Fact]
    public void ADirectoryIsFoundWithOrWithoutTheSeparatorTheAuthorHasTypedYet()
    {
        var (first, second) = Roots();

        Assert.Equal(
            Sources(Enumerate("billing/", first, second)),
            Sources(Enumerate("billing", first, second)));
    }

    [Fact]
    public void NonProtoFilesAreNeverOffered()
    {
        var (first, second) = Roots();

        var offered = Enumerate(string.Empty, first, second).Select(candidate => candidate.Path);

        Assert.DoesNotContain("notes.txt", offered);
        Assert.DoesNotContain("reading.protolang", offered);
    }

    /// <summary>
    /// A directory is offered whatever it holds, because there is no way to know without walking into
    /// it and walking into it is the request the user is about to make anyway.
    /// </summary>
    [Fact]
    public void ADirectoryHoldingNoSchemasIsStillAStepTheUserCanTake()
    {
        var root = TestPaths.CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        Assert.Equal(["empty/"], Enumerate(string.Empty, root).Select(candidate => candidate.Path));
    }

    [Fact]
    public void EveryCandidatePathIsSpelledTheWayProtobufSpellsIt()
    {
        var (first, second) = Roots();

        foreach (var directory in new[] { string.Empty, "billing/", "billing/tax/" })
        {
            foreach (var candidate in SchemaCatalog.Enumerate(directory, [first, second]).Candidates)
            {
                Assert.DoesNotContain('\\', candidate.Path);
                Assert.False(
                    Path.IsPathRooted(candidate.Path),
                    $"'{candidate.Path}' must be relative to a root, since that is what protoc is given");
            }
        }
    }

    /// <summary>
    /// A user on Windows who typed the separator their operating system uses is answered rather than
    /// refused, and answered with paths that will compile on somebody else's machine.
    /// </summary>
    [Fact]
    public void ABackslashIsReadAsASeparatorAndAnsweredWithForwardOnes()
    {
        var (first, second) = Roots();

        Assert.Equal(
            Sources(Enumerate("billing/", first, second)),
            Sources(Enumerate("billing\\", first, second)));
    }

    [Fact]
    public void DirectoriesAreOfferedBeforeSchemas()
    {
        var (first, second) = Roots();

        var candidates = Enumerate("billing/", first, second);

        Assert.Equal(
            candidates.OrderByDescending(candidate => candidate.IsDirectory).Select(candidate => candidate.Path),
            candidates.Select(candidate => candidate.Path));
    }

    // ------------------------------------------------------- roots that answer nothing

    [Fact]
    public void ARootThatIsNotThereContributesNothingRatherThanFailing()
    {
        var (first, _) = Roots();

        Assert.Equal(
            Sources(Enumerate(string.Empty, first)),
            Sources(Enumerate(string.Empty, first, Path.Combine(TestPaths.CreateTempDirectory(), "gone"))));
    }

    [Fact]
    public void NoRootsAtAllIsAnEmptyListRatherThanAFailure()
    {
        Assert.Empty(SchemaCatalog.Enumerate(string.Empty, []).Candidates);
    }

    /// <summary>
    /// A prefix that climbs out of the roots names a place protoc would not resolve either, and
    /// enumerating it would show the user the contents of a directory outside their workspace.
    /// </summary>
    [Theory]
    [InlineData("../")]
    [InlineData("billing/../../")]
    [InlineData("/etc/")]
    public void APrefixThatCouldNotNameAPlaceUnderARootOffersNothing(string prefix)
    {
        var (first, second) = Roots();

        Assert.Empty(Enumerate(prefix, first, second));
    }

    // ------------------------------------------------------- the well-known schemas

    /// <summary>
    /// The case the issue says a user is least likely to know to type, and the one that proves the
    /// loader's own roots are in the list rather than only the caller's.
    /// </summary>
    [Fact]
    public void TheWellKnownSchemasAreOfferedFromTheLoadersOwnRoots()
    {
        var loader = DescriptorLoader.CreateDefault();

        if (loader.ImplicitIncludePaths.Count == 0)
        {
            Assert.Skip($"'{loader.ProtocPath}' ships no well-known schemas as files.");
        }

        var roots = SchemaCatalog.RootsFor([], loader);

        Assert.Contains("google/", SchemaCatalog.Enumerate(string.Empty, roots).Candidates.Select(c => c.Path));
        Assert.Contains(
            "google/protobuf/timestamp.proto",
            SchemaCatalog.Enumerate("google/protobuf/", roots).Candidates.Select(c => c.Path));
    }

    // ------------------------------------------------------- the near match

    [Fact]
    public void ANearMissNamesTheSchemaItAlmostNamed()
    {
        var (first, second) = Roots();

        Assert.Equal("billing/credit.proto", SchemaCatalog.NearestTo("billing/credits.proto", [first, second]));
    }

    [Fact]
    public void ANearMissAtTheTopLevelIsFoundToo()
    {
        var (first, _) = Roots();

        Assert.Equal("shared.proto", SchemaCatalog.NearestTo("shard.proto", [first]));
    }

    /// <summary>
    /// The one case a case-sensitive file system produces and a case-folding one cannot: the import
    /// did not resolve, and the only thing wrong with it is invisible in every other diagnostic.
    /// </summary>
    [Fact]
    public void ASchemaDifferingOnlyInCaseIsNamed()
    {
        var (first, _) = Roots();

        Assert.Equal("shared.proto", SchemaCatalog.NearestTo("Shared.proto", [first]));
    }

    [Fact]
    public void APathWithNothingLikeItNamesNothing()
    {
        var (first, second) = Roots();

        Assert.Null(SchemaCatalog.NearestTo("completely_unrelated.proto", [first, second]));
    }

    [Fact]
    public void ASuggestionIsNeverThePathThatWasWritten()
    {
        var (first, _) = Roots();

        Assert.Null(SchemaCatalog.NearestTo("shared.proto", [first]));
    }

    [Fact]
    public void ADirectoryIsNeverSuggestedBecauseADirectoryCannotBeImported()
    {
        var (first, _) = Roots();

        Assert.Null(SchemaCatalog.NearestTo("billin", [first]));
    }

    /// <summary>
    /// The stated limitation, pinned so that widening it later is a deliberate change rather than a
    /// surprise: the search is beside the file the author named, not through the whole tree.
    /// </summary>
    [Fact]
    public void ASchemaInADirectoryTheAuthorDidNotNameIsNotSuggested()
    {
        var (first, _) = Roots();

        Assert.Null(SchemaCatalog.NearestTo("invoice.proto", [first]));
    }

    [Fact]
    public void TheNearestOfSeveralIsTheOneNamed()
    {
        var (first, second) = Roots();

        Assert.Equal(
            "billing/invoice.proto",
            SchemaCatalog.NearestTo("billing/invoce.proto", [first, second]));
    }

    [Fact]
    public void AnEmptyPathNamesNothing()
    {
        var (first, _) = Roots();

        Assert.Null(SchemaCatalog.NearestTo(string.Empty, [first]));
    }

    // ------------------------------------------------------- what it is allowed to cost

    /// <summary>
    /// The walk stops on its budget and says it stopped, rather than reading a directory somebody
    /// pointed at a vendored tree or a network mount to the end.
    /// </summary>
    [Fact]
    public void AWalkThatRanOutOfBudgetSaysItDidNotSeeEverything()
    {
        var root = Crowded(entries: 10);

        var cut = SchemaCatalog.Enumerate(string.Empty, [root], budget: 4);

        Assert.False(cut.SawEverything);
        Assert.True(cut.Candidates.Count <= 4, "no more candidates than entries examined");

        Assert.True(SchemaCatalog.Enumerate(string.Empty, [root], budget: 10).SawEverything);
    }

    /// <summary>
    /// The budget counts entries walked rather than candidates kept, because the cost is in reading
    /// the directory and not in what reading it turned up. A root holding ten thousand images and one
    /// schema is exactly the case the budget is for, and one that counted candidates would walk all
    /// of it and report a budget of one as never reached.
    /// </summary>
    [Fact]
    public void TheBudgetCountsWhatWasWalkedRatherThanWhatWasKept()
    {
        var root = TestPaths.CreateTempDirectory();
        for (var index = 0; index < 10; index++)
        {
            Write(root, $"image{index}.png");
        }

        Write(root, "invoice.proto");

        Assert.False(SchemaCatalog.Enumerate(string.Empty, [root], budget: 5).SawEverything);
    }

    /// <summary>
    /// A partial walk cannot know that what it did not reach was further away than what it did, so it
    /// names nothing rather than presenting the nearest of an arbitrary prefix as the nearest of the
    /// directory. This is the compiler's error path: it runs on every failed import, from the command
    /// line as well as the editor.
    /// </summary>
    /// <remarks>
    /// The near match is in the first root and the breadth is in the second, so the walk reaches it
    /// and then runs out. A test that put both in one directory would rest on the order the file
    /// system happened to list in, and would pass whether the walk was cut short before the match or
    /// after it -- which is to say it would pass with the rule removed.
    /// </remarks>
    [Fact]
    public void ADirectoryTooBroadToReadWithinTheBudgetSuggestsNothing()
    {
        var near = TestPaths.CreateTempDirectory();
        Write(near, "invoice.proto");

        var broad = Crowded(entries: 10);

        Assert.Equal("invoice.proto", SchemaCatalog.NearestTo("invoce.proto", [near, broad], budget: 64));
        Assert.Null(SchemaCatalog.NearestTo("invoce.proto", [near, broad], budget: 4));
    }

    /// <summary>
    /// The budget a caller names none of is the published one, so the guard is in force on the path
    /// the compiler actually takes rather than only where a test passes a number.
    /// </summary>
    /// <remarks>
    /// Every other test here supplies a budget, so without this one the published figure could be
    /// raised to infinity and nothing would notice. Observing a default means exceeding it, which
    /// means one more file than it allows; a few thousand empty ones cost well under a second, and
    /// asserting the parameter through reflection instead would pin the declaration rather than the
    /// behaviour.
    /// </remarks>
    [Fact]
    public void TheBudgetACallerDoesNotNameIsThePublishedOne()
    {
        var root = Crowded(SchemaCatalog.MostEntriesExamined + 1);

        Assert.False(SchemaCatalog.Enumerate(string.Empty, [root]).SawEverything);
        Assert.Null(SchemaCatalog.NearestTo("filler1.protoo", [root]));
    }

    /// <summary>A root holding <paramref name="entries"/> schemas and nothing else.</summary>
    private static string Crowded(int entries)
    {
        var root = TestPaths.CreateTempDirectory();

        for (var index = 0; index < entries; index++)
        {
            // Empty, and no directory check per file: this is called with a few thousand, and what is
            // under test is how many entries the walk looks at rather than what is in them.
            File.Create(Path.Combine(root, $"filler{index}.proto")).Dispose();
        }

        return root;
    }

    private static SchemaCandidate Single(IReadOnlyList<SchemaCandidate> candidates, string path)
        => Assert.Single(candidates, candidate => candidate.Path == path);

    /// <summary>
    /// What each candidate is and where it came from, which is what two enumerations have to agree
    /// about. Comparing the records themselves would compare their shadow lists by reference, so two
    /// identical answers built from separate walks would never be equal.
    /// </summary>
    private static IEnumerable<(string Path, string Root)> Sources(IReadOnlyList<SchemaCandidate> candidates)
        => candidates.Select(candidate => (candidate.Path, candidate.Root));
}
