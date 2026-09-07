using System.Collections.Concurrent;
using System.Text.Json;
using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.Tests;

/// <summary>
/// Completion inside an <c>import proto</c> string: what is offered, where it is inserted, and where
/// it is not offered at all.
/// </summary>
/// <remarks>
/// Driven over the wire like the rest of the server's suite, because the dispatch, the position
/// conversion and the JSON shape are as much of this feature as the candidate list is. The one test
/// that does not is the staleness refusal, which needs the buffer to move during the file-system walk
/// and so has nowhere to stand outside the provider.
/// </remarks>
public class ImportCompletionTests
{
    /// <summary>
    /// A workspace whose schemas sit beside the source, so the search path under test is the source
    /// directory fallback -- the one a user gets without configuring anything, which is the audience
    /// this feature is for.
    /// </summary>
    private static string Workspace()
    {
        var directory = TestPaths.CreateTempDirectory();

        Schema(directory, "shared.proto");
        Schema(directory, "notes.txt");
        Schema(directory, "billing/invoice.proto");
        Schema(directory, "billing/credit.proto");
        Schema(directory, "billing/tax/rates.proto");

        return directory;
    }

    private static void Schema(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "syntax = \"proto3\";\n");
    }

    private const string Body = "\nextend InvoiceItem {\n    fn f() -> int64 { return 1; }\n}\n";

    private static string UriOf(string path) => new Uri(path).AbsoluteUri;

    private static DidOpenTextDocumentParams Open(string uri, string text)
        => new()
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "protolang",
                Version = 1,
                Text = text,
            },
        };

    private static Func<ConfigurationParams, object?> Settings(Dictionary<string, object?> settings)
        => parameters => parameters.Items.Select(_ => settings).ToList();

    /// <summary>Opens a document in its own workspace and returns the client and its URI.</summary>
    private static async Task<(LanguageServerClient Client, string Uri, string Text, string Root)> OpenAsync(
        string source,
        Dictionary<string, object?>? settings = null,
        string? root = null)
    {
        var directory = root ?? Workspace();
        var document = Path.Combine(directory, "source.protolang");
        File.WriteAllText(document, source);

        var client = await LanguageServerClient.StartAsync(
            settings: settings is null ? null : Settings(settings));

        var uri = UriOf(document);
        client.Notify(Methods.DidOpen, Open(uri, source));

        return (client, uri, source, directory);
    }

    private static async Task<CompletionList> CompleteAsync(LanguageServerClient client, string uri, Position at)
    {
        var answer = await client.RequestAsync(
            Methods.Completion,
            new CompletionParams { TextDocument = new TextDocumentIdentifier { Uri = uri }, Position = at });

        return answer.Deserialize<CompletionList>(LspJson.Options)!;
    }

    /// <summary>Where the character at <paramref name="offset"/> is, in the coordinates LSP counts in.</summary>
    private static Position PositionOf(string text, int offset)
    {
        var line = 0;
        var start = 0;

        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                start = index + 1;
            }
        }

        return new Position(line, offset - start);
    }

    /// <summary>The offset just inside the opening quote of the <paramref name="which"/> import.</summary>
    private static int PathStart(string text, int which = 0)
    {
        var index = -1;

        for (var seen = 0; seen <= which; seen++)
        {
            index = text.IndexOf("import proto \"", index + 1, StringComparison.Ordinal);
        }

        return index + "import proto \"".Length;
    }

    private static IEnumerable<string> Paths(CompletionList list)
        => list.Items.Select(item => item.TextEdit!.NewText);

    // ------------------------------------------------------- what is offered

    [Fact]
    public async Task AnEmptyImportStringOffersTheTopLevelOfEveryRoot()
    {
        var (client, uri, text, _) = await OpenAsync("import proto \"\";" + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text)));

        Assert.Contains("billing/", Paths(offered));
        Assert.Contains("shared.proto", Paths(offered));
        Assert.DoesNotContain("notes.txt", Paths(offered));
    }

    [Fact]
    public async Task TypingASeparatorNarrowsToThatDirectorysContents()
    {
        const string Source = "import proto \"billing/\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + "billing/".Length));

        Assert.Equal(
            ["billing/tax/", "billing/credit.proto", "billing/invoice.proto"],
            Paths(offered));
    }

    /// <summary>
    /// The state this feature is always invoked in. A person types the quote and then the path, so
    /// every keystroke in between is a string the lexer has had to report as unterminated.
    /// </summary>
    [Fact]
    public async Task AnUnterminatedImportStringStillCompletes()
    {
        const string Source = "import proto \"billing/inv";
        var (client, uri, text, _) = await OpenAsync(Source);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, text.Length));

        Assert.Contains("billing/invoice.proto", Paths(offered));
    }

    [Fact]
    public async Task ADeeperDirectoryCompletesLikeAnyOther()
    {
        const string Source = "import proto \"billing/tax/\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + "billing/tax/".Length));

        Assert.Equal(["billing/tax/rates.proto"], Paths(offered));
    }

    /// <summary>
    /// The case a user is least likely to know to type, and the one that proves the loader's own
    /// include directories are consulted rather than only the ones a workspace names.
    /// </summary>
    [Fact]
    public async Task TheWellKnownSchemasAreOffered()
    {
        var loader = DescriptorLoader.CreateDefault();
        if (loader.ImplicitIncludePaths.Count == 0)
        {
            Assert.Skip($"'{loader.ProtocPath}' ships no well-known schemas as files.");
        }

        const string Source = "import proto \"google/protobuf/\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + "google/protobuf/".Length));

        Assert.Contains("google/protobuf/timestamp.proto", Paths(offered));
    }

    // ------------------------------------------------------- what is inserted

    /// <summary>
    /// Every item replaces the whole path, whatever part of it the caret is in. A client left to
    /// decide for itself where the word being completed starts disagrees with the next client about
    /// whether a slash ends one, and the disagreement inserts a path twice.
    /// </summary>
    [Fact]
    public async Task EveryItemReplacesTheWholePathHoweverFarIntoItTheCaretIs()
    {
        const string Source = "import proto \"billing/invoice.proto\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var start = PathStart(text);
        var expected = new Range(
            PositionOf(text, start),
            PositionOf(text, start + "billing/invoice.proto".Length));

        for (var caret = start; caret <= start + "billing/invoice.proto".Length; caret++)
        {
            var offered = await CompleteAsync(client, uri, PositionOf(text, caret));

            Assert.NotEmpty(offered.Items);
            Assert.All(offered.Items, item => Assert.Equal(expected, item.TextEdit!.Range));
        }
    }

    /// <summary>
    /// The unterminated case has no closing quote to stop at, and the semicolon it swallowed on its
    /// way to the end of the line is the one character of the declaration the author did get right.
    /// </summary>
    [Fact]
    public async Task AnEditNeverReachesPastThePathIntoTheDeclaration()
    {
        const string Source = "import proto \"bil;";
        var (client, uri, text, _) = await OpenAsync(Source);
        await using var _client = client;

        var start = PathStart(text);
        var offered = await CompleteAsync(client, uri, PositionOf(text, start + 3));

        Assert.All(
            offered.Items,
            item => Assert.Equal(PositionOf(text, start + 3), item.TextEdit!.Range.End));
    }

    /// <summary>
    /// A semicolon is an ordinary character inside a quoted string and a legal one in a directory
    /// name, so it ends the path only when the author has not closed the quote. Treating it as a
    /// terminator outright made a real directory impossible to offer or replace.
    /// </summary>
    [Fact]
    public async Task ASemicolonInsideAClosedStringIsPartOfThePathAndNotTheEndOfIt()
    {
        var root = Workspace();
        Schema(root, "odd;name/invoice.proto");

        const string Source = "import proto \"odd;name/\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body, root: root);
        await using var _client = client;

        var start = PathStart(text);
        var offered = await CompleteAsync(client, uri, PositionOf(text, start + "odd;name/".Length));

        Assert.Equal(["odd;name/invoice.proto"], Paths(offered));

        // And the edit still stops at the closing quote rather than at the semicolon inside the path.
        Assert.All(
            offered.Items,
            item => Assert.Equal(
                PositionOf(text, start + "odd;name/".Length),
                item.TextEdit!.Range.End));
    }

    /// <summary>
    /// A Windows user types their own separator and an editor closes the quote for them, which leaves
    /// a backslash immediately before the closing quote. Read as the lexer reads it that backslash
    /// escapes the quote, the path runs on to the semicolon, and the edit eats the quote the editor
    /// supplied. Read as a separator -- which is what it is here -- the line completes.
    /// </summary>
    [Fact]
    public async Task ASeparatorTypedRightBeforeAnAutoClosedQuoteStillCompletes()
    {
        const string Source = "import proto \"billing\\\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var caret = PathStart(text) + "billing\\".Length;
        var offered = await CompleteAsync(client, uri, PositionOf(text, caret));

        Assert.Equal(
            ["billing/tax/", "billing/credit.proto", "billing/invoice.proto"],
            Paths(offered));

        // And the edit stops at the quote rather than swallowing it and the semicolon behind it.
        Assert.All(offered.Items, item => Assert.Equal(PositionOf(text, caret), item.TextEdit!.Range.End));
    }

    [Fact]
    public async Task EveryPathIsOfferedInProtobufFormRatherThanThisMachinesSpelling()
    {
        var (client, uri, text, _) = await OpenAsync("import proto \"\";" + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text)));

        Assert.NotEmpty(offered.Items);
        Assert.All(offered.Items, item => Assert.DoesNotContain('\\', item.TextEdit!.NewText));
    }

    /// <summary>
    /// The list is filtered against the whole path rather than the label, because by the time a second
    /// segment is being typed the user has a separator on screen and a client matching one segment
    /// against a multi-segment word discards everything it was about to show.
    /// </summary>
    [Fact]
    public async Task AnItemIsFilteredByItsWholePathAndLabelledByItsLastSegment()
    {
        const string Source = "import proto \"billing/\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + "billing/".Length));

        var invoice = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "billing/invoice.proto");

        Assert.Equal("invoice.proto", invoice.Label);
        Assert.Equal("billing/invoice.proto", invoice.FilterText);
        Assert.Equal(CompletionItemKind.File, invoice.Kind);
    }

    [Fact]
    public async Task ADirectoryIsLabelledAsOneAndSortsAheadOfTheSchemas()
    {
        const string Source = "import proto \"billing/\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + "billing/".Length));

        var tax = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "billing/tax/");

        Assert.Equal("tax/", tax.Label);
        Assert.Equal(CompletionItemKind.Folder, tax.Kind);
        Assert.All(
            offered.Items.Where(item => item.Kind == CompletionItemKind.File),
            item => Assert.True(
                string.CompareOrdinal(tax.SortText, item.SortText) < 0,
                "a directory is a step towards an answer and must be offered before the answers"));
    }

    // ------------------------------------------------------- what the user could not otherwise see

    [Fact]
    public async Task AnAlreadyImportedSchemaIsStillOfferedAndSaysSo()
    {
        const string Source = "import proto \"shared.proto\";\nimport proto \"\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text, which: 1)));

        var shared = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "shared.proto");

        Assert.Contains("already imported", shared.Detail);
    }

    /// <summary>
    /// The declaration under the caret imports nothing yet, whatever it currently reads. Counting it
    /// would mark the schema on that very line as a duplicate of itself, on the one list where the
    /// user is deciding whether to keep it.
    /// </summary>
    [Fact]
    public async Task TheImportBeingEditedIsNotADuplicateOfItself()
    {
        const string Source = "import proto \"shared.proto\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + 3));

        var shared = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "shared.proto");

        Assert.DoesNotContain("already imported", shared.Detail);
    }

    [Fact]
    public async Task ASchemaThatIsNotYetImportedSaysOnlyWhereItIs()
    {
        const string Source = "import proto \"shared.proto\";\nimport proto \"\";";
        var (client, uri, text, root) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text, which: 1)));

        var billing = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "billing/");

        Assert.Equal(root, billing.Detail);
    }

    /// <summary>
    /// Which copy wins is decided by an order of <c>--proto_path</c> arguments nothing on screen
    /// shows, so a user editing one file and compiling another has no way to find out.
    /// </summary>
    [Fact]
    public async Task AShadowedSchemaSaysWhichRootWinsAndWhichLoses()
    {
        var vendored = Workspace();
        var checkout = TestPaths.CreateTempDirectory();
        Schema(checkout, "billing/invoice.proto");

        var (client, uri, text, _) = await OpenAsync(
            "import proto \"billing/\";" + Body,
            settings: new Dictionary<string, object?> { ["includePaths"] = new[] { checkout } },
            root: vendored);

        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text) + "billing/".Length));

        var invoice = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "billing/invoice.proto");

        // The setting is searched ahead of the source's own directory, so the checkout wins.
        Assert.Equal(checkout, invoice.Detail);
        Assert.Contains(vendored, invoice.Documentation);
    }

    // ------------------------------------------------------- where nothing is offered

    /// <summary>
    /// The sweep: for every position in a file, completion answers exactly where an import path can
    /// be typed and nowhere else. The regions are computed from the text rather than written down, so
    /// the test still means something after the fixture is edited.
    /// </summary>
    /// <remarks>
    /// The fixture holds the two things that look like an import path and are not: a comment naming
    /// one, which the lexer discards as trivia, and a string literal in expression position, which it
    /// does not. The second is the one that matters -- a token stream scanned for string literals
    /// alone finds it, and offering schema paths inside a return value is completion firing in a
    /// place no schema path can go.
    /// </remarks>
    [Fact]
    public async Task OnlyACaretInsideAnImportPathCompletesAtAll()
    {
        const string Source =
            "// nothing here is a \"path.proto\"\nimport proto \"\";\nimport proto \"billing/\";\n"
                + "\nextend InvoiceItem {\n    fn label() -> string { return \"billing/invoice.proto\"; }\n}\n";

        var (client, uri, text, _) = await OpenAsync(Source);
        await using var _client = client;

        var inside = PathRegions(text);

        for (var offset = 0; offset <= text.Length; offset++)
        {
            var offered = await CompleteAsync(client, uri, PositionOf(text, offset));
            var wanted = inside.Contains(offset);

            Assert.True(
                wanted == (offered.Items.Count > 0),
                $"offset {offset} is {(wanted ? "inside" : "outside")} an import path and offered "
                    + $"{offered.Items.Count} candidates");
        }
    }

    /// <summary>Every offset at which a character of an import path could be typed.</summary>
    private static HashSet<int> PathRegions(string text)
    {
        var offsets = new HashSet<int>();

        for (var index = 0; (index = text.IndexOf("import proto \"", index, StringComparison.Ordinal)) >= 0;)
        {
            var start = index + "import proto \"".Length;
            var end = text.IndexOf('"', start);

            for (var offset = start; offset <= end; offset++)
            {
                offsets.Add(offset);
            }

            index = end;
        }

        return offsets;
    }

    [Fact]
    public async Task ADocumentTheServerHasNeverSeenOffersNothingRatherThanFailing()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var offered = await CompleteAsync(
            client,
            "file:///nowhere/unopened.protolang",
            new Position(0, 0));

        Assert.Empty(offered.Items);
    }

    // ------------------------------------------------------- what it costs

    /// <summary>
    /// Nothing is compiled and protoc never runs, which is what lets this answer between keystrokes.
    /// Without the count this is a claim: a provider that compiled would return the same list.
    /// </summary>
    [Fact]
    public async Task CompletionNeverCompiles()
    {
        var (client, uri, text, _) = await OpenAsync("import proto \"\";" + Body);
        await using var _client = client;

        // The compile that opening the document scheduled has to have run and published first, or the
        // count moves underneath this for a reason that has nothing to do with completion.
        Assert.NotEmpty((await client.DiagnosticsAsync(uri)).Diagnostics);

        var before = client.Host.Compilations;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.NotEmpty((await CompleteAsync(client, uri, PositionOf(text, PathStart(text)))).Items);
        }

        Assert.Equal(before, client.Host.Compilations);
    }

    // ------------------------------------------------------- the negotiation

    [Fact]
    public async Task CompletionIsAdvertisedOnlyToAClientThatAsksAboutIt()
    {
        await using var asking = LanguageServerClient.Create();
        await using var withheld = LanguageServerClient.Create();

        var offered = await asking.InitializeAsync(LanguageServerClient.FullCapabilities, null);
        var silent = await withheld.InitializeAsync(
            new ClientCapabilities { TextDocument = new TextDocumentClientCapabilities() },
            null);

        Assert.NotNull(offered.Capabilities.CompletionProvider);
        Assert.Null(silent.Capabilities.CompletionProvider);
    }

    /// <summary>The two characters that open a path segment, which is when a client should ask.</summary>
    [Fact]
    public async Task TheTriggerCharactersAreTheOnesThatOpenAPathSegment()
    {
        await using var client = LanguageServerClient.Create();

        var offered = await client.InitializeAsync(LanguageServerClient.FullCapabilities, null);

        Assert.Equal(["\"", "/"], offered.Capabilities.CompletionProvider!.TriggerCharacters);
    }

    // ------------------------------------------------------- answering about text that is still there

    /// <summary>
    /// Spec 26.1: an answer about text the buffer has moved past is never published, and a request
    /// that can only answer about it is refused rather than answered. Driven against the provider
    /// because the window is the file-system walk, and nothing outside it can get an edit in there.
    /// </summary>
    [Fact]
    public Task ABufferEditedWhileTheWalkRanIsRefusedRatherThanAnswered()
        => RefusesWhen((documents, _, uri) => documents.Apply(
            uri,
            2,
            [new TextDocumentContentChangeEvent { Text = "import proto \"billing/\";" }]));

    /// <summary>
    /// Closing a document and reopening it starts the client's version numbering again at one, so a
    /// completion read at version one and answered afterwards compares equal to a buffer that may hold
    /// something else entirely. Spec 26.1 says a closed document's answers are abandoned; a version
    /// number cannot tell that this happened, and object identity can.
    /// </summary>
    [Fact]
    public Task ADocumentClosedAndReopenedAtTheSameVersionIsRefusedRatherThanAnswered()
        => RefusesWhen((documents, _, uri) =>
        {
            documents.Close(uri);
            documents.Open(uri, "protolang", 1, "import proto \"billing/\";");
        });

    /// <summary>
    /// And a configuration that moved while the walk ran, because the roots it searched are not the
    /// ones that now apply -- an include path removed mid-walk would otherwise be advertised as a
    /// place to import from. The compile scheduler asks the same question of the same generation.
    /// </summary>
    [Fact]
    public Task AConfigurationChangedWhileTheWalkRanIsRefusedRatherThanAnswered()
        => RefusesWhen((_, configuration, __) => configuration.ApplyPush(
            JsonSerializer.SerializeToElement(
                new Dictionary<string, object?> { ["protolang"] = new Dictionary<string, object?>() },
                LspJson.Options)));

    /// <summary>
    /// The shared shape of the three above: read a request, disturb the world during the one step that
    /// takes any time, and require a refusal rather than an answer about what was disturbed.
    /// </summary>
    private static async Task RefusesWhen(Action<DocumentStore, ConfigurationSync, DocumentUri> disturb)
    {
        var root = Workspace();
        const string Source = "import proto \"\";";

        var documents = new DocumentStore();
        var configuration = Configuration();
        var uri = Uri(Path.Combine(root, "source.protolang"));
        documents.Open(uri, "protolang", 1, Source);

        var provider = new CompletionProvider(documents, configuration, Loaders())
        {
            Enumerate = (directory, roots, token) =>
            {
                disturb(documents, configuration, uri);
                return SchemaCatalog.Enumerate(directory, roots, cancellationToken: token);
            },
        };

        var asked = provider.Read(At(uri, PositionOf(Source, PathStart(Source))));
        Assert.NotNull(asked);

        var refusal = await Assert.ThrowsAsync<JsonRpcException>(
            () => provider.AnswerAsync(asked, CancellationToken.None));

        Assert.Equal(ErrorCodes.ContentModified, refusal.Error.Code);
    }

    /// <summary>With the world left alone, so the three refusals above are about the disturbance.</summary>
    [Fact]
    public async Task AnAnswerAboutWhatWasReadIsSent()
    {
        var root = Workspace();
        const string Source = "import proto \"\";";

        var documents = new DocumentStore();
        var uri = Uri(Path.Combine(root, "source.protolang"));
        documents.Open(uri, "protolang", 1, Source);

        var provider = Provider(documents);
        var asked = provider.Read(At(uri, PositionOf(Source, PathStart(Source))));

        var offered = await provider.AnswerAsync(Assert.IsType<CompletionRequest>(asked), CancellationToken.None);

        Assert.Contains("shared.proto", offered.Items.Select(item => item.TextEdit!.NewText));
    }

    // ------------------------------------------------------- how much of it there can be at once

    /// <summary>
    /// A newer completion for a document replaces the outstanding one, so a client asking on every
    /// character cannot pile up walks against a slow root. The bound is the number of open documents,
    /// which is the same bound the compile queue has and for the same reason.
    /// </summary>
    [Fact]
    public async Task ANewerCompletionForADocumentSupersedesTheOneStillWalking()
    {
        using var gate = new Gate();

        var root = Workspace();
        const string Source = "import proto \"\";";

        var documents = new DocumentStore();
        var uri = Uri(Path.Combine(root, "source.protolang"));
        documents.Open(uri, "protolang", 1, Source);

        var provider = new CompletionProvider(documents, Configuration(), Loaders()) { Enumerate = gate.Walk };
        var at = At(uri, PositionOf(Source, PathStart(Source)));

        var superseded = new List<Task<CompletionList>>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            superseded.Add(provider.AnswerAsync(provider.Read(at)!, CancellationToken.None));

            // Short, because the way this fails is a walk that never starts: without supersession the
            // earlier ones hold every slot, and waiting the full patience for each would turn one
            // broken assumption into minutes.
            await gate.EnteredAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, provider.Outstanding);

        gate.Open();

        var last = superseded[^1];
        foreach (var answering in superseded[..^1])
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answering);
        }

        Assert.NotEmpty((await last).Items);
        Assert.Equal(0, provider.Outstanding);
    }

    /// <summary>
    /// And across documents a limit, because ten open files must not mean ten simultaneous walks of an
    /// include root. Supersession bounds one document; this bounds the rest.
    /// </summary>
    [Fact]
    public async Task ManyDocumentsAskingAtOnceNeverExceedTheConcurrencyLimit()
    {
        using var gate = new Gate();

        var root = Workspace();
        const string Source = "import proto \"\";";

        var documents = new DocumentStore();
        var provider = new CompletionProvider(documents, Configuration(), Loaders(), concurrency: 2)
        {
            Enumerate = gate.Walk,
        };

        var answering = new List<Task<CompletionList>>();
        for (var index = 0; index < 8; index++)
        {
            var uri = Uri(Path.Combine(root, $"source{index}.protolang"));
            documents.Open(uri, "protolang", 1, Source);

            answering.Add(provider.AnswerAsync(
                provider.Read(At(uri, PositionOf(Source, PathStart(Source))))!,
                CancellationToken.None));
        }

        // Two get in; the rest are waiting for a slot rather than holding one.
        await gate.EnteredAsync(TimeSpan.FromSeconds(5));
        await gate.EnteredAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, provider.InFlight);

        gate.Open();
        await Task.WhenAll(answering);

        Assert.True(
            provider.PeakInFlight <= 2,
            $"{provider.PeakInFlight} walks ran at once against a limit of two");
    }

    /// <summary>
    /// Which buffer a request is about is settled while messages are still being read in order, so a
    /// <c>didChange</c> queued behind the request cannot decide it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deferring the lookup does not fail every time -- it is a race between the reading worker taking
    /// the next message and a pool thread picking up the deferred work, and the worker usually wins,
    /// which is precisely the failure. So the run is repeated, and each repetition sends edits
    /// immediately behind the request to widen the window. The test is deterministic in the direction
    /// that matters: with the lookup ordered it cannot fail, because the read finishes before the next
    /// message is dequeued at all.
    /// </para>
    /// <para>
    /// Two things give the defect away. The walk is never reached, because the position measured
    /// against the newer and shorter text lands outside the string; or it is reached for the wrong
    /// directory. Both are asserted, because which one happens depends on where the read landed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheBufferACompletionIsAboutIsSettledBeforeTheNextMessageIsRead()
    {
        const string Asked = "import proto \"billing/\";";
        const string Edited = "import proto \"\";";

        var root = Workspace();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var gate = new Gate();

            var (client, uri, text, _) = await OpenAsync(Asked + Body, root: root);
            await using var _client = client;

            client.Host.Completion.Enumerate = gate.Walk;

            var completion = client.Ask(
                Methods.Completion,
                new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position = PositionOf(text, PathStart(text) + "billing/".Length),
                });

            for (var edit = 0; edit < 3; edit++)
            {
                client.Notify(
                    Methods.DidChange,
                    new DidChangeTextDocumentParams
                    {
                        TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = edit + 2 },
                        ContentChanges = [new TextDocumentContentChangeEvent { Text = Edited + Body }],
                    });
            }

            await gate.EnteredAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["billing/"], gate.Directories);

            // An ordered request behind the edits: its answer cannot come back until they have been
            // applied, so releasing the walk now means the walk is answering about text that moved.
            // Capped well under the client's own patience, because a server that has stopped reading
            // the wire should fail this in seconds rather than in half a minute per repetition.
            await client
                .RequestAsync(
                    Methods.SemanticTokensFull,
                    new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
                .WaitAsync(TimeSpan.FromSeconds(5));

            gate.Open();

            var answered = await client.AnswerToAsync(completion);

            Assert.Equal(ErrorCodes.ContentModified, answered.Error!.Code);
        }
    }

    /// <summary>A walk that announces itself and then waits, so a test can hold one open.</summary>
    private sealed class Gate : IDisposable
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly ManualResetEventSlim _open = new(false);
        private readonly ConcurrentQueue<string> _directories = new();

        /// <summary>Which directory each walk was asked about, in the order they started.</summary>
        public IReadOnlyList<string> Directories => [.. _directories];

        public SchemaListing Walk(string directory, IReadOnlyList<string> roots, CancellationToken cancellationToken)
        {
            _directories.Enqueue(directory);
            _entered.Release();
            _open.Wait(cancellationToken);

            return SchemaCatalog.Enumerate(directory, roots, cancellationToken: cancellationToken);
        }

        public async Task EnteredAsync(TimeSpan? patience = null)
            => Assert.True(
                await _entered.WaitAsync(patience ?? LanguageServerClient.Patience),
                "a walk that was expected to start never did");

        public void Open() => _open.Set();

        public void Dispose()
        {
            _open.Set();
            _entered.Dispose();
            _open.Dispose();
        }
    }

    // ------------------------------------------------------- what it holds while it answers

    /// <summary>
    /// The connection drains one queue with one worker, so a handler that opens a directory on that
    /// worker stops the server reading the wire for as long as the walk takes -- every edit, every
    /// close, and the cancellation that would have shortened it, all queued behind a list the user may
    /// already have dismissed.
    /// </summary>
    /// <remarks>
    /// Asked by holding a completion inside the file-system step and then requiring the server to
    /// answer something else. Semantic tokens is the something else because it needs nothing but the
    /// buffer, so a reply to it proves the worker is free rather than that the machine was quick.
    /// </remarks>
    [Fact]
    public async Task ACompletionStillWalkingDoesNotStopTheServerAnsweringAnythingElse()
    {
        using var walking = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);

        var (client, uri, text, _) = await OpenAsync("import proto \"\";" + Body);
        await using var _client = client;

        client.Host.Completion.Enumerate = (directory, roots, token) =>
        {
            walking.Release();
            release.Wait(LanguageServerClient.Patience);

            return SchemaCatalog.Enumerate(directory, roots, cancellationToken: token);
        };

        var completion = client.Ask(
            Methods.Completion,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = PositionOf(text, PathStart(text)),
            });

        Assert.True(await walking.WaitAsync(LanguageServerClient.Patience), "the completion never started");

        var classified = await client
            .RequestAsync(
                Methods.SemanticTokensFull,
                new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(classified.Deserialize<SemanticTokens>(LspJson.Options));

        release.Release();

        var answered = await client.AnswerToAsync(completion);
        Assert.NotEmpty(answered.Result.Deserialize<CompletionList>(LspJson.Options)!.Items);
    }

    /// <summary>
    /// And a client that withdraws the question stops the walk, rather than waiting for an answer to
    /// something it is no longer showing. A keystroke supersedes an open completion list, so this is
    /// the ordinary case rather than the exotic one.
    /// </summary>
    [Fact]
    public async Task ACompletionTheClientWithdrawsStopsWalkingRatherThanFinishing()
    {
        using var walking = new SemaphoreSlim(0);

        var (client, uri, text, _) = await OpenAsync("import proto \"\";" + Body);
        await using var _client = client;

        client.Host.Completion.Enumerate = (directory, roots, token) =>
        {
            walking.Release();

            // Until the client withdraws it, which is what the token carries.
            token.WaitHandle.WaitOne(LanguageServerClient.Patience);

            return SchemaCatalog.Enumerate(directory, roots, cancellationToken: token);
        };

        var completion = client.Ask(
            Methods.Completion,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = PositionOf(text, PathStart(text)),
            });

        Assert.True(await walking.WaitAsync(LanguageServerClient.Patience), "the completion never started");

        client.Notify(Methods.CancelRequest, new Dictionary<string, object?> { ["id"] = completion });

        var answered = await client.AnswerToAsync(completion);

        Assert.Equal(ErrorCodes.RequestCancelled, answered.Error!.Code);
    }

    // ------------------------------------------------------- what it reads to answer

    /// <summary>
    /// Completion settles where imports resolve and nothing else. The language policy lives in a file
    /// that has to be searched for and parsed, and this request runs per keystroke rather than per
    /// debounced compile -- so a refused configuration file must neither stop it nor be read by it.
    /// </summary>
    [Fact]
    public async Task ADocumentWhoseConfigurationFileWasRefusedStillCompletes()
    {
        var root = Workspace();
        File.WriteAllText(Path.Combine(root, ProjectConfig.FileName), "<this is not a configuration file");

        var (client, uri, text, _) = await OpenAsync("import proto \"\";" + Body, root: root);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text)));

        Assert.Contains("shared.proto", Paths(offered));
    }

    /// <summary>
    /// Where the file system folds case, a schema already imported under another spelling is the same
    /// schema. Asserted against <see cref="PathIdentity"/> rather than against a platform, so the test
    /// says the same thing everywhere and is right on both.
    /// </summary>
    [Fact]
    public async Task AnImportSpelledInAnotherCaseIsRecognisedWhereverPathsFoldCase()
    {
        const string Source = "import proto \"Shared.proto\";\nimport proto \"\";";
        var (client, uri, text, _) = await OpenAsync(Source + Body);
        await using var _client = client;

        var offered = await CompleteAsync(client, uri, PositionOf(text, PathStart(text, which: 1)));

        var shared = Assert.Single(offered.Items, item => item.TextEdit!.NewText == "shared.proto");

        Assert.Equal(
            !PathIdentity.IsCaseSensitive,
            shared.Detail!.Contains("already imported", StringComparison.Ordinal));
    }

    private static DocumentUri Uri(string path)
    {
        Assert.True(DocumentUri.TryParse(UriOf(path), out var uri));

        return uri!;
    }

    private static CompletionParams At(DocumentUri uri, Position position)
        => new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.Text },
            Position = position,
        };

    private static CompletionProvider Provider(DocumentStore documents)
        => new(documents, Configuration(), Loaders());

    /// <summary>
    /// A configuration that has never spoken to a client: no folders, no settings, so the only
    /// include root is the document's own directory.
    /// </summary>
    private static ConfigurationSync Configuration()
    {
        var log = new ServerLog();

        return new ConfigurationSync(new JsonRpcConnection(Stream.Null, Stream.Null, log), log);
    }

    private static LoaderPool Loaders() => new(new ServerLog());
}
