using Google.Protobuf.Reflection;
using ProtoLang.Ir;
using ProtoLang.Symbols;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Every declaration says where it was written, and every reference can say which declaration it
/// means without holding that declaration's object.
/// </summary>
/// <remarks>
/// These run the whole pipeline against real schemas rather than hand-building IR, because the
/// property under test is that the binder records this while it works. A hand-built
/// <see cref="DeclarationSite"/> would prove only that the type can hold a span.
/// </remarks>
public class SymbolIdentityTests
{
    /// <summary>
    /// One method that declares all four kinds and refers to three of them, and a second that calls
    /// the first, so a call has a callee to find.
    /// </summary>
    private const string Fixture =
        """
        import proto "invoice.proto";
        extend Invoice {
            fn helper(factor: int64) -> int64 {
                var total: int64 = 0;
                for line in items {
                    total = total + line.quantity * factor;
                }

                return total;
            }

            fn caller() -> int64 {
                return helper(2);
            }
        }
        """;

    // ------- declaration locations

    [Fact]
    public void ALocalReferenceReachesTheDeclarationThatIntroducedIt()
    {
        var result = Compile(Fixture);

        var declaration = LocalReference(result, "total").Local.Declaration;

        Assert.Equal(
            Fixture.IndexOf("var total", StringComparison.Ordinal) + "var ".Length,
            declaration.Name.Span.Start.Offset);
    }

    [Fact]
    public void AParameterReferenceReachesItsDeclaration()
    {
        var result = Compile(Fixture);

        var reference = Assert.Single(Expressions(result).OfType<IrParameterReference>());

        Assert.Equal(
            Fixture.IndexOf("factor", StringComparison.Ordinal),
            reference.Parameter.Declaration.Name.Span.Start.Offset);
    }

    [Fact]
    public void ALoopBindingReachesTheLoopThatIntroducedIt()
    {
        var result = Compile(Fixture);

        var declaration = LocalReference(result, "line").Local.Declaration;

        Assert.Equal(
            Fixture.IndexOf("for line", StringComparison.Ordinal) + "for ".Length,
            declaration.Name.Span.Start.Offset);
    }

    /// <summary>
    /// The gap that mattered most: a call resolves to a signature, and a signature used to be the
    /// one thing in the IR that knew what it was without knowing where it came from.
    /// </summary>
    [Fact]
    public void ACallReachesTheDeclarationOfTheMethodItCalls()
    {
        var result = Compile(Fixture);

        var call = Assert.Single(Expressions(result).OfType<IrMethodCall>());

        Assert.Equal(
            Fixture.IndexOf("fn helper", StringComparison.Ordinal) + "fn ".Length,
            call.Target.Declaration.Name.Span.Start.Offset);
    }

    [Fact]
    public void ADeclarationCarriesBothItsNameRangeAndItsWholeExtent()
    {
        var result = Compile(Fixture);

        var declaration = LocalReference(result, "total").Local.Declaration;

        var statement = Fixture.IndexOf("var total", StringComparison.Ordinal);

        Assert.Equal(statement, declaration.Extent.Start.Offset);
        Assert.Equal(
            Fixture.IndexOf(';', statement) + 1,
            declaration.Extent.End.Offset);
        Assert.Equal("total".Length, declaration.Name.Span.Length);
    }

    /// <summary>
    /// The invariant an editor depends on: LSP selects the name range out of the extent, so one has
    /// to lie inside the other. The half-typed parameter is the case that breaks it if nobody is
    /// looking -- what a parameter spans starts at its name, so a parameter with no name spans from
    /// its colon while its name is the empty point after the preceding comma.
    /// </summary>
    [Fact]
    public void EveryDeclarationsNameRangeLiesInsideItsExtent()
    {
        var result = Compile(Fixture + "\n" + HalfTypedParameters);

        var declarations = Declarations(result).ToList();

        Assert.NotEmpty(declarations);

        foreach (var declaration in declarations)
        {
            Assert.True(
                declaration.Extent.Start.Offset <= declaration.Name.Span.Start.Offset
                && declaration.Name.Span.End.Offset <= declaration.Extent.End.Offset,
                $"the name range of {declaration.Id} must lie inside its extent");
        }
    }

