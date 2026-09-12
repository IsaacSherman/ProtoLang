using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.Tests;

/// <summary>
/// Who uses this, and where else does this name appear right here: find-all-references and
/// occurrence highlighting, which are one question rendered two ways.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the providers rather than through a server, for the reason <see cref="HoverTests"/>
/// gives: what is being asserted is the answer. The wire and the capability negotiation belong to
/// <see cref="LanguageServerTests"/>.
/// </para>
/// <para>
/// Expectations are computed by searching the fixture for the name, never written as counts, so a
/// test goes on meaning what it meant after somebody edits the source above it.
/// </para>
/// </remarks>
public class ReferenceTests
{
    /// <summary>
    /// One of everything a name can be, with two same-named locals in sibling blocks.
    /// </summary>
    /// <remarks>
    /// Deliberately free of any name written inside a string literal: that case is real and is worth
    /// its own test, but leaving it here would mean every sweep below had to carve it out and would
    /// be re-implementing the lexer to do so.
    /// </remarks>
    private const string Source =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn scaled(scale: int64) -> int64 {
                var total: int64 = count * scale;
                total = total + 1;

                if total > 0 {
                    var shadowed: int64 = total;
                    total = total + shadowed;
                }

                for value in nested_values {
                    var shadowed: int64 = 2;
                    total = total + shadowed;
                }

                return total;
            }

            fn held() -> int64 {
                var kept: Inner = inner;

                return count;
            }

