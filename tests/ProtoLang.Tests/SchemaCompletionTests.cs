using ProtoLang.Binding;
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

    // ------- the promise, checked by keeping it

    /// <summary>
    /// Several receivers of different shapes in one file, including two that must offer nothing, so
    /// the sweep meets the cases that would be wrong rather than only the ones that are easy.
    /// </summary>
    /// <remarks>
    /// The second <c>extend</c> block is load-bearing rather than decoration. With one receiver in the
    /// file, "offer the methods of this receiver" and "offer every method in the file" are the same
    /// list, and a sweep over such a fixture passes whichever one the code does -- which is exactly
    /// what it did until a mutation went unnoticed and said so.
    /// </remarks>
    private const string Reachable =
        """
        extend Mapped {
            fn onlyOnMapped() -> int64 { return count; }
        }

        extend Outer {
            fn helper(scale: int64) -> int64 { return count * scale; }

            fn f(other: Outer, mapped: Mapped) -> int64 {
                var here: Inner = other.inner;
                return other.count + mapped.count + here.deep + nested_values. + TopLevelStatus. + other.;
            }
        }
        """;

    private static DescriptorLoader Loader()
    {
        Assert.True(Pool.Value.TryGet(null, out var loader, out _), "the tests need a protoc to compile against");
        return loader!;
    }

    private static async Task<IReadOnlyList<AppliedItem>> SweepAsync(string body)
    {
        var (provider, uri, text) = Beside(body);

        return await CompletionProbe.SweepAsync(
            provider, uri, text, CompletionProbe.AfterEveryDot(text), uri.Path!, Loader());
    }

    /// <summary>
    /// The rule the whole feature rests on, checked by keeping it rather than by reading lists: every
    /// item, accepted, must produce a name that resolves to something.
    /// </summary>
    [Fact]
    public async Task EveryItemOfferedAfterADotBindsWhenItIsAccepted()
    {
        var applied = await SweepAsync(Reachable);

        Assert.True(applied.Count > 20, $"the sweep must apply something; it applied {applied.Count}");

        foreach (var attempt in applied)
        {
            var refused = attempt.About
                .Where(diagnostic => CompletionProbe.DidNotBind.Contains(diagnostic.Code))
                .ToList();

            Assert.True(
                refused.Count == 0,
                $"accepting '{attempt.Item.Label}' at offset {attempt.Caret} produced "
                    + string.Join(", ", refused.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")));
        }
    }

    /// <summary>
    /// Without this the sweep is a claim rather than a measurement: one recompile per item across a
    /// corpus is thousands of compilations, and a loader per compilation would turn a suite that
    /// takes minutes into one that takes hours.
    /// </summary>
    [Fact]
    public async Task TheSweepRunsProtocOnceHoweverManyItemsItApplies()
    {
        var before = Loader().ProtocInvocations;
        var applied = await SweepAsync(Reachable);

        Assert.NotEmpty(applied);
        Assert.True(
            Loader().ProtocInvocations - before <= 1,
            $"the sweep applied {applied.Count} items and started "
                + $"{Loader().ProtocInvocations - before} protoc processes");
    }

    /// <summary>
    /// A buffer mid-edit is the state completion is invoked in, so a sweep that only ever met
    /// well-formed files would be a sweep over the easy half.
    /// </summary>
    [Fact]
    public async Task EveryItemOfferedInABufferThatDoesNotParseBindsWhenItIsAccepted()
    {
        var applied = await SweepAsync(
            "extend Outer {\n    fn f(other: Outer) -> int64 {\n        return other.");

        Assert.NotEmpty(applied);
        Assert.All(
            applied,
            attempt => Assert.DoesNotContain(
                attempt.About,
                diagnostic => CompletionProbe.DidNotBind.Contains(diagnostic.Code)));
    }

    // ------- what a bare identifier offers

    /// <summary>
    /// Written with explicit newlines rather than as a raw string literal, so a marker containing one
    /// matches whatever line endings the file itself is stored with.
    /// </summary>
    private const string Scoped =
        "extend Outer {\n"
        + "    fn helper(scale: int64) -> int64 { return count * scale; }\n"
        + "\n"
        + "    fn f(given: int64) -> int64 {\n"
        + "        var local: int64 = 1;\n"
        + "        for each in nested_values {\n"
        + "            local = local + 1;\n"
        + "        }\n"
        + "        return local;\n"
        + "    }\n"
        + "}\n";

    [Fact]
    public async Task ABareIdentifierOffersTheLocalsAndParametersInScope()
    {
        var offered = await OfferedAsync(Scoped, "return loc");

        Assert.Contains("local", Labels(offered));
        Assert.Contains("given", Labels(offered));
    }

    /// <summary>
    /// The language permits a bare field reference against the implicit receiver, so the receiver's
    /// fields are names that bind exactly where a local does.
    /// </summary>
    [Fact]
    public async Task ABareIdentifierOffersTheFieldsOfTheImplicitReceiver()
    {
        var offered = await OfferedAsync(Scoped, "return loc");

        Assert.Contains("count", Labels(offered));
        Assert.Contains("label", Labels(offered));
    }

    /// <summary>
    /// A bare name resolves against the implicit receiver, so 'helper()' binds -- while 'helper'
    /// alone is PL0037, since BindName never looks at methods. The parentheses are what make the item
    /// one that binds.
    /// </summary>
    [Fact]
    public async Task ABareIdentifierOffersMethodsAsCallsRatherThanAsNames()
    {
        var offered = await OfferedAsync(Scoped, "return loc");

        Assert.Contains("helper()", Labels(offered));
        Assert.DoesNotContain("helper", Labels(offered));
    }

    /// <summary>
    /// ScopeAt already drops a map field, for the reason the binder refuses one. Asserted here as
    /// well as after a dot because the two contexts reach the field set by different routes.
    /// </summary>
    [Fact]
    public async Task ABareIdentifierNeverOffersAMapField()
    {
        var offered = await OfferedAsync(
            "extend Mapped {\n    fn f() -> int64 {\n        return count;\n    }\n}\n",
            "return cou");

        Assert.Contains("count", Labels(offered));
        Assert.DoesNotContain("tags", Labels(offered));
    }

    /// <summary>
    /// Where a statement can begin, both sets apply. Part-way through an expression only the
    /// expression starters can go, because 'return return;' is not something an author can accept.
    /// </summary>
    [Fact]
    public async Task AKeywordIsOfferedOnlyWhereTheGrammarWouldTakeIt()
    {
        var starting = await OfferedAsync(Scoped, "var local: int64 = 1;\n");
        var midExpression = await OfferedAsync(Scoped, "return loc");

        foreach (var keyword in new[] { "var", "return", "if", "while", "for" })
        {
            Assert.Contains(keyword, Labels(starting));
            Assert.DoesNotContain(keyword, Labels(midExpression));
        }

        // The other way round, and exclusively so. A caret starting a statement sits in front of
        // whatever is already written, and an operator accepted there swallows the next name as its
        // operand -- 'has' before 'local = ...' asks for the presence of a local, which does not bind.
        foreach (var keyword in new[] { "has", "not", "true", "false" })
        {
            Assert.DoesNotContain(keyword, Labels(starting));
            Assert.Contains(keyword, Labels(midExpression));
        }
    }

    /// <summary>
    /// They are statements only inside a loop, so offering them elsewhere offers something the parser
    /// takes and the binder then refuses.
    /// </summary>
    [Fact]
    public async Task BreakAndContinueAreOfferedOnlyInsideALoop()
    {
        var outside = await OfferedAsync(Scoped, "var local: int64 = 1;\n");
        var inside = await OfferedAsync(Scoped, "local = local + 1;\n");

        Assert.DoesNotContain("break", Labels(outside));
        Assert.DoesNotContain("continue", Labels(outside));
        Assert.Contains("break", Labels(inside));
        Assert.Contains("continue", Labels(inside));
    }

    /// <summary>
    /// The loop binding is in scope for the body and nowhere else, which the scope query already
    /// knows; completion offering it outside would be offering a name that does not resolve.
    /// </summary>
    [Fact]
    public async Task ALoopBindingIsOfferedInsideItsLoopAndNotOutsideIt()
    {
        var inside = await OfferedAsync(Scoped, "local = local + 1;");
        var outside = await OfferedAsync(Scoped, "return loc");

        Assert.Contains("each", Labels(inside));
        Assert.DoesNotContain("each", Labels(outside));
    }

    [Fact]
    public async Task EveryNameOfferedForABareIdentifierCarriesItsResolvedTypeAsDetail()
    {
        var offered = await OfferedAsync(Scoped, "return loc");
        var local = Assert.Single(offered, item => item.Label == "local");

        Assert.Equal(CompletionItemKind.Variable, local.Kind);
        Assert.Equal("int64", local.Detail);
    }

    /// <summary>
    /// A name the author wrote three lines up is more likely to be what they are typing than a field
    /// of the receiver, and a keyword is likelier still to be neither.
    /// </summary>
    [Fact]
    public async Task WhatTheAuthorDeclaredSortsAheadOfTheSchemaAndBothAheadOfKeywords()
    {
        var offered = await OfferedAsync(Scoped, "return loc");

        string Sort(string label) => Assert.Single(offered, item => item.Label == label).SortText!;

        Assert.True(
            string.CompareOrdinal(Sort("local"), Sort("count")) < 0,
            "a local sorts ahead of a field of the receiver");

        // 'has' rather than a statement keyword, because the caret here is part-way through an
        // expression and a statement keyword is correctly not offered at all.
        Assert.True(
            string.CompareOrdinal(Sort("count"), Sort("has")) < 0,
            "a field of the receiver sorts ahead of a keyword");
    }

    /// <summary>
    /// The same promise, over the context where most of the items are: every name in scope, every
    /// method, and every keyword, applied at every position one could be typed.
    /// </summary>
    [Fact]
    public async Task EveryItemOfferedForABareIdentifierBindsWhenItIsAccepted()
    {
        var (provider, uri, text) = Beside(Scoped);

        var applied = await CompletionProbe.SweepAsync(
            provider,
            uri,
            text,
            CompletionProbe.AtEveryNameAndStatementStart(text),
            uri.Path!,
            Loader());

        Assert.True(applied.Count > 100, $"the sweep must apply plenty; it applied {applied.Count}");

        foreach (var attempt in applied)
        {
            var refused = attempt.About
                .Where(diagnostic => CompletionProbe.DidNotBind.Contains(diagnostic.Code))
                .ToList();

            Assert.True(
                refused.Count == 0,
                $"accepting '{attempt.Item.Label}' at offset {attempt.Caret} produced "
                    + string.Join(", ", refused.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")));
        }
    }

    // ------- what a type position offers

    private const string Typed =
        "extend Outer {\n"
        + "    fn f(given: int64) -> int64 {\n"
        + "        var local: Inner = inner;\n"
        + "        return count;\n"
        + "    }\n"
        + "}\n";

    [Fact]
    public async Task ATypePositionOffersEveryScalarSpelling()
    {
        var offered = await OfferedAsync(Typed, "var local: Inn");

        foreach (var scalar in new[] { "int32", "int64", "uint32", "uint64", "double", "float", "bool", "string", "bytes" })
        {
            Assert.Contains(scalar, Labels(offered));
        }
    }

    /// <summary>It is a return-type marker and nothing else, so it belongs only where one goes.</summary>
    [Fact]
    public async Task VoidIsOfferedAsAReturnTypeAndNowhereElse()
    {
        var returning = await OfferedAsync(Typed, "fn f(given: int64) -> int");
        var declaring = await OfferedAsync(Typed, "var local: Inn");

        Assert.Contains("void", Labels(returning));
        Assert.DoesNotContain("void", Labels(declaring));
    }

    [Fact]
    public async Task ATypePositionOffersTheImportedMessagesAndEnumsUnderBothSpellings()
    {
        var offered = await OfferedAsync(Typed, "var local: Inn");

        Assert.Contains("Outer", Labels(offered));
        Assert.Contains("protolang.tests.Outer", Labels(offered));
        Assert.Contains("TopLevelStatus", Labels(offered));
        Assert.Contains("protolang.tests.TopLevelStatus", Labels(offered));
    }

    /// <summary>
    /// Only by descending: FileDescriptor.MessageTypes lists the top level alone, so a nested enum is
    /// the first thing an independent walk of the descriptors omits.
    /// </summary>
    [Fact]
    public async Task ATypePositionOffersTypesNestedInsideAMessage()
    {
        var offered = await OfferedAsync(Typed, "var local: Inn");

        Assert.Contains("Inner", Labels(offered));
        Assert.Contains("protolang.tests.Outer.Inner", Labels(offered));
        Assert.Contains("protolang.tests.Outer.Nested", Labels(offered));
    }

    /// <summary>
    /// Two packages declaring one simple name make it PL0074 to write unqualified, so offering it
    /// would offer a name the compiler is about to refuse -- and that diagnostic's own help says to
    /// qualify it. The simple name stays as filter text so typing it still finds the qualified forms.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousSimpleTypeNameIsOfferedOnlyInItsQualifiedForms()
    {
        var offered = await OfferedAsync(
            "import proto \"ambiguous_enums.proto\";\n\n" + Typed, "var local: Inn");

        Assert.DoesNotContain("Kind", Labels(offered));
        Assert.Contains("protolang.tests.ambiguous.First.Kind", Labels(offered));
        Assert.Contains("protolang.tests.ambiguous.Second.Kind", Labels(offered));

        var qualified = Assert.Single(
            offered, item => item.Label == "protolang.tests.ambiguous.First.Kind");

        Assert.Equal("Kind", qualified.FilterText);
    }

    [Fact]
    public async Task AMessageAndAnEnumAreDistinguishableInATypePosition()
    {
        var offered = await OfferedAsync(Typed, "var local: Inn");

        Assert.Equal(CompletionItemKind.Class, Assert.Single(offered, item => item.Label == "Outer").Kind);
        Assert.Equal(
            CompletionItemKind.Enum, Assert.Single(offered, item => item.Label == "TopLevelStatus").Kind);
    }

    /// <summary>
    /// A type position is where the scope query deliberately answers nothing, so a name in scope
    /// offered here would be one the binder cannot resolve as a type.
    /// </summary>
    [Fact]
    public async Task ATypePositionNeverOffersTheNamesThatAreInScope()
    {
        var offered = await OfferedAsync(Typed, "var local: Inn");

        Assert.DoesNotContain("given", Labels(offered));
        Assert.DoesNotContain("count", Labels(offered));
        Assert.DoesNotContain("return", Labels(offered));
    }

    [Fact]
    public async Task EveryItemOfferedInATypePositionBindsWhenItIsAccepted()
    {
        var (provider, uri, text) = Beside(Typed);
        var carets = new[] { After(text, "var local: Inn"), After(text, "fn f(given: int") };

        var applied = await CompletionProbe.SweepAsync(
            provider, uri, text, carets, uri.Path!, Loader());

        Assert.True(applied.Count > 20, $"the sweep must apply something; it applied {applied.Count}");

        foreach (var attempt in applied)
        {
            var refused = attempt.About
                .Where(diagnostic => CompletionProbe.DidNotBind.Contains(diagnostic.Code))
                .ToList();

            Assert.True(
                refused.Count == 0,
                $"accepting '{attempt.Item.Label}' at offset {attempt.Caret} produced "
                    + string.Join(", ", refused.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")));
        }
    }

    // ------- what an extend receiver and a test fixture offer

    private const string Fixtured =
        "extend Outer {\n"
        + "    fn scaled(factor: int64, other: int64) -> int64 { return count * factor; }\n"
        + "}\n"
        + "\n"
        + "test Outer.scaled \"scales\" {\n"
        + "    receiver {\n"
        + "        count = 2;\n"
        + "    }\n"
        + "    arg factor = 3;\n"
        + "    expect return 6;\n"
        + "}\n";

    [Fact]
    public async Task AfterExtendOnlyMessageNamesAreOffered()
    {
        var offered = await OfferedAsync(Fixtured, "extend Out");

        Assert.Contains("Outer", Labels(offered));
        Assert.Contains("protolang.tests.Outer", Labels(offered));
        Assert.All(offered, item => Assert.Equal(CompletionItemKind.Class, item.Kind));
    }

    /// <summary>
    /// ResolveMessage never looks at enums, so one offered here would be PL0021 the moment it was
    /// accepted.
    /// </summary>
    [Fact]
    public async Task AnEnumIsNeverOfferedAfterExtend()
    {
        var offered = await OfferedAsync(Fixtured, "extend Out");

        Assert.DoesNotContain("TopLevelStatus", Labels(offered));
        Assert.DoesNotContain("Nested", Labels(offered));
    }

    /// <summary>
    /// A receiver is ambiguous against messages alone, so a message whose simple name an enum shares
    /// is still unambiguous as a receiver. Answering with the type rule -- which counts enums too --
    /// would withhold a name the compiler accepts, and this is the one fixture shape that can tell
    /// the two rules apart at all.
    /// </summary>
    [Fact]
    public async Task AMessageWhoseSimpleNameAnEnumSharesIsStillOfferedAfterExtend()
    {
        var offered = await OfferedAsync(
            "import proto \"shared_name.proto\";\n\n" + Fixtured, "extend Out");

        Assert.Contains("Shape", Labels(offered));
        Assert.Contains("protolang.tests.shared.Shape", Labels(offered));
    }

    /// <summary>
    /// The other half of the same fixture, and the other answer. In a type position messages and
    /// enums share one name space, so the very name that is unambiguous as a receiver is PL0074 here
    /// and may only be written qualified.
    /// </summary>
    [Fact]
    public async Task TheSameNameIsAmbiguousInATypePositionAndNotAsAReceiver()
    {
        var body = "import proto \"shared_name.proto\";\n\n"
            + "extend Shape {\n    fn f() -> int64 {\n        var local: Shape = sides;\n"
            + "        return sides;\n    }\n}\n";

        var asAType = await OfferedAsync(body, "var local: Sha");
        var asAReceiver = await OfferedAsync(body, "extend Sha");

        Assert.DoesNotContain("Shape", Labels(asAType));
        Assert.Contains("protolang.tests.shared.Shape", Labels(asAType));
        Assert.Contains("protolang.tests.shared.Holder.Shape", Labels(asAType));

        Assert.Contains("Shape", Labels(asAReceiver));
    }

    [Fact]
    public async Task ATestFixtureOffersTheFieldsOfTheMessageBeingBuilt()
    {
        var offered = await OfferedAsync(Fixtured, "        count = 2;\n");

        Assert.Contains("label", Labels(offered));
        Assert.Contains("inner", Labels(offered));
        Assert.All(offered, item => Assert.Equal(CompletionItemKind.Field, item.Kind));
    }

    /// <summary>A singular field written twice is PL0061, so one already given a value is spent.</summary>
    [Fact]
    public async Task AFieldAlreadySetInAFixtureIsNotOfferedAgain()
    {
        var offered = await OfferedAsync(Fixtured, "        count = 2;\n");

        Assert.DoesNotContain("count", Labels(offered));
    }

    /// <summary>A map in a fixture is PL0060 rather than PL0038 -- a different code, the same refusal.</summary>
    [Fact]
    public async Task ATestFixtureNeverOffersAMapField()
    {
        var offered = await OfferedAsync(
            "extend Mapped {\n    fn f() -> int64 { return count; }\n}\n"
                + "\ntest Mapped.f \"counts\" {\n    receiver {\n        count = 1;\n    }\n"
                + "    expect return 1;\n}\n",
            "        count = 1;\n");

        Assert.DoesNotContain("tags", Labels(offered));
    }

    [Fact]
    public async Task AnArgOffersTheParametersOfTheMethodUnderTest()
    {
        var offered = await OfferedAsync(Fixtured, "arg fact");

        Assert.Contains("factor", Labels(offered));
    }

    /// <summary>An argument supplied twice is PL0065, so one already written is spent.</summary>
    [Fact]
    public async Task AnArgumentAlreadySuppliedIsNotOfferedAgain()
    {
        var offered = await OfferedAsync(
            Fixtured.Replace("arg factor = 3;", "arg factor = 3;\n    arg o", StringComparison.Ordinal),
            "    arg o");

        Assert.Contains("other", Labels(offered));
        Assert.DoesNotContain("factor", Labels(offered));
    }

    /// <summary>
    /// BindTest returns null when the target does not resolve, so there is no IrTest at all -- and
    /// nothing to say until the compiler knows which message and which method the fixture is for.
    /// </summary>
    [Fact]
    public async Task ATestWhoseTargetDoesNotResolveOffersNothingInsideIt()
    {
        var offered = await OfferedAsync(
            "test NoSuchMessage.nothing \"unresolved\" {\n    receiver {\n        cou\n    }\n"
                + "    expect return 1;\n}\n",
            "        cou");

        Assert.Empty(offered);
    }

    [Fact]
    public async Task EveryItemOfferedInATestOrAfterExtendBindsWhenItIsAccepted()
    {
        var (provider, uri, text) = Beside(Fixtured);

        var carets = new[]
        {
            After(text, "extend Out"),
            After(text, "        count = 2;\n"),
            After(text, "arg fact"),

            // Inside the expectation, which is neither a fixture field nor an argument, and where
            // bare names resolve against nothing at all: the binder binds a test's expressions with
            // no scope and no implicit receiver fields.
            After(text, "expect return 6"),
        };

        var applied = await CompletionProbe.SweepAsync(provider, uri, text, carets, uri.Path!, Loader());

        Assert.True(applied.Count > 10, $"the sweep must apply something; it applied {applied.Count}");

        foreach (var attempt in applied)
        {
            var refused = attempt.About
                .Where(diagnostic => CompletionProbe.DidNotBind.Contains(diagnostic.Code))
                .ToList();

            Assert.True(
                refused.Count == 0,
                $"accepting '{attempt.Item.Label}' at offset {attempt.Caret} produced "
                    + string.Join(", ", refused.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")));
        }
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
