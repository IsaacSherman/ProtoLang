using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Symbols;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The index of nameable schema types, which the binder resolves against and a host predicting what
/// the binder would accept reads instead of walking the descriptors again.
/// </summary>
/// <remarks>
/// <para>
/// The sweeps are the point. Every property here is one a second, hand-rolled walk over the same
/// descriptors would get subtly wrong -- a nested enum omitted, a simple name answered as unambiguous
/// because only the top level was looked at -- and those failures are silent: a completion list that
/// offers a name the compiler then refuses, or withholds one it would have accepted.
/// </para>
/// <para>
/// Asserted against the descriptors the compilation actually loaded rather than against a list of
/// names written out here, because a hardcoded list stops meaning anything the moment a fixture gains
/// a message.
/// </para>
/// </remarks>
public class SchemaTypesTests
{
    private const string Fixtures = "import proto \"fixtures.proto\";\n";
    private const string Ambiguous = "import proto \"ambiguous_enums.proto\";\n";

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    private static SchemaTypes TypesOf(string source)
    {
        var result = Compile(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code is "PL0002" or "PL0003");
        return result.Types;
    }

    /// <summary>Every message the loaded schemas declare, nested ones included.</summary>
    private static IEnumerable<MessageDescriptor> AllMessages(IEnumerable<MessageDescriptor> messages)
    {
        foreach (var message in messages)
        {
            yield return message;

            foreach (var nested in AllMessages(message.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Every enum the loaded schemas declare, including those nested in a message.</summary>
    private static IEnumerable<EnumDescriptor> AllEnums(CompilationResult result)
        => result.Descriptors.SelectMany(file => file.EnumTypes)
            .Concat(result.Descriptors
                .SelectMany(file => AllMessages(file.MessageTypes))
                .SelectMany(message => message.EnumTypes));

    // ------- everything declared is reachable

    [Fact]
    public void EveryMessageTheSchemasDeclareIsFoundByItsFullName()
    {
        var result = Compile(Fixtures);
        var messages = AllMessages(result.Descriptors.SelectMany(file => file.MessageTypes)).ToList();

        Assert.True(messages.Count > 3, $"the fixtures must declare several messages; they declare {messages.Count}");

        foreach (var message in messages)
        {
            Assert.True(
                ReferenceEquals(result.Types.FindMessage(message.FullName), message),
                $"'{message.FullName}' is declared and must be found by its full name");
        }
    }

    [Fact]
    public void EveryEnumTheSchemasDeclareIsFoundByItsFullName()
    {
        var result = Compile(Fixtures + Ambiguous);
        var enums = AllEnums(result).ToList();

        Assert.True(enums.Count > 1, $"the fixtures must declare several enums; they declare {enums.Count}");

        foreach (var enumType in enums)
        {
            Assert.True(
                ReferenceEquals(result.Types.FindEnum(enumType.FullName), enumType),
                $"'{enumType.FullName}' is declared and must be found by its full name");
        }
    }

    [Fact]
    public void EveryTypeFoundByItsFullNameIsAlsoFoundAmongTheOnesSharingItsSimpleName()
    {
        var result = Compile(Fixtures + Ambiguous);

        foreach (var message in AllMessages(result.Descriptors.SelectMany(file => file.MessageTypes)))
        {
            Assert.Contains(message, result.Types.MessagesNamed(message.Name));
        }

        foreach (var enumType in AllEnums(result))
        {
            Assert.Contains(enumType, result.Types.EnumsNamed(enumType.Name));
        }
    }

    // ------- asking by identity

    /// <summary>
    /// What a caret gives is an identity, because a name in type position resolves to a type and
    /// leaves no IR node behind. Swept over every message and enum the fixtures declare, since the
    /// shape a hand-rolled lookup omits first is the one nested inside something else.
    /// </summary>
    [Fact]
    public void EveryTypeIsReachableByTheIdentityTheIrCarriesForIt()
    {
        var result = Compile(Fixtures);
        var types = result.Types;

        foreach (var message in AllMessages(result.Descriptors.SelectMany(file => file.MessageTypes)))
        {
            var found = types.Find(SymbolId.ForType(message));

            Assert.True(found is SchemaMessageName, $"'{message.FullName}' is not reachable by identity");
            Assert.Equal(message.FullName, found!.FullName);
        }

        foreach (var enumType in AllEnums(result))
        {
            var found = types.Find(SymbolId.ForType(enumType));

            Assert.True(found is SchemaEnumName, $"'{enumType.FullName}' is not reachable by identity");
            Assert.Equal(enumType.FullName, found!.FullName);
        }
    }

    /// <summary>
    /// A field identity and a type identity can spell the same full name -- protobuf puts a field in
    /// its message and the index is over types alone -- so the kind has to be part of what is
    /// matched rather than an afterthought.
    /// </summary>
    [Fact]
    public void AnIdentityThatIsNotATypeReachesNothing()
    {
        var result = Compile(Fixtures);
        var field = result.Types.FindMessage("protolang.tests.Outer")!.FindFieldByName("count");

        Assert.Null(result.Types.Find(SymbolId.ForField(field)));
    }

    [Fact]
    public void TheIndexOverNoSchemasReachesNothing()
        => Assert.Null(SchemaTypes.Empty.Find(default));

    // ------- nesting, which is what a second walk omits

    [Fact]
    public void AMessageNestedInAnotherMessageIsIndexed()
    {
        var types = TypesOf(Fixtures);

        Assert.NotNull(types.FindMessage("protolang.tests.Outer.Inner"));
        Assert.Single(types.MessagesNamed("Inner"));
    }

    [Fact]
    public void AnEnumNestedInAMessageIsIndexed()
    {
        var types = TypesOf(Ambiguous);

        Assert.NotNull(types.FindEnum("protolang.tests.ambiguous.First.Kind"));
        Assert.NotNull(types.FindEnum("protolang.tests.ambiguous.Second.Kind"));
    }

    // ------- the three ambiguity questions are three questions

    [Fact]
    public void ASimpleNameTwoNestedEnumsShareAnswersWithBothOfThem()
    {
        var types = TypesOf(Ambiguous);
        var candidates = types.EnumsNamed("Kind");

        Assert.Equal(
            ["protolang.tests.ambiguous.First.Kind", "protolang.tests.ambiguous.Second.Kind"],
            candidates.Select(candidate => candidate.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnEnumIsNeverOfferedAsAMessageAndAMessageIsNeverOfferedAsAnEnum()
    {
        var result = Compile(Fixtures + Ambiguous);

        foreach (var enumType in AllEnums(result))
        {
            Assert.Null(result.Types.FindMessage(enumType.FullName));
        }

        foreach (var message in AllMessages(result.Descriptors.SelectMany(file => file.MessageTypes)))
        {
            Assert.Null(result.Types.FindEnum(message.FullName));
        }
    }

    // ------- what it says about what it does not have

    [Fact]
    public void ANameNoSchemaDeclaresIsAnsweredWithNothingRatherThanNull()
    {
        var types = TypesOf(Fixtures);

        Assert.Null(types.FindMessage("protolang.tests.NoSuchMessage"));
        Assert.Null(types.FindEnum("protolang.tests.NoSuchEnum"));
        Assert.Empty(types.MessagesNamed("NoSuchMessage"));
        Assert.Empty(types.EnumsNamed("NoSuchEnum"));
    }

    [Fact]
    public void TheEmptyIndexKnowsNoTypesAndStillAnswersEveryQuestion()
    {
        Assert.Null(SchemaTypes.Empty.FindMessage("anything"));
        Assert.Null(SchemaTypes.Empty.FindEnum("anything"));
        Assert.Empty(SchemaTypes.Empty.MessagesNamed("anything"));
        Assert.Empty(SchemaTypes.Empty.EnumsNamed("anything"));
    }

    [Fact]
    public void ACompilationThatNeverBoundStillPublishesAnIndexToAsk()
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript("import proto \"no_such_schema.proto\";\n"),
            [TestPaths.FixtureProtoDirectory]);

        Assert.Null(result.Module);
        Assert.Empty(result.Types.MessagesNamed("Outer"));
    }

    // ------- names are compared the way protobuf compares them

    [Fact]
    public void ANameDifferingOnlyInCaseIsADifferentName()
    {
        var types = TypesOf(Fixtures);

        Assert.NotNull(types.FindMessage("protolang.tests.Outer"));
        Assert.Null(types.FindMessage("protolang.tests.outer"));
        Assert.Empty(types.MessagesNamed("outer"));
    }
}
