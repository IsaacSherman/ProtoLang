using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Completion driven by the schema and by the binder's own rules: after a dot, on a bare identifier,
/// in a type position, after <c>extend</c>, and inside a <c>test</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rule the whole feature rests on is that nothing is offered that would not bind, and it is not
/// checkable by reading lists. What checks it is applying each offered item and recompiling; those
/// tests arrive with the contexts they are about. What this file starts with is the question asked
/// first and answered before anything is offered -- which context, if any, the caret is in -- because
/// an answer produced in the wrong context is wrong however good the list is.
/// </para>
/// <para>
/// Driven through the provider rather than through the context types, which are internal. What a
/// caret resolved to is read off <see cref="CompletionRequest.Kind"/>, which exists so that this is
/// observable at all.
/// </para>
/// </remarks>
public class SchemaCompletionTests
{
    private const string Source =
        """
        import proto "invoice.proto";

        // A line comment mentioning items and InvoiceItem.
        extend InvoiceItem {
            fn label() -> string {
                var name: string = "quantity";
                return name;
            }

            /* A block comment mentioning quantity. */
            fn total() -> int64 {
                return quantity;
            }
        }
        """;

    private static ConfigurationSync Configuration()
    {
        var log = new ServerLog();

        return new ConfigurationSync(new JsonRpcConnection(Stream.Null, Stream.Null, log), log);
    }

    private static LoaderPool Loaders() => new(new ServerLog());

    private static (CompletionProvider Provider, DocumentStore Documents, DocumentUri Uri) Open(string text)
    {
        var documents = new DocumentStore();
        var path = Path.Combine(TestPaths.CreateTempDirectory(), "source.protolang");
        var uri = DocumentUri.Parse(new Uri(path).AbsoluteUri);

        documents.Open(uri, "protolang", 1, text);

        return (new CompletionProvider(documents, Configuration(), Loaders()), documents, uri);
    }

    private static CompletionParams At(DocumentUri uri, string text, int offset)
    {
        var line = 0;
        var character = 0;

        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                character = 0;
                continue;
            }