    [Fact]
    public void ADeclarationKnowsWhichKindOfThingItDeclares()
    {
        var result = Compile(Fixture);

        var helper = MethodNamed(result, "helper");

        Assert.Equal(SymbolKind.Method, helper.Signature.Declaration.Kind);
        Assert.Equal(SymbolKind.Parameter, Assert.Single(helper.Parameters).Declaration.Kind);
        Assert.Equal(SymbolKind.Local, LocalReference(result, "total").Local.Declaration.Kind);
        Assert.Equal(SymbolKind.LoopBinding, LocalReference(result, "line").Local.Declaration.Kind);
    }

    // ------- identity

    /// <summary>
    /// The case that says identity is not a name. Both branches declare <c>total</c>, neither is
    /// inside the other, and conflating them would highlight one when the caret is on the other.
    /// </summary>
    [Fact]
    public void TwoSameNamedLocalsInSiblingScopesAreDifferentSymbols()
    {
        var result = Compile(SiblingScopes);

        var totals = Declarations(result)
            .Where(declaration => declaration.Name.Text == "total")
            .ToList();

        Assert.Equal(2, totals.Count);
        Assert.NotEqual(totals[0].Id, totals[1].Id);
    }

    [Fact]
    public void EveryReferenceToOneDeclarationCarriesOneIdentity()
    {
        var result = Compile(Fixture);

        var references = Expressions(result)
            .OfType<IrLocalReference>()
            .Where(reference => reference.Local.Name == "total")
            .ToList();

        Assert.Equal(2, references.Count);
        Assert.Equal(references[0].Local.Id, references[1].Local.Id);
    }

    /// <summary>
    /// Without this, occurrence highlighting flickers on every keystroke and nothing downstream may
    /// cache anything keyed by a symbol.
    /// </summary>
    [Fact]
    public void IdentityIsUnchangedByRecompilingTheSameText()
    {
        var identity = SourceIdentity.FromPath(
            Path.Combine(TestPaths.CreateTempDirectory(), "buffer.protolang"));

        var first = Compile(identity, Fixture);
        var second = Compile(identity, Fixture);

        Assert.Equal(
            Declarations(first).Select(declaration => declaration.Id).ToList(),
            Declarations(second).Select(declaration => declaration.Id).ToList());
    }

    /// <summary>
    /// A buffer being typed into is full of names nobody has written yet, and two of them are still
    /// two declarations. Identity comes from where the name would go, and the parser anchors each
    /// hole after a different token.
    /// </summary>
    [Fact]
    public void TwoNamesThatWereNeverWrittenAreStillTwoSymbols()
    {
        var result = Compile(Prelude + HalfTypedParameters);

        var parameters = MethodNamed(result, "half").Parameters;

        Assert.All(parameters, parameter => Assert.True(
            parameter.Declaration.Name.IsMissing,
            "the fixture writes neither parameter name"));
        Assert.NotEqual(parameters[0].Id, parameters[1].Id);
    }

    // ------- schema members

    /// <summary>
    /// The awkward case the issue names: a schema member has no ProtoLang declaration to point at,
    /// and field names collide across messages constantly.
    /// </summary>
    [Fact]
    public void SameNamedFieldsOnDifferentMessagesAreDifferentSymbols()
    {
        var result = CompileAgainstFixtures(AmbiguousSchema);

        var first = MessageNamed(result, "First").FindFieldByName("kind");
        var second = MessageNamed(result, "Second").FindFieldByName("kind");

        Assert.Equal("kind", first.Name);
        Assert.Equal(second.Name, first.Name);
        Assert.NotEqual(SymbolId.ForField(first), SymbolId.ForField(second));
    }

