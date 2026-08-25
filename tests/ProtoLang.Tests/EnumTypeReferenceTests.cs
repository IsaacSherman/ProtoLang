using ProtoLang.Ir;
using ProtoLang.Types;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Explicit enum type references (issue #1). Inferred enum locals always worked, because
/// <see cref="TypeFactory.FromFieldValue"/> produces <see cref="EnumPlType"/> for enum-typed fields;
/// what did not work was naming the enum, because the binder indexed only messages.
/// </summary>
public class EnumTypeReferenceTests
{
    private const string FixturePrelude = "import proto \"fixtures.proto\";\n";
    private const string AmbiguousPrelude = "import proto \"ambiguous_enums.proto\";\n";

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    private static PlType TypeOfLocal(CompilationResult result, string methodName, string localName)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var body = result.Module!.Methods.Single(m => m.Name == methodName).Body;
        var declaration = body.Statements.OfType<IrVariableDeclaration>().Single(d => d.Local.Name == localName);
        return declaration.Local.Type;
    }

    [Fact]
    public void ResolvesATopLevelEnumInAVariableDeclaration()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var s: TopLevelStatus = status;
                    return count;
                }
            }
            """);

        var type = Assert.IsType<EnumPlType>(TypeOfLocal(result, "f", "s"));
        Assert.Equal("protolang.tests.TopLevelStatus", type.Descriptor.FullName);
    }

    [Fact]
    public void ResolvesATopLevelEnumByItsFullyQualifiedName()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var s: protolang.tests.TopLevelStatus = status;
                    return count;
                }
            }
            """);

        Assert.IsType<EnumPlType>(TypeOfLocal(result, "f", "s"));
    }

    [Fact]
    public void ResolvesAnEnumAsAReturnType()
    {
        var result = Compile(
            FixturePrelude + "extend Outer { fn f() -> TopLevelStatus { return status; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var method = result.Module!.Methods.Single(m => m.Name == "f");
        var type = Assert.IsType<EnumPlType>(method.ReturnType);
        Assert.Equal("protolang.tests.TopLevelStatus", type.Descriptor.FullName);
    }

    [Fact]
    public void ResolvesAnEnumAsAParameterType()
    {
        var result = Compile(
            FixturePrelude + "extend Outer { fn f(other: TopLevelStatus) -> bool { return other == status; } }");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var method = result.Module!.Methods.Single(m => m.Name == "f");
        Assert.IsType<EnumPlType>(Assert.Single(method.Parameters).Type);
    }

    [Fact]
    public void ResolvesAnEnumNestedInAMessageBySimpleName()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var n: Nested = nested;
                    return count;
                }
            }
            """);

        var type = Assert.IsType<EnumPlType>(TypeOfLocal(result, "f", "n"));
        Assert.Equal("protolang.tests.Outer.Nested", type.Descriptor.FullName);
    }

    [Fact]
    public void ResolvesAnEnumNestedInAMessageByFullName()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend Outer {
                fn f() -> int64 {
                    var n: protolang.tests.Outer.Nested = nested;
                    return count;
                }
            }
            """);

        Assert.IsType<EnumPlType>(TypeOfLocal(result, "f", "n"));
    }

    /// <summary>
    /// Two levels of nesting. The binder only reaches this descriptor by recursing through
    /// <c>Outer</c> and then <c>Outer.Inner</c>, so it fails if enums are indexed at one level only.
    /// </summary>
    [Fact]
    public void ResolvesADeeplyNestedEnum()
    {
        var result = Compile(
            FixturePrelude +
            """
            extend protolang.tests.Outer.Inner {
                fn f() -> protolang.tests.Outer.Inner.Deep {
                    var d: Deep = deep;
                    return d;
                }
            }
            """);

        var type = Assert.IsType<EnumPlType>(TypeOfLocal(result, "f", "d"));
        Assert.Equal("protolang.tests.Outer.Inner.Deep", type.Descriptor.FullName);
    }

    [Fact]
    public void RejectsAnUnknownTypeName()
    {
        var result = Compile(
            FixturePrelude + "extend Outer { fn f() -> NoSuchStatus { return status; } }");

        Assert.Contains(result.Diagnostics, d => d.Code == "PL0025");
    }

    [Fact]
    public void RejectsAnAmbiguousEnumSimpleName()
    {
        var result = Compile(
            AmbiguousPrelude +
            """
            extend First {
                fn f() -> Kind {
                    return kind;
                }
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0074");
        Assert.Contains("protolang.tests.ambiguous.First.Kind", diagnostic.ToString(), StringComparison.Ordinal);
        Assert.Contains("protolang.tests.ambiguous.Second.Kind", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesAnAmbiguousSimpleNameWhenQualified()
    {
        var result = Compile(
            AmbiguousPrelude +
            """
            extend First {
                fn f() -> protolang.tests.ambiguous.First.Kind {
                    return kind;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }
}