            character++;
        }

        return new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = new Position(line, character),
        };
    }

    private static CompletionContextKind? KindAt(string text, int offset)
    {
        var (provider, _, uri) = Open(text);

        return provider.Read(At(uri, text, offset))?.Kind;
    }

    private static int After(string text, string marker)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(offset >= 0, $"the fixture must contain '{marker}'");
        return offset + marker.Length;
    }

    // ------- where completion declines to say anything

    /// <summary>
    /// Comments are trivia and leave no token behind, so this is the case a scan over the token stream
    /// alone cannot see. Offering inside prose is what gets completion switched off.
    /// </summary>
    [Fact]
    public void ACaretInsideACommentIsInNoContextAtAll()
    {
        foreach (var marker in new[] { "// A line comment mention", "/* A block comment mention" })
        {
            var offset = After(Source, marker);

            Assert.Null(KindAt(Source, offset));
        }
    }

    [Fact]
    public void EveryOffsetInsideACommentIsInNoContextAtAll()
    {
        var start = Source.IndexOf("// A line comment", StringComparison.Ordinal);
        var end = Source.IndexOf('\n', start);

        Assert.True(start > 0 && end > start, "the fixture must hold a line comment");

        for (var offset = start; offset <= end - 1; offset++)
        {
            Assert.Null(KindAt(Source, offset));
        }
    }

    [Fact]
    public void ACaretInsideAnOrdinaryStringLiteralIsInNoContextAtAll()
        => Assert.Null(KindAt(Source, After(Source, "\"quant")));

    // ------- which context wins

    /// <summary>
    /// An import path is total and exclusive -- a caret inside one can be nothing else -- which is why
    /// it is decided before the general probe rather than carved out of it.
    /// </summary>
    [Fact]
    public void ACaretInsideAnImportPathIsAnImportPathRatherThanASchemaContext()
        => Assert.Equal(CompletionContextKind.ImportPath, KindAt(Source, After(Source, "import proto \"inv")));

    [Fact]
    public void ACaretOnACodeIdentifierIsASchemaContext()
        => Assert.Equal(CompletionContextKind.Schema, KindAt(Source, After(Source, "return quan")));

    [Fact]
    public void EveryOffsetInABufferIsEitherClassifiedOrDeclinedAndNeverThrows()
    {
        var (provider, _, uri) = Open(Source);
        var schema = 0;

        for (var offset = 0; offset <= Source.Length; offset++)
        {
            var asked = provider.Read(At(uri, Source, offset));

            if (asked?.Kind is CompletionContextKind.Schema)
            {
                schema++;
            }
        }

        Assert.True(schema > 20, $"most of a file is a schema context; only {schema} offsets were");
    }

    // ------- whether the client should ask again

    /// <summary>
    /// An import path list is one directory level, so typing a separator widens it and the client has
    /// to come back. A schema list does not change as the user types -- after a dot the answer is that
    /// message's members, whatever is typed next -- so it is finished when it is sent and the client
    /// filters it locally. Saying otherwise would put a compile on every keystroke.
    /// </summary>
    [Fact]
    public async Task AnImportPathListMayStillGrowAndASchemaListIsFinished()
    {
        var (provider, _, uri) = Open(Source);

        var path = provider.Read(At(uri, Source, After(Source, "import proto \"inv")));
        var schema = provider.Read(At(uri, Source, After(Source, "return quan")));

        Assert.NotNull(path);
        Assert.NotNull(schema);

        Assert.True(
            (await provider.AnswerAsync(path!, CancellationToken.None)).IsIncomplete,
            "an import path list is one directory level and typing a separator widens it");

        Assert.False(
            (await provider.AnswerAsync(schema!, CancellationToken.None)).IsIncomplete,
            "a schema list does not change as the user types, so the client need not ask again");
    }

    // ------- what a dot offers

    /// <summary>
    /// One directory holding a copy of the schemas, shared by every test in this class.
    /// </summary>
    /// <remarks>
    /// Shared, together with <see cref="Pool"/>, because otherwise each test is a cold descriptor
    /// cache and shells out to protoc: a dozen protoc runs where one will do. That is most of this
    /// class's wall time, and enough contention during a full suite run to push tests elsewhere past
    /// their timeouts -- which is how this came to be shared rather than as a matter of taste.
    /// </remarks>
    private static readonly Lazy<string> Schemas = new(() =>
    {
        var directory = TestPaths.CreateTempDirectory();

        foreach (var proto in Directory.GetFiles(TestPaths.FixtureProtoDirectory, "*.proto"))
        {
            File.Copy(proto, Path.Combine(directory, Path.GetFileName(proto)));
        }

        return directory;
    });

    /// <summary>One loader pool, so one descriptor cache, for every test in this class.</summary>
    private static readonly Lazy<LoaderPool> Pool = new(Loaders);

    private static int _sources;

    /// <summary>
    /// The source is written beside the schemas, so the document's own directory is the include root
    /// and no configuration has to be pushed to make imports resolve.
    /// </summary>
    private static (CompletionProvider Provider, DocumentUri Uri, string Text) Beside(string body)
    {
        var text = "import proto \"fixtures.proto\";\n\n" + body;
        var documents = new DocumentStore();

        // A name of its own per test, so that sharing the directory does not mean sharing a file.
        var name = $"source{Interlocked.Increment(ref _sources)}.protolang";
        var uri = DocumentUri.Parse(new Uri(Path.Combine(Schemas.Value, name)).AbsoluteUri);

        documents.Open(uri, "protolang", 1, text);

        return (new CompletionProvider(documents, Configuration(), Pool.Value), uri, text);
    }

    /// <summary>What is offered at the caret marked by the end of <paramref name="marker"/>.</summary>
    private static async Task<IReadOnlyList<CompletionItem>> OfferedAsync(string body, string marker)
    {
        var (provider, uri, text) = Beside(body);
        var asked = provider.Read(At(uri, text, After(text, marker)));

        Assert.NotNull(asked);

        return (await provider.AnswerAsync(asked!, CancellationToken.None)).Items;
    }

    private static IReadOnlyList<string> Labels(IReadOnlyList<CompletionItem> items)
        => [.. items.Select(item => item.Label)];

    [Fact]
    public async Task ADotAfterAMessageValuedExpressionOffersEveryFieldOfThatMessage()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f(other: Outer) -> int64 {\n        return other.\n    }\n}\n",
            "return other.");

        Assert.Contains("count", Labels(offered));
        Assert.Contains("label", Labels(offered));
        Assert.Contains("inner", Labels(offered));

        // Every field of the message, and nothing that is not a field or a method declared on it --
        // the method being 'f' itself, which is as reachable through this receiver as any other.
        Assert.All(
            offered,
            item => Assert.True(
                item.Kind is CompletionItemKind.Field or CompletionItemKind.Method,
                $"'{item.Label}' is neither a field of the receiver nor a method on it"));
    }

    /// <summary>
    /// Reading a map is PL0038, so a map field is a name that resolves and is then refused. Offering
    /// it would be the completion list breaking its one promise.
    /// </summary>
    [Fact]
    public async Task AMapFieldIsNeverOfferedAfterADot()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f(m: Mapped) -> int64 {\n        return m.\n    }\n}\n",
            "return m.");

        Assert.Contains("count", Labels(offered));
        Assert.DoesNotContain("tags", Labels(offered));
    }

    /// <summary>
    /// There is no member access into a repetition -- only 'for x in ...' -- so the element type's
    /// members are names that cannot be written where the caret is.
    /// </summary>
    [Fact]
    public async Task ADotAfterARepeatedFieldOffersNothingRatherThanTheElementTypesMembers()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f() -> int64 {\n        return nested_values.\n    }\n}\n",
            "return nested_values.");

        Assert.Empty(offered);
    }

    [Fact]
    public async Task ADotAfterAnEnumTypeNameOffersThatEnumsValues()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f() -> int64 {\n        return TopLevelStatus.\n    }\n}\n",
            "return TopLevelStatus.");

        Assert.Contains("TOP_LEVEL_STATUS_OK", Labels(offered));
        Assert.Contains("OTHER_RESULT", Labels(offered));
        Assert.All(offered, item => Assert.Equal(CompletionItemKind.EnumMember, item.Kind));
    }

    [Fact]
    public async Task ADotAfterAMessageValuedExpressionOffersTheMethodsDeclaredOnIt()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn helper(scale: int64) -> int64 { return count * scale; }\n\n"
                + "    fn f(other: Outer) -> int64 {\n        return other.\n    }\n}\n",
            "return other.");

        // Both of them: the helper, and 'f' itself, which is declared on the same receiver and is
        // reachable through it like any other method.
        var method = Assert.Single(offered, item => item.Label == "helper()");

        Assert.Equal(CompletionItemKind.Method, method.Kind);
        Assert.Equal("fn helper(scale: int64) -> int64", method.Detail);
        Assert.Contains("f()", Labels(offered));
    }

    /// <summary>
    /// The binder settles this before completion sees it: a name in scope as a value wins over an
    /// enum type spelled the same way (spec 12), so the receiver here is the local, whose type has no
    /// members at all. Completion inherits the rule rather than imitating it.
    /// </summary>
    [Fact]
    public async Task AValueNameWinsOverAnEnumTypeOfTheSameSpelling()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f() -> int64 {\n        var TopLevelStatus: int64 = 1;\n"
                + "        return TopLevelStatus.\n    }\n}\n",
            "return TopLevelStatus.");

        Assert.Empty(offered);
    }

    /// <summary>
    /// A method named without being called is PL0040, so the parentheses are part of the item rather
    /// than something the author is left to add.
    /// </summary>
    [Fact]
    public async Task AMethodIsOfferedAsACallRatherThanAsAName()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn helper() -> int64 { return count; }\n\n"
                + "    fn f(other: Outer) -> int64 {\n        return other.\n    }\n}\n",
            "return other.");

        Assert.All(
            offered.Where(item => item.Kind == CompletionItemKind.Method),
            item => Assert.EndsWith("()", item.Label, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryItemOfferedAfterADotCarriesItsResolvedTypeAsDetail()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f(other: Outer) -> int64 {\n        return other.\n    }\n}\n",
            "return other.");

        Assert.NotEmpty(offered);
        Assert.All(offered, item => Assert.False(string.IsNullOrWhiteSpace(item.Detail)));

        var count = Assert.Single(offered, item => item.Label == "count");

        Assert.Equal("int64", count.Detail);
    }

    [Fact]
    public async Task AFieldWhoseSchemaCarriesACommentOffersItAsDocumentation()
    {
        var offered = await OfferedAsync(
            "extend Outer {\n    fn f(other: Outer) -> int64 {\n        return other.\n    }\n}\n",
            "return other.");

        var documented = Assert.Single(offered, item => item.Label == "small_count");

        Assert.Contains("Narrower than count", documented.Documentation ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list is declared complete, so a client re-applies an item it already holds after the user
    /// types more of the name. An edit range covering only the prefix typed when the list was built
    /// would leave the rest of the old name behind -- 'counount' from accepting 'count' at 'co|unt'.
    /// </summary>
    [Fact]
    public async Task EveryItemReplacesTheWholeIdentifierUnderTheCaretRatherThanThePrefixTypedSoFar()
    {
        const string body = "extend Outer {\n    fn f(other: Outer) -> int64 {\n        return other.count;\n    }\n}\n";

        var (provider, uri, text) = Beside(body);
        var name = text.IndexOf("count;", StringComparison.Ordinal);

        Assert.True(name > 0, "the fixture must read a field through a receiver");

        // Two characters in, which is where a client re-asks while the name is half typed.
        var asked = provider.Read(At(uri, text, name + 2));

        Assert.NotNull(asked);

        var offered = (await provider.AnswerAsync(asked!, CancellationToken.None)).Items;

        Assert.NotEmpty(offered);

        var lines = new LineMap(text);
        var start = lines.PositionOf(name);
        var end = lines.PositionOf(name + "count".Length);

        Assert.All(
            offered,
            item =>
            {
                Assert.NotNull(item.TextEdit);
                Assert.Equal(start.Line - 1, item.TextEdit!.Range.Start.Line);
                Assert.Equal(start.Column - 1, item.TextEdit.Range.Start.Character);
                Assert.Equal(end.Column - 1, item.TextEdit.Range.End.Character);
            });
    }

    // ------- a buffer that does not parse is the ordinary case

    /// <summary>
    /// The state this feature is always invoked in: the dot has just been typed, so there is no member
    /// name, no semicolon, and no closing brace. Nothing here may depend on the file parsing.
    /// </summary>
    [Fact]
    public void ATrailingDotWithNothingAfterItIsASchemaContext()
    {
        const string unfinished = "import proto \"invoice.proto\";\n\nextend InvoiceItem {\n    fn f() -> int64 {\n        return quantity.";

        Assert.Equal(CompletionContextKind.Schema, KindAt(unfinished, unfinished.Length));
    }

    [Fact]
    public void AnUnterminatedStringIsStillNoPlaceForASchemaName()
    {
        const string unterminated = "extend InvoiceItem {\n    fn f() -> string { return \"still typing";

        Assert.Null(KindAt(unterminated, unterminated.Length));
    }
}
