using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// How a method describes itself, and how the methods declared on one receiver are found.
/// </summary>
/// <remarks>
/// Both questions have exactly one home because several surfaces ask them and a reader has to
/// reconcile the answers: a completion list, a hover, and signature help all show a method, and the
/// binder and everything predicting it must agree on which receiver a method belongs to.
/// </remarks>
public class MethodSignatureTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";

    /// <param name="errorFree">
    /// Whether the source is expected to bind cleanly. False for the one fixture that declares a
    /// parameter name twice: the language refuses it and the binder still produces the method, which
    /// is the state a reader looking at the signature is actually in.
    /// </param>
    private static IrModule Bind(string source, bool errorFree = true)
    {
        var result = Compilation.Compile(
            TestPaths.WriteTempScript(Prelude + source),
            [TestPaths.FixtureProtoDirectory]);

        if (errorFree)
        {
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }

        return Assert.IsType<IrModule>(result.Module);
    }

    private static IrMethod MethodNamed(IrModule module, string name)
        => Assert.Single(module.Methods, method => method.Name == name);

    // ------- how a method reads

    [Fact]
    public void AMethodRendersTheWayItsDeclarationReads()
    {
        var module = Bind(
            """
            extend Outer {
                fn scaled(factor: int64, label: string) -> int64 { return count * factor; }
            }
            """);

        Assert.Equal(
            "fn scaled(factor: int64, label: string) -> int64",
            MethodNamed(module, "scaled").Signature.DisplayName);
    }

    [Fact]
    public void AMethodTakingNothingRendersEmptyParentheses()
    {
        var module = Bind(
            """
            extend Outer {
                fn plain() -> int64 { return count; }
            }
            """);

        Assert.Equal("fn plain() -> int64", MethodNamed(module, "plain").Signature.DisplayName);
    }

    /// <summary>
    /// Because what a call produces is what decides whether it may be used as a value, and a method
    /// declared without an arrow returns nothing just as surely as one declared with 'void'.
    /// </summary>
    [Fact]
    public void AMethodThatReturnsNothingSaysSoEvenWhereTheAuthorLeftTheArrowOff()
    {
        var module = Bind(
            """
            extend Outer {
                fn implicit() { var unused: int64 = count; }
                fn explicit() -> void { var unused: int64 = count; }
            }
            """);

        Assert.Equal("fn implicit() -> void", MethodNamed(module, "implicit").Signature.DisplayName);
        Assert.Equal("fn explicit() -> void", MethodNamed(module, "explicit").Signature.DisplayName);
    }

    /// <summary>
    /// Virtual says how a method is dispatched rather than how it is called, so it is not part of
    /// the signature and must not appear in one.
    /// </summary>
    [Fact]
    public void WhetherAMethodIsVirtualIsNoPartOfHowItIsCalled()
    {
        var module = Bind(
            """
            extend Outer {
                virtual fn overridable() -> int64 { return count; }
            }
            """);

        var method = MethodNamed(module, "overridable");

        Assert.True(method.IsVirtual, "the fixture declares this method virtual");
        Assert.DoesNotContain("virtual", method.Signature.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMethodDescribesItselfWithItsOwnNameAndEveryParameterItTakes()
    {
        var module = Bind(
            """
            extend Outer {
                fn a() -> int64 { return count; }
                fn b(one: int64) -> int64 { return one; }
                fn c(one: int64, two: string) -> int64 { return one; }
            }

            extend Inner {
                fn d(flag: bool) -> int64 { return 1; }
            }
            """);

        Assert.Equal(4, module.Methods.Count);

        foreach (var method in module.Methods)
        {
            var rendered = method.Signature.DisplayName;

            Assert.StartsWith($"fn {method.Name}(", rendered, StringComparison.Ordinal);
            Assert.EndsWith($" -> {method.ReturnType.DisplayName}", rendered, StringComparison.Ordinal);

            foreach (var parameter in method.Parameters)
            {
                Assert.Contains($"{parameter.Name}: {parameter.Type.DisplayName}", rendered, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Every parameter's label range picks that parameter out of the line, and nothing else.
    /// </summary>
    /// <remarks>
    /// A sweep rather than a case, because what it guards is that two things built from one format
    /// stay in step: change how a signature reads and the ranges move with it or this fails. The
    /// duplicated name is the reason the ranges exist at all -- a caller searching the line for a
    /// parameter's text would find the first of the two whichever one it meant.
    /// </remarks>
    [Fact]
    public void EveryParameterLabelPicksThatParameterOutOfTheSignature()
    {
        var module = Bind(
            """
            extend Outer {
                fn a() -> int64 { return count; }
                fn b(one: int64) -> int64 { return one; }
                fn c(one: int64, two: string) -> int64 { return one; }
                fn d(same: int64, same: int64) -> int64 { return 1; }
            }
            """,
            errorFree: false);

        Assert.Equal(4, module.Methods.Count);

        foreach (var method in module.Methods)
        {
            var rendered = method.Signature.DisplayName;
            var labels = method.Signature.ParameterLabels;

            Assert.Equal(method.Parameters.Count, labels.Count);

            for (var index = 0; index < labels.Count; index++)
            {
                var parameter = method.Parameters[index];

                Assert.InRange(labels[index].Start, 0, rendered.Length);
                Assert.InRange(labels[index].End, labels[index].Start, rendered.Length);

                Assert.Equal(
                    $"{parameter.Name}: {parameter.Type.DisplayName}",
                    rendered[labels[index].Start..labels[index].End]);
            }
        }
    }

    // ------- which receiver a method belongs to

    [Fact]
    public void EveryMethodIsFoundOnTheReceiverItWasDeclaredOnAndOnNoOther()
    {
        var module = Bind(
            """
            extend Outer {
                fn onOuter() -> int64 { return count; }
                fn alsoOnOuter() -> int64 { return count; }
            }

            extend Inner {
                fn onInner() -> int64 { return 1; }
            }
            """);

        var receivers = module.Methods.Select(method => method.Receiver.FullName).Distinct().ToList();

        Assert.Equal(2, receivers.Count);

        foreach (var receiver in receivers)
        {
            var declared = module.MethodsOn(receiver);

            Assert.NotEmpty(declared);
            Assert.All(declared, method => Assert.Equal(receiver, method.Receiver.FullName));
        }

        foreach (var method in module.Methods)
        {
            Assert.Contains(method, module.MethodsOn(method.Receiver.FullName));
        }
    }

    [Fact]
    public void TheMethodsOnAReceiverComeBackInTheOrderTheyWereDeclared()
    {
        var module = Bind(
            """
            extend Outer {
                fn first() -> int64 { return count; }
                fn second() -> int64 { return count; }
                fn third() -> int64 { return count; }
            }
            """);

        Assert.Equal(
            ["first", "second", "third"],
            module.MethodsOn("protolang.tests.Outer").Select(method => method.Name));
    }

    [Fact]
    public void AReceiverThisFileNeverExtendedHasNoMethods()
    {
        var module = Bind(
            """
            extend Outer {
                fn only() -> int64 { return count; }
            }
            """);

        Assert.Empty(module.MethodsOn("protolang.tests.Outer.Inner"));
        Assert.Empty(module.MethodsOn("protolang.tests.NoSuchMessage"));
    }

    /// <summary>
    /// Ordinally, because that is how the binder keys the same question when it resolves a call.
    /// </summary>
    [Fact]
    public void AReceiverNameDifferingOnlyInCaseIsADifferentReceiver()
    {
        var module = Bind(
            """
            extend Outer {
                fn only() -> int64 { return count; }
            }
            """);

        Assert.Single(module.MethodsOn("protolang.tests.Outer"));
        Assert.Empty(module.MethodsOn("protolang.tests.outer"));
    }
}
