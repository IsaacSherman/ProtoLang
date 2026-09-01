using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Types;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// A file that does not parse is still bound, so an editor has types for the parts that do.
/// </summary>
/// <remarks>
/// These run the whole pipeline against the real example schema, because the property under test is
/// a property of the pipeline: the binder was always willing to do this and was never asked.
/// </remarks>
public class PartialBindingTests
{
    private static CompilationResult CompileSource(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.ExampleProtoDirectory]);

    private const string Prelude = "import proto \"invoice.proto\";\n";

    /// <summary>The motivating case: the caret sits after a dot and a completion list is due.</summary>
    private const string TrailingDot =
        """
        import proto "invoice.proto";
        extend Invoice {
            fn f() -> int64 {
                for line in items {
                    return line.
                }

                return 0;
            }
        }
        """;

    // ------- the trailing dot

    [Fact]
    public void ATrailingDotBindsItsReceiverAndExposesItsType()
    {
        var awaiting = MissingMemberAccessIn(CompileSource(TrailingDot));

        Assert.Equal(
            "protolang.examples.InvoiceItem",
            Assert.IsType<MessageType>(awaiting.Receiver.Type).Descriptor.FullName);
    }

    [Fact]
    public void ATrailingDotIsAnchoredWhereTheMemberNameWouldGo()
    {
        var awaiting = MissingMemberAccessIn(CompileSource(TrailingDot));

        var afterTheDot = TrailingDot.IndexOf("line.", StringComparison.Ordinal) + "line.".Length;

        Assert.True(awaiting.Span.IsEmpty, "an insertion point covers no text");
        Assert.Equal(afterTheDot, awaiting.Span.Start.Offset);
    }

    /// <summary>
    /// A dot awaiting a name is a different thing in the IR from a member access that failed for
    /// any other reason, all of which collapse to an error-typed literal.
    /// </summary>
    [Fact]
    public void AnAccessAwaitingANameIsNotTheSameAsAnAccessThatFailed()
    {
        var unknownField = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return nosuchfield.x; } }");

        Assert.False(unknownField.Success);
        Assert.Empty(Walk(unknownField.Module!).OfType<IrMissingMemberAccess>());
    }

    /// <summary>
    /// The other trailing dot an editor sees constantly. <c>Level.</c> is a reach into an enum, not
    /// a read of a value called Level, and the completed access has always known that -- so the
    /// unfinished one has to know it too, or enum-value completion is answered with an error type.
    /// </summary>
    [Fact]
    public void ATrailingDotOnAnEnumTypeExposesTheEnum()
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(
                "import proto \"fixtures.proto\";\n"
                + "extend Outer { fn f() -> TopLevelStatus { return TopLevelStatus. } }"),
            [TestPaths.FixtureProtoDirectory]);

        var awaiting = Assert.Single(Walk(result.Module!).OfType<IrMissingMemberAccess>());

        Assert.Equal(
            "protolang.tests.TopLevelStatus",
            Assert.IsType<EnumPlType>(awaiting.Receiver.Type).Descriptor.FullName);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0037");
    }

    /// <summary>
    /// Nothing can be called through a name that has not been typed, but the arguments were typed
    /// and are still the author's code.
    /// </summary>
    [Fact]
    public void ArgumentsOfACallWithNoMethodNameAreStillChecked()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity.(nope); } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0037");
    }

    // ------- what survives a parse error

    [Fact]
    public void AFileWithAParseErrorStillHasTypesForWhatParsed()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem {\n"
            + "    fn broken() -> int64 { return name. }\n"
            + "    fn whole() -> int64 { return quantity; }\n"
            + "}");

        Assert.True(result.Diagnostics.HasErrors);

        var whole = result.Module!.Methods.Single(m => m.Name == "whole");
        var returned = Assert.IsType<IrReturn>(Assert.Single(whole.Body.Statements));

        Assert.Equal(ScalarType.Int64Type, whole.ReturnType);
        Assert.Equal("quantity", Assert.IsType<IrFieldAccess>(returned.Value).Field.Name);
    }

    [Fact]
    public void AModuleIsCarriedOutOfACompilationThatFailedToParse()
    {
        Assert.NotNull(CompileSource(TrailingDot).Module);
    }

    /// <summary>
    /// A method whose name is still being typed has a body that is not, and the body is where the
    /// author's caret is. Nothing can call it, which is why it is never declared -- but dropping it
    /// leaves the one method being worked on as the one method with no types.
    /// </summary>
    [Fact]
    public void AMethodWithNoNameStillBindsItsBody()
    {
        var result = CompileSource(
            Prelude
            + "extend Invoice {\n"
            + "    fn () -> int64 {\n"
            + "        for line in items {\n"
            + "            return line.\n"
            + "        }\n"
            + "\n"
            + "        return 0;\n"
            + "    }\n"
            + "}");

        var awaiting = Assert.Single(Walk(result.Module!).OfType<IrMissingMemberAccess>());

        Assert.Equal(
            "protolang.examples.InvoiceItem",
            Assert.IsType<MessageType>(awaiting.Receiver.Type).Descriptor.FullName);
    }

    /// <summary>
    /// A declaration that cannot be called is still a declaration, so what it declares is still
    /// resolved and mistakes in it are still reported. Skipping the whole declaration meant a void
    /// parameter went unmentioned for as long as the method name was unfinished.
    /// </summary>
    [Fact]
    public void AMethodWithNoNameStillReportsMistakesInItsSignature()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn (x: void) -> int64 { return 1; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0010");
        Assert.Contains(result.Diagnostics, d => d.Code == "PL0024");
    }

    /// <summary>
    /// Every declaration binds against the types it states, never against those of whichever other
    /// declaration happens to share its name. Reading the first one's parameter list to bind the
    /// second was an unhandled exception the moment the two differed in length.
    /// </summary>
    [Fact]
    public void EachDeclarationBindsAgainstItsOwnParameterList()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem {\n"
            + "    fn f() -> int64 { return 1; }\n"
            + "    fn f(a: int64, b: int64) -> int64 { return a; }\n"
            + "}");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0022");

        var declared = result.Module!.Methods
            .Where(method => method.Name == "f")
            .Select(method => method.Parameters.Count)
            .Order();

        Assert.Equal([0, 2], declared);
    }

    /// <summary>
    /// The same for a method refused because its name is a field's. Refusal governs what may be
    /// called; the body is the author's either way, and an editor wants its types.
    /// </summary>
    [Fact]
    public void AMethodRefusedForItsNameStillBindsItsBody()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn quantity() -> int64 { return name. } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0023");

        var awaiting = Assert.Single(Walk(result.Module!).OfType<IrMissingMemberAccess>());
        Assert.Equal(ScalarType.StringType, awaiting.Receiver.Type);
    }

    /// <summary>
    /// A parameter list with a name still missing from it says nothing about which arguments a test
    /// ought to supply, so demanding one called the empty string describes nothing the author did.
    /// </summary>
    [Fact]
    public void AParameterWithNoNameIsNotDemandedOfATest()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f(: int64) -> int64 { return 1; } }\n"
            + "test InvoiceItem.f \"x\" { receiver { } expect return 1; }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0010");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0066");
    }

    /// <summary>
    /// Whether a name was written is a fact about one parameter, not about the signature. The
    /// parameter beside the hole is one the author named, one the test could have supplied, and one
    /// the test should still be told about.
    /// </summary>
    [Fact]
    public void AParameterThatWasNamedIsStillDemandedWhenAnotherWasNot()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f(a: int64, : int64) -> int64 { return 1; } }\n"
            + "test InvoiceItem.f \"x\" { receiver { } expect return 1; }");

        var missing = Assert.Single(result.Diagnostics, d => d.Code == "PL0066");

        Assert.Contains("'a'", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A syntax error and a schema that is not there are two independent problems, and an author
    /// with both of them has both of them. The parse gate used to report the first and stop, so the
    /// second only appeared once the first was fixed.
    /// </summary>
    [Theory]
    [InlineData("import proto \"nosuch.proto\";\nextend InvoiceItem { fn f() -> int64 { return quantity. } }", "PL0002")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity. } }", "PL0001")]
    public void AParseErrorNoLongerHidesAProblemWithTheImports(string source, string code)
    {
        var result = CompileSource(source);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0010");
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    /// <summary>
    /// An import being typed is not an import of a schema called the empty string. It still stops
    /// the compilation -- there is nothing behind it to bind against -- but it says so once.
    /// </summary>
    [Fact]
    public void AnImportWithNoPathIsNotAlsoReportedAsNotFound()
    {
        var result = CompileSource("import proto ;\nextend InvoiceItem { fn f() -> int64 { return 1; } }");

        Assert.Equal("PL0010", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void AnImportOfAnEmptyPathIsStillReportedAsNotFound()
    {
        var result = CompileSource("import proto \"\";\nextend InvoiceItem { fn f() -> int64 { return 1; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0002");
    }

    /// <summary>
    /// The other direction, which is the one that would have made the change pointless: imports
    /// that resolve perfectly must not be stopped by a syntax error elsewhere in the file.
    /// </summary>
    [Fact]
    public void GoodImportsAreStillLoadedForAFileThatDidNotParse()
    {
        Assert.NotEmpty(CompileSource(TrailingDot).Descriptors);
    }

    /// <summary>
    /// The safety property the whole change rests on. Every consumer in the repository asks this
    /// before touching the module, so a module that now exists where none did must not make it true.
    /// </summary>
    [Theory]
    [InlineData("extend Invoice { fn f() -> int64 { return items. } }")]
    [InlineData("extend { }")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity }")]
    [InlineData("}{")]
    public void SuccessIsStillFalseWhenTheFileDidNotParse(string body)
    {
        var result = CompileSource(Prelude + body);

        Assert.True(result.Diagnostics.HasErrors);
        Assert.False(result.Success);
    }

    /// <summary>
    /// The rule an emitter follows, in the type rather than in an ordering convention. A partial
    /// module exists and is exactly what must not be written from.
    /// </summary>
    [Fact]
    public void NothingIsEmittableFromAFileThatDidNotParse()
    {
        var result = CompileSource(TrailingDot);

        Assert.NotNull(result.Module);
        Assert.Null(result.EmittableModule);
    }

    [Fact]
    public void AWholeCompilationIsEmittableFromTheModuleItBound()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity; } }");

        Assert.True(result.Success, Render(result));
        Assert.Same(result.Module, result.EmittableModule);
    }

    // ------- a call that could not be made

    /// <summary>
    /// A call fails for six different reasons and keeps its arguments for one: they are source the
    /// author wrote, they are where the caret is while the call is being typed, and every one of
    /// these paths used to answer an editor with an error-typed literal spanning the whole call.
    /// </summary>
    [Theory]
    [InlineData("return nosuchname.f(1, 2);")]
    [InlineData("return quantity.f(1, 2);")]
    [InlineData("return 1(1, 2);")]
    [InlineData("return nosuchmethod(1, 2);")]
    [InlineData("return f(1, 2);")]
    [InlineData("return quantity.(1, 2);")]
    public void ACallThatCouldNotBeMadeStillHoldsItsArguments(string body)
    {
        var result = CompileSource(Prelude + "extend InvoiceItem { fn f() -> int64 { " + body + " } }");

        var call = Assert.Single(Walk(result.Module!).OfType<IrUncallableInvocation>());

        Assert.Equal(2, call.Arguments.Count);
    }

    /// <summary>
    /// A call whose only fault is an argument's type is not one of those. The receiver, the method
    /// and the signature all resolved, and an editor asking what is being called there has an
    /// answer that would be thrown away by treating it as uncallable.
    /// </summary>
    [Fact]
    public void ACallWhoseArgumentHasTheWrongTypeStillKnowsWhatItCalls()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem {\n"
            + "    fn g(a: int64) -> int64 { return a; }\n"
            + "    fn f() -> int64 { return g(\"s\"); }\n"
            + "}");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0046");

        var call = Assert.Single(Walk(result.Module!).OfType<IrMethodCall>());

        Assert.Equal("g", call.Target.Name);
    }

    /// <summary>
    /// A callee that could never name a method keeps the call's arguments and nothing else. The
    /// callee is where this stops: the parser's nesting budget bounds its own recursion and not the
    /// chain its postfix loop builds, so descending one is how a buffer of unbalanced parentheses
    /// stops a bind from finishing. <see cref="BinderResilienceTests"/> is what says so out loud.
    /// </summary>
    [Fact]
    public void ACalleeThatCouldNeverNameAMethodLeavesNoReceiverBehind()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return (quantity + 1)(77); } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0043");

        var call = Assert.Single(Walk(result.Module!).OfType<IrUncallableInvocation>());

        Assert.Null(call.Receiver);
        Assert.Equal(77L, Assert.IsType<IrLiteral>(Assert.Single(call.Arguments)).Value);
    }

    /// <summary>
    /// The receiver survives too, where there was one. It is the value the call would have been made
    /// on, and go-to-definition and hover are asked about it whether or not the call resolved.
    /// </summary>
    [Fact]
    public void ACallOnAValueWithNoMethodsKeepsTheReceiverItWasMadeOn()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f() -> int64 { return quantity.nope(1); } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0042");

        var call = Assert.Single(Walk(result.Module!).OfType<IrUncallableInvocation>());

        Assert.Equal("quantity", Assert.IsType<IrFieldAccess>(call.Receiver).Field.Name);
    }

    // ------- one mistake, one diagnostic

    /// <summary>
    /// The cascade the error-typed placeholder exists to stop: a name that resolves to nothing must
    /// not also make every expression derived from it complain about its type.
    /// </summary>
    [Fact]
    public void OneUnresolvableNameProducesOneDiagnostic()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { var x: int64 = nope; return x + x * x; } }");

        Assert.Equal("PL0037", Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>
    /// The same property inside a call that cannot be made. Every failure path binds its arguments
    /// exactly once: two of them arrive with the arguments already bound and hand over the list they
    /// built, and binding a second time would report one bad name twice.
    /// </summary>
    [Theory]
    [InlineData("return nosuchname.f(nope);")]
    [InlineData("return quantity.f(nope);")]
    [InlineData("return 1(nope);")]
    [InlineData("return nosuchmethod(nope);")]
    [InlineData("return f(nope, 1);")]
    [InlineData("return quantity.(nope);")]
    public void OneUnresolvableNameInsideAFailedCallAlsoProducesOneDiagnostic(string body)
    {
        var result = CompileSource(Prelude + "extend InvoiceItem { fn f() -> int64 { " + body + " } }");

        // The name in the argument, not every unresolvable name: the receiver of the first case is
        // itself unknown, which is what puts that call on the path being tested.
        Assert.Single(
            result.Diagnostics,
            d => d.Code == "PL0037" && d.Message.Contains("'nope'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A name that is missing has already been reported by the parser, at the position it is missing
    /// from. Resolving it anyway would say the same thing a second time in different words.
    /// </summary>
    [Theory]
    [InlineData("extend { }", "PL0010", "PL0021")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { var x: = 1; return 1; } }", "PL0013", "PL0025")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return name. } }", "PL0010", "PL0041")]
    [InlineData("extend InvoiceItem { fn f() -> int64 { return quantity.(); } }", "PL0010", "PL0044")]
    [InlineData("extend InvoiceItem { fn f() -> bool { return has name.; } }", "PL0010", "PL0041")]
    public void AMissingNameIsNotDiagnosedASecondTimeByTheBinder(
        string body,
        string reportedByTheParser,
        string notReportedAgain)
    {
        var result = CompileSource(Prelude + body);

        Assert.Contains(result.Diagnostics, d => d.Code == reportedByTheParser);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == notReportedAgain);
    }

    /// <summary>
    /// Two methods being typed at once are two unnamed methods, not one method declared twice.
    /// </summary>
    [Fact]
    public void HalfTypedMethodsDoNotCollideWithEachOther()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn () -> int64 { return 1; } fn () -> int64 { return 2; } }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0022");
    }

    [Fact]
    public void HalfTypedParametersDoNotCollideWithEachOther()
    {
        var result = CompileSource(
            Prelude + "extend InvoiceItem { fn f(: int64, : int64) -> int64 { return 1; } }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0026");
    }

    [Fact]
    public void HalfTypedVariablesDoNotCollideWithEachOther()
    {
        var result = CompileSource(
            Prelude
            + "extend InvoiceItem { fn f() -> int64 { var = 1; var = 2; return 1; } }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0029");
    }

    // ------- what a message calls something that has no name

    /// <summary>
    /// A name that was never written has no spelling to quote, and <c>''</c> reads as a defect in
    /// the compiler rather than as a fact about the program. The span already says where it is.
    /// </summary>
    [Theory]
    [InlineData("extend InvoiceItem { fn () -> int64 { } }", "This method declares a return type")]
    [InlineData(
        "extend InvoiceItem { fn f(: void) -> int64 { return 1; } }",
        "This parameter cannot be declared void.")]
    [InlineData(
        "extend InvoiceItem { fn f() -> int64 { var : void = 1; return 1; } }",
        "This variable cannot be declared void.")]
    [InlineData(
        "extend InvoiceItem { fn f() -> int64 { var : int64 = \"s\"; return 1; } }",
        "Cannot initialize this variable of type")]
    public void ADiagnosticAboutSomethingUnnamedSaysWhatItIsInsteadOfQuotingNothing(
        string body,
        string expected)
    {
        var result = CompileSource(Prelude + body);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("''", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the wording for a name that is there does not move, because that wording is published.
    /// </summary>
    [Theory]
    [InlineData("extend InvoiceItem { fn f() -> int64 { } }", "'f' declares a return type")]
    [InlineData(
        "extend InvoiceItem { fn f(x: void) -> int64 { return 1; } }",
        "Parameter 'x' cannot be declared void.")]
    [InlineData(
        "extend InvoiceItem { fn f() -> int64 { var x: void = 1; return 1; } }",
        "Variable 'x' cannot be declared void.")]
    [InlineData(
        "extend InvoiceItem { fn f() -> int64 { var x: int64 = \"s\"; return 1; } }",
        "Cannot initialize 'x' of type")]
    public void ADiagnosticAboutSomethingNamedStillQuotesTheName(string body, string expected)
    {
        var result = CompileSource(Prelude + body);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains(expected, StringComparison.Ordinal));
    }

    // ------- helpers

    private static string Render(CompilationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));

    private static IrMissingMemberAccess MissingMemberAccessIn(CompilationResult result)
    {
        Assert.False(result.Success, "the motivating case is a file with a syntax error in it");

        return Assert.Single(Walk(result.Module!).OfType<IrMissingMemberAccess>());
    }

    /// <summary>Every expression in a module, however deeply nested. See <see cref="IrWalk"/>.</summary>
    private static IEnumerable<IrExpression> Walk(IrModule module)
        => IrWalk.DescendantsAndSelf(module).OfType<IrExpression>();
}
