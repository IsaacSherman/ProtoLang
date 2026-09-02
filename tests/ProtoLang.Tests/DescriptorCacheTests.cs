using ProtoLang.Binding;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Whether a descriptor load can be reused, and -- far more of the work -- whether it can be trusted
/// once it has been.
/// </summary>
/// <remarks>
/// <para>
/// A cache that serves a stale schema is worse than no cache at all: the compiler reports errors
/// about a file the author has already fixed, or accepts a field they have already removed, and
/// nothing in the output says why. So the cases worth naming here are the ones a plausible
/// implementation gets wrong -- a change to a schema nobody imported directly, a file that appears in
/// a root that was previously empty, a reordering that changes which of two identical names wins.
/// </para>
/// <para>
/// Every test that claims something was or was not reloaded says so through
/// <see cref="DescriptorLoader.ProtocInvocations"/>. Asserting on the descriptors instead would pass
/// just as happily against a cache that did nothing at all, because a correct reload and a hit return
/// equal answers -- which is the whole point of one and the whole hazard of the other.
/// </para>
/// </remarks>
public class DescriptorCacheTests
{
    private const string LeafSchema =
        """
        syntax = "proto3";

        package cache.tests;

        // A doc comment, so a test can prove source info survived a cache hit.
        message Leaf {
            int64 count = 1;
        }
        """;

    private const string RootSchema =
        """
        syntax = "proto3";

        package cache.tests;

        import "leaf.proto";

        message Root {
            Leaf leaf = 1;
        }
        """;

    // ------------------------------------------------------------------ hits

    [Fact]
    public void ASecondLoadOfUnchangedInputsDoesNotInvokeProtoc()
    {
        var directory = WriteSchemas();
        var cache = new DescriptorCache();
        var loader = Loader(cache);

        loader.LoadBundle(["root.proto"], [directory]);
        loader.LoadBundle(["root.proto"], [directory]);

        Assert.Equal(1, loader.ProtocInvocations);
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.Equal(1, cache.Statistics.Misses);
    }

    /// <summary>
    /// The counter has to be able to say "yes it ran", or every assertion that it did not run is
    /// satisfied by a counter that is simply never incremented.
    /// </summary>
    [Fact]
    public void WithoutACacheEveryLoadInvokesProtoc()
    {
        var directory = WriteSchemas();
        var loader = Loader();

        loader.LoadBundle(["root.proto"], [directory]);
        loader.LoadBundle(["root.proto"], [directory]);

        Assert.Equal(2, loader.ProtocInvocations);
        Assert.Null(loader.Cache);
    }

    [Fact]
    public void AHitReturnsTheDescriptorsThatWereAlreadyBuilt()
    {
        var directory = WriteSchemas();
        var loader = Loader(new DescriptorCache());

        var first = loader.LoadBundle(["root.proto"], [directory]);
        var second = loader.LoadBundle(["root.proto"], [directory]);

        Assert.Same(first, second);
        Assert.Same(first.Descriptors[0], second.Descriptors[0]);
    }

    /// <summary>
    /// The reason this issue could not cache descriptors alone. Source info is what #41 resolves a
    /// schema declaration and its doc comment from, and a hit that could not answer it would send
    /// every such question back to protoc -- which is the run being avoided.
    /// </summary>
    [Fact]
    public void SourceInfoIsReachableFromACacheHit()
    {
        var directory = WriteSchemas();
        var loader = Loader(new DescriptorCache());

        loader.LoadBundle(["root.proto"], [directory]);
        var hit = loader.LoadBundle(["root.proto"], [directory]);

        var leaf = hit.ProtoFor("leaf.proto");

        Assert.Equal(1, loader.ProtocInvocations);
        Assert.NotNull(leaf);
        Assert.NotEmpty(leaf.SourceCodeInfo.Location);
        Assert.Contains(
            leaf.SourceCodeInfo.Location,
            location => location.LeadingComments.Contains("prove source info survived"));
    }

    [Fact]
    public void ALoadRecordsWhichFileEachSchemaNameCameFrom()
    {
        var directory = WriteSchemas();

        var bundle = Loader().LoadBundle(["root.proto"], [directory]);

        Assert.Equal(Path.Combine(directory, "leaf.proto"), bundle.PathFor("leaf.proto"));
        Assert.Equal(Path.Combine(directory, "root.proto"), bundle.PathFor("root.proto"));
    }

