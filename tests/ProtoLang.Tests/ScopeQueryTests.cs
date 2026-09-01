using System.Text;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Symbols;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// What a bare identifier may mean at a position: the set the binder had while it was descending,
/// asked for afterwards, at a position nobody knew about at the time.
/// </summary>
/// <remarks>
/// <para>
/// The contract has two halves and they fail in opposite directions. Offer too little and completion
/// hides a name the author could have written. Offer too much and it hands them a word that binds to
/// something else, or to nothing -- which is the worse of the two, and the reason the sweep at the
/// bottom of this file writes every reported name into the source and makes the compiler agree.
/// </para>
/// <para>
/// The cases worth naming are the ones a plausible implementation gets wrong: a declaration is not
/// in scope inside its own initializer, a duplicate never enters scope at all and the *outer* name
/// keeps binding, a loop binding cannot be seen from the collection it iterates, and a local hides a
/// field of the receiver rather than sitting beside it.
/// </para>
/// </remarks>
public class ScopeQueryTests
{
    /// <summary>
    /// One method holding every construct that declares a name -- a parameter, a local, a loop
    /// binding, and two locals in sibling branches -- over a receiver with fields to be hidden by.
    /// A second method shadows one of those fields, and the <c>test</c> is where no bare name
    /// resolves at all.
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
                    var guarded: int64 = optional_count;
                    running = running + guarded;
                } else {
                    var fallback: int64 = 0;
                    running = running + fallback;
                }

                return running;
            }

            fn shadows() -> int64 {
                var count: int64 = 5;
                return count;
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

    // ------- what a position can see

    [Fact]
    public void AParameterIsVisibleEverywhereInTheBodyItBelongsTo()
    {
        Assert.Contains("factor", NamesAt(Offset("var running")));
        Assert.Contains("factor", NamesAt(Offset("running = running + 1")));
        Assert.Contains("factor", NamesAt(Offset("return running;")));
    }

    [Fact]
    public void ALocalIsVisibleAfterItsDeclarationAndNotBefore()
    {
        Assert.DoesNotContain("running", NamesAt(Offset("var running")));
        Assert.Contains("running", NamesAt(Offset("return running;")));
    }

    /// <summary>
    /// The boundary case #49 asks to have decided. The binder settles it by construction: it binds
    /// the initializer and only then puts the name in scope, so a self-reference is an unknown name
    /// -- which is asserted here from the same source rather than taken on trust.
    /// </summary>
    [Fact]
    public void ALocalIsNotVisibleInsideItsOwnInitializer()
    {
        Assert.DoesNotContain("running", NamesAt(Offset("count * factor")));

        var source = Method("var total: int64 = total;\n        return 0;");
        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var names = NamesAt(SemanticModel.For(result), source.IndexOf("total;", StringComparison.Ordinal));

        Assert.DoesNotContain("total", names);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PL0037");
    }

    [Fact]
    public void ALocalDeclaredInASiblingBlockIsNotVisible()
    {
        Assert.DoesNotContain("fallback", NamesAt(Offset("running + guarded")));
        Assert.DoesNotContain("guarded", NamesAt(Offset("running + fallback")));
    }

    [Fact]
    public void ALocalDeclaredInAnEnclosingBlockIsVisible()
    {
        Assert.Contains("running", NamesAt(Offset("var guarded")));
        Assert.Contains("running", NamesAt(Offset("running + fallback")));
    }

    [Fact]
    public void ALoopBindingIsVisibleInTheBodyAndNowhereElse()
    {
        Assert.Contains("value", NamesAt(Offset("running = running + 1")));
        Assert.DoesNotContain("value", NamesAt(Offset("return running;")));
        Assert.DoesNotContain("value", NamesAt(Offset("var running")));
    }

    /// <summary>
    /// The same rule as an initializer, in the construct where it is easiest to get wrong: the loop
    /// binding's scope covers the whole <c>for</c> statement, collection included, and the binding
    /// still does not resolve there because the collection was bound before it existed.
    /// </summary>
    [Fact]
    public void ALoopBindingIsNotVisibleInTheCollectionItIteratesOver()
    {
        Assert.DoesNotContain("value", NamesAt(Offset("nested_values {")));

        var source = Method("for item in item {\n            return 1;\n        }\n\n        return 0;");
        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var names = NamesAt(SemanticModel.For(result), source.IndexOf("in item", StringComparison.Ordinal) + 3);

        Assert.DoesNotContain("item", names);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PL0037");
    }

    [Fact]
    public void APositionAfterTheLastStatementOfABlockStillSeesTheBlock()
    {
        var closingBrace = Fixture.IndexOf("}", Offset("running = running + 1"), StringComparison.Ordinal);

        Assert.Contains("value", NamesAt(closingBrace));
    }

    // ------- fields of the receiver

    [Fact]
    public void FieldsOfTheReceiverAreVisibleByTheirBareNamesWithTheirTypes()
    {
        var scope = ScopeAt(Offset("return running;"));
        var count = Assert.Single(scope.Names, name => name.Name == "count");

        Assert.Equal("protolang.tests.Outer", scope.Receiver.DisplayName);
        Assert.Equal(SymbolKind.Field, count.Symbol.Kind);
        Assert.Equal("int64", count.Type.DisplayName);
        Assert.Null(count.Declaration);
    }

    /// <summary>
    /// The one place this query decides something rather than reporting it. A local wins the name,
    /// so the field is not offered: writing <c>count</c> there means the local, and a list holding
    /// both would be offering a word that does not mean what it says.
    /// </summary>
    [Fact]
    public void ALocalHidesAFieldOfTheReceiverRatherThanSittingBesideIt()
    {
        var count = Assert.Single(ScopeAt(Offset("return count;")).Names, name => name.Name == "count");

        Assert.Equal(SymbolKind.Local, count.Symbol.Kind);
        Assert.Equal(Model().DeclarationOf(count.Symbol)!.Id, count.Symbol);
    }

    /// <summary>
    /// Reading a map is PL0038, so a map field is a name that resolves and is then refused. It is
    /// never offered, which is the rule #43 depends on: nothing in the list may fail to bind.
    /// </summary>
    [Fact]
    public void AMapFieldIsNeverOffered()
    {
        const string source =
            """
            import proto "fixtures.proto";

            extend Mapped {
                fn f() -> int64 {
                    return count;
                }
            }
            """;

        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var names = NamesAt(SemanticModel.For(result), source.IndexOf("return count;", StringComparison.Ordinal));

        Assert.Contains("count", names);
        Assert.DoesNotContain("tags", names);
    }

    /// <summary>
    /// A field whose presence has not been established is still offered. PL0078 is reported on a
    /// name that resolved, and the way out of it is to write that name inside a guard -- so
    /// withholding it would hide the field from the author who has to guard it.
    /// </summary>
    [Fact]
    public void AMessageFieldWithoutEstablishedPresenceIsStillOffered()
        => Assert.Contains("inner", NamesAt(Offset("var running")));

    // ------- inside a test

    [Fact]
    public void NoBareNameResolvesInsideATestAndTheReceiverIsStillKnown()
    {
        var scope = ScopeAt(Offset("count = 7;"));

        Assert.Empty(scope.Names);
        Assert.Equal("protolang.tests.Outer", scope.Receiver.DisplayName);
    }

    [Fact]
    public void APositionOutsideEveryMethodAndTestHasNoAnswer()
    {
        Assert.Null(Model().ScopeAt(Offset("import proto")));
        Assert.Null(Model().ScopeAt(Offset("extend Outer")));
    }

    // ------- names that never entered scope

    /// <summary>
    /// ProtoLang forbids shadowing outright: an inner <c>var</c> of a name already in scope is
    /// PL0029 and never enters, so the *outer* one keeps binding inside the inner block. A walk of
    /// the tree assuming the innermost declaration wins would disagree with the compiler here, and
    /// would disagree silently.
    /// </summary>
    [Fact]
    public void AShadowingDeclarationIsRefusedAndTheOuterNameKeepsBinding()
    {
        var source = Method(
            """
            var total: int64 = 1;

                    if has optional_count {
                        var total: int64 = 2;
                        return total;
                    }

                    return total;
            """);

        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var model = SemanticModel.For(result);
        var inner = source.IndexOf("return total;", StringComparison.Ordinal);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PL0029");

        var visible = Assert.Single(ScopeAt(model, inner).Names, name => name.Name == "total");
        var declaration = model.DeclarationOf(visible.Symbol);

        Assert.NotNull(declaration);
        Assert.Equal(
            source.IndexOf("total", source.IndexOf("var total", StringComparison.Ordinal), StringComparison.Ordinal),
            declaration.Name.Span.Start.Offset);
    }

    /// <summary>
    /// A signature can hold two parameters of one name -- PL0026, and still bound. Only the first
    /// entered scope, and the body means that one, so only that one may be offered.
    /// </summary>
    [Fact]
    public void ADuplicateParameterIsInScopeOnceAsTheOneTheBodyMeans()
    {
        const string source =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn f(n: int64, n: int64) -> int64 {
                    return n;
                }
            }
            """;

        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var model = SemanticModel.For(result);
        var visible = Assert.Single(
            ScopeAt(model, source.IndexOf("return n;", StringComparison.Ordinal)).Names,
            name => name.Name == "n");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PL0026");
        Assert.Equal(
            source.IndexOf("n: int64", StringComparison.Ordinal),
            model.DeclarationOf(visible.Symbol)!.Name.Span.Start.Offset);
    }

    /// <summary>
    /// A parameter whose name has not been written keeps its place in the signature so call sites
    /// line up, and is put in no scope, because there is no name for anything to write.
    /// </summary>
    [Fact]
    public void AParameterWithNoNameIsInNoScope()
    {
        const string source =
            """
            import proto "fixtures.proto";

            extend Outer {
                fn f(: int64) -> int64 {
                    return 0;
                }
            }
            """;

        var result = Compile(source, TestPaths.FixtureProtoDirectory);
        var scope = ScopeAt(SemanticModel.For(result), source.IndexOf("return 0;", StringComparison.Ordinal));

        Assert.DoesNotContain(scope.Names, name => name.Symbol.Kind == SymbolKind.Parameter);
    }

    // ------- broken and partial input, which is the normal case while typing

    [Fact]
    public void AMethodWithNoClosingBraceStillAnswersAtItsEnd()
    {
        var source = Truncated("var total: int64 = 1;");
        var names = NamesAt(SemanticModel.For(Compile(source, TestPaths.FixtureProtoDirectory)), source.Length);

        Assert.Contains("total", names);
        Assert.Contains("count", names);
    }

    [Fact]
    public void ALoopBodyWithNoClosingBraceStillAnswersAtItsEnd()
    {
        var source = Truncated("for value in nested_values {\n            var inner: int64 = 1;");
        var names = NamesAt(SemanticModel.For(Compile(source, TestPaths.FixtureProtoDirectory)), source.Length);

        Assert.Contains("value", names);
        Assert.Contains("inner", names);
    }

    [Fact]
    public void AHalfWrittenIfStillAnswersAtItsEnd()
    {
        var source = Truncated("var total: int64 = 1;\n\n        if has optional_count {");
        var names = NamesAt(SemanticModel.For(Compile(source, TestPaths.FixtureProtoDirectory)), source.Length);

        Assert.Contains("total", names);
    }

    /// <summary>
    /// Asked after the statement that follows, not before it. The expression parser consumes the
    /// token it could not use, so the semicolon is gone and the declaration's span reaches over the
    /// <c>return</c> after it -- and a name is visible from the end of its own declaration, wherever
    /// recovery decided that is. The name is in scope; the range it is in scope from is the
    /// parser's answer about a file that does not parse, and this query reports it rather than
    /// second-guessing it.
    /// </summary>
    [Fact]
    public void ADeclarationWithNoInitializerStillPutsItsNameInScope()
    {
        var source = Method("var total: int64 = ;\n        return 0;");
        var names = NamesAt(
            SemanticModel.For(Compile(source, TestPaths.FixtureProtoDirectory)),
            source.IndexOf("return 0;", StringComparison.Ordinal) + "return 0;".Length);

        Assert.Contains("total", names);
    }

    /// <summary>
    /// The buffer in the corpus that does not parse. Whatever bound is still answerable, which is
    /// what #36 bought and what an editor spends most of its time asking about.
    /// </summary>
    [Fact]
    public void AMethodWhoseBodyDidNotBindStillAnswersForWhatDid()
    {
        var model = SemanticModel.For(CompiledCorpus.Broken.Result);
        var offset = CompiledCorpus.Broken.Text.IndexOf("return line.", StringComparison.Ordinal);
        var scope = model.ScopeAt(offset);

        Assert.NotNull(scope);
        Assert.Contains(scope.Names, name => name.Name == "line");
        Assert.Contains(scope.Names, name => name.Name == "items");
    }

    // ------- order and identity

    [Fact]
    public void TheSameTextAnswersIdenticallyEveryTimeItIsCompiled()
    {
        var again = SemanticModel.For(Compile(Fixture, TestPaths.FixtureProtoDirectory));
        var offset = Offset("return running;");

        Assert.Equal(
            ScopeAt(offset).Names.Select(name => (name.Name, name.Symbol.Kind)),
            ScopeAt(again, offset).Names.Select(name => (name.Name, name.Symbol.Kind)));
    }

    /// <summary>
    /// The property that says the hiding rule left exactly one winner. Two entries of one spelling
    /// would mean an editor had to choose, and whichever it chose would sometimes be the one the
    /// binder does not mean.
    /// </summary>
    [Fact]
    public void NoTwoNamesVisibleAtOnePositionShareASpelling()
    {
        // The fixture joins the corpus for this one, because it is the only source in the repository
        // that declares a local over a field of its own receiver -- which is the collision the
        // hiding rule exists to resolve, and a sweep that never meets one proves nothing about it.
        var swept = CompiledCorpus.All.Append(new CorpusSource("fixture", Fixture, Fixed.Value));

        foreach (var source in swept)
        {
            if (source.Result.Module is not { } module)
            {
                continue;
            }

            var model = SemanticModel.For(source.Result);

            foreach (var offset in EveryNodeStart(module))
            {
                if (model.ScopeAt(offset) is not { } scope)
                {
                    continue;
                }

                var names = scope.Names.Select(name => name.Name).ToList();

                Assert.True(
                    names.Count == names.Distinct(StringComparer.Ordinal).Count(),
                    $"one spelling must mean one thing at offset {offset} of {source.Name}: "
                    + string.Join(", ", names));
            }
        }
    }

    // ------- the two directions the query has to agree with the binder in

    /// <summary>
    /// Nothing that binds is missing. Every bare name the binder actually resolved -- a local, a
    /// parameter, a loop binding, a field reached with no receiver written, and the same through
    /// <c>has</c> -- must be offered at its own position, as the same symbol. Swept over every
    /// source the repository keeps, so a recording site that stopped firing fails here rather than
    /// in an editor.
    /// </summary>
    [Fact]
    public void EveryBareNameTheBinderResolvedIsOfferedWhereItWasWritten()
    {
        var checkedNames = 0;

        foreach (var source in CompiledCorpus.All)
        {
            if (source.Result.Module is not { } module)
            {
                continue;
            }

            var model = SemanticModel.For(source.Result);

            foreach (var (symbol, offset) in BareNamesIn(module))
            {
                var scope = model.ScopeAt(offset);

                Assert.True(scope is not null, $"offset {offset} of {source.Name} must be in a body");
                Assert.True(
                    scope!.Names.Any(name => name.Symbol == symbol),
                    $"{symbol} binds at offset {offset} of {source.Name} and must be offered there, "
                    + $"among: {string.Join(", ", scope.Names.Select(name => name.Name))}");

                checkedNames++;
            }
        }

        Assert.True(checkedNames > 100, $"the sweep must have something to sweep; it saw {checkedNames}");
    }

    /// <summary>
    /// Everything offered binds, and binds to what was promised. Every name the query reports at
    /// every statement position of a real source is written back into that source as a statement of
    /// its own, and the whole thing recompiled: no unknown name, no unsupported map, and each probe
    /// resolving to the declaration the query named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direction that cannot be checked by walking anything, because it is a claim about names
    /// that were <em>not</em> written. Only the binder can settle it, so the binder is asked -- from
    /// real source rather than from cases invented to pass.
    /// </para>
    /// <para>
    /// One compilation per source rather than one per position: the probes are spliced in left to
    /// right, and <c>Shifted</c> maps an offset in the original onto the mutated text. Nothing
    /// visible at a probe was declared after it, so every declaration compared here is one the
    /// insertion at that probe did not move.
    /// </para>
    /// <para>
    /// PL0078 is expected and allowed. A message field with no established presence is a name that
    /// resolved and then drew a diagnostic about its value, which is a different thing from a name
    /// that did not bind.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryNameTheQueryOffersBindsWhereItWasOffered(bool overTheWorkedExample)
    {
        var (text, protos, source) = overTheWorkedExample
            ? (CompiledCorpus.SimpleScript.Text, TestPaths.ExampleProtoDirectory, CompiledCorpus.SimpleScript.Result)
            : (Fixture, TestPaths.FixtureProtoDirectory, Fixed.Value);

        var probed = Probe(text, source);
        var result = Compile(probed.Text, protos);
        var mutated = SemanticModel.For(result);

        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code is "PL0037" or "PL0038");

        foreach (var (offset, expected) in probed.Probes)
        {
            var reference = mutated.ReferenceAt(offset);

            Assert.True(
                reference is not null,
                $"'{expected.Name}' was offered and must bind when written at offset {offset}");
            Assert.Equal(expected.Symbol.Kind, reference!.Symbol.Kind);

            if (expected.Declaration is null)
            {
                Assert.Equal(expected.Symbol, reference.Symbol);
                continue;
            }

            var declaration = mutated.DeclarationOf(reference.Symbol);

            Assert.True(
                declaration is not null,
                $"'{expected.Name}' must reach a declaration when written at offset {offset}");
            Assert.Equal(
                probed.Shifted(expected.Declaration.Name.Span.Start.Offset),
                declaration!.Name.Span.Start.Offset);
        }

        Assert.True(probed.Probes.Count > 20, $"the sweep must probe something; it wrote {probed.Probes.Count}");
    }

    // ------- helpers

    /// <summary>
    /// Every place the binder resolved a bare identifier, and the offset it was written at. The
    /// receiver of an implicit field access carries the name's own range, which is what makes the
    /// last two shapes addressable: there is no <c>this</c> in the language, so an
    /// <see cref="IrThis"/> is always one the binder introduced for a name with no receiver.
    /// </summary>
    private static IEnumerable<(SymbolId Symbol, int Offset)> BareNamesIn(IrModule module)
        => module.Methods
            .SelectMany(IrWalk.DescendantsAndSelf)
            .Select(node => node switch
            {
                IrLocalReference local => (local.Local.Id, node.Span.Start.Offset),
                IrParameterReference parameter => (parameter.Parameter.Id, node.Span.Start.Offset),
                IrFieldAccess { Receiver: IrThis bare } field
                    => (SymbolId.ForField(field.Field), bare.Span.Start.Offset),
                IrFieldPresence { Receiver: IrThis bare } presence
                    => (SymbolId.ForField(presence.Field), bare.Span.Start.Offset),
                _ => (default, -1),
            })
            .Where(found => found.Item2 >= 0);

    private static IEnumerable<int> EveryNodeStart(IrModule module)
        => IrWalk.DescendantsAndSelf(module)
            .Select(node => node.Span.Start.Offset)
            .Distinct();

    /// <summary>Source with every offered name written back into it, and how that moved things.</summary>
    private sealed record Probed(
        string Text,
        IReadOnlyList<(int Offset, VisibleName Name)> Probes,
        IReadOnlyList<(int Position, int Length)> Insertions)
    {
        /// <summary>Where an offset in the original text ended up once the probes were spliced in.</summary>
        public int Shifted(int offset)
            => offset + Insertions.Where(inserted => inserted.Position <= offset).Sum(inserted => inserted.Length);
    }

    private static Probed Probe(string text, CompilationResult source)
    {
        var model = SemanticModel.For(source);
        var probes = new List<(int Offset, VisibleName Name)>();
        var insertions = new List<(int Position, int Length)>();
        var builder = new StringBuilder();
        var cursor = 0;

        foreach (var position in StatementPositions(source.SyntaxTree!))
        {
            if (model.ScopeAt(position) is not { } scope)
            {
                continue;
            }

            builder.Append(text, cursor, position - cursor);
            cursor = position;

            var before = builder.Length;

            foreach (var name in scope.Names)
            {
                probes.Add((builder.Length, name));
                builder.Append(name.Name).Append("; ");
            }

            insertions.Add((position, builder.Length - before));
        }

        builder.Append(text, cursor, text.Length - cursor);

        return new Probed(builder.ToString(), probes, insertions);
    }

    /// <summary>
    /// Every offset another statement could be written at: before each statement of a block, and
    /// just inside its closing brace, which is where the caret sits when a line is being added.
    /// </summary>
    /// <remarks>
    /// Taken from the blocks rather than from every statement, because a statement is not always at
    /// a place another one may go: the <c>if</c> after an <c>else</c> is a statement, and writing
    /// one in front of it is not ProtoLang.
    /// </remarks>
    private static IReadOnlyList<int> StatementPositions(CompilationUnit tree) =>
    [
        .. SyntaxWalk.DescendantsAndSelf(tree)
            .OfType<BlockStatement>()
            .SelectMany(block => block.Statements
                .Select(statement => statement.Span.Start.Offset)
                .Append(block.Span.End.Offset - 1))
            .Distinct()
            .Order(),
    ];

    private static string Method(string body) =>
        $$"""
        import proto "fixtures.proto";

        extend Outer {
            fn f() -> int64 {
                {{body}}
            }
        }
        """;

    /// <summary>A file that stops mid-method, as one being typed into does.</summary>
    private static string Truncated(string body) =>
        $$"""
        import proto "fixtures.proto";

        extend Outer {
            fn f() -> int64 {
                {{body}}
        """;

    private static CompilationResult Compile(string source, string protoDirectory)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [protoDirectory]);

    private static int Offset(string text)
    {
        var offset = Fixture.IndexOf(text, StringComparison.Ordinal);

        Assert.True(offset >= 0, $"the fixture must contain '{text}'");
        return offset;
    }

    private static ScopeAtPosition ScopeAt(int offset) => ScopeAt(Model(), offset);

    private static ScopeAtPosition ScopeAt(SemanticModel model, int offset)
    {
        var scope = model.ScopeAt(offset);

        Assert.True(scope is not null, $"offset {offset} must be inside a method or a test");
        return scope!;
    }

    private static IReadOnlyList<string> NamesAt(int offset) => NamesAt(Model(), offset);

    private static IReadOnlyList<string> NamesAt(SemanticModel model, int offset)
        => [.. ScopeAt(model, offset).Names.Select(name => name.Name)];

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
