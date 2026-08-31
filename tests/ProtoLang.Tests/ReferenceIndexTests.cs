using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Symbols;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// A declaration can find every use of itself, and every use can find the declaration it means --
/// the two directions of one question, which have to agree.
/// </summary>
/// <remarks>
/// The properties worth asserting here are the ones a name-based implementation gets wrong: two
/// locals called <c>total</c> in sibling blocks are two symbols, one field reached bare and through a
/// receiver is one, and a name written in type position is a use like any other even though the IR
/// keeps no node for it. Ranges get their own sweep, because a range that covers the construct
/// instead of the name is the failure that looks correct in every screenshot and corrupts every
/// rename.
/// </remarks>
public class ReferenceIndexTests
{
    /// <summary>
    /// Two methods between them naming every kind of symbol there is: a message type as the extend
    /// target and again as a parameter type, an enum type in a declaration and as the qualifier of a
    /// value, fields read bare and through a receiver and asked about with <c>has</c>, a local
    /// written and read, a loop binding, a parameter, and a call. The <c>test</c> adds the three
    /// places a name is written outside a method body.
    /// </summary>
    private const string Fixture =
        """
        import proto "fixtures.proto";

        extend Outer {
            fn scaled(factor: int64) -> int64 {
                var running: int64 = count * factor;

                for value in nested_values {
                    running = running + 1;
                }

                if has optional_count {
                    running = running + small_count as int64;
                }

                return running;
            }

            fn compare(peer: Outer) -> int64 {
                var mine: TopLevelStatus = TopLevelStatus.TOP_LEVEL_STATUS_OK;

                if mine == status {
                    return count - peer.count;
                }

                return scaled(2);
            }
        }

        test Outer.scaled "multiplies the count" {
            receiver {
                count = 7;
            }

            arg factor = 2;

            expect return 14;
        }
        """;

    // ------- the range is the name

    /// <summary>
    /// The sweep with the teeth in it. Every recording site in the binder passes through this at
    /// once, and the expectation is computed from the source rather than written down: whatever text
    /// the range covers, its last dotted segment has to be what the symbol is called.
    /// </summary>
    /// <remarks>
    /// Dotted rather than exact, because two ranges legitimately cover a qualified name -- an enum
    /// named <c>protolang.conformance.EnumCase.Level</c>, and a test target written
    /// <c>Invoice.total_cents</c> -- and in both the compiler has no narrower range to offer.
    /// </remarks>
    [Fact]
    public void EveryRecordedRangeCoversTheNameItClaims()
    {
        var checkedAny = false;

        foreach (var source in CompiledCorpus.All)
        {
            var model = SemanticModel.For(source.Result);

            foreach (var reference in source.Result.Module!.References)
            {
                var written = source.Text[reference.Span.Start.Offset..reference.Span.End.Offset];
                var segment = written[(written.LastIndexOf('.') + 1)..];

                Assert.Equal(SimpleNameOf(model, reference), segment);
                checkedAny = true;
            }
        }

        Assert.True(checkedAny, "the corpus must contain references for this to have asserted anything");
    }

    /// <summary>
    /// Said once concretely, because the sweep above proves the ranges agree with the names and this
    /// proves they are the narrow ones. The IR node behind each of these spans the whole construct.
    /// </summary>
    [Theory]
    [InlineData("optional_count")]
    [InlineData("small_count")]
    [InlineData("nested_values")]
    [InlineData("TOP_LEVEL_STATUS_OK")]
    [InlineData("scaled(2)", "scaled")]
    [InlineData("peer.count", "peer")]
    public void ARangeIsTheNameAloneAndNotTheConstructAroundIt(string written, string? name = null)
    {
        var offset = Offset(written);
        var reference = ReferenceAt(offset);

        Assert.Equal(offset, reference.Span.Start.Offset);
        Assert.Equal((name ?? written).Length, reference.Span.Length);
    }

    // ------- identity is semantic, not textual

