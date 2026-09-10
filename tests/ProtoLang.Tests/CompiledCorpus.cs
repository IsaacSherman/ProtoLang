using ProtoLang.Tests.Conformance;

namespace ProtoLang.Tests;

/// <summary>One compiled source, kept beside the text it came from.</summary>
internal sealed record CorpusSource(string Name, string Text, CompilationResult Result);

/// <summary>
/// Every ProtoLang source the repository maintains, compiled once, for the tests that assert a
/// property of all of them.
/// </summary>
/// <remarks>
/// <para>
/// The conformance vectors are the corpus worth sweeping: between them they use every construct the
/// language has, they are kept compiling by a test of their own, and they are edited when the
/// language grows. A hand-written fixture claiming the same coverage would be a second corpus to
/// remember to extend, and the first thing it would fall behind on is the construct that was just
/// added.
/// </para>
/// <para>
/// Compiled once for the whole assembly because each source shells out to protoc. The broken buffer
/// is here for the same reason the others are: error recovery is what puts nodes in surprising
/// places, so a sweep that only ever sees well-formed files is a sweep over the easy half.
/// </para>
/// </remarks>
internal static class CompiledCorpus
{
    /// <summary>
    /// A file with several distinct mistakes in it: a member name never written, a parameter list
    /// that was abandoned, a call to a method that is not there, and a call through something that
    /// could never be one -- the last two being the shapes that put an
    /// <see cref="Ir.IrUncallableInvocation"/> in the tree with each of its two halves.
    /// </summary>
    public const string BrokenText =
        """
        import proto "invoice.proto";
        extend Invoice {
            fn f() -> int64 {
                for line in items {
                    return line.
                }

                return items.
            }

            fn g( -> int64 { return nosuchmethod(1, 2); }

            fn h() -> int64 { return 1(2); }
        }
        """;

    /// <summary>
    /// A buffer that simply stops, mid-construct, the way every buffer does between one keystroke
    /// and the next: a loop that closed, inside a method that did not, inside an <c>extend</c> that
    /// did not either.
    /// </summary>
    /// <remarks>
    /// <see cref="BrokenText"/> is broken and <em>balanced</em> -- every brace in it has its pair,
    /// and its mistakes are ones the binder finds. Nothing in the corpus was unfinished, so no sweep
    /// over it ever met a construct the parser could not close, which is the state a file spends
    /// most of its life in while someone is typing into it. The distinction is not academic: a query
    /// answering at the end of this file has to tell the loop, which a brace closed, from the method,
    /// which nothing did, and they end at the same offset.
    /// </remarks>
    public const string UnclosedText =
        """
        import proto "invoice.proto";

        extend Invoice {
            fn totals() -> int64 {
                var total: int64 = 0;

                for item in items {
                    total = total + item.quantity;
                }
        """;

    public static CorpusSource SimpleScript { get; } = new(
        "simpleScript",
        File.ReadAllText(TestPaths.SimpleScript),
        Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]));

    public static CorpusSource Broken { get; } = new(
        "broken",
        BrokenText,
        Compilation.Compile(TestPaths.WriteTempScript(BrokenText), [TestPaths.ExampleProtoDirectory]));

    public static CorpusSource Unclosed { get; } = new(
        "unclosed",
        UnclosedText,
        Compilation.Compile(TestPaths.WriteTempScript(UnclosedText), [TestPaths.ExampleProtoDirectory]));

    /// <summary>
    /// The shapes a dotted name can take, which the rest of the corpus does not happen to contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every source here was written to exercise the language, and between them they do. None was
    /// written to exercise a <em>name</em>, so the corpus reached this far without one qualified type
    /// reference, one package-qualified <c>extend</c> receiver, or one name spread over two lines --
    /// and each of those was a defect found by hand after the sweep had passed over the file that
    /// should have contained it. A fixture written beside the test that found the defect protects
    /// that test; a source in the corpus protects every sweep there will ever be.
    /// </para>
    /// <para>
    /// It does not compile, and that is deliberate in one place only: an enum-valued field with a dot
    /// after it. There is no valid way to write that -- constants are reached through the enum's name
    /// and never through a value of it -- so the shape exists solely in a buffer someone is midway
    /// through, which is the state completion is asked about. <see cref="BrokenText"/> is here on the
    /// same argument.
    /// </para>
    /// <para>
    /// Compiled against the fixture schemas as well as the example ones, because those are where a
    /// package, a nested type and an enum-valued field all exist together.
    /// </para>
    /// </remarks>
    public const string QualifiedText =
        """
        import proto "fixtures.proto";

        extend protolang.tests.Outer {
            fn qualified(other: protolang.
                tests.Outer) -> int64 {
                var level: protolang.tests.TopLevelStatus = other.status;
                return other.count;
            }

            fn deep(inner: protolang.tests.Outer.Inner) -> protolang.tests.Outer.Inner.Deep {
                return inner.deep;
            }

            fn plain() -> int64 {
                return count;
            }

            fn reached() -> int64 {
                return status.
            }
        }

        test protolang.tests.Outer.plain "a package-qualified test target" {
            receiver {
                count = 2;
            }
            expect return 2;
        }
        """;

    /// <inheritdoc cref="QualifiedText"/>
    public static CorpusSource Qualified { get; } = new(
        "qualified",
        QualifiedText,
        Compilation.Compile(
            TestPaths.WriteTempScript(QualifiedText),
            [TestPaths.ExampleProtoDirectory, TestPaths.FixtureProtoDirectory]));

    /// <summary>
    /// The example, the broken buffer, the unfinished one, the qualified names, and every
    /// conformance vector.
    /// </summary>
    public static IReadOnlyList<CorpusSource> All { get; } =
    [
        SimpleScript,
        Broken,
        Unclosed,
        Qualified,
        .. ConformanceVectors.All.Select(vector => new CorpusSource(
            vector.Name,
            File.ReadAllText(vector.SourcePath),
            ConformanceVectors.Compile(vector))),
    ];
}
