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