            fn state() -> TopLevelStatus {
                return TopLevelStatus.TOP_LEVEL_STATUS_OK;
            }
        }

        test Outer.scaled "doubles what it is given" {
            receiver { count = 2; }
            arg scale = 3;
            expect return 12;
        }
        """;

    // ------------------------------------------------------- driving the providers

    /// <summary>One open document and both providers over one compilation of it.</summary>
    /// <remarks>
    /// <para>
    /// Opened once per test rather than once per question. Two openings are two temp directories and
    /// therefore two URIs, so a test that asked twice and compared the answers would be comparing
    /// locations in two different files and finding them all distinct -- which is a test that passes
    /// or fails for a reason that has nothing to do with references.
    /// </para>
    /// <para>
    /// Both providers share one <see cref="DocumentSemantics"/>, which is what the server does, and is
    /// what makes the round trip below a statement about two readings of one model.
    /// </para>
    /// </remarks>
    private sealed class Session
    {
        private readonly ReferenceProvider _references;
        private readonly HighlightProvider _highlights;

        public Session(string text)
        {
            var (documents, uri) = EditorFixture.Open(text);
            var configuration = EditorFixture.Configuration();
            var loaders = EditorFixture.Loaders();

            Text = text;
            Uri = uri;
            Documents = documents;
            Semantics = new DocumentSemantics(loaders);

            _references = new ReferenceProvider(documents, configuration, loaders, semantics: Semantics);
            _highlights = new HighlightProvider(documents, configuration, loaders, semantics: Semantics);

            Definition = new DefinitionProvider(documents, configuration, loaders, semantics: Semantics)
            {
                LinkSupport = true,
            };
        }

        public string Text { get; }

        public DocumentUri Uri { get; }

        public DocumentStore Documents { get; }

        public DocumentSemantics Semantics { get; }

        public DefinitionProvider Definition { get; }

        public async Task<Location[]> LocationsAsync(int offset, bool includeDeclaration = true)
        {
            var asked = _references.Read(new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = Uri.ToString() },
                Position = EditorPositions.PositionAt(new LineMap(Text), offset),
                Context = new ReferenceContext { IncludeDeclaration = includeDeclaration },
            });

            Assert.NotNull(asked);

            return await _references.AnswerAsync(asked!, includeDeclaration, CancellationToken.None) ?? [];
        }

        public async Task<DocumentHighlight[]> HighlightsAsync(int offset)
        {
            var asked = _highlights.Read(EditorFixture.Ask(Uri, Text, offset));

            Assert.NotNull(asked);

            return await _highlights.AnswerAsync(asked!, CancellationToken.None) ?? [];
        }

        /// <summary>Where in this document the answer points, in ascending order.</summary>
        public List<int> Here(IEnumerable<Location> locations)
            => [.. Starts(Text, InThisDocument(locations).Select(location => location.Range)).Order()];

        public IEnumerable<Location> InThisDocument(IEnumerable<Location> locations)
            => locations.Where(location => string.Equals(location.Uri, Uri.ToString(), StringComparison.Ordinal));

        /// <summary>The part of an answer that is in some other file, which only a schema symbol has.</summary>
        public IEnumerable<Location> Elsewhere(IEnumerable<Location> locations)
            => locations.Where(location => !string.Equals(location.Uri, Uri.ToString(), StringComparison.Ordinal));
    }

    // ------------------------------------------------------- expectations from the fixture

    /// <summary>Every offset where <paramref name="name"/> is written as a whole identifier.</summary>
    /// <remarks>
    /// Whole-identifier rather than substring, or asking about <c>count</c> would claim the
    /// <c>count</c> inside <c>small_count</c> and every sweep would be asserting the wrong thing
    /// confidently.
    /// </remarks>
    private static List<int> Written(string text, string name)
    {
        var offsets = new List<int>();

        for (var at = text.IndexOf(name, StringComparison.Ordinal);
            at >= 0;
            at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            if (!IsPartOfAName(text, at - 1) && !IsPartOfAName(text, at + name.Length))
            {
                offsets.Add(at);
            }
        }

        Assert.True(offsets.Count > 0, $"the fixture must write '{name}'");
        return offsets;
    }

    private static bool IsPartOfAName(string text, int at)
        => at >= 0 && at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_');

    /// <summary>The offsets a set of ranges begins at, so an answer can be compared to the fixture.</summary>
    private static List<int> Starts(string text, IEnumerable<Range> ranges)
    {
        var lines = new LineMap(text);

        return [.. ranges.Select(range => EditorPositions.OffsetOf(lines, range.Start))];
    }

    private static string TextIn(string text, Range range)
    {
        var lines = new LineMap(text);
        var start = EditorPositions.OffsetOf(lines, range.Start);

        return text[start..EditorPositions.OffsetOf(lines, range.End)];
    }

    // ------------------------------------------------------- every place a name is written

    /// <summary>
    /// The sweep: asking about any occurrence of a name brings back every occurrence of it.
    /// </summary>
    /// <remarks>
    /// Every kind the language has, and asked from each place the name appears rather than from the
    /// first -- an index that answered only from a declaration, or only from a use, would pass a test
    /// that asked once.
    /// </remarks>
    [Theory]
    [InlineData("total")]
    [InlineData("scale")]
    [InlineData("count")]
    [InlineData("nested_values")]
    [InlineData("value")]
    [InlineData("kept")]
    [InlineData("inner")]
    [InlineData("scaled")]
    [InlineData("Outer")]
    [InlineData("Inner")]
    [InlineData("TopLevelStatus")]
    [InlineData("TOP_LEVEL_STATUS_OK")]
    public async Task EveryPlaceANameIsWrittenAnswersWithAllOfThem(string name)
    {
        var session = new Session(Source);
        var written = Written(Source, name);

        foreach (var asked in written)
        {
            var locations = await session.LocationsAsync(asked);

            Assert.Equal(written, session.Here(locations));
            Assert.All(
                session.InThisDocument(locations),
                location => Assert.Equal(name, TextIn(Source, location.Range)));
        }
    }

    /// <summary>A name is answered about from the very end of it, where the caret sits after typing.</summary>
    [Fact]
    public async Task ACaretJustPastANameStillAnswersAboutIt()
    {
        var session = new Session(Source);

        var locations = await session.LocationsAsync(EditorFixture.After(Source, "nested_values"));

        Assert.Equal(Written(Source, "nested_values"), session.Here(locations));
    }

    /// <summary>Results come back in source order, which is the order the index publishes.</summary>
    [Fact]
    public async Task ReferencesComeBackInSourceOrder()
    {
        var session = new Session(Source);

        var starts = Starts(
            Source,
            session.InThisDocument(await session.LocationsAsync(EditorFixture.At(Source, "total")))
                .Select(location => location.Range));

        // An empty list and a single-element one are sorted whatever the order rule is.
        Assert.True(starts.Count > 1, "the fixture must write `total` more than once");

        Assert.Equal(starts.Order(), starts);
    }

    /// <summary>A caret on nothing that resolved answers with nothing rather than with a guess.</summary>
    [Theory]
    [InlineData("import")]
    [InlineData("int64")]
    [InlineData("doubles what")]
    public async Task ACaretThatNamesNothingAnswersWithNothing(string marker)
    {
        var session = new Session(Source);

        Assert.Empty(await session.LocationsAsync(EditorFixture.After(Source, marker)));
    }

    // ------------------------------------------------------- the declaration among them

    /// <summary>The declaration is in the list and is the place the name was introduced.</summary>
    [Fact]
    public async Task TheDeclarationIsInTheListAndIsTheOneTheLanguageIntroducedTheNameAt()
    {
        var session = new Session(Source);
        var at = EditorFixture.At(Source, "total");

        var withIt = session.Here(await session.LocationsAsync(at));
        var without = session.Here(await session.LocationsAsync(at, includeDeclaration: false));

        // `var total` is where the name enters scope, and it is the only entry the flag removes.
        Assert.Equal([at], withIt.Except(without));
    }

    /// <summary>
    /// A schema symbol has uses here and its declaration in the <c>.proto</c>, so asking for the
    /// declaration crosses the file boundary.
    /// </summary>
    /// <remarks>
    /// The asymmetry the index states out loud: a field is declared in a file this compiler reads and
    /// does not own, so there is no declaration among the references, and the flag can only be
    /// honoured by asking the other door -- the one go-to-definition already uses.
    /// </remarks>
    [Fact]
    public async Task AFieldsDeclarationIsInTheProtoAndItsUsesAreHere()
    {
        var session = new Session(Source);
        var at = EditorFixture.At(Source, "count");

        var withIt = await session.LocationsAsync(at);
        var without = await session.LocationsAsync(at, includeDeclaration: false);

        // The uses are the same either way: a schema symbol has no declaration among them to drop.
        Assert.Equal(Written(Source, "count"), session.Here(without));
        Assert.Equal(session.Here(without), session.Here(withIt));
        Assert.Empty(session.Elsewhere(without));

        var declaration = Assert.Single(session.Elsewhere(withIt));

        Assert.EndsWith(".proto", declaration.Uri, StringComparison.Ordinal);

        var schema = File.ReadAllText(DocumentUri.Parse(declaration.Uri).Path!);

        Assert.Equal("count", TextIn(schema, declaration.Range));
    }

    /// <summary>
    /// Following a name to its declaration and asking that declaration for its references brings back
    /// the name you started from.
    /// </summary>
    /// <remarks>
    /// The round trip #51 asks for by name, because this is where two indices drift. There are not two
    /// here -- go-to-definition and this both turn a caret into a symbol the same way -- and the test
    /// is what keeps that true rather than merely currently so.
    /// </remarks>
    [Theory]
    [InlineData("total")]
    [InlineData("scale")]
    [InlineData("scaled")]
    [InlineData("value")]
    [InlineData("kept")]
    public async Task FollowingANameToItsDeclarationAndBackFindsWhereYouStarted(string name)
    {
        var session = new Session(Source);

        // The last written occurrence, so the journey is a real one: starting at the declaration
        // would make this a statement about one position rather than about two.
        var started = Written(Source, name)[^1];

        var asked = session.Definition.Read(EditorFixture.Ask(session.Uri, Source, started));
        Assert.NotNull(asked);

        var links = (LocationLink[]?)await session.Definition.AnswerAsync(asked!, CancellationToken.None);
        var declared = Assert.Single(links ?? []);

        var back = EditorPositions.OffsetOf(new LineMap(Source), declared.TargetSelectionRange.Start);

        Assert.Contains(started, session.Here(await session.LocationsAsync(back)));
    }

    // ------------------------------------------------------- semantic, never textual

    /// <summary>
    /// Two locals of one name in sibling blocks are two symbols and do not answer about each other.
    /// </summary>
    /// <remarks>
    /// The case that separates a highlighter which knows what a name means from one that matched text.
    /// Getting it wrong is worse than not shipping the feature, because it teaches a reader that the
    /// tool understands scope and is then believed about the case where it does not.
    /// </remarks>
    [Fact]
    public async Task TwoSameNamedLocalsInSiblingBlocksDoNotAnswerAboutEachOther()
    {
        var session = new Session(Source);
        var both = Written(Source, "shadowed");

        Assert.Equal(4, both.Count);

        var first = await session.HighlightsAsync(both[0]);
        var second = await session.HighlightsAsync(both[2]);

        Assert.Equal([both[0], both[1]], Starts(Source, first.Select(h => h.Range)).Order());
        Assert.Equal([both[2], both[3]], Starts(Source, second.Select(h => h.Range)).Order());
    }

    /// <summary>A name written inside a string literal is not a name.</summary>
    [Fact]
    public async Task ANameInsideAStringLiteralAnswersWithNothing()
    {
        const string Quoted =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn labelled() -> string {
                    var note: string = "count is a word here and nothing more";

                    return note;
                }
            }
            """;

        var session = new Session(Quoted);
        var inside = Quoted.IndexOf("count is", StringComparison.Ordinal);

        Assert.Empty(await session.HighlightsAsync(inside));
        Assert.Empty(await session.LocationsAsync(inside));
    }

    /// <summary>A cast target that names a message is a reference to it, error or not.</summary>
    /// <remarks>
    /// The cast does not type-check, which is the ordinary state of a file somebody is editing. What
    /// the binder resolved it still resolved, and navigation is most wanted when something is broken.
    /// </remarks>
    [Fact]
    public async Task ACastTargetThatNamesAMessageAnswersAboutThatMessage()
    {
        const string Cast =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn f() -> int64 {
                    return count as Inner;
                }
            }
            """;

        var session = new Session(Cast);

        var locations = await session.LocationsAsync(
            EditorFixture.At(Cast, "Inner"), includeDeclaration: false);

        Assert.Equal(Written(Cast, "Inner"), session.Here(locations));
    }

    // ------------------------------------------------------- reads, writes and declarations

    /// <summary>An assignment is tinted differently from a read, and a declaration from both.</summary>
    [Fact]
    public async Task AnAssignmentAReadAndADeclarationAreThreeDifferentKinds()
    {
        var session = new Session(Source);
        var highlights = await session.HighlightsAsync(EditorFixture.At(Source, "total"));
        var lines = new LineMap(Source);

        DocumentHighlight At(int offset) => Assert.Single(
            highlights,
            highlight => EditorPositions.OffsetOf(lines, highlight.Range.Start) == offset);

        // On `total = total + 1;` the first is assigned and the second is read, which is the whole
        // distinction: one symbol, one spelling, one line, two kinds.
        var assignment = EditorFixture.At(Source, "total = total + 1");

        Assert.Equal(DocumentHighlightKind.Text, At(EditorFixture.At(Source, "total")).Kind);
        Assert.Equal(DocumentHighlightKind.Write, At(assignment).Kind);
        Assert.Equal(DocumentHighlightKind.Read, At(assignment + "total = ".Length).Kind);
    }

    /// <summary>Highlighting answers about the same occurrences find-all-references does.</summary>
    /// <remarks>
    /// The two features share one lookup, and this is what says so from outside: a disagreement here
    /// would be the tool contradicting itself about one caret.
    /// </remarks>
    [Theory]
    [InlineData("total")]
    [InlineData("count")]
    [InlineData("scaled")]
    public async Task HighlightingAndReferencesAgreeAboutOneCaret(string name)
    {
        var session = new Session(Source);
        var at = EditorFixture.At(Source, name);

        var highlighted = Starts(Source, (await session.HighlightsAsync(at)).Select(h => h.Range)).Order();

        // Or two features that had both stopped answering would agree about nothing, which is the one
        // way this test could pass while saying nothing.
        Assert.NotEmpty(highlighted);

        Assert.Equal(session.Here(await session.LocationsAsync(at)), highlighted);
    }

    // ------------------------------------------------------- what it costs

    /// <summary>
    /// Moving a caret through a buffer nobody has edited compiles nothing further.
    /// </summary>
    /// <remarks>
    /// The property occurrence highlighting lives or dies by, asserted as a count rather than as a
    /// duration: a wall-clock assertion is a flaky test on a loaded machine and says nothing about
    /// why it failed. #57 owns the budget; what this owes #57 is that the work per caret move is a
    /// lookup over an index the last keystroke already built, so the number it goes on to measure is
    /// the one this shape implies.
    /// </remarks>
    [Fact]
    public async Task MovingTheCaretThroughAnUneditedBufferCompilesNothingFurther()
    {
        var session = new Session(Source);

        await session.HighlightsAsync(EditorFixture.At(Source, "total"));

        var afterFirst = session.Semantics.Compilations;
        Assert.Equal(1, afterFirst);

        foreach (var name in new[] { "scale", "count", "nested_values", "scaled", "Outer", "kept" })
        {
            await session.HighlightsAsync(EditorFixture.At(Source, name));
        }

        Assert.Equal(afterFirst, session.Semantics.Compilations);
    }
}
