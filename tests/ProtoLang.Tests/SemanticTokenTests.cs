using System.Text.Json;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Lexical classification: what the compiler tells an editor to colour, and how it is encoded
/// (spec 6.5).
/// </summary>
/// <remarks>
/// The properties worth holding onto are that a file which does not parse is still classified, that
/// no token crosses a line, that every token can be found in the text it came from, and that the
/// legend has room for #50 to refine identifiers without renegotiating anything.
/// </remarks>
public class SemanticTokenTests
{
    /// <summary>One token, back in absolute coordinates.</summary>
    private readonly record struct Painted(int Line, int Character, int Length, string Type);

    /// <summary>
    /// Undoes the delta encoding, which is the only way to assert anything about it.
    /// </summary>
    /// <remarks>
    /// Written out here rather than compared against a hand-computed array of integers, because an
    /// expected array says nothing to a reader and has to be recomputed by hand every time the fixture
    /// is touched.
    /// </remarks>
    private static List<Painted> Paint(string text)
    {
        var data = SemanticTokenEncoder.Encode(text, "test.protolang").Data;
        var painted = new List<Painted>();

        var line = 0;
        var character = 0;

        for (var index = 0; index + 4 < data.Count; index += 5)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];

            painted.Add(new Painted(line, character, data[index + 2], SemanticTokenLegend.TokenTypes[data[index + 3]]));
        }

        return painted;
    }

    private static string TextOf(string source, Painted token)
    {
        var lines = new LineMap(source);
        var start = lines.OffsetOf(token.Line + 1, token.Character + 1);

        return source.Substring(start, token.Length);
    }

    // ------------------------------------------------------- the legend

    [Fact]
    public void TheLegendCarriesTheCategoriesThatRefiningIdentifiersWillNeed()
    {
        // #50 turns one uniform identifier category into parameters, properties, methods and enum
        // members. The legend is negotiated once and indexed by position, so a category added later
        // renumbers every category after it: the whole set is declared now, and only part of it used.
        Assert.Contains(SemanticTokenLegend.Parameter, SemanticTokenLegend.TokenTypes);
        Assert.Contains(SemanticTokenLegend.Property, SemanticTokenLegend.TokenTypes);
        Assert.Contains(SemanticTokenLegend.Method, SemanticTokenLegend.TokenTypes);
        Assert.Contains(SemanticTokenLegend.EnumMember, SemanticTokenLegend.TokenTypes);
        Assert.Contains(SemanticTokenLegend.Type, SemanticTokenLegend.TokenTypes);

        Assert.Equal(SemanticTokenLegend.TokenTypes.Count, SemanticTokenLegend.TokenTypes.Distinct().Count());
    }

    [Fact]
    public void TheLegendIsSentToTheClientThatWillIndexIntoIt()
    {
        Assert.Equal(SemanticTokenLegend.TokenTypes, SemanticTokenLegend.Wire.TokenTypes);
        Assert.Equal(SemanticTokenLegend.TokenModifiers, SemanticTokenLegend.Wire.TokenModifiers);
    }

    // ------------------------------------------------------- what gets classified

    [Fact]
    public void KeywordsAreClassifiedFromTheLexersOwnReservedWordList()
    {
        // Every keyword, from the list the lexer resolves against, so a word added to the language
        // cannot end up coloured as an identifier because this file was not updated.
        var source = string.Join(' ', Lexer.Keywords.Keys);

        var painted = Paint(source);

        Assert.Equal(Lexer.Keywords.Count, painted.Count);
        Assert.All(painted, token => Assert.Equal(SemanticTokenLegend.Keyword, token.Type));
    }

    [Fact]
    public void EveryIdentifierIsClassifiedTheSameWay()
    {
        const string Source =
            """
            extend InvoiceItem {
                fn total(rate: int64) -> int64 {
                    var gross: int64 = quantity;
                    return gross * rate;
                }
            }
            """;

        var identifiers = Paint(Source)
            .Where(token => TextOf(Source, token) is "InvoiceItem" or "total" or "rate" or "gross" or "quantity")
            .ToList();

        // A receiver type, a method, a parameter, a local and a field: five different things, and this
        // server does not yet know which is which. A classification that is right sometimes is worse
        // than one that is consistently coarse, because the wrong colour reads as a fact about the code.
        Assert.Equal(7, identifiers.Count);
        Assert.All(identifiers, token => Assert.Equal(SemanticTokenLegend.Variable, token.Type));
    }

    [Fact]
    public void LiteralsAndOperatorsAreClassifiedAndStructuralPunctuationIsNot()
    {
        const string Source = "var x: int64 = 1 + 2; var s: string = \"text\";";

        var painted = Paint(Source);

        Assert.Contains(painted, token => token.Type == SemanticTokenLegend.Number && TextOf(Source, token) == "1");
        Assert.Contains(painted, token => token.Type == SemanticTokenLegend.Operator && TextOf(Source, token) == "+");
        Assert.Contains(painted, token => token.Type == SemanticTokenLegend.String);

        // Braces, semicolons, commas, colons and the member dot get no token at all, so whatever the
        // client's own grammar does with them survives. Nobody learns anything from the colour of a
        // semicolon. Assignment is an operator and does get one.
        Assert.DoesNotContain(painted, token => TextOf(Source, token) is ";" or ":");
        Assert.Contains(painted, token => token.Type == SemanticTokenLegend.Operator && TextOf(Source, token) == "=");
    }

    [Fact]
    public void LexicalTokensAreProducedForAFileThatDoesNotParse()
    {
        // Precisely when a user is staring at the screen. Nothing here parses; all of it colours.
        const string Source = "extend { fn (\n  var 1 = ;\n";

        var painted = Paint(Source);

        Assert.NotEmpty(painted);
        Assert.Contains(painted, token => token.Type == SemanticTokenLegend.Keyword);
    }

    // ------------------------------------------------------- comments

    [Fact]
    public void CommentsAreClassifiedByTheCompilerRatherThanLeftToTheClient()
    {
        const string Source = "// a note\nfn /* and another */ x";

        var comments = Paint(Source).Where(token => token.Type == SemanticTokenLegend.Comment).ToList();

        Assert.Equal(2, comments.Count);
        Assert.Equal("// a note", TextOf(Source, comments[0]));
        Assert.Equal("/* and another */", TextOf(Source, comments[1]));
    }

    [Fact]
    public void ABlockCommentIsSplitAtEveryLineBoundary()
    {
        const string Source = "/* one\ntwo\nthree */";

        var comments = Paint(Source).Where(token => token.Type == SemanticTokenLegend.Comment).ToList();

        // The encoding has no way to express a token that wraps, so a comment that does arrives as one
        // token per line. Publishing it as a single long token silently colours nothing at all.
        Assert.Equal(3, comments.Count);
        Assert.Equal([0, 1, 2], comments.Select(comment => comment.Line));
        Assert.Equal("/* one", TextOf(Source, comments[0]));
        Assert.Equal("three */", TextOf(Source, comments[2]));
    }

    [Fact]
    public void ABlockCommentDoesNotColourTheCarriageReturnThatEndsItsLine()
    {
        const string Source = "/* one\r\ntwo */";

        var comments = Paint(Source).Where(token => token.Type == SemanticTokenLegend.Comment).ToList();

        // A token covering the carriage return draws a box past the end of the visible line.
        Assert.Equal("/* one", TextOf(Source, comments[0]));
    }

    [Fact]
    public void AnUnterminatedBlockCommentIsStillColouredToTheEndOfTheFile()
    {
        const string Source = "fn x\n/* never closed\nstill a comment";

        var comments = Paint(Source).Where(token => token.Type == SemanticTokenLegend.Comment).ToList();

        // The alternative leaves the rest of the file looking like code the compiler is not reading.
        Assert.Equal(2, comments.Count);
        Assert.Equal("still a comment", TextOf(Source, comments[^1]));
    }

    // ------------------------------------------------------- the encoding itself

    [Fact]
    public void EveryTokenIsFoundWhereItSaysItIsAndNoneCrossesALine()
    {
        const string Source =
            """
            // leading
            import proto "invoice.proto";

            /* a block
               comment */
            extend InvoiceItem {
                fn total() -> int64 { return quantity * 2 + 1; }
            }
            """;

        var lines = Source.Split('\n');

        Assert.All(
            Paint(Source),
            token =>
            {
                Assert.InRange(token.Line, 0, lines.Length - 1);
                Assert.True(token.Length > 0, "a zero-length token is not something a client can paint");
                Assert.True(
                    token.Character + token.Length <= lines[token.Line].TrimEnd('\r').Length,
                    $"the token at {token.Line}:{token.Character} runs past the end of its line");
            });
    }

    [Fact]
    public void TokensArriveInOrderSoTheDeltasAreNeverNegative()
    {
        const string Source = "// one\nfn a\n/* two\nthree */ var b";

        var data = SemanticTokenEncoder.Encode(Source, "test.protolang").Data;

        Assert.Equal(0, data.Count % 5);

        for (var index = 0; index < data.Count; index += 5)
        {
            Assert.True(data[index] >= 0, "a line delta may never go backwards");
            Assert.True(data[index + 1] >= 0, "a character delta may never go backwards");
        }
    }

    [Fact]
    public void ColumnsCountUtf16CodeUnitsJustAsTheCompilerDoes()
    {
        // U+1D11E is one rune and two UTF-16 code units, which is what the default position encoding
        // counts. Counting runes instead would shift every token after the first astral character.
        const string Clef = "\U0001D11E";

        var source = $"/* {Clef} */ var a = 1;";

        var painted = Paint(source);
        var variable = painted.First(token => token.Type == SemanticTokenLegend.Variable);

        // Not IndexOf('a'), which finds the one in "var" -- 'var' is a keyword and is not the token
        // being measured.
        Assert.Equal(source.IndexOf("a =", StringComparison.Ordinal), variable.Character);
        Assert.Equal(1, variable.Length);
    }

    // ------------------------------------------------------- through the protocol

    [Fact]
    public async Task SemanticTokensAreAnsweredForAnOpenDocument()
    {
        await using var client = await LanguageServerClient.StartAsync();

        const string Source = "// note\nfn total() -> int64 { return 1; }\n";

        var path = Path.Combine(TestPaths.CreateTempDirectory(), "source.protolang");
        File.WriteAllText(path, Source);

        var uri = new Uri(path).AbsoluteUri;
        client.Notify(Methods.DidOpen, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "protolang",
                Version = 1,
                Text = Source,
            },
        });

        var answer = await client.RequestAsync(
            Methods.SemanticTokensFull,
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        Assert.Equal(
            SemanticTokenEncoder.Encode(Source, uri).Data,
            answer.Deserialize<SemanticTokens>(LspJson.Options)!.Data);
    }

    [Fact]
    public async Task ADocumentThatIsNotOpenIsAnsweredWithNoTokensRatherThanAnError()
    {
        await using var client = await LanguageServerClient.StartAsync();

        var answer = await client.RequestAsync(
            Methods.SemanticTokensFull,
            new SemanticTokensParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = "file:///nothing/here.protolang" },
            });

        // The client may have closed it between asking and being answered, which is not an error.
        Assert.Empty(answer.Deserialize<SemanticTokens>(LspJson.Options)!.Data);
    }
}