    [Fact]
    public void SameNamedNestedEnumsUnderDifferentMessagesAreDifferentSymbols()
    {
        var result = CompileAgainstFixtures(AmbiguousSchema);

        var first = MessageNamed(result, "First").EnumTypes.Single();
        var second = MessageNamed(result, "Second").EnumTypes.Single();

        Assert.Equal("Kind", first.Name);
        Assert.Equal(second.Name, first.Name);
        Assert.NotEqual(SymbolId.ForType(first), SymbolId.ForType(second));
    }

    /// <summary>
    /// Pins the scoping this identity is built on. Protobuf's own rules put an enum's constants in
    /// the enum's parent, following C++, and under that rule two same-named constants in sibling
    /// enums would be one symbol. The C# runtime scopes them under the enum instead, which is what
    /// makes the descriptor's full name usable here -- so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void AnEnumValueIsIdentifiedByTheEnumThatDeclaresIt()
    {
        var result = CompileAgainstFixtures(AmbiguousSchema);

        var value = MessageNamed(result, "First").EnumTypes.Single().Values.Single();

        Assert.Equal(
            $"protolang.tests.ambiguous.First.Kind.{value.Name}",
            SymbolId.ForEnumValue(value).Key);
    }

    [Fact]
    public void AKindIsPartOfWhatASymbolIdSays()
    {
        var result = CompileAgainstFixtures(AmbiguousSchema);

        var field = MessageNamed(result, "First").FindFieldByName("kind");

        Assert.Equal(SymbolKind.Field, SymbolId.ForField(field).Kind);
        Assert.StartsWith("Field:", SymbolId.ForField(field).ToString(), StringComparison.Ordinal);
    }

    // ------- shadowing, which the language forbids

    [Fact]
    public void AnInnerBlockCannotShadowAnOuterLocal()
    {
        var result = Compile(
            """
            import proto "invoice.proto";
            extend InvoiceItem {
                fn f() -> int64 {
                    var total: int64 = 1;
                    if quantity > 0 {
                        var total: int64 = 2;
                        return total;
                    }

                    return total;
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0029");
    }

    [Fact]
    public void ALoopBindingCannotShadowAnEnclosingLocal()
    {
        var result = Compile(
            """
            import proto "invoice.proto";
            extend Invoice {
                fn f() -> int64 {
                    var line: int64 = 1;
                    for line in items {
                    }

                    return line;
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0029");
    }

    // ------- fixtures

    private const string SiblingScopes =
        """
        import proto "invoice.proto";
        extend InvoiceItem {
            fn f() -> int64 {
                if quantity > 0 {
                    var total: int64 = 1;
                    return total;
                } else {
                    var total: int64 = 2;
                    return total;
                }
            }
        }
        """;

    private const string Prelude = "import proto \"invoice.proto\";\n";

    private const string HalfTypedParameters =
        "extend InvoiceItem { fn half(: int64, : int64) -> int64 { return 1; } }";

    private const string AmbiguousSchema =
        """
        import proto "ambiguous_enums.proto";
        extend First {
            fn f() -> int64 {
                return 1;
            }
        }
        """;

    // ------- helpers

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.ExampleProtoDirectory]);

    private static CompilationResult Compile(SourceIdentity identity, string source)
        => Compilation.Compile(new SourceDocument(identity, source), [TestPaths.ExampleProtoDirectory]);

    private static CompilationResult CompileAgainstFixtures(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    private static IEnumerable<IrExpression> Expressions(CompilationResult result)
        => IrWalk.Expressions(result.Module!);

    private static IEnumerable<DeclarationSite> Declarations(CompilationResult result)
        => IrWalk.Declarations(result.Module!);

    private static IrMethod MethodNamed(CompilationResult result, string name)
        => result.Module!.Methods.Single(method => method.Name == name);

    private static IrLocalReference LocalReference(CompilationResult result, string name)
        => Expressions(result).OfType<IrLocalReference>().First(reference => reference.Local.Name == name);

    private static MessageDescriptor MessageNamed(CompilationResult result, string name)
        => result.Descriptors.SelectMany(file => file.MessageTypes).Single(message => message.Name == name);
}
