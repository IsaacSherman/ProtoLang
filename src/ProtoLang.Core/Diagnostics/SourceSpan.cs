using System.Diagnostics;

namespace ProtoLang.Diagnostics;

/// <summary>
/// One point in a source text, described in both coordinate systems at once: the absolute offset
/// edits and containment tests are expressed against, and the line and column a person reads.
/// </summary>
/// <remarks>
/// <para>
/// Carrying both is deliberate redundancy, and each half pays for itself. Offsets make containment
/// a single integer comparison, and make sorting and overlap tests trivial. Line and column keep
/// <c>file:line:column</c> rendering free and convert directly to editor positions. Deriving either
/// from the other needs the source text and a line index; carrying both means no consumer has to be
/// handed the text just to say where something is.
/// </para>
/// <para>
/// <b>Origin.</b> <see cref="Line"/> and <see cref="Column"/> are 1-based. <see cref="Offset"/> is
/// 0-based. LSP is 0-based on all three, so the conversion is: subtract one from the line, subtract
/// one from the column, pass the offset through. It is stated here so that it is written once and
/// trusted rather than re-derived at each boundary.
/// </para>
/// <para>
/// <b>Units.</b> <see cref="Offset"/> and <see cref="Column"/> both count UTF-16 code units,
/// because the lexer indexes into a .NET string and derives the column from that index. That is
/// exactly what LSP's default position encoding wants. The agreement is tested rather than assumed:
/// a single astral-plane character in a string literal or a comment would otherwise silently shift
/// every squiggle after it on the line, and nothing would report it.
/// </para>
/// </remarks>
public readonly record struct SourcePosition(int Offset, int Line, int Column)
{
    /// <summary>The position of something that is not anywhere in a source text.</summary>
    /// <remarks>
    /// Line 0, which is out of band for a 1-based scheme and so can never collide with a real
    /// position. Ask <see cref="IsNone"/>; never hand one to an editor as a coordinate.
    /// </remarks>
    public static readonly SourcePosition None = new(0, 0, 0);

    /// <summary>Whether this is <see cref="None"/> rather than somewhere in a file.</summary>
    public bool IsNone => Line == 0;
}

/// <summary>
/// A range in a ProtoLang source file. Spec 22.2 requires the IR to preserve source locations, so
/// this travels all the way from the lexer into backend code generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ranges are half-open: <see cref="Start"/> is inclusive and <see cref="End"/> is exclusive.</b>
/// This is what LSP expects, and it is the convention that makes an empty range -- an insertion
/// point, a zero-width diagnostic at a missing token -- representable without a special case. An
/// empty range stays distinguishable from a one-character range: the first has <see cref="Length"/>
/// 0, the second has 1. Every consumer inherits this rather than deciding it again.
/// </para>
/// <para>
/// The two ends are consistent with each other by construction: <see cref="SingleLine"/> and
/// <see cref="Union(string, SourceSpan, SourceSpan)"/> compute one from the other, and the
/// constructor asserts the ordering in debug builds. See <see cref="SourcePosition"/> for the
/// origin and the units both ends are measured in.
/// </para>
/// <para>
/// This type sits on every AST and IR node, so it stays a value type and stays small: one reference
/// and six integers.
/// </para>
/// </remarks>
public readonly record struct SourceSpan
{
    /// <summary>A range that is not anywhere in a source file.</summary>
    /// <remarks>
    /// Line 0 and column 0 at both ends, out of band for the 1-based scheme, so a server can tell
    /// it apart from a real location and must never map it to an editor range. Ask
    /// <see cref="IsNone"/>.
    /// </remarks>
    public static readonly SourceSpan None = new("<none>", SourcePosition.None, SourcePosition.None);

    public SourceSpan(string file, SourcePosition start, SourcePosition end)
    {
        Debug.Assert(end.Offset >= start.Offset, "a span cannot end before it starts");
        Debug.Assert(end.Line >= start.Line, "a span cannot end on a line above the one it starts on");
        Debug.Assert(
            end.Line != start.Line || end.Column >= start.Column,
            "a span on one line cannot end left of where it starts");

        File = file;
        Start = start;
        End = end;
    }

    /// <summary>What the file calls itself; see <see cref="SourceIdentity.Name"/>.</summary>
    public string File { get; }

    /// <summary>The first position in the range.</summary>
    public SourcePosition Start { get; }

    /// <summary>The first position after the range.</summary>
    public SourcePosition End { get; }

    /// <summary>Where the range starts, for the many readers that only ever wanted that.</summary>
    public int Line => Start.Line;

    /// <inheritdoc cref="Line"/>
    public int Column => Start.Column;

    /// <summary>How many UTF-16 code units the range covers.</summary>
    public int Length => End.Offset - Start.Offset;

    /// <summary>Whether the range covers nothing -- an insertion point rather than any text.</summary>
    public bool IsEmpty => End.Offset == Start.Offset;

    /// <summary>Whether this is <see cref="None"/> rather than a real location.</summary>
    public bool IsNone => Start.IsNone;

    /// <summary>
    /// A range that begins and ends on one line, which is the shape of every token the lexer
    /// produces: identifiers, numbers and operators stop at the characters that end them, string
    /// literals stop at a newline, and block comments are trivia rather than tokens.
    /// </summary>
    public static SourceSpan SingleLine(string file, int offset, int line, int column, int length)
    {
        Debug.Assert(length >= 0, "a span cannot have negative length");

        return new SourceSpan(
            file,
            new SourcePosition(offset, line, column),
            new SourcePosition(offset + length, line, column + length));
    }

    /// <summary>The smallest range covering both, however many lines they cross.</summary>
    /// <remarks>
    /// Takes the earlier start and the later end by offset, so the result is the same whichever
    /// order the two arrive in -- which matters because the parser's error recovery can hand this
    /// an "end" that precedes its "start". A <see cref="None"/> operand contributes nothing; two of
    /// them give <see cref="None"/>.
    /// </remarks>
    public static SourceSpan Union(SourceSpan first, SourceSpan second)
        => first.IsNone ? second : Union(first.File, first, second);

    /// <inheritdoc cref="Union(SourceSpan, SourceSpan)"/>
    /// <param name="file">
    /// The file to stamp on the result, for callers that are the authority on it -- the parser
    /// knows what it is parsing even when it combines a synthesized span with a real one.
    /// </param>
    public static SourceSpan Union(string file, SourceSpan first, SourceSpan second)
    {
        if (first.IsNone && second.IsNone)
        {
            return None;
        }

        if (first.IsNone)
        {
            return new SourceSpan(file, second.Start, second.End);
        }

        if (second.IsNone)
        {
            return new SourceSpan(file, first.Start, first.End);
        }

        return new SourceSpan(
            file,
            first.Start.Offset <= second.Start.Offset ? first.Start : second.Start,
            first.End.Offset >= second.End.Offset ? first.End : second.End);
    }

    /// <summary>Formats as <c>file.protolang:line:column</c> per the spec 26 template.</summary>
    public override string ToString() => $"{File}:{Line}:{Column}";
}
