using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Classification refined by the binder: what each identifier turns out to name, what else is true
/// of the place it was written, and what happens when nothing bound (spec 6.5).
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="ClassificationProvider"/> rather than through a server, for the reason
/// <see cref="HoverTests"/> gives: what is being asserted is the colour. The wire, the lifecycle and
/// the capability negotiation belong to <see cref="LanguageServerTests"/>.
/// </para>
/// <para>
/// The lexical half -- the answer with nothing bound, the encoding, comments, line splitting -- is
/// <see cref="SemanticTokenTests"/>'s and is not repeated here.
/// </para>
/// </remarks>
public class SemanticRefinementTests
{
    /// <summary>
    /// One of everything the binder can resolve, with no spelling used for two different things.
    /// </summary>
    /// <remarks>
    /// The one-spelling-one-meaning property is what lets <see cref="EveryNameIsColouredByWhatItNames"/>
    /// sweep the whole file from a table instead of sampling. Names written in more than one form --
    /// a qualified message name, an enum reached through its package -- get tests of their own, since
    /// their whole difficulty is that one name is several tokens.
    /// </remarks>
    private const string Source =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn scaled(scale: int64) -> int64 {
                var doubled: int64 = count * scale;

                for value in nested_values {
                    doubled = doubled + 1;
                }

                return doubled;
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

    /// <summary>What the server would send a client that can paint the whole legend.</summary>
    private static async Task<IReadOnlyList<int>> DataAsync(string text, ClientLegend? client = null)
    {
        var (provider, _, uri) = Opened(text, client: client);

        var answer = await AskAsync(provider, uri);

        return Assert.IsType<SemanticTokens>(answer).Data;
    }

    private static async Task<List<PaintedToken>> PaintAsync(string text, ClientLegend? client = null)
        => PaintedToken.Decode(await DataAsync(text, client));

    /// <summary>Every identifier in <paramref name="text"/>, in the order it was written.</summary>
    /// <remarks>
    /// Identified by the text each token covers rather than by a category, so that a token which
    /// stopped being classified at all is a missing entry rather than a silently shorter sweep.
    /// </remarks>
    private static async Task<List<(string Text, PaintedToken Token)>> NamesAsync(
        string text, ClientLegend? client = null)
    {
        var names = new List<(string, PaintedToken)>();

        foreach (var token in await PaintAsync(text, client))
        {
            var written = token.TextIn(text);

            if (written.Length > 0 && (char.IsLetter(written[0]) || written[0] == '_')
                && !Syntax.Lexer.Keywords.ContainsKey(written))
            {
                names.Add((written, token));
            }
        }

        return names;
    }

    private static (ClassificationProvider Provider, DocumentStore Documents, DocumentUri Uri) Opened(
        string text, bool deltas = false, ClientLegend? client = null)
    {
        var (documents, uri) = EditorFixture.Open(text);

        var provider = new ClassificationProvider(
            documents, EditorFixture.Configuration(), EditorFixture.Loaders())
        {
            Deltas = deltas,
        };

        if (client is not null)
        {
            provider.Client = client;
        }

        return (provider, documents, uri);
    }

    private static async Task<object?> AskAsync(
        ClassificationProvider provider, DocumentUri uri, string? previousResultId = null)
    {
        var identifier = new TextDocumentIdentifier { Uri = uri.ToString() };

        var asked = previousResultId is null
            ? provider.Read(new SemanticTokensParams { TextDocument = identifier })
            : provider.Read(new SemanticTokensDeltaParams
            {
                TextDocument = identifier,
                PreviousResultId = previousResultId,
            });

        Assert.NotNull(asked);

        return await provider.AnswerAsync(asked!, CancellationToken.None);
    }

    /// <summary>
    /// The first place <paramref name="written"/> appears, having checked that every place it appears
    /// was coloured the same way.
    /// </summary>
    /// <remarks>
    /// The fixture gives each spelling one meaning, so every occurrence of a name agreeing with every
    /// other is a property worth asserting wherever a test names one -- a field reached bare in a
    /// method and set again in a test fixture is one symbol, and colouring it two ways would be the
    /// overlay losing its place.
    /// </remarks>
    private static PaintedToken Coloured(
        IEnumerable<(string Text, PaintedToken Token)> names, string written)
    {
        var found = names.Where(name => name.Text == written).Select(name => name.Token).ToList();

        Assert.True(found.Count > 0, $"the fixture must write '{written}'");
        Assert.All(found, token => Assert.Equal(found[0].Type, token.Type));

        return found[0];
    }

    // ------------------------------------------------------- what a name turns out to be

    /// <summary>
    /// The sweep: every identifier in a file that binds completely is coloured by the thing it names.
    /// </summary>
    /// <remarks>
    /// A table rather than a sample, because the failure this is guarding against is one token the
    /// overlay walked past -- which no amount of sampling elsewhere in the file would show. The last
    /// two assertions are what keep the sweep honest: the table must be exhausted, so a category that
    /// stopped appearing cannot pass by never being asked about.
    /// </remarks>
    [Fact]
    public async Task EveryNameIsColouredByWhatItNames()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Outer"] = SemanticTokenLegend.Class,
            ["scaled"] = SemanticTokenLegend.Method,
            ["scale"] = SemanticTokenLegend.Parameter,
            ["count"] = SemanticTokenLegend.Property,
            ["nested_values"] = SemanticTokenLegend.Property,
            ["state"] = SemanticTokenLegend.Method,
            ["TopLevelStatus"] = SemanticTokenLegend.Enum,
            ["TOP_LEVEL_STATUS_OK"] = SemanticTokenLegend.EnumMember,

            // A local and the name a loop binds are both `variable`; what separates them is the
            // readonly bit, which ALoopBindingIsAVariableThatCannotBeAssigned asserts.
            ["doubled"] = SemanticTokenLegend.Variable,
            ["value"] = SemanticTokenLegend.Variable,
        };

