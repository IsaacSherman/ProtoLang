using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.Tests;

/// <summary>
/// The outline of a file: its <c>extend</c> blocks, and the methods and tests inside them.
/// </summary>
/// <remarks>
/// Nothing here compiles anything, and that is the property under test as much as the shape is. The
/// outline is parsed, so the fixtures that matter most are the ones that do not parse and the one
/// whose schema is not importable at all -- both of which still have declarations a reader is trying
/// to navigate.
/// </remarks>
public class DocumentSymbolTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        extend protolang.tests.Outer {
            fn scaled(scale: int64) -> int64 {
                return count * scale;
            }

            virtual fn described() {
                return;
            }
        }

        test protolang.tests.Outer.scaled "doubles what it is given" {
            receiver { count = 2; }
            arg scale = 3;
            expect return 12;
        }

        extend protolang.tests.Mapped {
            fn tallied() -> int64 {
                return count;
            }
        }
        """;

    private static IReadOnlyList<DocumentSymbol> Outline(string text) => DocumentOutline.Of(text);

    private static DocumentSymbol Named(IEnumerable<DocumentSymbol> outline, string name)
        => Assert.Single(outline, symbol => symbol.Name == name);

    // ------- the shape of a file

    [Fact]
    public void AnExtendBlockIsAContainerHoldingItsMethods()
    {
        var extend = Named(Outline(Source), "protolang.tests.Outer");

        Assert.Equal(SymbolKind.Class, extend.Kind);
        Assert.Equal(
            ["scaled", "described", "doubles what it is given"],
            extend.Children!.Select(child => child.Name));
    }

    [Fact]
    public void AMethodShowsTheSignatureAsItWasWritten()
        => Assert.Equal(
            "(scale: int64) -> int64",
            Named(Named(Outline(Source), "protolang.tests.Outer").Children!, "scaled").Detail);

    /// <summary>
    /// <c>void</c> is written although the author left the arrow off, for the reason
    /// <c>IrMethodSignature.DisplayName</c> gives: what a call produces is what decides whether it
    /// may be used as a value.
    /// </summary>
    [Fact]
    public void AMethodThatReturnsNothingSaysSo()
        => Assert.Equal(
            "virtual () -> void",
            Named(Named(Outline(Source), "protolang.tests.Outer").Children!, "described").Detail);

    /// <summary>
    /// A test belongs with the methods of the message it tests, because that is where a reader
    /// looking for it will look.
    /// </summary>
    [Fact]
    public void ATestNestsUnderTheExtendBlockItsReceiverNames()
    {
        var test = Named(
            Named(Outline(Source), "protolang.tests.Outer").Children!, "doubles what it is given");

        Assert.Equal(SymbolKind.Function, test.Kind);
        Assert.Equal("protolang.tests.Outer.scaled", test.Detail);
    }

    /// <summary>
    /// Nesting is by spelling, because resolving two names to one message is the binder's job and the
    /// binder is not here. A test that cannot be placed is listed rather than dropped: a declaration
    /// missing from an outline reads as a declaration that is not there.
    /// </summary>
    [Fact]
    public void ATestWhoseReceiverNamesNoExtendBlockIsListedOnItsOwn()
    {
        var source = Source.Replace(
            "test protolang.tests.Outer.scaled", "test Outer.scaled", StringComparison.Ordinal);

        var outline = Outline(source);

        Assert.Equal(SymbolKind.Function, Named(outline, "doubles what it is given").Kind);
        Assert.DoesNotContain(
            Named(outline, "protolang.tests.Outer").Children!,
            child => child.Kind == SymbolKind.Function);
    }

    /// <summary>
    /// The compilation unit keeps extends and tests in two lists, and an author interleaves them. An
    /// outline that disagreed with the file it describes is one nobody trusts twice.
    /// </summary>
    [Fact]
    public void TopLevelDeclarationsAppearInSourceOrder()
    {
        var source = Source.Replace(
            "test protolang.tests.Outer.scaled", "test Outer.scaled", StringComparison.Ordinal);

        Assert.Equal(
            ["protolang.tests.Outer", "doubles what it is given", "protolang.tests.Mapped"],
            Outline(source).Select(symbol => symbol.Name));
    }

    // ------- the ranges

    /// <summary>
    /// LSP requires the selection range to lie inside the range it selects from, and a client handed
    /// the other way round has no defined behavior. Swept over every entry rather than sampled,
    /// because one entry getting it wrong is exactly how this fails.
    /// </summary>
    [Theory]
    [InlineData(Source)]
    [InlineData(Broken)]
    [InlineData(Unnamed)]
    [InlineData(Nothing)]
    public void EverySelectionRangeLiesInsideTheRangeItSelectsFrom(string text)
    {
        foreach (var symbol in Flatten(Outline(text)))
        {
            Assert.True(
                Contains(symbol.Range, symbol.SelectionRange),
                $"'{symbol.Name}' selects {Show(symbol.SelectionRange)}, which is not inside the "
                    + $"range {Show(symbol.Range)} it selects from");
        }
    }

    [Fact]
    public void AMethodSelectsItsOwnNameAndSpansItsWholeDeclaration()
    {
        var method = Named(Named(Outline(Source), "protolang.tests.Outer").Children!, "scaled");
        var start = EditorFixture.At(Source, "fn scaled");
        var lines = new Diagnostics.LineMap(Source);

        Assert.Equal(
            EditorPositions.Between(lines, start + 3, start + 3 + "scaled".Length), method.SelectionRange);
        Assert.Equal(EditorPositions.PositionAt(lines, start), method.Range.Start);
    }

    // ------- a file that is not finished

    private const string Broken =
        """
        import proto "fixtures.proto";

        extend protolang.tests.Outer {
            fn scaled(scale: int64) -> int64 {
                return count *
            }

            fn
        }
        """;

    /// <summary>
    /// An outline that vanishes while you type is worse than one that is briefly out of date, and
    /// the moments it would vanish are the moments somebody is navigating a file they are halfway
    /// through editing.
    /// </summary>
    [Fact]
    public void AnOutlineSurvivesAFileThatDoesNotParse()
    {
        var extend = Named(Outline(Broken), "protolang.tests.Outer");

        Assert.Contains(extend.Children!, child => child.Name == "scaled");
    }

    private const string Unnamed =
        """
        extend protolang.tests.Outer {
            fn () -> int64 {
                return 1;
            }
        }
        """;

    /// <summary>
    /// A declaration whose name has not been written is named for the keyword that introduced it. An
    /// empty name renders as a blank row: present in the tree and unreadable, which is the one
    /// outcome worse than being absent from it.
    /// </summary>
    [Fact]
    public void ADeclarationWithNoNameYetIsNamedForItsKeyword()
        => Assert.Contains(
            Named(Outline(Unnamed), "protolang.tests.Outer").Children!, child => child.Name == "fn");

    private const string Nothing = "extend";

    [Fact]
    public void AFileHoldingOnlyAKeywordStillProducesAnOutline()
        => Assert.Equal("extend", Assert.Single(Outline(Nothing)).Name);

    [Fact]
    public void AnEmptyFileProducesAnEmptyOutline() => Assert.Empty(Outline(string.Empty));

    // ------- a client that cannot show a tree

    /// <summary>
    /// The older shape, which is what a client gets unless it declared
    /// <c>hierarchicalDocumentSymbolSupport</c>. Sending it the newer one produces entries with no
    /// <c>location</c> member, which such a client discards in silence.
    /// </summary>
    [Fact]
    public void AFlattenedOutlineKeepsTheNestingAsAContainerName()
    {
        var flattened = DocumentOutline.Flattened(Outline(Source), "file:///source.protolang");

        Assert.Equal(
            "protolang.tests.Outer",
            Assert.Single(flattened, symbol => symbol.Name == "scaled").ContainerName);

        Assert.Null(
            Assert.Single(flattened, symbol => symbol.Name == "protolang.tests.Outer").ContainerName);
    }

    [Fact]
    public void AFlattenedOutlineHoldsEveryEntryTheTreeHeld()
        => Assert.Equal(
            Flatten(Outline(Source)).Count,
            DocumentOutline.Flattened(Outline(Source), "file:///source.protolang").Count);

    [Fact]
    public void EveryFlattenedEntryNamesTheDocumentItIsIn()
        => Assert.All(
            DocumentOutline.Flattened(Outline(Source), "file:///source.protolang"),
            symbol => Assert.Equal("file:///source.protolang", symbol.Location.Uri));

    // ------- helpers

    private static IReadOnlyList<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> outline)
        => [.. outline.SelectMany(symbol => new[] { symbol }.Concat(Flatten(symbol.Children ?? [])))];

    private static bool Contains(Range outer, Range inner)
        => Before(outer.Start, inner.Start) && Before(inner.End, outer.End);

    private static bool Before(Position first, Position second)
        => first.Line < second.Line || (first.Line == second.Line && first.Character <= second.Character);

    private static string Show(Range range)
        => $"{range.Start.Line}:{range.Start.Character}-{range.End.Line}:{range.End.Character}";
}
