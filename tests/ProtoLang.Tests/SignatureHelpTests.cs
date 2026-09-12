using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// What the editor shows while a call's arguments are being typed: which method, and which argument
/// the caret is supplying.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="SignatureHelpProvider"/> rather than through a server, for the reason
/// <see cref="HoverTests"/> gives: what is being asserted is the panel.
/// </para>
/// <para>
/// Most of these sources do not parse, and that is deliberate rather than incidental. A call with a
/// closing parenthesis is a call the author has finished writing, and signature help is for the one
/// they have not -- so a suite made of well-formed calls would be testing the state this feature is
/// never invoked in.
/// </para>
/// </remarks>
public class SignatureHelpTests
{
    private const string Source =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn scaled(factor: int64, label: string) -> int64 {
                return factor;
            }

            fn plain() -> int64 {
                return 1;
            }

            fn calls() -> int64 {
                return scaled(1, "x");
            }
        }
        """;

    /// <summary>The fixture with the body of <c>calls</c> replaced, closing braces and all.</summary>
    /// <remarks>
    /// So that an unterminated call can be written as the one line it is, rather than as a second
    /// copy of the whole fixture with one character missing.
    /// </remarks>
    private static string Typing(string statement)
        => Source.Replace("        return scaled(1, \"x\");\n", statement, StringComparison.Ordinal);

    private static async Task<SignatureHelp?> HelpAsync(string text, int offset)
    {
        var (documents, uri) = EditorFixture.Open(text);

        var provider = new SignatureHelpProvider(
            documents, EditorFixture.Configuration(), EditorFixture.Loaders());

        var asked = provider.Read(EditorFixture.Ask(uri, text, offset));

        Assert.NotNull(asked);

        return await provider.AnswerAsync(asked!, CancellationToken.None);
    }

    private static async Task<SignatureHelp> ShownAsync(string text, string marker)
    {
        var help = await HelpAsync(text, EditorFixture.After(text, marker));

        Assert.NotNull(help);
        return help!;
    }

    // ------------------------------------------------------- which method

    /// <summary>The panel shows the signature a hover would, because there is one spelling of it.</summary>
    [Fact]
    public async Task TheSignatureIsTheOneWrittenInTheDeclaration()
    {
        var help = await ShownAsync(Source, "return scaled(");

        var signature = Assert.Single(help.Signatures);

        Assert.Equal("fn scaled(factor: int64, label: string) -> int64", signature.Label);
        Assert.Equal(0, help.ActiveSignature);
    }

    /// <summary>
    /// A call with no closing parenthesis still names its method, which is the case that matters.
    /// </summary>
    /// <remarks>
    /// The binder cannot produce a call node here -- the argument count is wrong the moment a comma is
    /// typed, and the node it falls back to carries no target. What survives is the reference to the
    /// method name, recorded before the arity check that failed, and that is what this reads.
    /// </remarks>
    [Theory]
    [InlineData("        return scaled(\n")]
    [InlineData("        return scaled(1,\n")]
    [InlineData("        return scaled(1, \n")]
    [InlineData("        return scaled(1, \"x\"\n")]
    [InlineData("        return scaled(1, \"x\", 3\n")]
    public async Task AnUnterminatedCallStillNamesItsMethod(string statement)
    {
        var text = Typing(statement);

        // The caret sits where the author stopped typing: the end of the line, before the newline.
        var help = await HelpAsync(text, EditorFixture.After(text, statement.TrimEnd('\n')));

        Assert.NotNull(help);
        Assert.Equal(
            "fn scaled(factor: int64, label: string) -> int64",
            Assert.Single(help!.Signatures).Label);
    }

    // ------------------------------------------------------- which argument

    /// <summary>The active parameter is how many separators lie between the paren and the caret.</summary>
    [Theory]
    [InlineData("return scaled(", 0)]
    [InlineData("return scaled(1", 0)]
    [InlineData("return scaled(1,", 1)]
    [InlineData("return scaled(1, ", 1)]
    [InlineData("return scaled(1, \"x\"", 1)]
    public async Task TheActiveParameterFollowsTheSeparators(string marker, int expected)
    {
        Assert.Equal(expected, (await ShownAsync(Source, marker)).ActiveParameter);
    }

    /// <summary>
    /// Supplying one argument too many points past the last parameter rather than at it.
    /// </summary>
    /// <remarks>
    /// A client renders that by highlighting nothing, which is the honest picture: there is no
    /// parameter for what is being typed. Clamping to the last one would say the opposite.
    /// </remarks>
    [Fact]
    public async Task SupplyingTooManyArgumentsPointsPastTheLastParameter()
    {
        var text = Typing("        return scaled(1, \"x\", 3\n");
        var help = await HelpAsync(text, EditorFixture.After(text, "scaled(1, \"x\", "));

        Assert.NotNull(help);
        Assert.Equal(2, help!.ActiveParameter);
        Assert.Equal(2, Assert.Single(help.Signatures).Parameters.Count);
    }

    /// <summary>A call written inside an argument is the one the caret is supplying.</summary>
    [Fact]
    public async Task ANestedCallAnswersAboutTheInnerOne()
    {
        var text = Typing("        return scaled(plain(\n");
        var help = await HelpAsync(text, EditorFixture.After(text, "scaled(plain("));

        Assert.NotNull(help);
        Assert.Equal("fn plain() -> int64", Assert.Single(help!.Signatures).Label);
        Assert.Equal(0, help.ActiveParameter);
    }

    /// <summary>
    /// A parenthesis that groups is not a call, and the call around it is still the answer.
    /// </summary>
    /// <remarks>
    /// The commas belong to the frame they were written in, so a grouping parenthesis neither becomes
    /// the subject nor lets its contents count against the call outside it.
    /// </remarks>
    [Fact]
    public async Task AGroupingParenthesisDoesNotBecomeTheCall()
    {
        var text = Typing("        return scaled((1 + 2\n");
        var help = await HelpAsync(text, EditorFixture.After(text, "(1 + 2"));

        Assert.NotNull(help);
        Assert.Equal(
            "fn scaled(factor: int64, label: string) -> int64",
            Assert.Single(help!.Signatures).Label);
        Assert.Equal(0, help.ActiveParameter);
    }

    // ------------------------------------------------------- nothing rather than a guess

    /// <summary>A caret that is inside no call, or inside one that names nothing, shows no panel.</summary>
    [Theory]
    [InlineData("        return nosuchmethod(1\n", "nosuchmethod(")]
    [InlineData("        return factor(1\n", "factor(")]
    public async Task ACallThatNamesNoMethodShowsNothing(string statement, string marker)
    {
        var text = Typing(statement);

        Assert.Null(await HelpAsync(text, EditorFixture.After(text, marker)));
    }

    /// <summary>Outside any parentheses there is no call to describe.</summary>
    [Theory]
    [InlineData("return factor;")]
    [InlineData("extend Outer")]
    [InlineData("import proto")]
    public async Task ACaretOutsideAnyCallShowsNothing(string marker)
    {
        Assert.Null(await HelpAsync(Source, EditorFixture.After(Source, marker)));
    }

    /// <summary>A method's own parameter list is a declaration, not a call.</summary>
    /// <remarks>
    /// Lexically the two are indistinguishable -- <c>fn scaled(</c> is an identifier before an open
    /// parenthesis exactly as <c>scaled(</c> is -- so what separates them is what the binder recorded
    /// about the name. Describing a method to the author still writing its parameter list would be
    /// showing them what they have typed so far as though it were settled.
    /// </remarks>
    [Fact]
    public async Task ADeclarationsOwnParameterListShowsNothing()
    {
        Assert.Null(await HelpAsync(Source, EditorFixture.After(Source, "fn scaled(")));
        Assert.Null(await HelpAsync(Source, EditorFixture.After(Source, "fn scaled(factor: int64, ")));
    }

    /// <summary>Once the call is closed the caret is outside it again, which dismisses the panel.</summary>
    [Fact]
    public async Task ACaretPastTheClosingParenthesisShowsNothing()
    {
        Assert.Null(await HelpAsync(Source, EditorFixture.After(Source, "scaled(1, \"x\")")));
    }

    // ------------------------------------------------------- the parameters inside the line

    /// <summary>
    /// Each parameter's range picks that parameter out of the label the panel is showing.
    /// </summary>
    /// <remarks>
    /// Computed from the label rather than written out, so this says what a client would do with what
    /// it was sent: take the two numbers and cut that much out of the line.
    /// </remarks>
    [Fact]
    public async Task EachParameterRangeCutsThatParameterOutOfTheLabel()
    {
        var signature = Assert.Single((await ShownAsync(Source, "return scaled(")).Signatures);

        Assert.Equal(2, signature.Parameters.Count);

        Assert.Equal(
            ["factor: int64", "label: string"],
            signature.Parameters.Select(parameter => Cut(signature.Label, parameter)));
    }

    /// <summary>A method taking nothing offers nothing to point at.</summary>
    [Fact]
    public async Task AMethodWithNoParametersOffersNoParameterRanges()
    {
        var text = Typing("        return plain(\n");
        var help = await HelpAsync(text, EditorFixture.After(text, "return plain("));

        Assert.NotNull(help);

        var signature = Assert.Single(help!.Signatures);

        Assert.Equal("fn plain() -> int64", signature.Label);
        Assert.Empty(signature.Parameters);
    }

    /// <summary>
    /// Two parameters spelled the same way get two different ranges, which is why ranges are sent.
    /// </summary>
    /// <remarks>
    /// The language refuses the declaration and binds the method anyway, so this is a signature a
    /// reader can actually be looking at. A client told to find the parameter's text in the line would
    /// point at the first of the two whichever one the caret was supplying.
    /// </remarks>
    [Fact]
    public async Task TwoParametersSpelledAlikeArePointedAtSeparately()
    {
        const string Twice =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn twice(same: int64, same: int64) -> int64 {
                    return 1;
                }

                fn calls() -> int64 {
                    return twice(1,
                }
            }
            """;

        var help = await HelpAsync(Twice, EditorFixture.After(Twice, "twice(1,"));

        Assert.NotNull(help);

        var signature = Assert.Single(help!.Signatures);

        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal(2, signature.Parameters.Count);
        Assert.NotEqual(signature.Parameters[0].Label, signature.Parameters[1].Label);
        Assert.All(
            signature.Parameters,
            parameter => Assert.Equal("same: int64", Cut(signature.Label, parameter)));
    }

    private static string Cut(string label, ParameterInformation parameter)
    {
        Assert.Equal(2, parameter.Label.Count);

        return label[parameter.Label[0]..parameter.Label[1]];
    }
}