    /// <summary>
    /// The case a name-keyed index gets wrong. Neither block encloses the other, so highlighting one
    /// <c>total</c> must not light up the other.
    /// </summary>
    [Fact]
    public void TwoSameNamedLocalsInSiblingScopesHaveDisjointReferences()
    {
        const string source =
            """
            import proto "invoice.proto";
            extend InvoiceItem {
                fn f() -> int64 {
                    if quantity > 0 {
                        var total: int64 = 1;
                        return total;
                    } else {
                        var total: int64 = 2;
                        return total * total;
                    }
                }
            }
            """;

        var model = SemanticModel.For(Compile(source, TestPaths.ExampleProtoDirectory));

        var first = At(model, source, "var total: int64 = 1", "total");
        var second = At(model, source, "var total: int64 = 2", "total");

        Assert.NotEqual(first.Symbol, second.Symbol);

        // One declaration and one read in the first branch; one declaration and two reads in the
        // second, so a set that merged them would be visible as a count either way round.
        Assert.Equal(2, model.ReferencesTo(first.Symbol).Count);
        Assert.Equal(3, model.ReferencesTo(second.Symbol).Count);
        Assert.Empty(model.ReferencesTo(first.Symbol).Intersect(model.ReferencesTo(second.Symbol)));
    }

    /// <summary>
    /// The other half of the same point: <c>count</c> and <c>peer.count</c> are one symbol, even
    /// though one of them has no receiver written at all and a text search would find two spellings.
    /// </summary>
    [Fact]
    public void AFieldReachedBareAndThroughAReceiverIsOneSymbol()
    {
        var bare = ReferenceAt(Offset("count * factor"));
        var qualified = ReferenceAt(Offset("peer.count") + "peer.".Length);

        Assert.Equal(SymbolKind.Field, bare.Symbol.Kind);
        Assert.Equal(bare.Symbol, qualified.Symbol);

        var uses = Model().ReferencesTo(bare.Symbol);

        Assert.Contains(bare, uses);
        Assert.Contains(qualified, uses);
    }

    // ------- every reference site

    [Fact]
    public void AnExtendBlockIsAReferenceToTheMessageItExtends()
    {
        var reference = ReferenceAt(Offset("Outer {"));

        Assert.Equal(SymbolKind.MessageType, reference.Symbol.Kind);
        Assert.Equal("protolang.tests.Outer", reference.Symbol.Key);
    }

    /// <summary>
    /// The site the IR cannot express: a type reference resolves to a type and leaves no node, so
    /// <c>peer: Outer</c> mentions <c>Outer</c> nowhere in the tree the binder produced.
    /// </summary>
    [Fact]
    public void AParameterTypeIsAReferenceToTheTypeItNames()
    {
        var reference = ReferenceAt(Offset("Outer) -> int64"));

        Assert.Equal(SymbolKind.MessageType, reference.Symbol.Kind);
        Assert.Equal(ReferenceAt(Offset("Outer {")).Symbol, reference.Symbol);
    }

    [Fact]
    public void ADeclaredVariableTypeIsAReferenceToTheTypeItNames()
    {
        var reference = ReferenceAt(Offset("TopLevelStatus = TopLevelStatus"));

        Assert.Equal(SymbolKind.EnumType, reference.Symbol.Kind);
        Assert.Equal("protolang.tests.TopLevelStatus", reference.Symbol.Key);
    }

    /// <summary>
    /// A scalar target names nothing an editor could navigate to, so the only cast that produces a
    /// reference is one whose target is a message or an enum -- which the compiler then refuses. The
    /// refusal does not unwrite the name.
    /// </summary>
    [Fact]
    public void ACastTargetThatNamesAMessageIsStillAReferenceToIt()
    {
        const string source =
            """
            import proto "fixtures.proto";
            extend Outer {
                fn f() -> int64 {
                    return count as Inner;
                }
            }
            """;

        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var model = SemanticModel.For(result);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var reference = At(model, source, "Inner;", "Inner");

        Assert.Equal(SymbolKind.MessageType, reference.Symbol.Kind);
        Assert.Equal("protolang.tests.Outer.Inner", reference.Symbol.Key);
    }