    // ------------------------------------------------------------ invalidation

    /// <summary>
    /// Nothing in the request names <c>leaf.proto</c>: it is reached only through an import inside
    /// <c>root.proto</c>. A cache keyed on the files it was asked for would serve the old descriptors
    /// forever.
    /// </summary>
    [Fact]
    public void AChangeToATransitivelyImportedSchemaInvalidates()
    {
        var directory = WriteSchemas();
        var cache = new DescriptorCache();
        var loader = Loader(cache);

        loader.LoadBundle(["root.proto"], [directory]);

        File.WriteAllText(
            Path.Combine(directory, "leaf.proto"),
            LeafSchema.Replace("int64 count = 1;", "int64 count = 1;\n    string label = 2;"));

        var second = loader.LoadBundle(["root.proto"], [directory]);

        Assert.Equal(2, loader.ProtocInvocations);
        Assert.Equal(1, cache.Statistics.Invalidations);
        Assert.Equal(2, second.ProtoFor("leaf.proto")!.MessageType[0].Field.Count);
    }

    /// <summary>
    /// Resolution is first-match, so the same directories in a different order can resolve one name
    /// to a different file. The set is identical; only the order is not.
    /// </summary>
    [Fact]
    public void ReorderingIncludePathsInvalidates()
    {
        var (first, second) = WriteSplitSchemas();
        var loader = Loader(new DescriptorCache());

        loader.LoadBundle(["root.proto"], [first, second]);
        loader.LoadBundle(["root.proto"], [second, first]);

        Assert.Equal(2, loader.ProtocInvocations);
    }

    /// <summary>
    /// The case a "have any of my files changed?" check cannot see: nothing the old closure knew
    /// about was touched. A file appeared in a root that had nothing to say before, and it now
    /// shadows one that used to win.
    /// </summary>
    [Fact]
    public void AFileAppearingInAHigherPriorityRootInvalidates()
    {
        var main = WriteSchemas();
        var shadow = TestPaths.CreateTempDirectory();
        var cache = new DescriptorCache();
        var loader = Loader(cache);

        var before = loader.LoadBundle(["root.proto"], [shadow, main]);
        Assert.Equal(Path.Combine(main, "leaf.proto"), before.PathFor("leaf.proto"));

        File.WriteAllText(Path.Combine(shadow, "leaf.proto"), LeafSchema);

        var after = loader.LoadBundle(["root.proto"], [shadow, main]);

        Assert.Equal(2, loader.ProtocInvocations);
        Assert.Equal(1, cache.Statistics.Invalidations);
        Assert.Equal(Path.Combine(shadow, "leaf.proto"), after.PathFor("leaf.proto"));
    }

    [Fact]
    public void DeletingASchemaInvalidates()
    {
        var directory = WriteSchemas();
        var loader = Loader(new DescriptorCache());

        loader.LoadBundle(["root.proto"], [directory]);
        File.Delete(Path.Combine(directory, "leaf.proto"));

        // protoc is reached, and refuses: the import it names is gone. Being told so is the point --
        // the failure a cache must never hide is the one where the schema is no longer there.
        Assert.Throws<DescriptorLoadException>(() => loader.LoadBundle(["root.proto"], [directory]));
        Assert.Equal(2, loader.ProtocInvocations);
    }

    /// <summary>
    /// A second install brings its own well-known schemas with it, so an entry populated under one
    /// protoc says nothing about what the other would produce.
    /// </summary>
    [Fact]
    public void TwoProtocInstallsDoNotShareACacheEntry()
    {
        var directory = WriteSchemas();
        var cache = new DescriptorCache();

        var installed = Loader(cache);
        var copied = new DescriptorLoader(CopyProtoc(), new DescriptorLoaderOptions { Cache = cache });

        installed.LoadBundle(["root.proto"], [directory]);
        copied.LoadBundle(["root.proto"], [directory]);

        Assert.Equal(1, installed.ProtocInvocations);
        Assert.Equal(1, copied.ProtocInvocations);
        Assert.Equal(2, cache.Count);
    }

    // ------------------------------------------------------------- the request