        var names = await NamesAsync(Source);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (written, token) in names)
        {
            Assert.True(
                expected.TryGetValue(written, out var category),
                $"the fixture wrote '{written}', which this table does not account for");

            Assert.True(
                category == token.Type,
                $"'{written}' is a {category} and was coloured as a {token.Type}");

            seen.Add(written);
        }

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A bare name that silently means a field of the receiver is coloured as a field.
    /// </summary>
    /// <remarks>
    /// The single most valuable classification in the language, and the one the token stream cannot
    /// reach: `count` and `doubled` are the same shape to a lexer, and one of them is storage on the
    /// message this method extends.
    /// </remarks>
    [Fact]
    public async Task AnImplicitReceiverFieldIsAFieldAndNotALocal()
    {
        var names = await NamesAsync(Source);

        Assert.Equal(SemanticTokenLegend.Property, Coloured(names, "count").Type);
        Assert.Equal(SemanticTokenLegend.Variable, Coloured(names, "doubled").Type);
    }

    /// <summary>A field reached through a receiver is the same thing as one reached bare.</summary>
    [Fact]
    public async Task AFieldIsAFieldWhicheverWayItIsReached()
    {
        const string Reached =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn deep() -> int64 {
                    var held: Inner = inner;

                    return count;
                }
            }
            """;

        var names = await NamesAsync(Reached);

        Assert.Equal(SemanticTokenLegend.Property, Coloured(names, "inner").Type);
        Assert.Equal(SemanticTokenLegend.Property, Coloured(names, "count").Type);
        Assert.Equal(SemanticTokenLegend.Class, Coloured(names, "Inner").Type);
    }

    // ------------------------------------------------------- what else is true of the place

    /// <summary>Where a name was introduced is distinguishable from where it was used.</summary>
    [Fact]
    public async Task ADeclarationIsMarkedAndAUseIsNot()
    {
        var names = await NamesAsync(Source);

        var declaration = names.First(name => name.Text == "scale").Token;
        var use = names.Last(name => name.Text == "scale").Token;

        Assert.True(declaration.Has(SemanticTokenLegend.Declaration), "`scale` is declared here");
        Assert.False(use.Has(SemanticTokenLegend.Declaration), "the last `scale` is an argument, not a declaration");
    }

    /// <summary>An assignment is distinguishable from a read of the same name.</summary>
    /// <remarks>
    /// Both halves of `doubled = doubled + 1` are the same symbol and the same spelling, so nothing
    /// but the binder's record of what the reference does can tell them apart.
    /// </remarks>
    [Fact]
    public async Task AnAssignmentIsMarkedAndTheReadBesideItIsNot()
    {
        const string Assigned =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn stepped() -> int64 {
                    var total: int64 = 0;
                    total = total + 1;

                    return total;
                }
            }
            """;

        var written = (await NamesAsync(Assigned)).Where(name => name.Text == "total").ToList();

        Assert.Equal(4, written.Count);
        Assert.True(written[0].Token.Has(SemanticTokenLegend.Declaration), "the var declares it");
        Assert.True(written[1].Token.Has(SemanticTokenLegend.Modification), "the target is assigned");
        Assert.False(written[2].Token.Has(SemanticTokenLegend.Modification), "the operand is read");
        Assert.False(written[3].Token.Has(SemanticTokenLegend.Modification), "the return reads it");
    }

    /// <summary>
    /// The name a loop binds is a variable that cannot be assigned, which is the whole of what
    /// separates it from a local.
    /// </summary>
    /// <remarks>
    /// LSP publishes no category for a loop binding and spec 6.5 fixed the set, so the distinction has
    /// to live in a modifier. It is not a workaround: spec 18 makes a local the only thing a method
    /// may assign, so the bit is simply true.
    /// </remarks>
    [Fact]
    public async Task ALoopBindingIsAVariableThatCannotBeAssigned()
    {
        var names = await NamesAsync(Source);

        var binding = Coloured(names, "value");
        var local = names.First(name => name.Text == "doubled").Token;

        Assert.Equal(SemanticTokenLegend.Variable, binding.Type);
        Assert.True(binding.Has(SemanticTokenLegend.ReadOnly), "a loop binding cannot be assigned");
        Assert.False(local.Has(SemanticTokenLegend.ReadOnly), "a local is the one thing that can be");
    }

    /// <summary>Everything a method may not assign says so, and a method and a type do not.</summary>
    /// <remarks>
    /// A sweep over the categories rather than a case each, because the property is about the whole
    /// mapping: `readonly` describes a place a value could have been stored and was not allowed to be,
    /// and saying it of a method or a message would be saying something of everything.
    /// </remarks>
    [Fact]
    public async Task OnlyAPlaceAValueCouldBeStoredIsCalledReadOnly()
    {
        var storage = new[]
        {
            SemanticTokenLegend.Parameter, SemanticTokenLegend.Property, SemanticTokenLegend.EnumMember,
        };

        var elsewhere = new[]
        {
            SemanticTokenLegend.Method, SemanticTokenLegend.Class, SemanticTokenLegend.Enum,
        };

        var names = await NamesAsync(Source);

        Assert.All(
            names.Where(name => storage.Contains(name.Token.Type, StringComparer.Ordinal)),
            name => Assert.True(
                name.Token.Has(SemanticTokenLegend.ReadOnly),
                $"'{name.Text}' is a {name.Token.Type}, which spec 18 forbids assigning"));

        Assert.All(
            names.Where(name => elsewhere.Contains(name.Token.Type, StringComparer.Ordinal)),
            name => Assert.False(
                name.Token.Has(SemanticTokenLegend.ReadOnly),
                $"'{name.Text}' is a {name.Token.Type}, which is not a place a value is kept"));
    }

    // ------------------------------------------------------- agreeing with the binder

    /// <summary>
    /// A value in scope wins over an enum type spelled the same way, because that is what the binder
    /// does with the name.
    /// </summary>
    /// <remarks>
    /// The case that breaks an implementation which classifies by looking names up itself: an
    /// implementation that asked "is there an enum called TopLevelStatus?" would colour this one
    /// `enum` and be confidently wrong. Nothing here asks -- the colour is the binder's own record of
    /// what it resolved, which is also what #43's completion inherits and what the backends emit, so
    /// the three cannot disagree. Spec 12 states the rule.
    /// </remarks>
    [Fact]
    public async Task AValueInScopeIsColouredAsTheValueAndNotAsTheEnumItShadows()
    {
        const string Shadowed =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn f() -> Deep {
                    if not has inner {
                        return Deep.DEEP_NONE;
                    }

                    var TopLevelStatus: Inner = inner;
                    return TopLevelStatus.deep;
                }
            }
            """;

        var names = await NamesAsync(Shadowed);
        var shadowing = names.Where(name => name.Text == "TopLevelStatus").ToList();

        Assert.Equal(2, shadowing.Count);
        Assert.All(shadowing, name => Assert.Equal(SemanticTokenLegend.Variable, name.Token.Type));

        // And the member reached through it is a field of Inner rather than a constant of the enum.
        Assert.Equal(SemanticTokenLegend.Property, Coloured(names, "deep").Type);
    }

    /// <summary>
    /// A name written in several tokens colours every one of them, and the dots stay unclassified.
    /// </summary>
    /// <remarks>
    /// A qualified name is one name to the parser and one reference to the binder, so the overlay has
    /// to work by containment rather than by matching a token to a range. Spec 6.5 keeps the member
    /// dot unclassified, and that has to survive a name written across one.
    /// </remarks>
    [Fact]
    public async Task AQualifiedNameColoursEveryPartOfItselfAndNoneOfTheDots()
    {
        const string Qualified =
            """
            import proto "fixtures.proto";

            extend protolang.tests.Outer {
                fn state() -> protolang.tests.TopLevelStatus {
                    return protolang.tests.TopLevelStatus.TOP_LEVEL_STATUS_OK;
                }
            }
            """;

        var painted = await PaintAsync(Qualified);

        Assert.DoesNotContain(painted, token => token.TextIn(Qualified) == ".");

        var extending = painted
            .Where(token => token.Line == 2 && token.TextIn(Qualified) is "protolang" or "tests" or "Outer")
            .ToList();

        Assert.Equal(3, extending.Count);
        Assert.All(extending, token => Assert.Equal(SemanticTokenLegend.Class, token.Type));

        var enumerated = painted
            .Where(token => token.Line == 3 && token.TextIn(Qualified) is "protolang" or "tests" or "TopLevelStatus")
            .ToList();

        Assert.Equal(3, enumerated.Count);
        Assert.All(enumerated, token => Assert.Equal(SemanticTokenLegend.Enum, token.Type));
    }

    // ------------------------------------------------------- degrading rather than vanishing

    /// <summary>
    /// A file that does not parse keeps every token it had, and gains refinement wherever the binder
    /// still reached.
    /// </summary>
    /// <remarks>
    /// The moment a reader most needs the colour is the moment the file is broken, so this is the
    /// property the whole design is arranged around. The binder runs through parse errors (#36), so
    /// the answer here is not merely the lexical one.
    /// </remarks>
    [Fact]
    public async Task ABrokenFileKeepsEveryTokenAndStillRefinesWhatBound()
    {
        const string Broken =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn scaled(scale: int64) -> int64 {
                    var doubled: int64 = count * scale;
                    return doubled +
                }
            }
            """;

        var names = await NamesAsync(Broken);

        Assert.Equal(SemanticTokenLegend.Class, Coloured(names, "Outer").Type);
        Assert.Equal(SemanticTokenLegend.Property, Coloured(names, "count").Type);
        Assert.Contains(names, name => name.Text == "scale" && name.Token.Type == SemanticTokenLegend.Parameter);

        // And nothing was dropped: the lexical pass over the same text finds the same tokens.
        Assert.Equal(
            PaintedToken.Decode(SemanticTokenEncoder.Encode(Broken, "source.protolang").Data).Count,
            (await PaintAsync(Broken)).Count);
    }

    /// <summary>
    /// A file whose schema cannot be loaded is classified completely, and lexically.
    /// </summary>
    /// <remarks>
    /// Nothing binds without descriptors, so there is nothing to refine and the answer is exactly what
    /// #42 shipped. Losing the colouring instead would punish a reader for a broken import.
    /// </remarks>
    [Fact]
    public async Task AFileWhoseSchemaIsMissingIsStillClassifiedLexically()
    {
        const string Unloadable =
            """
            import proto "nothing_here.proto";

            extend Outer {
                fn scaled(scale: int64) -> int64 {
                    return scale;
                }
            }
            """;

        Assert.Equal(
            SemanticTokenEncoder.Encode(Unloadable, "source.protolang").Data,
            await DataAsync(Unloadable));
    }

    // ------------------------------------------------------- what this client can paint

    /// <summary>
    /// A category the client never declared degrades to the answer it was already being sent.
    /// </summary>
    /// <remarks>
    /// A client that meets a category it has no rule for paints the token with nothing at all, so
    /// publishing one it did not ask for takes colour away rather than adding it. What it degrades to
    /// is #42's answer and never less: spec 6.5 says classification never fails.
    /// </remarks>
    [Fact]
    public async Task ACategoryTheClientCannotPaintFallsBackToTheLexicalAnswer()
    {
        var lexical = ClientLegend.Of(new SemanticTokensClientCapabilities
        {
            TokenTypes =
            [
                SemanticTokenLegend.Variable, SemanticTokenLegend.Keyword, SemanticTokenLegend.String,
                SemanticTokenLegend.Number, SemanticTokenLegend.Comment, SemanticTokenLegend.Operator,
            ],
        });

        var names = await NamesAsync(Source, lexical);

        Assert.All(names, name => Assert.Equal(SemanticTokenLegend.Variable, name.Token.Type));

        // The whole document is still classified, not merely the identifiers in it.
        Assert.Equal(
            SemanticTokenEncoder.Encode(Source, "source.protolang").Data.Count,
            (await DataAsync(Source, lexical)).Count);
    }

    /// <summary>A client that declared some categories keeps those and loses the rest.</summary>
    [Fact]
    public async Task OnlyTheCategoriesTheClientNamedAreSent()
    {
        var partial = ClientLegend.Of(new SemanticTokensClientCapabilities
        {
            TokenTypes = [SemanticTokenLegend.Variable, SemanticTokenLegend.Property, SemanticTokenLegend.Keyword],
        });

        var names = await NamesAsync(Source, partial);

        Assert.Equal(SemanticTokenLegend.Property, Coloured(names, "count").Type);
        Assert.Equal(SemanticTokenLegend.Variable, Coloured(names, "scale").Type);
        Assert.Equal(SemanticTokenLegend.Variable, Coloured(names, "Outer").Type);
    }

    /// <summary>A modifier the client never declared is not sent either.</summary>
    [Fact]
    public async Task OnlyTheModifiersTheClientNamedAreSent()
    {
        var partial = ClientLegend.Of(new SemanticTokensClientCapabilities
        {
            TokenModifiers = [SemanticTokenLegend.Declaration],
        });

        var names = await NamesAsync(Source, partial);
        var binding = Coloured(names, "value");

        Assert.True(binding.Has(SemanticTokenLegend.Declaration), "declaration was asked for");
        Assert.False(binding.Has(SemanticTokenLegend.ReadOnly), "readonly was not");
    }

    /// <summary>
    /// A client that stated no lists at all is taken to support the standard set.
    /// </summary>
    /// <remarks>
    /// LSP requires the lists of a client that asks for semantic tokens, so an absent one means the
    /// capability was not filled in rather than that nothing is supported. Reading it the other way
    /// would switch the feature off for every such client in silence -- including this repository's
    /// own, which is exactly why it is worth a test rather than an argument.
    /// </remarks>
    [Fact]
    public async Task AClientThatStatedNoLegendIsSentTheWholeOne()
    {
        var silent = ClientLegend.Of(new SemanticTokensClientCapabilities());

        Assert.Equal(
            SemanticTokenLegend.Property,
            Coloured(await NamesAsync(Source, silent), "count").Type);
    }

    // ------------------------------------------------------- differences rather than whole answers

    /// <summary>Nothing is kept, and no answer is named, for a client that never asked for a delta.</summary>
    [Fact]
    public async Task NothingIsRetainedForAClientThatDoesNotWantDeltas()
    {
        var (provider, _, uri) = Opened(Source);

        var answer = Assert.IsType<SemanticTokens>(await AskAsync(provider, uri));

        Assert.Null(answer.ResultId);
        Assert.Equal(0, provider.Retained);
    }

    /// <summary>
    /// Applying the edits to the answer the client holds produces exactly the answer for the new text.
    /// </summary>
    /// <remarks>
    /// The one property a delta has to have, swept over the shapes an edit takes. Compared against a
    /// whole answer the server itself produces for the edited buffer rather than against a
    /// hand-written array -- so what is asserted is that the two paths agree, which is the only thing
    /// a client can rely on.
    /// </remarks>
    [Theory]
    [InlineData("var doubled: int64 = count * scale;", "var doubled: int64 = count * scale + 1;")]
    [InlineData("    return doubled;\n", "    return doubled;\n    // added\n")]
    [InlineData("        for value in nested_values {\n            doubled = doubled + 1;\n        }\n\n", "")]
    [InlineData("import proto \"fixtures.proto\";", "import proto \"fixtures.proto\"; // moved")]
    public async Task ApplyingADeltaProducesExactlyTheWholeAnswerForTheNewText(string before, string after)
    {
        var edited = Source.Replace(before, after, StringComparison.Ordinal);
        Assert.NotEqual(Source, edited);

        var (provider, documents, uri) = Opened(Source, deltas: true);

        var first = Assert.IsType<SemanticTokens>(await AskAsync(provider, uri));
        Assert.NotNull(first.ResultId);

        documents.Apply(uri, 2, [new TextDocumentContentChangeEvent { Text = edited }]);

        var delta = Assert.IsType<SemanticTokensDelta>(await AskAsync(provider, uri, first.ResultId));

        Assert.Equal(await DataAsync(edited), Applied(first.Data, delta));
    }

    /// <summary>Every edit begins and ends on a token boundary.</summary>
    /// <remarks>
    /// A sweep, because an edit that split a token's five integers would leave the client decoding
    /// nonsense from that point to the end of the file -- and would still look plausible in any test
    /// that only compared the reassembled array.
    /// </remarks>
    [Theory]
    [InlineData("var doubled: int64 = count * scale;", "var doubled: int64 = count;")]
    [InlineData("    return doubled;\n", "    return doubled + 1;\n")]
    [InlineData("fn state() -> TopLevelStatus {", "fn state2() -> TopLevelStatus {")]
    public async Task EveryEditStartsAndEndsOnATokenBoundary(string before, string after)
    {
        var edited = Source.Replace(before, after, StringComparison.Ordinal);
        Assert.NotEqual(Source, edited);

        var (provider, documents, uri) = Opened(Source, deltas: true);

        var first = Assert.IsType<SemanticTokens>(await AskAsync(provider, uri));

        documents.Apply(uri, 2, [new TextDocumentContentChangeEvent { Text = edited }]);

        var delta = Assert.IsType<SemanticTokensDelta>(await AskAsync(provider, uri, first.ResultId));

        Assert.All(delta.Edits, edit =>
        {
            Assert.Equal(0, edit.Start % SemanticTokens.IntegersPerToken);
            Assert.Equal(0, edit.DeleteCount % SemanticTokens.IntegersPerToken);
            Assert.Equal(0, (edit.Data?.Count ?? 0) % SemanticTokens.IntegersPerToken);
        });
    }

    /// <summary>A buffer nobody touched is a delta with nothing in it.</summary>
    [Fact]
    public async Task AnUntouchedBufferProducesNoEdits()
    {
        var (provider, _, uri) = Opened(Source, deltas: true);

        var first = Assert.IsType<SemanticTokens>(await AskAsync(provider, uri));
        var delta = Assert.IsType<SemanticTokensDelta>(await AskAsync(provider, uri, first.ResultId));

        Assert.Empty(delta.Edits);
        Assert.NotNull(delta.ResultId);
    }

    /// <summary>
    /// A client naming an answer this server does not hold is sent a whole one instead.
    /// </summary>
    /// <remarks>
    /// Which is what happens after a restart, after a close and reopen, and after an answer that was
    /// produced and then refused. It is the reason a retained answer is paired with the name it was
    /// published under: an identifier the client cannot have received matches nothing it can say, so
    /// the worst a mismatch costs is one whole answer.
    /// </remarks>
    [Fact]
    public async Task AnUnknownPreviousAnswerIsRepliedToInFull()
    {
        var (provider, _, uri) = Opened(Source, deltas: true);

        await AskAsync(provider, uri);

        var answer = Assert.IsType<SemanticTokens>(await AskAsync(provider, uri, "not one of ours"));

        Assert.NotEmpty(answer.Data);
        Assert.NotNull(answer.ResultId);
    }

    /// <summary>Closing a document gives back what was being kept for it.</summary>
    [Fact]
    public async Task ClosingADocumentForgetsTheAnswerItWasGiven()
    {
        var (provider, _, uri) = Opened(Source, deltas: true);

        await AskAsync(provider, uri);
        Assert.Equal(1, provider.Retained);

        provider.Forget(uri);
        Assert.Equal(0, provider.Retained);
    }

    private static List<int> Applied(IReadOnlyList<int> previous, SemanticTokensDelta delta)
    {
        var data = new List<int>(previous);

        // Back to front, so an earlier edit's indices still describe the array a later one was
        // measured against. This server sends one edit, and the rule costs nothing to obey anyway.
        foreach (var edit in delta.Edits.OrderByDescending(edit => edit.Start))
        {
            data.RemoveRange(edit.Start, edit.DeleteCount);
            data.InsertRange(edit.Start, edit.Data ?? []);
        }

        return data;
    }
}
