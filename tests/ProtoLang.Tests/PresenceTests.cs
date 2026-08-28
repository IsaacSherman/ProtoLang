using ProtoLang.Backend;
using ProtoLang.Backend.Cpp;
using ProtoLang.Backend.CSharp;
using ProtoLang.Diagnostics;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Field presence and the guard rule (spec 8.4, 13.1).
/// </summary>
/// <remarks>
/// The conformance corpus covers what a guarded read <em>does</em> in both backends. These cover
/// what the compiler refuses, which a vector cannot: a vector has to compile.
/// </remarks>
public class PresenceTests
{
    private const string Prelude = "import proto \"fixtures.proto\";\n";
    private const string ConformancePrelude = "import proto \"conformance.proto\";\n";

    private static readonly string ConformanceProtoDirectory =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance", "protos");

    private static CompilationResult Compile(string source)
        => Compilation.Compile(TestPaths.WriteTempScript(source), [TestPaths.FixtureProtoDirectory]);

    private static CompilationResult CompileBody(string body)
        => Compile(Prelude + "extend Outer {\n" + body + "\n}");

    private static CompilationResult CompileConformanceBody(string body)
        => Compilation.Compile(
            TestPaths.WriteTempScript(ConformancePrelude + "extend PresenceCase {\n" + body + "\n}"),
            [ConformanceProtoDirectory]);

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    private static void AssertCode(CompilationResult result, string code)
        => Assert.Contains(result.Diagnostics, d => d.Code == code);

    // ---------------------------------------------------------------- PL0078: the guard rule

    /// <summary>
    /// The case issue #33 reported: one source, a NullReferenceException in C# and a zero in C++.
    /// </summary>
    [Fact]
    public void ReadingThroughAnUnguardedMessageFieldIsRejected()
    {
        var result = CompileBody("fn f() -> Deep { return inner.deep; }");

        AssertCode(result, "PL0078");
    }

    /// <summary>
    /// The rule is about the value, not about reading through it. Each of these launders exactly
    /// the same null, so catching only the first would leave the divergence one keystroke away.
    /// </summary>
    [Theory]
    [InlineData("fn f() -> Inner { var m: Inner = inner; return m; }")]
    [InlineData("fn f() -> Inner { return inner; }")]
    [InlineData("fn g(m: Inner) -> int64 { return 1; }\nfn f() -> int64 { return g(inner); }")]
    public void UsingAnUnguardedMessageFieldAsAValueIsRejected(string body)
    {
        var result = CompileBody(body);

        AssertCode(result, "PL0078");
    }

