using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The compilation a question asked between two keystrokes is answered from: built for one exact
/// buffer, and kept only while that buffer is the one the editor is showing.
/// </summary>
/// <remarks>
/// <para>
/// Every property here is about identity rather than about compiling, so the tests count compiles
/// rather than inspecting them. A cache that quietly stopped answering would produce identical
/// results and pass every assertion about content; the count is the only thing that separates it
/// from one that works.
/// </para>
/// <para>
/// Driven directly rather than over the wire because the question is what happens between a buffer
/// moving and a compile starting, and nothing outside this object can stand there.
/// </para>
/// </remarks>
public class DocumentSemanticsTests
{
    private const string Source = "import proto \"fixtures.proto\";\n\nextend Outer {\n    fn f() -> int64 { return count; }\n}\n";

    private static LoaderPool Loaders() => new(new ServerLog());

    private static WorkspaceConfiguration Settings(string root)
        => WorkspaceConfiguration.Empty with
        {
            Generation = 1,
            User = new ProtoLangSettings { IncludePaths = [root] },
        };

    private static (DocumentSemantics Semantics, DocumentStore Documents, WorkspaceConfiguration Configuration)
        Fresh()
    {
        var documents = new DocumentStore();

        return (new DocumentSemantics(Loaders()), documents, Settings(TestPaths.FixtureProtoDirectory));
    }

    private static DocumentUri Uri(string name)
        => DocumentUri.Parse(new Uri(Path.Combine(TestPaths.CreateTempDirectory(), name)).AbsoluteUri);

    // ------- what counts as the same question

    /// <summary>A schema edit changes the answer even when the source buffer has not moved.</summary>
    [Fact]
    [Trait("ReviewRegression", "SchemaCompletion")]
    public void AChangedSchemaInvalidatesAnOtherwiseUnchangedDocumentCompilation()
    {
        var root = TestPaths.CreateTempDirectory();
        var schema = Path.Combine(root, "changing.proto");
        File.WriteAllText(schema, "syntax = \"proto3\"; message Counter { int64 before = 1; }");

        var uri = DocumentUri.Parse(new Uri(Path.Combine(root, "unchanged.protolang")).AbsoluteUri);
        var document = new DocumentStore().Open(
            uri, "protolang", 1,
            "import proto \"changing.proto\"; extend Counter { fn f() -> int64 { return 1; } }");
        var configuration = Settings(root);
        var loaders = Loaders();
        var semantics = new DocumentSemantics(loaders);
        var first = semantics.For(document, configuration, CancellationToken.None);

        Assert.NotNull(first.Result);
        Assert.True(first.Result!.Success, "the original schema and source must compile");
        Assert.NotNull(first.Result.Types.FindMessage("Counter")?.FindFieldByName("before"));

        File.WriteAllText(schema, "syntax = \"proto3\"; message Counter { int64 after = 1; }");

        // A fresh document cache over the same loader proves the schema change is visible below it.
        var control = new DocumentSemantics(loaders).For(document, configuration, CancellationToken.None);
        Assert.NotNull(control.Result?.Types.FindMessage("Counter")?.FindFieldByName("after"));

        var refreshed = semantics.For(document, configuration, CancellationToken.None);

        Assert.True(
            refreshed.Result?.Types.FindMessage("Counter")?.FindFieldByName("after") is not null,
            "an unchanged source must see the edited schema rather than a cached field named 'before'");
    }

    /// <summary>Repairing a policy file must release a cached refusal without editing the source.</summary>
    [Fact]
    [Trait("ReviewRegression", "SchemaCompletion")]
    public void ARepairedConfigurationIsRetriedWithoutAnEditorSettingsChange()
    {
        var root = TestPaths.CreateTempDirectory();
        var policy = Path.Combine(root, "protolang.config.xml");
        File.WriteAllText(policy, "<not-valid-xml");

        var uri = DocumentUri.Parse(new Uri(Path.Combine(root, "unchanged.protolang")).AbsoluteUri);
        var document = new DocumentStore().Open(uri, "protolang", 1, Source);
        var configuration = Settings(TestPaths.FixtureProtoDirectory);
        var loaders = Loaders();
        var semantics = new DocumentSemantics(loaders);

        Assert.Null(semantics.For(document, configuration, CancellationToken.None).Result);

        File.WriteAllText(policy, "<ProtoLang />");

        var control = new DocumentSemantics(loaders).For(document, configuration, CancellationToken.None);
        Assert.True(control.Result?.Success == true, "the repaired policy and unchanged source must compile");

        Assert.True(
            semantics.For(document, configuration, CancellationToken.None).Result?.Success == true,
            "a refusal must not survive the repair merely because the editor settings generation is unchanged");
    }

