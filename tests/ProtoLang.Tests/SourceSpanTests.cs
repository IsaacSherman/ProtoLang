using ProtoLang.Diagnostics;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The span contract every consumer inherits: half-open ranges, two ends that stay consistent with
/// each other, and a <see cref="SourceSpan.None"/> that can never be mistaken for a place in a file.
/// </summary>
/// <remarks>
/// Decided here rather than re-derived downstream. A language server that had to work out for itself
/// whether the end position was inclusive would get it right on the first construct and wrong on the
/// next one.
/// </remarks>
public class SourceSpanTests
{
    private const string Name = "test.protolang";

    // ---------------------------------------------------------------- the range itself

    [Fact]
    public void RangesAreHalfOpen()
    {
        // 'foo' at line 1, column 1.
        var span = SourceSpan.SingleLine(Name, 0, 1, 1, 3);

        Assert.Equal(new SourcePosition(0, 1, 1), span.Start);
        Assert.Equal(new SourcePosition(3, 1, 4), span.End);
        Assert.Equal(3, span.Length);
        Assert.False(span.IsEmpty);
    }

    [Fact]
    public void AnEmptyRangeIsDistinguishableFromAOneCharacterRange()
    {
        var insertionPoint = SourceSpan.SingleLine(Name, 7, 2, 4, 0);
        var oneCharacter = SourceSpan.SingleLine(Name, 7, 2, 4, 1);

        Assert.True(insertionPoint.IsEmpty);
        Assert.Equal(0, insertionPoint.Length);
        Assert.Equal(insertionPoint.Start, insertionPoint.End);

        Assert.False(oneCharacter.IsEmpty);
        Assert.Equal(1, oneCharacter.Length);
        Assert.NotEqual(insertionPoint, oneCharacter);
    }

    [Fact]
    public void TheStartIsWhatTheOldSingleLineReadersAskedFor()
    {
        var span = SourceSpan.SingleLine(Name, 20, 3, 7, 4);

        Assert.Equal(3, span.Line);
        Assert.Equal(7, span.Column);
        Assert.Equal(4, span.Length);
    }

    // ---------------------------------------------------------------- combining

    [Fact]
    public void UnionCoversARangeThatCrossesLines()
    {
        // 'extend' on line 1 through the '}' that closes it on line 4.
        var start = SourceSpan.SingleLine(Name, 0, 1, 1, 6);
        var end = SourceSpan.SingleLine(Name, 40, 4, 1, 1);

        var union = SourceSpan.Union(start, end);

        Assert.Equal(new SourcePosition(0, 1, 1), union.Start);
        Assert.Equal(new SourcePosition(41, 4, 2), union.End);
        Assert.Equal(41, union.Length);
    }

    [Fact]
    public void UnionDoesNotCareWhichOperandArrivesFirst()
    {
        var first = SourceSpan.SingleLine(Name, 0, 1, 1, 6);
        var second = SourceSpan.SingleLine(Name, 40, 4, 1, 1);

        Assert.Equal(SourceSpan.Union(first, second), SourceSpan.Union(second, first));
    }

    [Fact]
    public void UnionOfNestedRangesIsTheOuterOne()
    {
        var outer = new SourceSpan(Name, new SourcePosition(0, 1, 1), new SourcePosition(60, 5, 2));
        var inner = SourceSpan.SingleLine(Name, 20, 3, 5, 4);

        Assert.Equal(outer, SourceSpan.Union(outer, inner));
        Assert.Equal(outer, SourceSpan.Union(inner, outer));
    }

    [Fact]
    public void UnionIgnoresNoneOperands()
    {
        var real = SourceSpan.SingleLine(Name, 4, 1, 5, 2);

        Assert.Equal(real, SourceSpan.Union(real, SourceSpan.None));
        Assert.Equal(real, SourceSpan.Union(SourceSpan.None, real));
        Assert.Equal(SourceSpan.None, SourceSpan.Union(SourceSpan.None, SourceSpan.None));
        Assert.Equal(SourceSpan.None, SourceSpan.Union(Name, SourceSpan.None, SourceSpan.None));
    }