    /// <summary>
    /// Two references over one expression, and neither is the other's range: the qualifier names the
    /// enum type and the member names the constant.
    /// </summary>
    [Fact]
    public void AnEnumValueAndTheTypeQualifyingItAreTwoReferences()
    {
        var qualifier = ReferenceAt(Offset("TopLevelStatus.TOP_LEVEL_STATUS_OK"));
        var value = ReferenceAt(Offset("TOP_LEVEL_STATUS_OK"));

        Assert.Equal(SymbolKind.EnumType, qualifier.Symbol.Kind);
        Assert.Equal(SymbolKind.EnumValue, value.Symbol.Kind);
        Assert.Equal("protolang.tests.TopLevelStatus.TOP_LEVEL_STATUS_OK", value.Symbol.Key);
    }

    [Fact]
    public void APresenceTestIsAReferenceToTheFieldItAsksAbout()
    {
        var reference = ReferenceAt(Offset("optional_count"));

        Assert.Equal(SymbolKind.Field, reference.Symbol.Kind);
        Assert.Equal("protolang.tests.Outer.optional_count", reference.Symbol.Key);
    }

    [Fact]
    public void ACallIsAReferenceToTheMethodItCalls()
    {
        var reference = ReferenceAt(Offset("scaled(2)"));

        Assert.Equal(SymbolKind.Method, reference.Symbol.Kind);
        Assert.Equal(
            Offset("fn scaled") + "fn ".Length,
            Model().DeclarationOf(reference.Symbol)!.Name.Span.Start.Offset);
    }

    /// <summary>
    /// Three names outside any method body, which is where a walk of method bodies alone would stop
    /// finding anything.
    /// </summary>
    [Fact]
    public void ATestNamesTheMethodItRunsTheFieldsItSetsAndTheParametersItSupplies()
    {
        var target = ReferenceAt(Offset("Outer.scaled \"multiplies"));
        var field = ReferenceAt(Offset("count = 7"));
        var argument = ReferenceAt(Offset("factor = 2"));

        Assert.Equal(ReferenceAt(Offset("scaled(2)")).Symbol, target.Symbol);
        Assert.Equal(ReferenceAt(Offset("count * factor")).Symbol, field.Symbol);
        Assert.Equal(ReferenceAt(Offset("factor;")).Symbol, argument.Symbol);
    }

    /// <summary>
    /// A test target is the one range the compiler cannot narrow: the parser produces one name for
    /// <c>Outer.scaled</c>, so both halves share it and the method is what the range is recorded
    /// against. Pinned rather than left to be discovered, because splitting it later is a change to
    /// the parser.
    /// </summary>
    [Fact]
    public void ATestTargetIsRecordedOverItsWholeQualifiedName()
    {
        var target = ReferenceAt(Offset("Outer.scaled \"multiplies"));

        Assert.Equal("Outer.scaled".Length, target.Span.Length);
        Assert.Equal(SymbolKind.Method, target.Symbol.Kind);
    }

    // ------- reads, writes, and declarations

    [Fact]
    public void AnAssignmentIsAWriteAndEveryOtherUseIsARead()
    {
        var declaration = ReferenceAt(Offset("running: int64"));
        var uses = Model().ReferencesTo(declaration.Symbol);

        Assert.Equal(ReferenceKind.Declaration, Assert.Single(uses, use => use.Span == declaration.Span).Kind);
        Assert.Equal(2, uses.Count(use => use.Kind == ReferenceKind.Write));
        Assert.All(
            uses.Where(use => use.Kind == ReferenceKind.Write),
            write => Assert.Equal("running", Fixture[write.Span.Start.Offset..write.Span.End.Offset]));
        Assert.Contains(uses, use => use.Kind == ReferenceKind.Read);
    }

    /// <summary>
    /// The change that made the write recordable at all: the target of an assignment used to be a
    /// symbol rather than a node, so it was the one written name the IR placed nowhere.
    /// </summary>
    [Fact]
    public void TheTargetOfAnAssignmentIsANodeInTheIr()
    {
        var offset = Offset("running = running + 1");

        var found = Model().IrAt(offset + 1)?.Node;

        var reference = Assert.IsType<IrLocalReference>(found);
        Assert.Equal("running", reference.Local.Name);
        Assert.Equal(offset, reference.Span.Start.Offset);
    }

