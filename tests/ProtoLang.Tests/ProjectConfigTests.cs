using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The project configuration file and how it reaches the IR (spec 10.4).
/// </summary>
/// <remarks>
/// The behavior each policy produces is pinned by the conformance corpus, which compiles and runs
/// it in both backends. What is checked here is the layer above that: that the file is found, that
/// it is read exactly, and that a malformed one stops the compilation instead of quietly being
/// replaced by the defaults.
/// </remarks>
public class ProjectConfigTests
{
    private static string WriteConfig(string xml)
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, ProjectConfig.FileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private static (ProjectConfig? Config, DiagnosticBag Diagnostics) Load(string xml)
    {
        var diagnostics = new DiagnosticBag();
        return (ProjectConfig.Load(WriteConfig(xml), diagnostics), diagnostics);
    }

    private const string Wrapper =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ProtoLang>\n{0}\n</ProtoLang>\n";

    private static (ProjectConfig? Config, DiagnosticBag Diagnostics) LoadBody(string body)
        => Load(string.Format(Wrapper, body));

    // ---------------------------------------------------------------- reading

    [Fact]
    public void AnEmptyConfigIsTheDefaultConfig()
    {
        var (config, diagnostics) = LoadBody("");

        Assert.Empty(diagnostics);
        Assert.NotNull(config);
        Assert.Equal(OverflowPolicy.Wrapping, config!.Overflow);
        Assert.Equal(ConversionPolicy.WrapOrSaturate, config.Conversion);
        Assert.Equal(DivideByZeroPolicy.RequireOnZero, config.DivideByZero);
        Assert.Equal(UnsetMessageReadPolicy.RequireGuard, config.UnsetMessageRead);
    }

    [Theory]
    [InlineData("Wrapping", OverflowPolicy.Wrapping)]
    [InlineData("Checked", OverflowPolicy.Checked)]
    [InlineData("Saturating", OverflowPolicy.Saturating)]
    public void EveryOverflowModeIsAccepted(string text, OverflowPolicy expected)
    {
        var (config, diagnostics) = LoadBody($"<Arithmetic><Overflow>{text}</Overflow></Arithmetic>");

        Assert.Empty(diagnostics);
        Assert.Equal(expected, config!.Overflow);
    }

    /// <summary>
    /// A setting the file states is distinguishable from a default left in place, because a
    /// command-line override has to refuse one and not the other.
    /// </summary>
    [Fact]
    public void OnlyStatedSettingsAreRecordedAsExplicit()
    {
        var (config, _) = LoadBody("<Arithmetic><Overflow>Checked</Overflow></Arithmetic>");

        Assert.Contains("Arithmetic/Overflow", config!.ExplicitKeys);
        Assert.DoesNotContain("Arithmetic/Conversion", config.ExplicitKeys);
    }

    /// <summary>
    /// Surrounding whitespace is the author's formatting, not part of the value. Case is not:
    /// accepting "checked" as well as "Checked" would let a project be written one way and read
    /// another, which is the failure this file exists to prevent.
    /// </summary>
    [Fact]
    public void ValuesAreTrimmedButCaseSensitive()
    {
        var (spaced, spacedDiagnostics) = LoadBody(
            "<Arithmetic>\n  <Overflow>  Checked  </Overflow>\n</Arithmetic>");
        Assert.Empty(spacedDiagnostics);
        Assert.Equal(OverflowPolicy.Checked, spaced!.Overflow);

        var (lowercased, lowercasedDiagnostics) = LoadBody(
            "<Arithmetic><Overflow>checked</Overflow></Arithmetic>");
        Assert.Null(lowercased);
        Assert.Contains(lowercasedDiagnostics, d => d.Code == "PL2002");
    }

    // ---------------------------------------------------------------- rejecting

