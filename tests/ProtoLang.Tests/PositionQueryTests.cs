using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Syntax;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Every editor request is a position, and this is what answers one: which node is here, what stands
/// above it, and what the other tree calls the same thing.
/// </summary>
/// <remarks>
/// The awkward positions are the point. A caret is between characters, not on one, and it spends
/// most of its life immediately after the token that was just typed, in whitespace, or somewhere the
/// parser had to guess -- so the cases below are named for those rather than for the comfortable
/// middle of an identifier.
/// </remarks>
public class PositionQueryTests
{
    /// <summary>
    /// One method holding a bare field, a local, a parameter, a presence test, a conversion and a
    /// division with a fallback -- six shapes that bind to six different kinds of IR -- and a second
    /// method that calls the first.
    /// </summary>
    private const string Fixture =
        """
        import proto "fixtures.proto";

        extend Outer {
            // Between two declarations, which is a position an editor asks about.
            fn total(factor: int64) -> int64 {
                var sub: int64 = count * factor;

                if has optional_count {
                    sub = sub + small_count as int64;
                }

                return sub / factor on_zero 0;
            }

            fn caller() -> int64 {
                return total(2);
            }
        }
        """;

    // ------- finding a node

    [Fact]
    public void APositionInsideAnIdentifierFindsThatIdentifier()
    {
        var name = Assert.IsType<NameExpression>(SyntaxAt(Offset("count * factor") + 2).Node);

        Assert.Equal("count", name.Name.Text);
    }

    [Fact]
    public void APositionOnTheFirstCharacterOfANodeFindsIt()
    {
        var name = Assert.IsType<NameExpression>(SyntaxAt(Offset("count * factor")).Node);

        Assert.Equal("count", name.Name.Text);
    }

    /// <summary>
    /// Where the caret is the moment a word is finished and completion is asked for. Half-open spans
    /// say the end is outside the range; containment deliberately disagrees, or the answer at the one
    /// position editors ask about most would be the expression around the word instead of the word.
    /// </summary>
    [Fact]
    public void ACaretJustAfterAnIdentifierStillFindsIt()
    {
        var name = Assert.IsType<NameExpression>(SyntaxAt(Offset("count * factor") + "count".Length).Node);

        Assert.Equal("count", name.Name.Text);
    }

    /// <summary>
    /// Two nodes meet where a statement's semicolon is followed immediately by the next statement,
    /// and both cover the point between them. The shorter one wins -- the rule is about spans, not
    /// about sides -- and the two are only ever told apart by length or, failing that, by which was
    /// reached first.
    /// </summary>
    [Fact]
    public void WhereTwoNodesMeetTheShorterOfThemWins()
    {
        const string source =
            "import proto \"invoice.proto\";\n"
            + "extend InvoiceItem { fn f() -> int64 { var a: int64 = 1;return a; } }";

        var model = SemanticModel.For(Compile(source, TestPaths.ExampleProtoDirectory));
        var between = source.IndexOf("return a;", StringComparison.Ordinal);

        var found = model.SyntaxAt(between);

        Assert.NotNull(found);
        Assert.IsType<ReturnStatement>(found.Node);
    }

    [Fact]
    public void APositionInWhitespaceFindsTheConstructThatSpansIt()
    {
        // The blank line between the declaration and the if.
        var blankLine = Fixture.IndexOf("\n\n", Offset("var sub"), StringComparison.Ordinal) + 1;

        Assert.IsType<BlockStatement>(SyntaxAt(blankLine).Node);
    }

    [Fact]
    public void APositionInsideACommentFindsTheConstructThatSpansIt()
    {
        Assert.IsType<ExtendDeclaration>(SyntaxAt(Offset("// Between") + 3).Node);
    }

    /// <summary>
    /// A line ends where its last token does, so the position after it is inside whatever spans the
    /// line rather than nowhere. Nothing special happens here, which is the property.
    /// </summary>
    [Fact]
    public void APositionPastTheEndOfALineIsAnOrdinaryPosition()
    {
        var endOfLine = Fixture.IndexOf('\n', Offset("var sub"));

        Assert.Equal("total", SyntaxAt(endOfLine).Method?.Name.Text);
    }