    [Fact]
    public void ADeclarationIsInTheListAndDistinguishableFromTheUses()
    {
        var symbol = ReferenceAt(Offset("scaled(2)")).Symbol;

        var declarations = Model().ReferencesTo(symbol)
            .Where(reference => reference.Kind == ReferenceKind.Declaration)
            .ToList();

        var declaration = Assert.Single(declarations);

        Assert.Equal(Model().DeclarationOf(symbol)!.Name.Span, declaration.Span);
    }

    /// <summary>
    /// The boundary this issue draws: a schema member is reported and never edited, because its
    /// declaration is in a .proto ProtoLang does not own. #41 is what will answer with a location
    /// there.
    /// </summary>
    [Fact]
    public void ASchemaSymbolHasReferencesAndNoDeclaration()
    {
        var symbol = ReferenceAt(Offset("count * factor")).Symbol;

        Assert.NotEmpty(Model().ReferencesTo(symbol));
        Assert.DoesNotContain(Model().ReferencesTo(symbol), r => r.Kind == ReferenceKind.Declaration);
        Assert.Null(Model().DeclarationOf(symbol));
    }

    /// <summary>
    /// A loop binding nothing uses still exists, so an editor asked about it answers with the
    /// declaration rather than with nothing.
    /// </summary>
    [Fact]
    public void ADeclarationNothingUsesIsStillInTheIndex()
    {
        var reference = ReferenceAt(Offset("value in nested_values"));

        Assert.Equal(SymbolKind.LoopBinding, reference.Symbol.Kind);
        Assert.Equal(ReferenceKind.Declaration, Assert.Single(Model().ReferencesTo(reference.Symbol)).Kind);
    }

    // ------- the two directions agree

    /// <summary>
    /// The round trip, swept: whatever the binder recorded, asking the position it recorded gives
    /// back the same symbol, and a ProtoLang symbol reaches the declaration #39 records for it.
    /// </summary>
    [Fact]
    public void EveryReferenceIsFoundAtItsOwnPositionAndReachesItsDeclaration()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var model = SemanticModel.For(source.Result);

