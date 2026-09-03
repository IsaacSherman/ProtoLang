using System.Text.Json;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.Tests;

/// <summary>
/// The language server, driven the way an editor drives it: framed JSON-RPC over a pair of streams.
/// </summary>
/// <remarks>
/// Every test here goes through <see cref="LanguageServerClient"/> rather than calling a handler,
/// because the lifecycle gate, the framing, the dispatch order and the diagnostic routing are the
/// parts that are worth having and the parts a direct call would skip.
/// </remarks>
public class LanguageServerTests
{
    /// <summary>A file with no imports: it compiles as far as PL0001 and never reaches protoc.</summary>
    /// <remarks>
    /// Most of what this suite asserts -- ranges, help text, severities, clearing, staleness -- is true
    /// of every diagnostic, and asserting it against one that needs no toolchain keeps those tests
    /// runnable on a machine with no protoc and fast on one that has it.
    /// </remarks>
    private const string NoImports =
        """
        extend InvoiceItem {
            fn total() -> int64 {
                return 1;
            }
        }
        """;

    private static string UriOf(string path) => new Uri(path).AbsoluteUri;

    private static string WriteDocument(string source, string name = "source.protolang")
    {
        var path = Path.Combine(TestPaths.CreateTempDirectory(), name);
        File.WriteAllText(path, source);

        return path;
    }