    [Fact]
    public void UnionStampsTheFileItIsGiven()
    {
        var first = SourceSpan.SingleLine("a.protolang", 0, 1, 1, 1);
        var second = SourceSpan.SingleLine("b.protolang", 4, 1, 5, 1);

        Assert.Equal(Name, SourceSpan.Union(Name, first, second).File);
        Assert.Equal("a.protolang", SourceSpan.Union(first, second).File);
    }

    // ---------------------------------------------------------------- nowhere

    [Fact]
    public void NoneStaysOutOfBandForAOneBasedScheme()
    {
        Assert.True(SourceSpan.None.IsNone);
        Assert.True(SourceSpan.None.Start.IsNone);
        Assert.True(SourceSpan.None.End.IsNone);
        Assert.Equal(0, SourceSpan.None.Line);
        Assert.Equal(0, SourceSpan.None.Column);

        Assert.False(SourceSpan.SingleLine(Name, 0, 1, 1, 1).IsNone);
    }

    [Fact]
    public void RenderingIsTheSpec26Template()
    {
        Assert.Equal("test.protolang:3:7", SourceSpan.SingleLine(Name, 20, 3, 7, 4).ToString());
        Assert.Equal("<none>:0:0", SourceSpan.None.ToString());
    }

    // ---------------------------------------------------------------- line map

    [Fact]
    public void TheLineMapAgreesWithItselfInBothDirections()
    {
        const string Text = "extend M {\r\n    fn f() -> int64 {\n        return 1;\n    }\n}";
        var lines = new LineMap(Text);

        for (var offset = 0; offset <= Text.Length; offset++)
        {
            var position = lines.PositionOf(offset);
            Assert.Equal(offset, lines.OffsetOf(position.Line, position.Column));
        }
    }

    [Fact]
    public void ACarriageReturnBelongsToTheLineItEnds()
    {
        // The lexer ends a line at '\n' and treats a lone '\r' as ordinary whitespace. A line map
        // that disagreed with the lexer would be worse than none at all.
        var lines = new LineMap("ab\r\ncd");

        Assert.Equal(2, lines.LineCount);
        Assert.Equal(new SourcePosition(2, 1, 3), lines.PositionOf(2));
        Assert.Equal(new SourcePosition(4, 2, 1), lines.PositionOf(4));
    }

    [Fact]
    public void ATextWithNoNewlineIsOneLine()
    {
        Assert.Equal(1, new LineMap("abc").LineCount);
        Assert.Equal(1, new LineMap(string.Empty).LineCount);
        Assert.Equal(new SourcePosition(0, 1, 1), new LineMap(string.Empty).PositionOf(0));
    }

    [Fact]
    public void TheLineMapClampsRatherThanThrowing()
    {
        var lines = new LineMap("abc\ndef");

        Assert.Equal(new SourcePosition(0, 1, 1), lines.PositionOf(-5));
        Assert.Equal(7, lines.PositionOf(99).Offset);
        Assert.Equal(0, lines.OffsetOf(0, 0));
        Assert.Equal(7, lines.OffsetOf(99, 99));
    }

    // ---------------------------------------------------------------- what it is all for

    /// <summary>
    /// The end-to-end shape of the defect this replaced: a diagnostic reported against a multi-line
    /// declaration used to carry the closing token's length, so an editor would have squiggled one
    /// arbitrary character. It now covers the declaration.
    /// </summary>
    [Fact]
    public void ADiagnosticAgainstAMultiLineDeclarationCoversAllOfIt()
    {
        const string Source =
            """
            import proto "fixtures.proto";
            extend Outer {
                fn f() -> int64 {
                    return count;
                }

                fn f() -> int64 {
                    return count;
                }
            }
            """;

        var path = TestPaths.WriteTempScript(Source);
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory]);

        var span = Assert.Single(result.Diagnostics, d => d.Code == "PL0022").Span;
        var reported = Source.Substring(span.Start.Offset, span.Length);

        Assert.StartsWith("fn f()", reported, StringComparison.Ordinal);
        Assert.EndsWith("}", reported, StringComparison.Ordinal);
        Assert.Contains("return count;", reported, StringComparison.Ordinal);
        Assert.True(span.End.Line > span.Start.Line, "the duplicate spans more than one line");
    }
}