    /// <summary>
    /// Two protocs at one path -- an install upgraded underneath a running editor. The path is the
    /// same, so only the executable's own identity can tell them apart.
    /// </summary>
    [Fact]
    public void AProtocUpgradedInPlaceIsADifferentRequest()
    {
        var before = Request(protocLength: 4_000_000);
        var after = Request(protocLength: 4_200_000);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ADifferentWellKnownIncludeRootIsADifferentRequest()
    {
        var before = Request(implicitIncludePaths: ["/opt/protobuf-31/include"]);
        var after = Request(implicitIncludePaths: ["/opt/protobuf-33/include"]);

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Folding case here would answer a request for one schema with another schema's descriptors on
    /// any file system that tells them apart, and the closure check could not catch it: the entry
    /// names the file it really loaded, and that file really is unchanged. Two entries on Windows for
    /// two spellings of one path is the price, and a duplicate entry is only a wasted protoc run.
    /// </summary>
    [Fact]
    public void TwoSpellingsOfOneSchemaNameAreDifferentRequests()
    {
        Assert.NotEqual(Request(protoFiles: ["leaf.proto"]), Request(protoFiles: ["Leaf.proto"]));
        Assert.NotEqual(Request(includePaths: ["/schemas"]), Request(includePaths: ["/Schemas"]));
    }

    [Fact]
    public void TheSameLoadDescribedTwiceIsOneRequest()
    {
        Assert.Equal(Request(), Request());
        Assert.Equal(Request().GetHashCode(), Request().GetHashCode());
    }

    /// <summary>
    /// The components are rendered into one string to be compared, and a rendering that let one
    /// component's last entry run into the next component's first would make two different loads
    /// indistinguishable.
    /// </summary>
    [Fact]
    public void OneComponentCannotBleedIntoTheNext()
    {
        var caller = Request(includePaths: ["/schemas"], implicitIncludePaths: []);
        var implicitly = Request(includePaths: [], implicitIncludePaths: ["/schemas"]);

        Assert.NotEqual(caller, implicitly);
    }

    // ---------------------------------------------------------------- mechanics

    /// <summary>
    /// Two keystrokes arriving while the first load is still running is the ordinary case in an
    /// editor, and running protoc once per arrival is exactly the waste this cache exists to stop.
    /// </summary>
    [Fact]
    public async Task ThreadsRacingToPopulateOneEntryInvokeProtocOnce()
    {
        const int Racers = 4;

        var directory = WriteSchemas();
        var loader = Loader(new DescriptorCache());

        // A barrier and dedicated threads rather than the thread pool: the property under test is
        // what happens when the loads genuinely overlap, and a pool that ran them one after another
        // would satisfy the assertion without ever having raced.
        var gate = new Barrier(Racers);

        var bundles = await Task.WhenAll(
            Enumerable.Range(0, Racers)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        gate.SignalAndWait();
                        return loader.LoadBundle(["root.proto"], [directory]);
                    },
                    TaskCreationOptions.LongRunning)));