    [Fact]
    public void AnUnknownSectionIsRejected()
    {
        var (config, diagnostics) = LoadBody("<Arythmetic><Overflow>Checked</Overflow></Arythmetic>");

        Assert.Null(config);
        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PL2001");
        Assert.Contains("Arithmetic", diagnostic.Help!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSettingIsRejected()
    {
        var (config, diagnostics) = LoadBody("<Arithmetic><Overflowe>Checked</Overflowe></Arithmetic>");

        Assert.Null(config);
        Assert.Contains(diagnostics, d => d.Code == "PL2001");
    }

    /// <summary>An unknown value names the legal ones, since there is a short closed list of them.</summary>
    [Fact]
    public void AnUnknownValueIsRejectedAndTheLegalOnesAreNamed()
    {
        var (config, diagnostics) = LoadBody("<Arithmetic><Overflow>Wrap</Overflow></Arithmetic>");

        Assert.Null(config);
        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PL2002");
        Assert.Contains("Wrapping", diagnostic.Help!, StringComparison.Ordinal);
        Assert.Contains("Saturating", diagnostic.Help!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateSettingIsRejected()
    {
        var (config, diagnostics) = LoadBody(
            "<Arithmetic><Overflow>Checked</Overflow><Overflow>Saturating</Overflow></Arithmetic>");

        Assert.Null(config);
        Assert.Contains(diagnostics, d => d.Code == "PL2004");
    }

    [Fact]
    public void MalformedXmlIsRejectedWithItsLocation()
    {
        var (config, diagnostics) = Load("<ProtoLang><Arithmetic></ProtoLang>");

        Assert.Null(config);
        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PL2003");
        Assert.True(diagnostic.Span.Line > 0, "a malformed file should still report where it went wrong");
    }

    [Fact]
    public void AWrongRootElementIsRejected()
    {
        var (config, diagnostics) = Load("<Protolang><Arithmetic /></Protolang>");

        Assert.Null(config);
        Assert.Contains(diagnostics, d => d.Code == "PL2003");
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Found the way <c>.editorconfig</c> and <c>Directory.Build.props</c> are, so a repository
    /// states its policy once at the root rather than in every directory.
    /// </summary>
    [Fact]
    public void DiscoveryWalksUpFromTheStartingDirectory()
    {
        var configPath = WriteConfig(string.Format(Wrapper, ""));
        var root = Path.GetDirectoryName(configPath)!;
        var nested = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(nested);

        Assert.Equal(configPath, ProjectConfig.Discover(nested));
    }

    /// <summary>The nearest one wins, which is how a subdirectory selects a different policy.</summary>
    [Fact]
    public void TheNearestConfigWins()
    {
        var outerPath = WriteConfig(string.Format(Wrapper, ""));
        var root = Path.GetDirectoryName(outerPath)!;
        var nested = Path.Combine(root, "inner");
        Directory.CreateDirectory(nested);

        var innerPath = Path.Combine(nested, ProjectConfig.FileName);
        File.WriteAllText(innerPath, string.Format(Wrapper, ""));

        Assert.Equal(innerPath, ProjectConfig.Discover(nested));
    }

    // ---------------------------------------------------------------- config versus command line

    /// <summary>
    /// The rule issue #20 asked for: the file wins, and a flag that contradicts it is refused
    /// rather than quietly applied. A build whose semantics depend on who typed the command is not
    /// reproducible, which is the whole reason the file exists.
    /// </summary>
    [Fact]
    public void AFlagContradictingAStatedSettingIsRefused()
    {
        var (config, _) = LoadBody("<Arithmetic><Overflow>Checked</Overflow></Arithmetic>");

        Assert.False(config!.TryOverrideOverflow(
            OverflowPolicy.Saturating, allowOverride: false, out _, out var conflict));

        Assert.NotNull(conflict);
        Assert.Contains("saturating", conflict!, StringComparison.Ordinal);
        Assert.Contains("Checked", conflict, StringComparison.Ordinal);
    }

    /// <summary>Trying another policy stays one flag away, but the flag has to be written.</summary>
    [Fact]
    public void TheOverrideFlagLetsTheCommandLineWin()
    {
        var (config, _) = LoadBody("<Arithmetic><Overflow>Checked</Overflow></Arithmetic>");

        Assert.True(config!.TryOverrideOverflow(
            OverflowPolicy.Saturating, allowOverride: true, out var overridden, out var conflict));

        Assert.Null(conflict);
        Assert.Equal(OverflowPolicy.Saturating, overridden.Overflow);
    }

    /// <summary>
    /// A default the file left in place is not an answer the project gave, so a flag may set it
    /// without ceremony. Otherwise every project would have to state every setting to stay usable
    /// from the command line.
    /// </summary>
    [Fact]
    public void AFlagMaySetWhatTheFileDidNotState()
    {
        var (config, _) = LoadBody("<Presence><UnsetMessageRead>RequireGuard</UnsetMessageRead></Presence>");

        Assert.True(config!.TryOverrideOverflow(
            OverflowPolicy.Checked, allowOverride: false, out var overridden, out _));

        Assert.Equal(OverflowPolicy.Checked, overridden.Overflow);
    }

    /// <summary>A flag that agrees with the file is not a conflict, whatever the file states.</summary>
    [Fact]
    public void AFlagAgreeingWithTheFileIsNotAConflict()
    {
        var (config, _) = LoadBody("<Arithmetic><Overflow>Checked</Overflow></Arithmetic>");

        Assert.True(config!.TryOverrideOverflow(
            OverflowPolicy.Checked, allowOverride: false, out _, out var conflict));

        Assert.Null(conflict);
    }

    // ---------------------------------------------------------------- the generated header

    private static string EmitHeader(IBackend backend, OverflowPolicy policy, string suffix)
    {
        var path = TestPaths.WriteTempScript(
            "import proto \"fixtures.proto\";\nextend Outer { fn f() -> int64 { return count + count; } }");

        var config = ProjectConfig.Default with { Overflow = policy };
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory], config: config);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var options = new BackendOptions(Path.GetFileName(path))
        {
            PolicyDescription = result.Config.DescribeForHeader(),
        };

        var contents = backend.Emit(result.Module!, options, new DiagnosticBag())
            .Single(f => f.RelativePath.EndsWith(suffix, StringComparison.Ordinal))
            .Contents;

        return string.Join(
            "\n",
            contents.Split('\n').TakeWhile(line => !line.Contains("</auto-generated>", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The header is the first thing a reader looks at, and the only place a generated file explains
    /// itself. It used to claim wrapping semantics unconditionally, which was true until a second
    /// mode existed and then became a lie in exactly the files someone would be reading because the
    /// arithmetic had surprised them.
    /// </summary>
    [Theory]
    [InlineData(OverflowPolicy.Wrapping, "Wrapping")]
    [InlineData(OverflowPolicy.Checked, "Checked")]
    [InlineData(OverflowPolicy.Saturating, "Saturating")]
    public void TheGeneratedHeaderNamesThePolicyThatProducedIt(OverflowPolicy policy, string expected)
    {
        foreach (var (backend, suffix) in new (IBackend Backend, string Suffix)[]
                 {
                     (new CSharpBackend(), "test.g.cs"),
                     (new CppBackend(), "test.pl.h"),
                 })
        {
            var header = EmitHeader(backend, policy, suffix);

            Assert.Contains(expected, header, StringComparison.Ordinal);

            // Naming the right policy is not enough on its own: the old header named a policy too.
            foreach (var other in Enum.GetValues<OverflowPolicy>())
            {
                if (other != policy)
                {
                    Assert.DoesNotContain(other.ToString(), header, StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>
    /// Both targets say the same thing about the same build. A header claiming reproducibility while
    /// describing the compilation differently per language would be worth less than none, which is
    /// why the lines are rendered once in the core rather than written out by each backend.
    /// </summary>
    [Fact]
    public void BothBackendsDescribeTheBuildIdentically()
    {
        var csharp = EmitHeader(new CSharpBackend(), OverflowPolicy.Saturating, "test.g.cs");
        var cpp = EmitHeader(new CppBackend(), OverflowPolicy.Saturating, "test.pl.h");

        foreach (var line in (ProjectConfig.Default with { Overflow = OverflowPolicy.Saturating })
                     .DescribeForHeader())
        {
            Assert.Contains(line, csharp, StringComparison.Ordinal);
            Assert.Contains(line, cpp, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A caller that compiles with no configuration still gets an accurate header rather than none.
    /// That is what let every existing call site stay as it was instead of being updated to repeat
    /// the default back to the compiler.
    /// </summary>
    [Fact]
    public void BackendOptionsDescribeTheDefaultPolicyWhenNothingSaysOtherwise()
    {
        Assert.Equal(
            ProjectConfig.Default.DescribeForHeader(),
            new BackendOptions("test.protolang").PolicyDescription);
    }

    // ---------------------------------------------------------------- reaching the IR

    /// <summary>
    /// The end of the chain: a value in the file becomes the annotation the backends switch on.
    /// Without this, the file could parse perfectly and change nothing.
    /// </summary>
    [Theory]
    [InlineData(OverflowPolicy.Wrapping, ArithmeticBehavior.Wrap)]
    [InlineData(OverflowPolicy.Checked, ArithmeticBehavior.Check)]
    [InlineData(OverflowPolicy.Saturating, ArithmeticBehavior.Saturate)]
    public void ThePolicyReachesTheTypedIr(OverflowPolicy policy, ArithmeticBehavior expected)
    {
        var source = TestPaths.WriteTempScript(
            "import proto \"fixtures.proto\";\nextend Outer { fn f() -> int64 { return count + count; } }");

        var result = Compilation.Compile(
            source,
            [TestPaths.FixtureProtoDirectory],
            config: ProjectConfig.Default with { Overflow = policy });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var returned = result.Module!.Methods.Single(m => m.Name == "f").Body
            .Statements.OfType<IrReturn>().Single().Value!;

        Assert.Equal(expected, Assert.IsType<IrBinary>(returned).Behavior);
    }

    /// <summary>
    /// A project that states a policy and is then ignored is worse off than one that states
    /// nothing, so a bad config file stops the compilation rather than falling back.
    /// </summary>
    [Fact]
    public void ABadConfigFileStopsTheCompilation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, ProjectConfig.FileName),
            string.Format(Wrapper, "<Arithmetic><Overflow>Nonsense</Overflow></Arithmetic>"));

        var source = Path.Combine(directory, "test.protolang");
        File.WriteAllText(
            source,
            "import proto \"fixtures.proto\";\nextend Outer { fn f() -> int64 { return count; } }");

        var result = Compilation.Compile(source, [TestPaths.FixtureProtoDirectory]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "PL2002");
    }

    /// <summary>
    /// The repository's own config file has to be readable by the loader that reads it. It is not a
    /// hypothetical: the first draft used "--override-config" inside an XML comment, which is not
    /// valid XML, and this is what noticed.
    /// </summary>
    [Fact]
    public void TheRepositoryConfigLoadsCleanly()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, ProjectConfig.FileName);
        Assert.True(File.Exists(path), $"{path} should exist");

        var diagnostics = new DiagnosticBag();
        var config = ProjectConfig.Load(path, diagnostics);

        Assert.Empty(diagnostics);
        Assert.NotNull(config);

        // It states the defaults, so the repository behaves the same with or without it.
        Assert.Equal(ProjectConfig.Default.Overflow, config!.Overflow);
        Assert.Equal(ProjectConfig.Default.Conversion, config.Conversion);
        Assert.Equal(ProjectConfig.Default.DivideByZero, config.DivideByZero);
        Assert.Equal(ProjectConfig.Default.UnsetMessageRead, config.UnsetMessageRead);
    }

    /// <summary>
    /// Each policy directory in the conformance corpus has to actually select its policy, or the
    /// vectors in it would silently test the default one and still pass.
    /// </summary>
    [Theory]
    [InlineData("checked", OverflowPolicy.Checked)]
    [InlineData("saturating", OverflowPolicy.Saturating)]
    public void EachConformancePolicyDirectorySelectsItsPolicy(string directory, OverflowPolicy expected)
    {
        var path = Path.Combine(
            TestPaths.RepositoryRoot, "tests", "conformance", "vectors", directory, ProjectConfig.FileName);

        var diagnostics = new DiagnosticBag();
        var config = ProjectConfig.Load(path, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(expected, config!.Overflow);
    }
}
