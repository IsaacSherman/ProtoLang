using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// What the editor shows when the pointer rests on something: the type it resolved to, the comment
/// the schema author wrote, and the policy the source text does not show.
/// </summary>
/// <remarks>
/// Driven through <see cref="HoverProvider"/> rather than through a server, because what is being
/// asserted is the card. The wire, the lifecycle and the capability negotiation are
/// <see cref="LanguageServerTests"/>'s, and a test that went through all of them to read a string
/// would be slower and would fail for reasons that are not about hover.
/// </remarks>
public class HoverTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        extend protolang.tests.Outer {
            fn scaled(scale: int64) -> int64 {
                var doubled: int64 = count * scale;
                var shared: int64 = doubled / scale on_zero 0;
                var narrowed: int64 = small_count as int64;
                var label_length: int64 = 0;

                for value in nested_values {
                    label_length = label_length + 1;
                }

                return doubled + shared + narrowed;
            }

            fn describes() -> bool {
                var picked: protolang.tests.TopLevelStatus = TopLevelStatus.TOP_LEVEL_STATUS_OK;

                return has optional_count;
            }
        }

        test protolang.tests.Outer.scaled "doubles what it is given" {
            receiver { count = 2; }
            arg scale = 3;
            expect return 12;
        }
        """;

    private static async Task<Hover?> CardAsync(string text, int offset)
    {
        var (documents, uri) = EditorFixture.Open(text);
        var provider = new HoverProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());

        var asked = provider.Read(EditorFixture.Ask(uri, text, offset));

        return asked is null ? null : await provider.AnswerAsync(asked, CancellationToken.None);
    }

    private static async Task<string> TextAsync(string text, int offset)
    {
        var card = await CardAsync(text, offset);

        Assert.NotNull(card);
        Assert.Equal(MarkupKind.Markdown, card!.Contents.Kind);

        return card.Contents.Value;
    }

    private static Task<string> TextAfterAsync(string marker)
        => TextAsync(Source, EditorFixture.After(Source, marker));

    /// <summary>The signature block a card opens with, whole, for an assertion that means it.</summary>
    private static string Fenced(string signature) => $"```protolang\n{signature}\n```";

    // ------- what a name is

    [Fact]
    public async Task ALocalShowsItsDeclaredType()
        => Assert.Contains("doubled: int64", await TextAfterAsync("var doubl"), StringComparison.Ordinal);

    [Fact]
    public async Task ALocalSaysWhatKindOfNameItIs()
        => Assert.Contains("Local variable.", await TextAfterAsync("var doubl"), StringComparison.Ordinal);

    [Fact]
    public async Task AParameterShowsItsDeclaredType()
    {
        var card = await TextAfterAsync("count * sca");

        Assert.Contains("scale: int64", card, StringComparison.Ordinal);
        Assert.Contains("Parameter.", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// The element type rather than the repeated type, because that is what the name holds on each
    /// pass -- and it is the one type in the language that is nowhere in the text of the declaration.
    /// </summary>
    [Fact]
    public async Task ALoopBindingShowsTheTypeOfOneElement()
    {
        // The whole signature line, so that the repeated type the collection has cannot pass as the
        // element type the name holds -- a substring check would accept either.
        Assert.Contains(
            Fenced("value: protolang.tests.Outer.Nested"),
            await TextAfterAsync("for val"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMethodShowsItsWholeSignature()
        => Assert.Contains(
            "fn scaled(scale: int64) -> int64",
            await TextAfterAsync("test protolang.tests.Outer.scal"),
            StringComparison.Ordinal);

    [Fact]
    public async Task AMethodSaysWhichMessageItIsAttachedTo()
        => Assert.Contains(
            "Method on `protolang.tests.Outer`.",
            await TextAfterAsync("test protolang.tests.Outer.scal"),
            StringComparison.Ordinal);

    [Fact]
    public async Task AFieldShowsItsTypeAndTheMessageItBelongsTo()
    {
        var card = await TextAfterAsync("var doubled: int64 = cou");

        Assert.Contains("count: int64", card, StringComparison.Ordinal);
        Assert.Contains("Field of `protolang.tests.Outer`", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of hovering a schema member. The type is usually obvious from the name beside it and
    /// the comment never is, and it is written in a file the reader may never have opened.
    /// </summary>
    [Fact]
    public async Task AFieldShowsTheCommentTheSchemaAuthorWroteAboveIt()
        => Assert.Contains(
            "Narrower than count",
            await TextAfterAsync("var narrowed: int64 = small_cou"),
            StringComparison.Ordinal);

    [Fact]
    public async Task AFieldSaysWhichSchemaDeclaresIt()
        => Assert.Contains(
            "declared in `fixtures.proto`",
            await TextAfterAsync("var doubled: int64 = cou"),
            StringComparison.Ordinal);

    [Fact]
    public async Task AnEnumValueShowsItsEnumType()
        => Assert.Contains(
            "TOP_LEVEL_STATUS_OK: protolang.tests.TopLevelStatus",
            await TextAfterAsync("TopLevelStatus.TOP_LEVEL_STATUS_O"),
            StringComparison.Ordinal);

    /// <summary>
    /// The name it resolved to rather than the one that was written, which is the whole reason
    /// somebody hovers a simple name in a file whose schema has packages.
    /// </summary>
    [Fact]
    public async Task AMessageTypeShowsTheFullNameEvenWhereASimpleOneWasWritten()
    {
        var source = Source.Replace(
            "var picked: protolang.tests.TopLevelStatus",
            "var picked: TopLevelStatus",
            StringComparison.Ordinal);

        Assert.Contains(
            "protolang.tests.TopLevelStatus",
            await TextAsync(source, EditorFixture.After(source, "var picked: TopLevelSta")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExtendReceiverShowsTheMessageItNames()
    {
        var card = await TextAfterAsync("extend protolang.tests.Out");

        Assert.Contains("protolang.tests.Outer", card, StringComparison.Ordinal);
        Assert.Contains("Message", card, StringComparison.Ordinal);
    }

    // ------- what the language is doing here

    /// <summary>
    /// The arithmetic policy is a choice <c>protolang.config.xml</c> made that the source text does
    /// not show. The generated file's header says it for a reader of the output; this is the only
    /// place a reader of the input can learn it.
    /// </summary>
    [Fact]
    public async Task AnArithmeticOperationStatesTheOverflowPolicyInForce()
        => Assert.Contains(
            "Overflow wraps, two's complement (spec 10.1).",
            await TextAsync(Source, EditorFixture.At(Source, "* scale")),
            StringComparison.Ordinal);

    [Fact]
    public async Task AnIntegerDivisionStatesWhatAZeroDivisorDoes()
    {
        var card = await TextAsync(Source, EditorFixture.At(Source, "/ scale"));

        Assert.Contains("Integer division.", card, StringComparison.Ordinal);
        Assert.Contains("declared `on_zero` value", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConversionStatesWhatAnUnrepresentableValueDoes()
        => Assert.Contains(
            "truncates toward zero",
            await TextAsync(Source, EditorFixture.At(Source, "as int64")),
            StringComparison.Ordinal);

    /// <summary>
    /// The policy a project chose, not the one this repository defaults to. A card that always said
    /// "wraps" would pass every test above and tell every checked project the wrong thing.
    /// </summary>
    [Theory]
    [InlineData("Checked", "terminates the program")]
    [InlineData("Saturating", "clamps to the type's bounds")]
    public async Task TheOverflowPolicyStatedIsTheOneTheProjectConfigured(string policy, string expected)
    {
        var (documents, uri) = EditorFixture.Open(Source);

        File.WriteAllText(
            Path.Combine(uri.Directory!, "protolang.config.xml"),
            $"<ProtoLang><Arithmetic><Overflow>{policy}</Overflow></Arithmetic></ProtoLang>");

        var provider = new HoverProvider(documents, EditorFixture.Configuration(), EditorFixture.Loaders());
        var asked = provider.Read(EditorFixture.Ask(uri, Source, EditorFixture.At(Source, "* scale")));

        Assert.NotNull(asked);

        var card = await provider.AnswerAsync(asked!, CancellationToken.None);

        Assert.NotNull(card);
        Assert.Contains(expected, card!.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 13.1: an unset singular message field has no value to read. It is invisible in the source
    /// and belongs to a declaration in a <c>.proto</c> the reader may never have opened.
    /// </summary>
    [Fact]
    public async Task AMessageTypedFieldSaysThatReadingItNeedsAnEstablishedGuard()
    {
        var source = Source.Replace(
            "return has optional_count;", "return has inner;", StringComparison.Ordinal);

        Assert.Contains(
            "requires an established presence test",
            await TextAsync(source, EditorFixture.After(source, "return has inn")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScalarFieldSaysNothingAboutPresence()
        => Assert.DoesNotContain(
            "presence test", await TextAfterAsync("var doubled: int64 = cou"), StringComparison.Ordinal);

    // ------- where there is nothing to say

    /// <summary>
    /// An empty card is worse than none: the client draws a box over the code and the reader learns
    /// that the server is running rather than what the name means.
    /// </summary>
    [Theory]
    [InlineData("import proto \"fixtures.proto\";\n")]
    [InlineData("var label_length: int64 = 0;")]
    public async Task NothingSensibleToSayMeansNoCardAtAll(string marker)
        => Assert.Null(await CardAsync(Source, EditorFixture.After(Source, marker)));

    [Fact]
    public async Task ANameThatDoesNotResolveHasNoCard()
    {
        var source = Source.Replace("count * scale", "nonexistent * scale", StringComparison.Ordinal);

        Assert.Null(await CardAsync(source, EditorFixture.After(source, "= nonexist")));
    }

    // ------- what it is about

    /// <summary>
    /// So the editor highlights what is being described rather than whatever it believes the word
    /// under the cursor to be -- which for a qualified name is one segment of a name the card is
    /// describing all of.
    /// </summary>
    [Fact]
    public async Task TheRangeCoversTheWholeNameBeingDescribed()
    {
        var card = await CardAsync(Source, EditorFixture.After(Source, "extend protolang.tests.Out"));
        var start = EditorFixture.At(Source, "protolang.tests.Outer {");

        Assert.NotNull(card);
        Assert.NotNull(card!.Range);
        Assert.Equal(
            EditorPositions.Between(new Diagnostics.LineMap(Source), start, start + "protolang.tests.Outer".Length),
            card.Range);
    }

    /// <summary>
    /// Navigation and explanation are most wanted when something is broken, so nothing here may
    /// depend on the rest of the file compiling.
    /// </summary>
    [Fact]
    public async Task ACardIsStillProducedInAFileWithErrorsElsewhere()
    {
        var source = Source.Replace(
            "return doubled + shared + narrowed;",
            "return doubled + nonexistent(;",
            StringComparison.Ordinal);

        Assert.Contains(
            "doubled: int64",
            await TextAsync(source, EditorFixture.After(source, "var doubl")),
            StringComparison.Ordinal);
    }
}
