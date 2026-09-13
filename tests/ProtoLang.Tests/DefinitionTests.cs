using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.Tests;

/// <summary>
/// Where the name under the caret was declared: in this buffer for what ProtoLang declares, and in
/// the <c>.proto</c> for everything the schema does.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion computes the expected range from the fixture rather than naming a line number,
/// because a hardcoded position stops meaning anything the moment somebody edits the source above
/// it -- and a navigation test that silently checks the wrong line is a navigation test that passes
/// while the feature is broken.
/// </para>
/// <para>
/// The schema side is checked against the <c>.proto</c> text the same way, by searching it for the
/// declaration. That is what makes "jumps into the <c>.proto</c>" a claim about the right line of
/// the right file rather than about any line of it.
/// </para>
/// </remarks>
public class DefinitionTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        extend protolang.tests.Outer {
            fn scaled(scale: int64) -> int64 {
                var doubled: int64 = count * scale;

                for value in nested_values {
                    doubled = doubled + 1;
                }

                return doubled + scaled(scale);
            }

            fn picked() -> protolang.tests.TopLevelStatus {
                var chosen: protolang.tests.TopLevelStatus = TopLevelStatus.TOP_LEVEL_STATUS_OK;

                return chosen;
            }
        }

        test protolang.tests.Outer.scaled "doubles what it is given" {
            receiver { count = 2; }
            arg scale = 3;
            expect return 12;
        }
        """;

    private static async Task<IReadOnlyList<LocationLink>> LinksAsync(string text, int offset)
    {
        var (documents, uri) = EditorFixture.Open(text);
        var provider = new DefinitionProvider(
            documents, EditorFixture.Configuration(), EditorFixture.Loaders())
        {
            LinkSupport = true,
        };

        var asked = provider.Read(EditorFixture.Ask(uri, text, offset));

        Assert.NotNull(asked);

        return (LocationLink[]?)await provider.AnswerAsync(asked!, CancellationToken.None) ?? [];
    }

    /// <summary>The one place a name leads, which is all this language has: there is no overloading.</summary>
    private static async Task<LocationLink> OneAsync(string marker)
        => Assert.Single(await LinksAsync(Source, EditorFixture.After(Source, marker)));

    /// <summary>The range covering <paramref name="marker"/> in a text, as the editor counts.</summary>
    private static Range RangeOf(string text, string marker)
    {
        var start = EditorFixture.At(text, marker);

        return EditorPositions.Between(new LineMap(text), start, start + marker.Length);
    }

    /// <summary>
    /// Where <paramref name="name"/> is declared, found by the longer <paramref name="marker"/> that
    /// makes it unambiguous.
    /// </summary>
    /// <remarks>
    /// A declared name is usually written several times in a fixture, and the declaration is not the
    /// first of them. The marker picks out the declaration and the name says how much of it the
    /// selection range should cover, so the expectation is computed from the text either way.
    /// </remarks>
    private static Range Declares(string text, string marker, string name)
    {
        var start = EditorFixture.At(text, marker) + marker.IndexOf(name, StringComparison.Ordinal);

        return EditorPositions.Between(new LineMap(text), start, start + name.Length);
    }

    private static string SchemaText(LocationLink link)
        => File.ReadAllText(DocumentUri.Parse(link.TargetUri).Path!);

    // ------- what ProtoLang declares

    [Fact]
    public async Task ALocalJumpsToItsDeclaration()
        => Assert.Equal(
            Declares(Source, "doubled: int64", "doubled"),
            SelectionOf(await OneAsync("return doubl"), "doubled"));

    [Fact]
    public async Task AParameterJumpsToItsDeclaration()
        => Assert.Equal(
            Declares(Source, "scale: int64", "scale"),
            SelectionOf(await OneAsync("count * sca"), "scale"));

    [Fact]
    public async Task ALoopBindingJumpsToItsDeclaration()
        => Assert.Equal(Declares(Source, "value in", "value"), SelectionOf(await OneAsync("for val"), "value"));

    [Fact]
    public async Task AMethodCallJumpsToTheMethodInItsExtendBlock()
        => Assert.Equal(
            Declares(Source, "scaled(scale: int64)", "scaled"),
            SelectionOf(await OneAsync("doubled + scal"), "scaled"));

    /// <summary>
    /// The one navigation a reader of a test wants: from the declaration that names a method to the
    /// method it is a test of.
    /// </summary>
    [Fact]
    public async Task ATestTargetJumpsToTheMethodUnderTest()
        => Assert.Equal(
            Declares(Source, "scaled(scale: int64)", "scaled"),
            SelectionOf(await OneAsync("test protolang.tests.Outer.scal"), "scaled"));

    /// <summary>
    /// The whole declaration and the name inside it, both carried, because LSP asks for both and
    /// derives neither -- and a client handed the other way round has no defined behavior.
    /// </summary>
    [Fact]
    public async Task TheSelectionRangeLiesInsideTheRangeItSelectsFrom()
    {
        foreach (var marker in new[]
                 {
                     "return doubl", "count * sca", "for val", "doubled + scal", "int64 = cou",
                     "extend protolang.tests.Out", "TopLevelStatus.TOP_LEVEL_STATUS_O",
                 })
        {
            var link = await OneAsync(marker);

            Assert.True(
                Contains(link.TargetRange, link.TargetSelectionRange),
                $"the declaration '{marker}' reaches selects {Show(link.TargetSelectionRange)}, which "
                    + $"is not inside the range {Show(link.TargetRange)} it selects from");
        }
    }

    // ------- what the schema declares

    [Fact]
    public async Task AFieldJumpsIntoTheProtoThatDeclaresIt()
    {
        var link = await OneAsync("int64 = cou");

        Assert.EndsWith("fixtures.proto", link.TargetUri, StringComparison.Ordinal);
        Assert.Equal(RangeOf(SchemaText(link), "count"), link.TargetSelectionRange);
    }

    [Fact]
    public async Task AnEnumValueJumpsIntoTheProtoThatDeclaresIt()
    {
        var link = await OneAsync("TopLevelStatus.TOP_LEVEL_STATUS_O");

        Assert.Equal(RangeOf(SchemaText(link), "TOP_LEVEL_STATUS_OK"), link.TargetSelectionRange);
    }

    [Fact]
    public async Task AMessageTypeInATypePositionJumpsIntoTheProtoThatDeclaresIt()
    {
        var link = await OneAsync("fn picked() -> protolang.tests.TopLevelSta");

        Assert.Equal(RangeOf(SchemaText(link), "TopLevelStatus"), link.TargetSelectionRange);
    }

    [Fact]
    public async Task AnExtendReceiverJumpsIntoTheProtoThatDeclaresIt()
    {
        var link = await OneAsync("extend protolang.tests.Out");

        Assert.Equal(RangeOf(SchemaText(link), "Outer"), link.TargetSelectionRange);
    }

    // ------- where there is nowhere to go

    /// <summary>
    /// <c>int64</c> is not declared anywhere. Sending a client somewhere plausible is worse than
    /// sending it nowhere.
    /// </summary>
    [Fact]
    public async Task AScalarTypeSpellingNavigatesNowhere()
        => Assert.Empty(await LinksAsync(Source, EditorFixture.After(Source, "var doubled: int")));

    [Fact]
    public async Task ANameThatDoesNotResolveNavigatesNowhere()
    {
        var source = Source.Replace("count * scale", "nonexistent * scale", StringComparison.Ordinal);

        Assert.Empty(await LinksAsync(source, EditorFixture.After(source, "= nonexist")));
    }

    [Fact]
    public async Task AKeywordNavigatesNowhere()
        => Assert.Empty(await LinksAsync(Source, EditorFixture.After(Source, "    retu")));

    // ------- what the client asked for

    /// <summary>
    /// A client that never declared <c>linkSupport</c> is sent plain locations, because the two
    /// shapes are not interchangeable: links handed to such a client have no <c>uri</c> member in
    /// them and are discarded in silence.
    /// </summary>
    [Fact]
    public async Task AClientThatDidNotAskForLinksIsSentPlainLocations()
    {
        var (documents, uri) = EditorFixture.Open(Source);
        var provider = new DefinitionProvider(
            documents, EditorFixture.Configuration(), EditorFixture.Loaders());

        var asked = provider.Read(
            EditorFixture.Ask(uri, Source, EditorFixture.After(Source, "return doubl")));

        Assert.NotNull(asked);

        var answer = Assert.Single(Assert.IsType<Location[]>(
            await provider.AnswerAsync(asked!, CancellationToken.None)));

        // The name rather than the whole declaration: arriving with a statement selected is worse
        // than arriving with the name it declares selected.
        Assert.Equal(RangeOf(Source, "doubled"), answer.Range);
    }

    /// <summary>
    /// A declaration in the buffer the client asked about is returned under the URI the client sent,
    /// not under this server's own spelling of the same path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case in the wild is a drive letter: an editor sends <c>file:///c%3A/Users/...</c> and
    /// <c>DocumentUri.FromPath</c> produces <c>file:///C:/Users/...</c>, so a client matching the
    /// target against its own open documents by string opens a second editor onto the file the caret
    /// is already in. That spelling is platform-specific, so what is written here is a redundant path
    /// segment, which is the same property -- two strings, one file -- on every platform.
    /// </para>
    /// <para>
    /// Asserted as string equality rather than as URI equivalence, because string equality is what a
    /// client does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeclarationInTheAskedDocumentKeepsTheUriTheClientSent()
    {
        var (documents, canonical) = EditorFixture.Open(Source);
        var file = Path.GetFileName(canonical.Path)!;
        var spelled = DocumentUri.Parse(
            canonical.Text[..^file.Length] + "./" + file);

        Assert.NotEqual(canonical.Text, spelled.Text);
        Assert.Equal(canonical.Key, spelled.Key);

        documents.Open(spelled, "protolang", 1, Source);

        var provider = new DefinitionProvider(
            documents, EditorFixture.Configuration(), EditorFixture.Loaders()) { LinkSupport = true };

        var asked = provider.Read(
            EditorFixture.Ask(spelled, Source, EditorFixture.After(Source, "return doubl")));

        Assert.NotNull(asked);

        var link = Assert.Single((LocationLink[])(await provider.AnswerAsync(asked!, CancellationToken.None))!);

        Assert.Equal(spelled.Text, link.TargetUri);
    }

    // ------- when the rest of the file is broken

    /// <summary>
    /// Navigation is most wanted when something is broken, so nothing here may depend on the rest of
    /// the file compiling.
    /// </summary>
    [Fact]
    public async Task ADeclarationIsStillReachedInAFileWithErrorsElsewhere()
    {
        var source = Source.Replace(
            "return chosen;", "return nonexistent(;", StringComparison.Ordinal);

        var link = Assert.Single(
            await LinksAsync(source, EditorFixture.After(source, "return doubl")));

        Assert.Equal(Declares(source, "doubled: int64", "doubled"), SelectionOf(link, "doubled"));
    }

    // ------- helpers

    /// <summary>
    /// The link's selection range, checked to be as long as the name it claims to select.
    /// </summary>
    /// <remarks>
    /// The length is asserted here rather than in each test, because a range that starts in the right
    /// place and is the wrong length is the failure this whole feature is most likely to have: both
    /// ends are recorded separately and only one of them is obviously wrong when it moves.
    /// </remarks>
    private static Range SelectionOf(LocationLink link, string name)
    {
        Assert.Equal(link.TargetSelectionRange.Start.Line, link.TargetSelectionRange.End.Line);
        Assert.Equal(
            name.Length,
            link.TargetSelectionRange.End.Character - link.TargetSelectionRange.Start.Character);

        return link.TargetSelectionRange;
    }

    private static bool Contains(Range outer, Range inner)
        => Before(outer.Start, inner.Start) && Before(inner.End, outer.End);

    private static bool Before(Position first, Position second)
        => first.Line < second.Line || (first.Line == second.Line && first.Character <= second.Character);

    private static string Show(Range range)
        => $"{range.Start.Line}:{range.Start.Character}-{range.End.Line}:{range.End.Character}";
}