    /// <summary>A close during binding cannot be undone by the compilation finishing afterwards.</summary>
    /// <remarks>
    /// The seam closes the document immediately before returning the compiled result. This fixes the
    /// interleaving without sleeps: binding may finish after cancellation, but its result must not
    /// repopulate an entry Forget has already removed. Either cancellation or an uncached result is
    /// acceptable; retaining the closed document is not.
    /// </remarks>
    [Fact]
    [Trait("ReviewRegression", "SchemaCompletion")]
    public void ACompilationFinishingAfterCloseCannotRestoreTheForgottenDocument()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("closing.protolang");
        var document = documents.Open(uri, "protolang", 1, Source);
        using var withdrawn = new CancellationTokenSource();
        var closed = false;

        semantics.Compile = (compilation, cancellationToken) =>
        {
            var result = compilation.Compile(cancellationToken);
            Assert.True(result.Success, "the close must happen after a real successful compilation");
            documents.Close(uri);
            withdrawn.Cancel();
            semantics.Forget(uri);
            closed = true;
            return result;
        };

        try
        {
            semantics.For(document, configuration, withdrawn.Token);
        }
        catch (OperationCanceledException) when (withdrawn.IsCancellationRequested)
        {
        }

        Assert.True(closed, "the test must reach the close-before-publication interleaving");
        Assert.Null(documents.Find(uri));
        Assert.True(semantics.Count == 0, "finishing abandoned work must not cache a closed document again");
    }

    [Fact]
    public void TheSameBufferIsCompiledOnceHoweverManyQuestionsAreAskedOfIt()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("same.protolang");
        var document = documents.Open(uri, "protolang", 1, Source);

        for (var asked = 0; asked < 5; asked++)
        {
            Assert.NotNull(semantics.For(document, configuration, CancellationToken.None).Result);
        }

        Assert.Equal(1, semantics.Compilations);
    }

    [Fact]
    public void AnEditedBufferIsCompiledAgainRatherThanAnsweredFromTheOneBefore()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("edited.protolang");

        var before = documents.Open(uri, "protolang", 1, Source);
        semantics.For(before, configuration, CancellationToken.None);

        var after = documents.Apply(uri, 2, [new TextDocumentContentChangeEvent { Text = Source + "\n" }]);

        Assert.NotNull(after);
        Assert.NotSame(before, after);

        semantics.For(after!, configuration, CancellationToken.None);

        Assert.Equal(2, semantics.Compilations);
    }

    /// <summary>
    /// Because the roots an import resolves against come from the settings, so an answer built under
    /// settings that have since moved describes somewhere the compiler no longer looks.
    /// </summary>
    [Fact]
    public void AConfigurationGenerationThatMovedIsCompiledAgain()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("resettled.protolang");
        var document = documents.Open(uri, "protolang", 1, Source);

        semantics.For(document, configuration, CancellationToken.None);
        semantics.For(document, configuration with { Generation = configuration.Generation + 1 }, CancellationToken.None);

        Assert.Equal(2, semantics.Compilations);
    }

    /// <summary>
    /// A version is unique only inside one open session, so a document closed and reopened starts
    /// again at one. Identity is what tells the two apart, and a check on the number would call them
    /// the same buffer and answer about text nobody has.
    /// </summary>
    [Fact]
    public void ADocumentClosedAndReopenedAtTheSameVersionIsNotTheSameBuffer()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("reopened.protolang");

        var first = documents.Open(uri, "protolang", 1, Source);
        semantics.For(first, configuration, CancellationToken.None);

        documents.Close(uri);
        var second = documents.Open(uri, "protolang", 1, Source);

        Assert.Equal(first.Version, second.Version);
        Assert.NotSame(first, second);

        semantics.For(second, configuration, CancellationToken.None);

        Assert.Equal(2, semantics.Compilations);
    }

    // ------- what it lets go of

    [Fact]
    public void AClosedDocumentIsForgotten()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("closed.protolang");
        var document = documents.Open(uri, "protolang", 1, Source);

        semantics.For(document, configuration, CancellationToken.None);

        Assert.Equal(1, semantics.Count);

        semantics.Forget(uri);

        Assert.Equal(0, semantics.Count);
    }

    [Fact]
    public void WhatIsHeldNeverGrowsPastTheNumberOfDocumentsAskedAbout()
    {
        var (semantics, documents, configuration) = Fresh();

        var documentsAsked = new List<OpenDocument>();

        for (var index = 0; index < 3; index++)
        {
            documentsAsked.Add(documents.Open(Uri($"many{index}.protolang"), "protolang", 1, Source));
        }

        // Every document edited several times, so an implementation keyed by anything finer than the
        // document -- the version, the text -- would hold twelve entries rather than three.
        for (var edit = 2; edit <= 5; edit++)
        {
            for (var index = 0; index < documentsAsked.Count; index++)
            {
                var moved = documents.Apply(
                    documentsAsked[index].Uri,
                    edit,
                    [new TextDocumentContentChangeEvent { Text = Source + new string('\n', edit) }]);

                documentsAsked[index] = moved!;
                semantics.For(moved!, configuration, CancellationToken.None);
            }
        }

        Assert.Equal(3, semantics.Count);
    }

    // ------- when there is nothing to compile

    /// <summary>
    /// A refused configuration is a property of the settings, which are part of what makes an entry
    /// answer, so it is remembered like any other answer rather than retried on every question.
    /// </summary>
    [Fact]
    public void ADocumentWhoseConfigurationWasRefusedIsRememberedRatherThanRetried()
    {
        var directory = TestPaths.CreateTempDirectory();

        File.WriteAllText(Path.Combine(directory, "protolang.config.xml"), "<not-valid-xml");

        var documents = new DocumentStore();
        var semantics = new DocumentSemantics(Loaders());
        var configuration = Settings(TestPaths.FixtureProtoDirectory);
        var uri = DocumentUri.Parse(new Uri(Path.Combine(directory, "refused.protolang")).AbsoluteUri);
        var document = documents.Open(uri, "protolang", 1, Source);

        var first = semantics.For(document, configuration, CancellationToken.None);

        Assert.Null(first.Result);
        Assert.Null(first.Semantics);

        semantics.For(document, configuration, CancellationToken.None);
        semantics.For(document, configuration, CancellationToken.None);

        Assert.Equal(0, semantics.Compilations);
        Assert.Equal(1, semantics.Count);
    }

    // ------- one per server, which is the point of it

    /// <summary>
    /// The host builds one and hands it to the scheduler, so a compile a keystroke scheduled and a
    /// question asked between two keystrokes are the same compile.
    /// </summary>
    /// <remarks>
    /// Over the wire, because what is being asserted is the wiring: nothing else holds the shared
    /// instance in place, and a host that built a second one for the scheduler would pass every other
    /// test in the suite while quietly compiling each buffer twice.
    /// </remarks>
    [Fact]
    public async Task TheCompileTheSchedulerRanIsTheOneEverythingElseAsksAbout()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var path = Path.Combine(TestPaths.CreateTempDirectory(), "shared.protolang");
        File.WriteAllText(path, "extend InvoiceItem {\n    fn f() -> int64 { return 1; }\n}\n");

        var uri = new Uri(path).AbsoluteUri;

        client.Notify(
            Methods.DidOpen,
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = uri,
                    LanguageId = "protolang",
                    Version = 1,
                    Text = File.ReadAllText(path),
                },
            });

        await client.DiagnosticsAsync(uri);

        Assert.Equal(1, client.Host.Compilations);
        Assert.Equal(1, client.Host.Semantics.Compilations);
        Assert.Equal(1, client.Host.Semantics.Count);
    }

    /// <summary>
    /// Because every question about a document comes through the store, so once it closes nothing can
    /// ask -- and what is held is a whole syntax tree and IR module.
    /// </summary>
    [Fact]
    public async Task ClosingADocumentLetsGoOfWhatWasCompiledForIt()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var path = Path.Combine(TestPaths.CreateTempDirectory(), "transient.protolang");
        var text = "extend InvoiceItem {\n    fn f() -> int64 { return 1; }\n}\n";
        File.WriteAllText(path, text);

        var uri = new Uri(path).AbsoluteUri;

        client.Notify(
            Methods.DidOpen,
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = uri, LanguageId = "protolang", Version = 1, Text = text,
                },
            });

        await client.DiagnosticsAsync(uri);

        Assert.Equal(1, client.Host.Semantics.Count);

        client.Notify(
            Methods.DidClose,
            new DidCloseTextDocumentParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        await client.DiagnosticsAsync(uri, published => published.Diagnostics.Count == 0);

        Assert.Equal(0, client.Host.Semantics.Count);
    }

    // ------- what it hands back

    [Fact]
    public void AnAnsweredBufferCarriesAModelThatCanBeAskedAboutPositions()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("model.protolang");
        var document = documents.Open(uri, "protolang", 1, Source);

        var compiled = semantics.For(document, configuration, CancellationToken.None);

        Assert.NotNull(compiled.Result);
        Assert.NotNull(compiled.Semantics);

        var offset = Source.IndexOf("count", StringComparison.Ordinal);

        Assert.True(offset > 0, "the fixture must read a field");
        Assert.NotNull(compiled.Semantics!.ScopeAt(offset));
    }

    [Fact]
    public void TheSettingsAnAnswerWasBuiltUnderComeBackWithIt()
    {
        var (semantics, documents, configuration) = Fresh();
        var uri = Uri("settings.protolang");
        var document = documents.Open(uri, "protolang", 1, Source);

        var compiled = semantics.For(document, configuration, CancellationToken.None);

        Assert.Same(document, compiled.Document);
        Assert.Equal(configuration.Generation, compiled.Configuration.Generation);
        Assert.Contains(TestPaths.FixtureProtoDirectory, compiled.Settings.IncludeDirectories);
    }
}