    private static DidOpenTextDocumentParams Open(string uri, string text, int version = 1)
        => new()
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "protolang",
                Version = version,
                Text = text,
            },
        };

    private static DidChangeTextDocumentParams Change(
        string uri,
        int version,
        params TextDocumentContentChangeEvent[] changes)
        => new()
        {
            TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = changes,
        };

    private static TextDocumentContentChangeEvent Replace(int startLine, int startCharacter, int endLine, int endCharacter, string text)
        => new()
        {
            Range = new Range(new Position(startLine, startCharacter), new Position(endLine, endCharacter)),
            Text = text,
        };

    /// <summary>Answers <c>workspace/configuration</c> with one settings object for every scope.</summary>
    private static Func<ConfigurationParams, object?> Settings(Dictionary<string, object?> settings)
        => parameters => parameters.Items.Select(_ => settings).ToList();

    private static string RequireBundledProtoc()
    {
        var protoc = ProtocLocator.FindBundledProtoc();
        if (protoc is null)
        {
            Assert.Skip("No Grpc.Tools protoc in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }

    // ------------------------------------------------------- the lifecycle

    [Fact]
    public async Task TheServerNegotiatesAndShutsDownCleanly()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var shutdown = await client.RequestAsync(Methods.Shutdown, null);

        // A present null, not an absent result: JSON-RPC says a successful response carries the member
        // either way, and a client is entitled to treat one without it as malformed.
        Assert.Equal(JsonValueKind.Null, shutdown.ValueKind);

        // Disposing sends the exit.
        await client.DisposeAsync();

        Assert.Equal(ServerState.Exited, client.Host.State);
        Assert.Equal(0, client.Host.ExitCode);
    }

    [Fact]
    public async Task ARequestBeforeInitializeIsRefusedRatherThanAnswered()
    {
        await using var client = LanguageServerClient.Create();

        var refusal = await client.RefusalAsync(
            Methods.SemanticTokensFull,
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = "file:///x.protolang" } });

        // Distinguishable from "too late", which is what lets a client tell a race from a defect.
        Assert.Equal(ErrorCodes.ServerNotInitialized, refusal.Code);
    }

    [Fact]
    public async Task ARequestAfterShutdownIsRefusedRatherThanAnswered()
    {
        await using var client = await LanguageServerClient.StartAsync();

        await client.RequestAsync(Methods.Shutdown, null);

        var refusal = await client.RefusalAsync(
            Methods.SemanticTokensFull,
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = "file:///x.protolang" } });

        Assert.Equal(ErrorCodes.InvalidRequest, refusal.Code);
    }

    [Fact]
    public async Task InitializingTwiceIsRefused()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var refusal = await client.RefusalAsync(Methods.Initialize, new InitializeParams());

        Assert.Equal(ErrorCodes.InvalidRequest, refusal.Code);
    }

    [Fact]
    public async Task ExitWithoutShutdownReportsFailure()
    {
        var client = await LanguageServerClient.StartAsync();

        await client.DisposeAsync();

        Assert.Equal(1, client.Host.ExitCode);
    }

    [Fact]
    public async Task AnUnknownRequestIsRefusedRatherThanIgnored()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var refusal = await client.RefusalAsync("textDocument/somethingNobodyImplemented", null);

        Assert.Equal(ErrorCodes.MethodNotFound, refusal.Code);
    }

    // ------------------------------------------------------- what was negotiated

    [Fact]
    public async Task SemanticTokensAreOfferedOnlyToAClientThatAskedAboutThem()
    {
        await using var asking = LanguageServerClient.Create();
        var offered = await asking.InitializeAsync(LanguageServerClient.FullCapabilities, null);

        await using var silent = LanguageServerClient.Create();
        var withheld = await silent.InitializeAsync(
            new ClientCapabilities { TextDocument = new TextDocumentClientCapabilities() },
            null);

        Assert.NotNull(offered.Capabilities.SemanticTokensProvider);
        Assert.Null(withheld.Capabilities.SemanticTokensProvider);
    }

    [Fact]
    public async Task TheServerNegotiatesTheEncodingItsColumnsAreActuallyMeasuredIn()
    {
        await using var client = LanguageServerClient.Create();

        var result = await client.InitializeAsync(LanguageServerClient.FullCapabilities, null);

        // SourcePosition counts UTF-16 code units, so this is a statement about the compiler rather
        // than a preference. Declaring anything else would shift every column on a line holding an
        // astral character.
        Assert.Equal("utf-16", result.Capabilities.PositionEncoding);
    }

    [Fact]
    public async Task AWorkspaceFolderThatIsNotADirectoryIsIgnoredRatherThanFatal()
    {
        await using var client = LanguageServerClient.Create();

        var result = await client.RequestAsync(
            Methods.Initialize,
            new InitializeParams
            {
                Capabilities = LanguageServerClient.FullCapabilities,
                WorkspaceFolders = [new WorkspaceFolder { Uri = "untitled:nowhere", Name = "nowhere" }],
            });

        // A virtual workspace has no file system behind it and resolves nothing, but the session still
        // has to start: the user may have a real file open in it.
        Assert.NotEqual(JsonValueKind.Null, result.ValueKind);
    }

    // ------------------------------------------------------- diagnostics

    [Fact]
    public async Task OpeningADocumentPublishesDiagnosticsForIt()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri);

        Assert.Equal(uri, published.Uri);
        Assert.Contains(published.Diagnostics, diagnostic => diagnostic.Code == "PL0001");
    }

    [Fact]
    public async Task EditingABufferUpdatesDiagnosticsWithoutSaving()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        await client.DiagnosticsAsync(uri, published => published.Diagnostics.Count > 0);

        // A brace the file never closes. Nothing is written to disk, and the file on disk still holds
        // text that parses -- so a server consulting the disk would report nothing.
        client.Notify(Methods.DidChange, Change(uri, 2, Replace(0, 0, 0, 0, "extend Broken {\n")));

        var updated = await client.DiagnosticsAsync(
            uri,
            published => published.Diagnostics.Any(diagnostic => diagnostic.Code != "PL0001"));

        Assert.Equal(2, updated.Version);
        Assert.Contains(updated.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ADiagnosticThatSpansSeveralLinesKeepsBothOfItsEnds()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);
        var wholeFile = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL0001");

        // PL0001 is about the compilation unit, which is every line of it. A range collapsed to its
        // start would still look plausible in an editor and would be wrong.
        Assert.Equal(0, wholeFile.Range.Start.Line);
        Assert.Equal(NoImports.Split('\n').Length - 1, wholeFile.Range.End.Line);
    }

    [Fact]
    public async Task EveryPublishedRangeLiesInsideTheDocumentAndEndsWhereItStartsOrLater()
    {
        await using var client = await LanguageServerClient.StartAsync();

        const string Broken = "extend {\n  fn (\n}\n";

        var uri = UriOf(WriteDocument(Broken));
        client.Notify(Methods.DidOpen, Open(uri, Broken));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);
        var lastLine = Broken.Split('\n').Length - 1;

        Assert.NotEmpty(published.Diagnostics);
        Assert.All(
            published.Diagnostics,
            diagnostic =>
            {
                Assert.True(diagnostic.Range.Start.Line >= 0, "a range may not start before the first line");
                Assert.True(diagnostic.Range.End.Line <= lastLine, "a range may not end past the last line");
                Assert.True(
                    diagnostic.Range.End.Line > diagnostic.Range.Start.Line
                        || (diagnostic.Range.End.Line == diagnostic.Range.Start.Line
                            && diagnostic.Range.End.Character >= diagnostic.Range.Start.Character),
                    "a range may not end before it starts");
            });
    }

    [Fact]
    public async Task SeverityMapsWithoutInventingALevel()
    {
        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["notASetting"] = "x" }));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 1);

        // The compiler has two severities and LSP has four. Anything at Information or Hint would be
        // this server asserting a distinction the language does not draw.
        Assert.All(
            published.Diagnostics,
            diagnostic => Assert.True(
                diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning,
                $"{diagnostic.Code} was published at {diagnostic.Severity}"));
    }

    [Fact]
    public async Task TheDiagnosticCodeAndHelpTextBothSurvive()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);
        var noImports = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL0001");

        // PL0001's help says what to write instead. This client declared relatedInformation, so the
        // help arrives as its own item rather than run into the message.
        var help = Assert.Single(noImports.RelatedInformation!);

        Assert.Contains("import proto", help.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("help:", noImports.Message, StringComparison.Ordinal);
        Assert.NotNull(noImports.Data);
    }

    [Fact]
    public async Task HelpIsKeptInTheMessageWhenTheClientCannotShowItSeparately()
    {
        await using var client = LanguageServerClient.Create();

        await client.InitializeAsync(
            new ClientCapabilities
            {
                TextDocument = new TextDocumentClientCapabilities
                {
                    PublishDiagnostics = new PublishDiagnosticsClientCapabilities { RelatedInformation = false },
                },
            },
            null);

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);
        var noImports = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL0001");

        // Losing it is not an option: several diagnostics put the only actionable instruction there.
        Assert.Contains("help:", noImports.Message, StringComparison.Ordinal);
        Assert.Null(noImports.RelatedInformation);
    }

    [Fact]
    public async Task ClosingADocumentClearsItsDiagnostics()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        client.Notify(Methods.DidClose, new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
        });

        var cleared = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count == 0);

        Assert.Empty(cleared.Diagnostics);
    }

    [Fact]
    public async Task ADiagnosticThatIsResolvedIsCleared()
    {
        await using var client = await LanguageServerClient.StartAsync();

        const string Unclosed = "extend InvoiceItem {\n";

        var uri = UriOf(WriteDocument(Unclosed));
        client.Notify(Methods.DidOpen, Open(uri, Unclosed));

        await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        // Replacing the whole buffer with something whose only complaint is PL0001.
        client.Notify(Methods.DidChange, Change(uri, 2, new TextDocumentContentChangeEvent { Text = NoImports }));

        var fixedUp = await client.DiagnosticsAsync(
            uri,
            message => message.Version == 2 && message.Diagnostics.All(diagnostic => diagnostic.Code == "PL0001"));

        Assert.Single(fixedUp.Diagnostics);
    }

    // ------------------------------------------------------- the buffer is the truth

    [Fact]
    public async Task SeveralChangesInOneNotificationApplyInOrder()
    {
        await using var client = await LanguageServerClient.StartAsync();

        const string Start = "aaa\nbbb\n";

        var uri = UriOf(WriteDocument(Start));
        client.Notify(Methods.DidOpen, Open(uri, Start));

        // The second range describes the text the first change produced, which is what the client
        // meant. Applying both against the original text, or in the other order, gives something else.
        client.Notify(
            Methods.DidChange,
            Change(uri, 2, Replace(0, 0, 0, 3, "extend X {"), Replace(1, 0, 1, 3, "}")));

        var tokens = await client.RequestAsync(
            Methods.SemanticTokensFull,
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        Assert.Equal(
            SemanticTokenEncoder.Encode("extend X {\n}\n", uri).Data,
            tokens.Deserialize<SemanticTokens>(LspJson.Options)!.Data);
    }

    [Fact]
    public async Task AFullTextChangeIsAcceptedAlongsideRangedOnes()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var uri = UriOf(WriteDocument("aaa\n"));
        client.Notify(Methods.DidOpen, Open(uri, "aaa\n"));

        // A change with no range replaces everything. A server that declared incremental sync still
        // has to accept it, because a client may send one whenever it likes.
        client.Notify(
            Methods.DidChange,
            Change(
                uri,
                2,
                new TextDocumentContentChangeEvent { Text = "zzz\n" },
                Replace(0, 0, 0, 3, "fn")));

        var tokens = await client.RequestAsync(
            Methods.SemanticTokensFull,
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        Assert.Equal(
            SemanticTokenEncoder.Encode("fn\n", uri).Data,
            tokens.Deserialize<SemanticTokens>(LspJson.Options)!.Data);
    }

    // ------------------------------------------------------- configuration

    [Fact]
    public async Task ASettingTheServerDoesNotUnderstandIsReportedRatherThanDropped()
    {
        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["protocPathh"] = "typo" }));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(
            uri,
            message => message.Diagnostics.Any(diagnostic => diagnostic.Code == "PL2102"));

        var ignored = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL2102");

        // A user who writes a setting and sees no effect cannot tell a typo from a refusal from a bug,
        // and guessing between those three is the most expensive minute in a support request.
        Assert.Equal(DiagnosticSeverity.Warning, ignored.Severity);
        Assert.Contains("protocPathh", ignored.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingThatStatesLanguagePolicyIsRefusedAndSaysWherePolicyLives()
    {
        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["overflow"] = "Checked" }));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(
            uri,
            message => message.Diagnostics.Any(diagnostic => diagnostic.Code == "PL2101"));

        var refused = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL2101");

        Assert.Contains("protolang.config.xml", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADiagnosticWithNoLocationIsPublishedAtTheStartOfTheDocument()
    {
        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["notASetting"] = "x" }));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(
            uri,
            message => message.Diagnostics.Any(diagnostic => diagnostic.Code == "PL2102"));

        var settingsWarning = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL2102");

        // A settings diagnostic is nowhere in the source. It must still be seen, and it must never go
        // through the 1-based conversion, which would put it on line -1.
        Assert.Equal(0, settingsWarning.Range.Start.Line);
        Assert.Equal(0, settingsWarning.Range.Start.Character);
        Assert.Equal(0, settingsWarning.Range.End.Line);
        Assert.Equal(0, settingsWarning.Range.End.Character);
    }

    [Fact]
    public async Task ASettingChangeTakesEffectWithoutARestart()
    {
        var stated = new Dictionary<string, object?>();

        await using var client = await LanguageServerClient.StartAsync(settings: Settings(stated));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var before = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        Assert.DoesNotContain(before.Diagnostics, diagnostic => diagnostic.Code == "PL2102");

        stated["notASetting"] = "x";
        client.Notify(Methods.DidChangeConfiguration, new Dictionary<string, object?> { ["settings"] = null });

        var after = await client.DiagnosticsAsync(
            uri,
            message => message.Diagnostics.Any(diagnostic => diagnostic.Code == "PL2102"));

        Assert.Contains(after.Diagnostics, diagnostic => diagnostic.Code == "PL2102");
    }

    // ------------------------------------------------------- surviving the client

    [Fact]
    public async Task MalformedJsonIsRefusedAndTheServerKeepsRunning()
    {
        await using var client = await LanguageServerClient.StartAsync();

        client.SendRaw("{ this is not json");

        var refusal = await client.WaitForAsync(
            message => message.Error?.Code == ErrorCodes.ParseError,
            "a parse error");

        Assert.NotNull(refusal.Error);

        // The header said how long the body was, so the stream is still synchronized and the next
        // message is exactly where it should be.
        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        Assert.NotEmpty(published.Diagnostics);
    }

    /// <remarks>
    /// A message with an id and no method is a response, however empty; one with neither is nothing at
    /// all, and saying so beats reading the next bytes as though they belonged to it.
    /// </remarks>
    [Fact]
    public async Task AMessageThatIsNeitherRequestNorResponseIsRefused()
    {
        await using var client = await LanguageServerClient.StartAsync();

        client.SendRaw("""{"jsonrpc":"2.0"}""");

        var refusal = await client.WaitForAsync(
            message => message.Error?.Code == ErrorCodes.InvalidRequest,
            "a refusal of a message with no method");

        Assert.NotNull(refusal.Error);
    }

    [Fact]
    public async Task ChangingADocumentThatWasNeverOpenedIsDroppedRatherThanFatal()
    {
        await using var client = await LanguageServerClient.StartAsync();

        client.Notify(Methods.DidChange, Change(UriOf(WriteDocument(NoImports)), 2, Replace(0, 0, 0, 0, "x")));

        // Still answering afterwards is the whole assertion.
        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        Assert.NotEmpty(published.Diagnostics);
    }

    // ------------------------------------------------------- scheduling

    [Fact]
    public async Task RapidEditsProduceOneCompilationRatherThanOnePerKeystroke()
    {
        await using var client = await LanguageServerClient.StartAsync(debounce: TimeSpan.FromMilliseconds(200));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        for (var version = 2; version <= 8; version++)
        {
            client.Notify(Methods.DidChange, Change(uri, version, Replace(0, 0, 0, 0, "\n")));
        }

        // Blank lines change nothing a diagnostic would say, so waiting for a publication would wait
        // forever; what is being measured is the work, not the answer.
        await client.StaysSilentAboutAsync(uri, TimeSpan.FromSeconds(2));

        // Seven more versions went by after the first compile. Anything close to eight compilations
        // means the debounce is not coalescing, which no assertion about diagnostics would notice.
        Assert.True(
            client.Host.Compilations <= 3,
            $"eight versions produced {client.Host.Compilations} compilations");
    }

    [Fact]
    public async Task DiagnosticsFromASupersededVersionAreNeverPublished()
    {
        await using var client = await LanguageServerClient.StartAsync(debounce: TimeSpan.FromMilliseconds(50));

        const string Broken = "extend {\n";

        var uri = UriOf(WriteDocument(Broken));
        client.Notify(Methods.DidOpen, Open(uri, Broken));
        client.Notify(Methods.DidChange, Change(uri, 2, new TextDocumentContentChangeEvent { Text = NoImports }));

        var settled = await client.DiagnosticsAsync(uri, message => message.Version == 2);

        Assert.All(
            settled.Diagnostics,
            diagnostic => Assert.Equal("PL0001", diagnostic.Code));

        // And nothing arrives afterwards to put the old errors back, which is the failure this rule
        // exists to prevent: the user fixes an error, the squiggle goes, and an older compile finishes.
        Assert.True(
            await client.StaysSilentAboutAsync(uri, TimeSpan.FromMilliseconds(750)),
            "a superseded compilation published after the version that replaced it");
    }

    // ------------------------------------------------------- protoc, and where its errors go

    [Fact]
    public async Task AValidDocumentPublishesNothingAgainstIt()
    {
        var protoc = RequireBundledProtoc();

        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?>
            {
                ["protocPath"] = protoc,
                ["includePaths"] = new[] { TestPaths.ExampleProtoDirectory },
            }));

        var uri = UriOf(TestPaths.SimpleScript);
        client.Notify(Methods.DidOpen, Open(uri, File.ReadAllText(TestPaths.SimpleScript)));

        var published = await client.DiagnosticsAsync(uri);

        Assert.Empty(published.Diagnostics);
    }

    [Fact]
    public async Task AProtocErrorIsPublishedAgainstTheProtoItNamesAndSummarizedOnTheImport()
    {
        var protoc = RequireBundledProtoc();

        var directory = TestPaths.CreateTempDirectory();
        var schema = Path.Combine(directory, "broken.proto");
        File.WriteAllText(schema, "syntax = \"proto3\";\n\nmessage Broken {\n  int64 quantity = ;\n}\n");

        const string Source = "import proto \"broken.proto\";\n\nextend Broken {\n    fn q() -> int64 { return quantity; }\n}\n";
        var document = Path.Combine(directory, "source.protolang");
        File.WriteAllText(document, Source);

        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["protocPath"] = protoc }));

        var uri = UriOf(document);
        client.Notify(Methods.DidOpen, Open(uri, Source));

        var onTheSchema = await client.DiagnosticsAsync(UriOf(schema), message => message.Diagnostics.Count > 0);
        var onTheDocument = await client.DiagnosticsAsync(uri, message => message.Diagnostics.Count > 0);

        // In the file protoc blamed, at the line protoc named.
        var schemaError = onTheSchema.Diagnostics[0];
        Assert.Equal(CompilationDiagnostics.ProtocSource, schemaError.Source);
        Assert.Null(schemaError.Code);
        Assert.Equal(3, schemaError.Range.Start.Line);

        // And on the import that pulled it in, so the buffer the user is looking at is not silent.
        var importError = Assert.Single(
            onTheDocument.Diagnostics,
            diagnostic => diagnostic.Source == CompilationDiagnostics.ProtocSource);

        Assert.Equal(0, importError.Range.Start.Line);
        Assert.Contains("broken.proto", importError.Message, StringComparison.Ordinal);

        // PL0003 reprints the whole of standard error, so it is replaced rather than published beside
        // the per-line diagnostics parsed out of that same text.
        Assert.DoesNotContain(onTheDocument.Diagnostics, diagnostic => diagnostic.Code == "PL0003");
    }

    [Fact]
    public async Task AProtoKeepsItsDiagnosticsWhileAnotherOpenDocumentStillReportsThem()
    {
        var protoc = RequireBundledProtoc();

        var directory = TestPaths.CreateTempDirectory();
        var schema = Path.Combine(directory, "broken.proto");
        File.WriteAllText(schema, "syntax = \"proto3\";\n\nmessage Broken {\n  int64 quantity = ;\n}\n");

        const string Source = "import proto \"broken.proto\";\n";

        var first = Path.Combine(directory, "first.protolang");
        var second = Path.Combine(directory, "second.protolang");
        File.WriteAllText(first, Source);
        File.WriteAllText(second, Source);

        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["protocPath"] = protoc }));

        client.Notify(Methods.DidOpen, Open(UriOf(first), Source));
        client.Notify(Methods.DidOpen, Open(UriOf(second), Source));

        await client.DiagnosticsAsync(UriOf(first), message => message.Diagnostics.Count > 0);
        await client.DiagnosticsAsync(UriOf(second), message => message.Diagnostics.Count > 0);

        var onTheSchema = await client.DiagnosticsAsync(UriOf(schema), message => message.Diagnostics.Count > 0);

        // Said once, not once per document that reported it: two buffers importing one broken schema
        // must not double every squiggle in it.
        Assert.Single(onTheSchema.Diagnostics, diagnostic => diagnostic.Range.Start.Line == 3);

        client.Notify(Methods.DidClose, new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = UriOf(first) },
        });

        await client.DiagnosticsAsync(UriOf(first), message => message.Diagnostics.Count == 0);

        // The schema is still broken and the second document still says so. Clearing it because one of
        // its two reporters closed would take a real error off the screen.
        Assert.True(
            await client.StaysSilentAboutAsync(UriOf(schema), TimeSpan.FromMilliseconds(750)),
            "closing one of two documents that report a schema error withdrew it");
    }

    [Fact]
    public async Task AProtocThatWasNamedAndIsNotThereIsReportedRatherThanReplaced()
    {
        var missing = Path.Combine(TestPaths.CreateTempDirectory(), "protoc-that-is-not-there.exe");

        await using var client = await LanguageServerClient.StartAsync(
            settings: Settings(new Dictionary<string, object?> { ["protocPath"] = missing }));

        var uri = UriOf(WriteDocument(NoImports));
        client.Notify(Methods.DidOpen, Open(uri, NoImports));

        var published = await client.DiagnosticsAsync(
            uri,
            message => message.Diagnostics.Any(diagnostic => diagnostic.Code == "PL2105"));

        var reported = Assert.Single(published.Diagnostics, diagnostic => diagnostic.Code == "PL2105");

        Assert.Contains("protoc", reported.Message, StringComparison.OrdinalIgnoreCase);
    }
}