    /// <summary>
    /// A diagnostic nobody can act on is worse than none, so the message says what to do instead:
    /// nothing reached through an unnamed value can be guarded, and binding it to a local gives it
    /// the name a guard needs.
    /// </summary>
    [Fact]
    public void AMessageFieldReachedThroughAMethodResultSaysHowToGuardIt()
    {
        var result = CompileBody(
            """
            fn identity(m: Outer) -> Outer {
                return m;
            }

            fn f(o: Outer) -> Deep {
                // The parameter is present by construction and so is the call's result, but the
                // message field reached through the call has nothing a guard could name.
                return identity(o).inner.deep;
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "PL0078");
        Assert.Contains("no name", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("local", diagnostic.Help!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the guard shapes

    /// <summary>
    /// Each shape a guard can take. The early-return form is the one that would have been most
    /// painful to live without, and it works because the analysis reuses the same reachability
    /// predicate the all-paths-return check already needed.
    /// </summary>
    [Theory]
    [InlineData("fn f() -> Deep { if has inner { return inner.deep; } return Deep.DEEP_NONE; }")]
    [InlineData("fn f() -> Deep { if not has inner { return Deep.DEEP_NONE; } return inner.deep; }")]
    [InlineData("fn f() -> Deep { if not has inner { return Deep.DEEP_NONE; } else { return inner.deep; } }")]
    [InlineData("fn f() -> bool { return has inner and inner.deep == Deep.DEEP_NONE; }")]
    public void AGuardedReadIsAccepted(string body)
    {
        AssertOk(CompileBody(body));
    }

    /// <summary>
    /// 'or' proves its operands on the false side, which is what makes the two-field early return
    /// read the way an author would write it.
    /// </summary>
    [Fact]
    public void AnOrGuardProvesBothOperandsAfterAnEarlyReturn()
    {
        AssertOk(CompileBody(
            """
            fn f() -> Deep {
                if not has inner or not has other_inner {
                    return Deep.DEEP_NONE;
                }

                if inner.deep == other_inner.deep {
                    return inner.deep;
                }

                return other_inner.deep;
            }
            """));
    }

    /// <summary>
    /// A loop body only runs when its condition is true, so a presence test in that condition is
    /// just as real inside the body as it is inside an <c>if</c> branch.
    /// </summary>
    [Fact]
    public void AWhileConditionProvesPresenceInsideItsBody()
    {
        AssertOk(CompileBody(
            """
            fn f() -> Deep {
                while has inner {
                    return inner.deep;
                }

                return Deep.DEEP_NONE;
            }
            """));
    }

    /// <summary>
    /// An <c>or</c> condition proves presence only on its false side. If it is true, either operand
    /// may have carried the branch, so neither message field is safe to read.
    /// </summary>
    [Fact]
    public void AnOrConditionDoesNotProveEitherFieldOnItsTrueSide()
    {
        var result = CompileBody(
            """
            fn f() -> Deep {
                if has inner or has other_inner {
                    return inner.deep;
                }

                return Deep.DEEP_NONE;
            }
            """);

        AssertCode(result, "PL0078");
    }

    /// <summary>
    /// A guard inside a loop can prove facts for the rest of that iteration, but it cannot prove
    /// anything after the loop: the loop might not run, or it might exit through the guarded break.
    /// </summary>
    [Fact]
    public void ALoopLocalGuardDoesNotEscapeTheLoop()
    {
        var result = CompileBody(
            """
            fn f() -> Deep {
                while count < 3 {
                    if not has inner {
                        break;
                    }

                    return inner.deep;
                }

                return inner.deep;
            }
            """);

        AssertCode(result, "PL0078");
    }

    /// <summary>
    /// Nested message paths need both links proved. The second guard depends on the first one,
    /// because asking about <c>inner.stamp</c> reads <c>inner</c> to reach the field.
    /// </summary>
    [Fact]
    public void AGuardClauseCanProveANestedMessagePath()
    {
        AssertOk(CompileConformanceBody(
            """
            fn f() -> int64 {
                if not has inner or not has inner.stamp {
                    return 0;
                }

                return inner.stamp.seconds;
            }
            """));
    }

    /// <summary>
    /// A guard inside one branch says nothing about the other, or about what follows a branch that
    /// can fall through.
    /// </summary>
    [Fact]
    public void AGuardDoesNotEscapeItsBranch()
    {
        var result = CompileBody(
            """
            fn f() -> Deep {
                if has inner {
                    var ignored: Deep = inner.deep;
                }

                return inner.deep;
            }
            """);

        AssertCode(result, "PL0078");
    }

    /// <summary>
    /// Presence facts are monotone within a method -- nothing can unset a field -- so a fact
    /// established before a loop still holds inside it, with no fixpoint needed to say so.
    /// </summary>
    [Fact]
    public void AGuardHoldsInsideALoopBody()
    {
        AssertOk(CompileBody(
            """
            fn f() -> int64 {
                if not has inner {
                    return 0;
                }

                var total: int64 = 0;
                var i: int64 = 0;

                while i < 3 {
                    if inner.deep == Deep.DEEP_NONE {
                        total = total + 1;
                    }

                    i = i + 1;
                }

                return total;
            }
            """));
    }

    // ---------------------------------------------------------------- PL0079: no presence to test

    /// <summary>
    /// Fields that cannot answer the question. A proto3 scalar without <c>optional</c> has no
    /// presence on the wire at all, and a repeated field never has any.
    /// </summary>
    [Theory]
    [InlineData("fn f() -> bool { return has count; }")]
    [InlineData("fn f() -> bool { return has label; }")]
    [InlineData("fn f() -> bool { return has status; }")]
    [InlineData("fn f() -> bool { return has nested_values; }")]
    public void PresenceOnAFieldThatHasNoneIsRejected(string body)
    {
        AssertCode(CompileBody(body), "PL0079");
    }

    /// <summary>
    /// The same schema, one keyword apart. This is the pair that makes the diagnostic above worth
    /// having rather than merely correct.
    /// </summary>
    [Fact]
    public void PresenceOnAnOptionalScalarIsAccepted()
    {
        AssertOk(CompileBody("fn f() -> bool { return has optional_count; }"));
    }

    /// <summary>Reading one needs no guard: an unset scalar has always read as its zero in both.</summary>
    [Fact]
    public void ReadingAnOptionalScalarNeedsNoGuard()
    {
        AssertOk(CompileBody("fn f() -> int64 { return optional_count; }"));
    }

    // ---------------------------------------------------------------- PL0080: not a field

    /// <summary>
    /// Only a field can be unset. A local, a parameter, and a method result always hold a value,
    /// so there is no question to ask about them.
    /// </summary>
    [Theory]
    [InlineData("fn f() -> bool { var x: int64 = 1; return has x; }")]
    [InlineData("fn f() -> bool { return has 3; }")]
    [InlineData("fn f(p: int64) -> bool { return has p; }")]
    public void PresenceOnSomethingThatIsNotAFieldIsRejected(string body)
    {
        AssertCode(CompileBody(body), "PL0080");
    }

    // ---------------------------------------------------------------- emission

    private static string Emit(IBackend backend, string body, string suffix)
    {
        var path = TestPaths.WriteTempScript(Prelude + "extend Outer {\n" + body + "\n}");
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = backend.Emit(result.Module!, new BackendOptions(Path.GetFileName(path)), diagnostics);
        Assert.Empty(diagnostics);

        return files.Single(f => f.RelativePath.EndsWith(suffix, StringComparison.Ordinal)).Contents;
    }

    /// <summary>
    /// protoc's C# generator emits <c>HasX</c> only for a field in a oneof, real or the synthetic
    /// one <c>optional</c> creates. A message field expresses presence by being nullable instead,
    /// so the two need different spellings and emitting <c>HasInner</c> would not compile.
    /// </summary>
    [Fact]
    public void CSharpTestsAMessageFieldForNullAndAnOptionalScalarWithHas()
    {
        var source = Emit(
            new CSharpBackend(),
            """
            fn message_field() -> bool { return has inner; }
            fn optional_scalar() -> bool { return has optional_count; }
            """,
            "test.g.cs");

        Assert.Contains("return (self.Inner != null);", source, StringComparison.Ordinal);
        Assert.Contains("return self.HasOptionalCount;", source, StringComparison.Ordinal);
    }

    /// <summary>C++ is uniform: protoc emits has_x() for every field that has presence.</summary>
    [Fact]
    public void CppTestsBothKindsWithHasAccessors()
    {
        var source = Emit(
            new CppBackend(),
            """
            fn message_field() -> bool { return has inner; }
            fn optional_scalar() -> bool { return has optional_count; }
            """,
            "test.pl.h");

        Assert.Contains("return self.has_inner();", source, StringComparison.Ordinal);
        Assert.Contains("return self.has_optional_count();", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard is a compile-time requirement, so the read it guards is emitted exactly as it was
    /// before the rule existed. That is the whole reason this shape of answer costs no runtime.
    /// </summary>
    [Fact]
    public void AGuardedReadEmitsThePlainAccessorChain()
    {
        const string Body =
            """
            fn f() -> Deep {
                if not has inner {
                    return Deep.DEEP_NONE;
                }

                return inner.deep;
            }
            """;

        Assert.Contains("return self.Inner.Deep;", Emit(new CSharpBackend(), Body, "test.g.cs"), StringComparison.Ordinal);
        Assert.Contains("return self.inner().deep();", Emit(new CppBackend(), Body, "test.pl.h"), StringComparison.Ordinal);
    }
}