        Assert.Equal(1, loader.ProtocInvocations);
        Assert.All(bundles, bundle => Assert.Same(bundles[0], bundle));
    }

    /// <summary>
    /// A session that opens files all day must not grow an entry per file it ever touched, and a
    /// descriptor set carrying source info is not small.
    /// </summary>
    [Fact]
    public void TheCacheDoesNotGrowPastItsCapacity()
    {
        var cache = new DescriptorCache(capacity: 2);
        var loader = Loader(cache);

        foreach (var directory in new[] { WriteSchemas(), WriteSchemas(), WriteSchemas() })
        {
            loader.LoadBundle(["root.proto"], [directory]);
        }

        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.Statistics.Evictions);
    }

    /// <summary>
    /// The most confusing thing a cache can do to someone who has just corrected their schema is to
    /// hand them the error from before the correction, without protoc ever looking at the new file.
    /// </summary>
    [Fact]
    public void AFailedLoadIsNotCachedAsASuccess()
    {
        var directory = WriteSchemas();
        var broken = Path.Combine(directory, "leaf.proto");
        var loader = Loader(new DescriptorCache());

        File.WriteAllText(broken, "syntax = \"proto3\"; this is not a schema");
        Assert.Throws<DescriptorLoadException>(() => loader.LoadBundle(["root.proto"], [directory]));

        File.WriteAllText(broken, LeafSchema);
        var repaired = loader.LoadBundle(["root.proto"], [directory]);

        Assert.Equal(2, loader.ProtocInvocations);
        Assert.NotEmpty(repaired.Descriptors);
    }

    /// <summary>
    /// A compiler an editor calls on every keystroke may not have a state in which it waits forever.
    /// A budget of nothing reaches the same kill path a real overrun does, without a fixture process
    /// to babysit.
    /// </summary>
    [Fact]
    public void AProtocThatOutlivesItsBudgetIsStoppedAndReported()
    {
        var directory = WriteSchemas();
        var loader = new DescriptorLoader(
            RequireProtoc(),
            new DescriptorLoaderOptions { Timeout = TimeSpan.Zero });

        var failure = Assert.Throws<DescriptorLoadException>(
            () => loader.LoadBundle(["root.proto"], [directory]));

        Assert.Contains("did not finish within", failure.Message);
    }

    // -------------------------------------------------------------- protoc output

    /// <summary>
    /// The structure #41 and #42 publish a protoc error against the <c>.proto</c> with. Flattened
    /// into prose it can only be recovered by parsing the sentence back apart.
    /// </summary>
    [Fact]
    public void ProtocFailureOutputKeepsItsFileAndLine()
    {
        var directory = WriteSchemas();
        File.WriteAllText(Path.Combine(directory, "leaf.proto"), "syntax = \"proto3\";\nmessage {");

        var failure = Assert.Throws<DescriptorLoadException>(
            () => Loader().LoadBundle(["root.proto"], [directory]));

        var located = Assert.Single(
            failure.Output,
            entry => entry.File is not null && entry.File.Contains("leaf.proto") && entry.HasPosition);

        Assert.True(located.Line > 0, "protoc reported a line and it must survive as a number");
        Assert.NotEmpty(located.Text);
    }

    /// <summary>
    /// Splitting on the first colon makes the drive letter the file name, and splitting on the last
    /// makes the column the message. Only a colon followed by two numbers is the separator.
    /// </summary>
    [Fact]
    public void AWindowsPathInProtocOutputKeepsItsDriveLetter()
    {
        var parsed = Assert.Single(ProtocDiagnostic.Parse(@"C:\schemas\invoice.proto:12:5: Expected "";""."));

        Assert.Equal(@"C:\schemas\invoice.proto", parsed.File);
        Assert.Equal(12, parsed.Line);
        Assert.Equal(5, parsed.Column);
        Assert.Equal(@"Expected "";"".", parsed.Text);
    }

    [Fact]
    public void AnUnpositionedLineStillKeepsTheFileItBlames()
    {
        var parsed = Assert.Single(
            ProtocDiagnostic.Parse("invoice.proto: Import \"missing.proto\" was not found."));

        Assert.Equal("invoice.proto", parsed.File);
        Assert.False(parsed.HasPosition);
        Assert.Equal("Import \"missing.proto\" was not found.", parsed.Text);
    }

    /// <summary>
    /// protoc's output is not a specified format. A line this parser does not recognize has to reach
    /// a reader intact rather than be dropped for failing to match, and must not be attributed to a
    /// schema it has nothing to do with.
    /// </summary>
    [Fact]
    public void ALineThatNamesNoSchemaIsKeptWhole()
    {
        const string Line = "[libprotobuf WARNING] Warning: unused import: 3:1";

        var parsed = Assert.Single(ProtocDiagnostic.Parse(Line));

        Assert.Null(parsed.File);
        Assert.Equal(Line, parsed.Text);
        Assert.Equal(Line, parsed.Raw);
    }

    /// <summary>
    /// The text comes from another process, so nothing bounds how many digits it can put where a line
    /// number goes. A compiler an editor calls on every keystroke may not throw at its caller over it.
    /// </summary>
    [Fact]
    public void ALineNumberTooLargeToBeOneLeavesTheLineWhole()
    {
        const string Line = "x.proto:99999999999:1: a line number no file has";

        var parsed = Assert.Single(ProtocDiagnostic.Parse(Line));

        Assert.Null(parsed.File);
        Assert.False(parsed.HasPosition);
        Assert.Equal(Line, parsed.Text);
    }

    /// <summary>
    /// PL0003 prints this message, and PL0003 is published output. Structuring the same text
    /// underneath it must not have moved a character of it.
    /// </summary>
    [Fact]
    public void TheMessageForAFailedLoadIsStillProtocsOwnOutput()
    {
        var directory = WriteSchemas();
        File.WriteAllText(Path.Combine(directory, "leaf.proto"), "syntax = \"proto3\";\nmessage {");

        var failure = Assert.Throws<DescriptorLoadException>(
            () => Loader().LoadBundle(["root.proto"], [directory]));

        Assert.Equal(
            $"protoc failed with exit code 1:{Environment.NewLine}{failure.RawOutput.Trim()}",
            failure.Message);
    }

    // -------------------------------------------------------------- compilations

    [Fact]
    public void ACompilationPublishesTheSchemaItBoundAgainst()
    {
        var result = Compilation.Compile(WriteScript(), [], Loader());

        Assert.True(result.Success, Describe(result));
        Assert.NotNull(result.Schema);
        Assert.Same(result.Descriptors, result.Schema.Descriptors);
        Assert.NotNull(result.Schema.PathFor("leaf.proto"));
    }

    /// <summary>
    /// The cache lives on the loader, so a caller holding a compilation has to be able to reach the
    /// loader that compilation used -- including the one it built for itself, which
    /// <see cref="CompilationOptions.Loader"/> never held.
    /// </summary>
    [Fact]
    public void ACompilationKeepsTheLoaderItResolvedForItself()
    {
        var script = WriteScript();
        var compilation = new Compilation(SourceDocument.ReadFrom(script), new CompilationOptions());

        compilation.Compile();
        var resolved = compilation.Loader;
        compilation.Compile();

        Assert.NotNull(resolved);
        Assert.Same(resolved, compilation.Loader);
    }

    [Fact]
    public void ASecondCompilationOverAnUnchangedSchemaDoesNotInvokeProtoc()
    {
        var script = WriteScript();
        var loader = Loader(new DescriptorCache());

        Compilation.Compile(script, [], loader);
        var second = Compilation.Compile(script, [], loader);

        Assert.True(second.Success, Describe(second));
        Assert.Equal(1, loader.ProtocInvocations);
    }

    // ------------------------------------------------------------------ fixtures

    private static DescriptorLoader Loader(DescriptorCache? cache = null)
        => new(RequireProtoc(), new DescriptorLoaderOptions { Cache = cache });

    private static string RequireProtoc()
    {
        var protoc = ProtocLocator.Locate();
        if (protoc is null)
        {
            Assert.Skip("No protoc on PATH and none in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }

    /// <summary>A second protoc install, at a path of its own, that really runs.</summary>
    private static string CopyProtoc()
    {
        var source = RequireProtoc();
        var destination = Path.Combine(TestPaths.CreateTempDirectory(), Path.GetFileName(source));

        File.Copy(source, destination);

        return destination;
    }

    /// <summary>A directory holding a schema and the schema it imports.</summary>
    private static string WriteSchemas()
    {
        var directory = TestPaths.CreateTempDirectory();

        File.WriteAllText(Path.Combine(directory, "leaf.proto"), LeafSchema);
        File.WriteAllText(Path.Combine(directory, "root.proto"), RootSchema);

        return directory;
    }

    /// <summary>The same two schemas, one per directory, so include order can be varied.</summary>
    private static (string First, string Second) WriteSplitSchemas()
    {
        var first = TestPaths.CreateTempDirectory();
        var second = TestPaths.CreateTempDirectory();

        File.WriteAllText(Path.Combine(first, "root.proto"), RootSchema);
        File.WriteAllText(Path.Combine(second, "leaf.proto"), LeafSchema);

        return (first, second);
    }

    /// <summary>A ProtoLang source extending the schema <see cref="WriteSchemas"/> writes.</summary>
    private static string WriteScript()
    {
        var directory = WriteSchemas();
        var script = Path.Combine(directory, "test.protolang");

        File.WriteAllText(
            script,
            """
            import proto "root.proto";

            extend Root {
                fn count() -> int64 {
                    if not has leaf {
                        return 0;
                    }

                    return leaf.count;
                }
            }
            """);

        return script;
    }

    private static DescriptorRequest Request(
        string protocPath = "/usr/bin/protoc",
        long protocLength = 4_000_000,
        IReadOnlyList<string>? includePaths = null,
        IReadOnlyList<string>? implicitIncludePaths = null,
        IReadOnlyList<string>? protoFiles = null)
        => new(
            protocPath,
            protocLength,
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            includePaths ?? ["/schemas"],
            implicitIncludePaths ?? ["/opt/protobuf/include"],
            protoFiles ?? ["root.proto"]);

    private static string Describe(CompilationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
}