            foreach (var reference in source.Result.Module!.References)
            {
                var found = model.ReferenceAt(reference.Span.Start.Offset);

                Assert.NotNull(found);
                Assert.Equal(reference.Symbol, found.Symbol);
                Assert.Contains(reference, model.ReferencesTo(reference.Symbol));

                if (reference.Symbol.Kind is SymbolKind.Field or SymbolKind.EnumValue
                    or SymbolKind.MessageType or SymbolKind.EnumType)
                {
                    continue;
                }

                var declaration = model.DeclarationOf(reference.Symbol);

                Assert.NotNull(declaration);
                Assert.Equal(reference.Symbol, declaration.Id);
            }
        }
    }

    /// <summary>
    /// What says nothing is recorded twice. A range shared by two entries would make which of them a
    /// position finds a matter of sort order, and the sweep above would still pass.
    /// </summary>
    [Fact]
    public void NoTwoReferencesShareAStartOffset()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var starts = source.Result.Module!.References
                .Select(reference => reference.Span.Start.Offset)
                .ToList();

            Assert.Equal(starts.Count, starts.Distinct().Count());
        }
    }

    // ------- order, and what is not there

    [Fact]
    public void ReferencesComeBackInSourceOrder()
    {
        var uses = Model().ReferencesTo(ReferenceAt(Offset("running: int64")).Symbol);

        Assert.Equal(
            uses.Select(use => use.Span.Start.Offset).Order().ToList(),
            uses.Select(use => use.Span.Start.Offset).ToList());
    }

    /// <summary>
    /// Without this an editor's reference list reshuffles on every keystroke, and nothing downstream
    /// may cache anything keyed by a symbol.
    /// </summary>
    [Fact]
    public void TheSameTextIndexesIdentically()
    {
        var identity = SourceIdentity.FromPath(
            Path.Combine(TestPaths.CreateTempDirectory(), "buffer.protolang"));

        var first = References(identity);
        var second = References(identity);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);

        List<SymbolReference> References(SourceIdentity document)
            => [.. Compilation.Compile(
                    new SourceDocument(document, Fixture),
                    [TestPaths.FixtureProtoDirectory])
                .Module!.References];
    }

    /// <summary>
    /// The normal state of a buffer. A file that does not parse still resolved the names it managed
    /// to bind, and those are the ones an editor is asking about.
    /// </summary>
    [Fact]
    public void ABufferThatDoesNotParseStillIndexesWhatBound()
    {
        var model = SemanticModel.For(CompiledCorpus.Broken.Result);

        var reference = At(model, CompiledCorpus.Broken.Text, "in items", "items");

        Assert.Equal("protolang.examples.Invoice.items", reference.Symbol.Key);
        Assert.NotEmpty(model.ReferencesTo(reference.Symbol));
    }

    /// <summary>
    /// A name that did not resolve refers to nothing, so it is not in the index. Recording it would
    /// put an entry under a symbol that does not exist, which is worse than no answer.
    /// </summary>
    [Fact]
    public void ANameThatDidNotResolveIsNotInTheIndex()
    {
        var model = SemanticModel.For(CompiledCorpus.Broken.Result);

        var offset = CompiledCorpus.Broken.Text.IndexOf("nosuchmethod", StringComparison.Ordinal);

        Assert.True(offset >= 0, "the broken buffer must contain the unresolved call");
        Assert.Null(model.ReferenceAt(offset + 1));
    }

    [Fact]
    public void AnOffsetThatIsNotOnANameHasNoReference()
    {
        Assert.Null(Model().ReferenceAt(Offset("count * factor") + "count ".Length));
        Assert.Null(Model().ReferenceAt(-1));
        Assert.Null(Model().ReferenceAt(Fixture.Length + 1));
    }

    [Fact]
    public void AnUnknownSymbolHasNoReferencesAndNoDeclaration()
    {
        var stranger = SymbolId.ForDeclaration(
            SymbolKind.Local,
            SourceIdentity.Unsaved("nowhere.protolang"),
            SourceSpan.None);

        Assert.Empty(Model().ReferencesTo(stranger));
        Assert.Null(Model().DeclarationOf(stranger));
    }

    // ------- helpers

    /// <summary>
    /// What the symbol is called, from whichever side owns the answer: a declaration for anything
    /// ProtoLang declares, and the last segment of the protobuf full name for anything the schema
    /// declares.
    /// </summary>
    private static string SimpleNameOf(SemanticModel model, SymbolReference reference)
    {
        if (model.DeclarationOf(reference.Symbol) is { } declaration)
        {
            return declaration.Name.Text;
        }

        var key = reference.Symbol.Key;
        return key[(key.LastIndexOf('.') + 1)..];
    }

    private static int Offset(string text)
    {
        var offset = Fixture.IndexOf(text, StringComparison.Ordinal);

        Assert.True(offset >= 0, $"the fixture must contain '{text}'");
        return offset;
    }

    private static SymbolReference ReferenceAt(int offset)
    {
        var found = Model().ReferenceAt(offset);

        Assert.NotNull(found);
        return found;
    }

    /// <summary>The reference on <paramref name="name"/> within the first <paramref name="near"/>.</summary>
    private static SymbolReference At(SemanticModel model, string source, string near, string name)
    {
        var anchor = source.IndexOf(near, StringComparison.Ordinal);

        Assert.True(anchor >= 0, $"the source must contain '{near}'");

        var found = model.ReferenceAt(source.IndexOf(name, anchor, StringComparison.Ordinal));

        Assert.NotNull(found);
        return found;
    }

    private static CompilationResult Compile(string source, string protoDirectory)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [protoDirectory]);

    /// <inheritdoc cref="PositionQueryTests"/>
    private static readonly Lazy<CompilationResult> Fixed = new(() =>
    {
        var result = Compile(Fixture, TestPaths.FixtureProtoDirectory);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        return result;
    });

    private static SemanticModel Model() => SemanticModel.For(Fixed.Value);
}