    /// <summary>
    /// The one piece of a file the tree does not cover. A compilation unit begins at its first
    /// token, because that range is what a diagnostic about the whole file is reported against, and
    /// widening it to swallow a leading comment would move where those are shown. So trivia before
    /// that token answers nothing here, which is the answer a client can act on -- unlike an
    /// approximation.
    /// </summary>
    [Fact]
    public void TriviaBeforeTheFirstTokenIsOutsideTheTree()
    {
        const string source =
            "// A comment above everything, which is where a file usually starts.\n"
            + "import proto \"invoice.proto\";\n"
            + "extend InvoiceItem { fn f() -> int64 { return quantity; } }";

        var model = SemanticModel.For(Compile(source, TestPaths.ExampleProtoDirectory));

        Assert.Null(model.SyntaxAt(3));
        Assert.NotNull(model.SyntaxAt(source.IndexOf("import", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void APositionOutsideTheFileIsNothingHere(int offset)
    {
        Assert.Null(Model().SyntaxAt(offset));
        Assert.Null(Model().IrAt(offset));
    }

    /// <summary>
    /// A compilation can stop before it parses anything -- an unreadable config file does it -- and
    /// the model over it has to answer the same clean nothing rather than dereferencing a tree that
    /// was never built.
    /// </summary>
    [Fact]
    public void AModelOverACompilationThatStoppedEarlyAnswersNothing()
    {
        var nothing = new CompilationResult(
            null,
            null,
            [],
            new DiagnosticBag(),
            ProjectConfig.Default,
            [],
            []);

        var model = SemanticModel.For(nothing);

        Assert.Null(model.SyntaxAt(0));
        Assert.Null(model.IrAt(0));
    }

    // ------- walking upward

    /// <summary>
    /// The chain is a real one: each step holds the next, all the way out to the compilation unit.
    /// Asserted through the walker rather than trusted, because a path assembled from anything but
    /// the descent that found the node would look identical and mean nothing.
    /// </summary>
    [Fact]
    public void AncestorsReachTheRootFromTheNodeThatWasFound()
    {
        var found = SyntaxAt(Offset("count * factor"));

        for (var index = 0; index < found.Path.Count - 1; index++)
        {
            Assert.Contains(SyntaxWalk.ChildrenOf(found.Path[index]), child => ReferenceEquals(child, found.Path[index + 1]));
        }

        Assert.IsType<CompilationUnit>(found.Path[0]);
        Assert.Same(found.Node, found.Path[^1]);
        Assert.Equal(found.Path.Count - 1, found.Ancestors.Count());
    }

    /// <summary>
    /// Asked for by nearly every feature before anything else, so it is swept rather than sampled:
    /// every position from the <c>fn</c> to the closing brace has to name the same method.
    /// </summary>
    [Fact]
    public void TheEnclosingMethodIsObtainableFromEveryPositionInsideIt()
    {
        var model = Model();
        var method = Assert.Single(Compile().SyntaxTree!.Extends).Methods.Single(m => m.Name.Text == "total");

        for (var offset = method.Span.Start.Offset; offset < method.Span.End.Offset; offset++)
        {
            Assert.Equal("total", model.SyntaxAt(offset)?.Method?.Name.Text);
        }
    }

    [Fact]
    public void TheEnclosingExtendIsObtainableFromEveryPositionInsideIt()
    {
        var model = Model();
        var extend = Assert.Single(Compile().SyntaxTree!.Extends);

        for (var offset = extend.Span.Start.Offset; offset < extend.Span.End.Offset; offset++)
        {
            Assert.Equal("Outer", model.SyntaxAt(offset)?.Extend?.MessageName.Text);
        }
    }

    [Fact]
    public void APositionInsideAnExtendButOutsideEveryMethodHasNoMethod()
    {
        var found = SyntaxAt(Offset("// Between") + 3);

        Assert.Null(found.Method);
        Assert.NotNull(found.Extend);
    }

    // ------- the typed IR

    [Fact]
    public void APositionInAMethodBodyFindsWhatWasBoundThere()
    {
        var field = Assert.IsType<IrFieldAccess>(IrAt(Offset("count * factor")).Node);

        Assert.Equal("count", field.Field.Name);
    }

    /// <summary>
    /// The tie-break, pinned. A bare field of the receiver binds to a field access over an implicit
    /// <c>this</c>, and the two carry the same span exactly -- so the rule has to name one, and the
    /// one it names is what the author wrote rather than what the binder introduced underneath it.
    /// </summary>
    [Fact]
    public void WhereTwoIrNodesShareOneSpanTheOuterOneAnswers()
    {
        var found = IrAt(Offset("count * factor"));

        Assert.IsType<IrFieldAccess>(found.Node);
        Assert.DoesNotContain(found.Path, node => node is IrThis);
    }

    [Fact]
    public void TheEnclosingIrMethodIsObtainableFromAPositionInItsBody()
    {
        Assert.Equal("total", IrAt(Offset("count * factor")).Method?.Name);
    }

    /// <summary>
    /// The IR covers what has meaning, not what was written, so it answers less often than the syntax
    /// tree does -- and a caller that wants an answer everywhere has to ask the other one.
    /// </summary>
    [Fact]
    public void APositionBetweenTwoMethodsHasNoIrButStillHasSyntax()
    {
        var comment = Offset("// Between") + 3;

        Assert.Null(Model().IrAt(comment));
        Assert.NotNull(Model().SyntaxAt(comment));
    }

    /// <summary>
    /// A compilation that stopped before the binder still has everything the parser produced, which
    /// is what an editor asks about while a schema is missing.
    /// </summary>
    [Fact]
    public void ACompilationThatNeverBoundStillAnswersAboutItsSyntax()
    {
        var result = Compile(
            "extend Outer { fn f() -> int64 { return 1; } }",
            TestPaths.FixtureProtoDirectory);

        Assert.Null(result.Module);

        var model = SemanticModel.For(result);

        Assert.Null(model.IrAt(0));
        Assert.NotNull(model.SyntaxAt(0));
    }

    // ------- crossing between the trees

    /// <summary>
    /// The same syntax shape becomes two different IR nodes depending on what it turned out to mean,
    /// which is exactly why a caller cannot answer this by looking at the syntax.
    /// </summary>
    [Theory]
    [InlineData("count * factor", typeof(IrBinary))]
    [InlineData("sub / factor on_zero 0", typeof(IrIntegerDivision))]
    public void ASyntaxNodeFindsTheIrItWasBoundFrom(string text, Type expected)
    {
        var model = Model();
        var syntax = model.SyntaxAt(Offset(text))!.Enclosing<BinaryExpression>();

        Assert.NotNull(syntax);
        Assert.IsType(expected, model.BoundFrom(syntax));
    }

    /// <summary>
    /// A bare identifier is the sharpest case: three of these are spelled identically and bind to
    /// three unrelated nodes.
    /// </summary>
    [Theory]
    [InlineData("count * factor", typeof(IrFieldAccess))]
    [InlineData("factor on_zero", typeof(IrParameterReference))]
    [InlineData("sub / factor", typeof(IrLocalReference))]
    public void ABareNameFindsWhicheverKindOfReferenceItBecame(string text, Type expected)
    {
        var model = Model();
        var name = model.SyntaxAt(Offset(text))!.Node;

        Assert.IsType<NameExpression>(name);
        Assert.IsType(expected, model.BoundFrom(name));
    }

    [Fact]
    public void SyntaxThatBecameNoIrHasNone()
    {
        var model = Model();
        var declaredType = model.SyntaxAt(Offset("int64 = count"))!.Enclosing<TypeReference>();

        Assert.NotNull(declaredType);
        Assert.Null(model.BoundFrom(declaredType));
    }

    [Fact]
    public void AnIrNodeFindsTheSyntaxItWasBoundFrom()
    {
        var model = Model();
        var field = Assert.IsType<IrFieldAccess>(model.IrAt(Offset("count * factor"))!.Node);

        var name = Assert.IsType<NameExpression>(model.SourceOf(field));

        Assert.Equal("count", name.Name.Text);
    }

    /// <summary>
    /// And the mapping is many-to-one in that direction, which is not a defect: the implicit receiver
    /// and the access over it are two nodes standing for one identifier.
    /// </summary>
    [Fact]
    public void SeveralIrNodesCanCorrespondToOneSyntaxNode()
    {
        var model = Model();
        var field = Assert.IsType<IrFieldAccess>(model.IrAt(Offset("count * factor"))!.Node);
        var receiver = Assert.IsType<IrThis>(field.Receiver);

        Assert.Same(model.SourceOf(field), model.SourceOf(receiver));
    }

    /// <summary>
    /// Span identity was assumed to be unique and is not, so what is established here is the weaker
    /// property the tie-break actually needs: nodes that share a span are always one inside another,
    /// which makes "the first one reached" mean "the outermost one" rather than "whichever the walk
    /// happened to see first".
    /// </summary>
    [Fact]
    public void IrNodesThatShareASpanAlwaysStandInsideOneAnother()
    {
        foreach (var source in CompiledCorpus.All)
        {
            var module = source.Result.Module;
            Assert.NotNull(module);

            foreach (var sharing in IrWalk.DescendantsAndSelf(module)
                         .GroupBy(node => node.Span)
                         .Where(group => group.Count() > 1))
            {
                var outermost = sharing.First();
                var inside = IrWalk.DescendantsAndSelf(outermost).ToList();

                foreach (var node in sharing)
                {
                    Assert.Contains(inside, held => ReferenceEquals(held, node));
                }
            }
        }
    }

    // ------- a call that could not be made

    /// <summary>
    /// The region a call is being typed in is the region signature help and completion ask about, and
    /// every one of these calls is one an author is part way through writing. Each path used to
    /// answer nothing at all there.
    /// </summary>
    [Theory]
    [InlineData("return nosuchname.f(77);")]
    [InlineData("return quantity.f(77);")]
    [InlineData("return 1(77);")]
    [InlineData("return nosuchmethod(77);")]
    [InlineData("return f(77, 88);")]
    [InlineData("return quantity.(77);")]
    public void APositionInsideTheParenthesesOfAFailedCallFindsTheArgument(string body)
    {
        var source = FailingCall(body);
        var model = SemanticModel.For(Compile(source, TestPaths.ExampleProtoDirectory));

        var found = model.IrAt(source.IndexOf("77", StringComparison.Ordinal) + 1);

        Assert.NotNull(found);
        Assert.Equal(77L, Assert.IsType<IrLiteral>(found.Node).Value);
        Assert.NotNull(found.Enclosing<IrUncallableInvocation>());
    }

    /// <summary>
    /// Where the IR stops, stated as a test so it is a limit rather than a surprise. The callee of a
    /// call through something that could never name a method is not bound -- descending one is how a
    /// buffer of unbalanced parentheses stops a bind from finishing, which
    /// <see cref="BinderResilienceTests"/> pins -- so a position on it answers with the call. The
    /// syntax tree still has the expression itself, which is the door a client should use for it.
    /// </summary>
    [Fact]
    public void APositionOnTheCalleeOfAnUncallableCallAnswersWithTheCall()
    {
        var source = FailingCall("return (quantity + 1)(77);");
        var model = SemanticModel.For(Compile(source, TestPaths.ExampleProtoDirectory));

        var inTheCallee = source.IndexOf("quantity", StringComparison.Ordinal) + 1;

        Assert.IsType<IrUncallableInvocation>(model.IrAt(inTheCallee)?.Node);
        Assert.Equal(
            "quantity",
            Assert.IsType<NameExpression>(model.SyntaxAt(inTheCallee)?.Node).Name.Text);
    }

    // ------- every position at once

    /// <summary>
    /// The property that has to hold everywhere rather than at the positions someone thought of: no
    /// offset in a real file throws, and no answer is a node that does not cover the offset it was
    /// found for. Swept one past each end, because an editor sends positions computed against a
    /// buffer it may have edited since.
    /// </summary>
    [Fact]
    public void EveryPositionInARealFileIsAnswerableAndCoveredByItsAnswer()
    {
        SweepEveryPosition(CompiledCorpus.SimpleScript);
    }

    /// <summary>
    /// And again over a file that does not parse, since a buffer mid-edit is the common case rather
    /// than the exception, and error recovery is what puts nodes in surprising places.
    /// </summary>
    [Fact]
    public void EveryPositionInABrokenBufferIsAnswerableAndCoveredByItsAnswer()
    {
        Assert.True(
            CompiledCorpus.Broken.Result.Diagnostics.HasErrors,
            "the point of this fixture is that it does not parse");

        SweepEveryPosition(CompiledCorpus.Broken);
    }

    /// <summary>
    /// The deep tree no well-formed file produces and an editor produces by accident. The parser
    /// caps the depth it recurses to, but its postfix loop builds calls and member accesses
    /// iteratively, so nothing caps the depth of what recovery leaves behind: 5000 unbalanced
    /// parentheses come back as an invocation chain 2436 nodes deep. Both queries have to answer
    /// over it without walking the stack out -- a <see cref="StackOverflowException"/> cannot be
    /// caught, and a language server that meets one is gone rather than degraded.
    /// </summary>
    [Fact]
    public void ADeeplyRecoveredTreeIsStillAnswerable()
    {
        const int Parentheses = 5_000;

        var source = "import proto \"invoice.proto\";\n"
            + "extend InvoiceItem { fn f() -> int64 { return "
            + new string('(', Parentheses) + "1" + new string(')', Parentheses) + "; } }";

        var model = SemanticModel.For(Compile(source, TestPaths.ExampleProtoDirectory));
        var insideTheChain = source.IndexOf("return", StringComparison.Ordinal) + Parentheses / 2;

        Assert.NotNull(model.SyntaxAt(insideTheChain));
        Assert.NotNull(model.SyntaxAt(source.Length - 1));
    }

    // ------- helpers

    private static void SweepEveryPosition(CorpusSource source)
    {
        var model = SemanticModel.For(source.Result);

        for (var offset = -1; offset <= source.Text.Length + 1; offset++)
        {
            AssertCovers(model.SyntaxAt(offset)?.Node.Span, offset);
            AssertCovers(model.IrAt(offset)?.Node.Span, offset);
        }
    }

    private static void AssertCovers(SourceSpan? span, int offset)
    {
        if (span is not { } found)
        {
            return;
        }

        Assert.True(
            found.Start.Offset <= offset && offset <= found.End.Offset,
            $"the node answered for offset {offset} spans {found.Start.Offset}..{found.End.Offset}");
    }

    private static string FailingCall(string body)
        => "import proto \"invoice.proto\";\n"
        + "extend InvoiceItem { fn f() -> int64 { " + body + " } }";

    private static int Offset(string text)
    {
        var offset = Fixture.IndexOf(text, StringComparison.Ordinal);

        Assert.True(offset >= 0, $"the fixture must contain '{text}'");
        return offset;
    }

    private static CompilationResult Compile(string source, string protoDirectory)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [protoDirectory]);

    /// <summary>
    /// Compiled once for the whole class. Every test here asks about the same text, and each
    /// compilation shells out to protoc -- which a test that sweeps a method's every position would
    /// otherwise do a few hundred times.
    /// </summary>
    private static readonly Lazy<CompilationResult> Fixed = new(() =>
    {
        var result = Compile(Fixture, TestPaths.FixtureProtoDirectory);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        return result;
    });

    private static CompilationResult Compile() => Fixed.Value;

    private static SemanticModel Model() => SemanticModel.For(Compile());

    private static SyntaxLocation SyntaxAt(int offset)
    {
        var found = Model().SyntaxAt(offset);

        Assert.NotNull(found);
        return found;
    }

    private static IrLocation IrAt(int offset)
    {
        var found = Model().IrAt(offset);

        Assert.NotNull(found);
        return found;
    }
}
