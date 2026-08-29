namespace ProtoLang.Diagnostics;

/// <summary>
/// Where the lines of a text begin, so that a point in it can be named in either of the two
/// coordinate systems a <see cref="SourcePosition"/> carries.
/// </summary>
/// <remarks>
/// <para>
/// The lexer needs none of this: it tracks the line and the line start as it goes, and its offset
/// is the index it is already reading from. This is for the places that are handed line and column
/// by something else -- an XML parser, an editor request -- and have to say where that is in
/// absolute terms.
/// </para>
/// <para>
/// A line ends at <c>\n</c> and nothing else. A lone <c>\r</c> is an ordinary character in the
/// middle of a line, and in <c>\r\n</c> the <c>\r</c> is the last character of the line it ends.
/// That is not a preference; it is what the lexer does, and a line map that disagreed with the
/// lexer would be worse than no line map at all.
/// </para>
/// </remarks>
public sealed class LineMap
{
    private readonly int _length;
    private readonly int[] _lineStarts;

    public LineMap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _length = text.Length;

        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    /// <summary>How many lines the text has. A text with no newline in it has one.</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>The offset a 1-based line and column name.</summary>
    /// <remarks>
    /// Clamps rather than throwing. Callers pass positions that came from somewhere else -- a
    /// parser reporting a syntax error, an editor describing a buffer it may have edited since --
    /// and a location just past the end of a line is a thing those legitimately produce.
    /// </remarks>
    public int OffsetOf(int line, int column)
    {
        var index = Math.Clamp(line, 1, _lineStarts.Length) - 1;
        var start = _lineStarts[index];
        var end = index + 1 < _lineStarts.Length ? _lineStarts[index + 1] : _length;

        return Math.Clamp(start + Math.Max(column, 1) - 1, start, end);
    }

    /// <summary>Which line and column a 0-based offset falls on.</summary>
    /// <inheritdoc cref="OffsetOf" path="/remarks"/>
    public SourcePosition PositionOf(int offset)
    {
        var clamped = Math.Clamp(offset, 0, _length);

        var index = Array.BinarySearch(_lineStarts, clamped);
        if (index < 0)
        {
            // The insertion point is the line after the one the offset falls on.
            index = ~index - 1;
        }

        return new SourcePosition(clamped, index + 1, clamped - _lineStarts[index] + 1);
    }
}
