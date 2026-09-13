using ProtoLang.Diagnostics;

namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>
/// The one place a compiler position becomes an editor position, and back.
/// </summary>
/// <remarks>
/// <para>
/// Two coordinate systems measuring the same characters. The compiler counts lines and columns
/// from one and offsets from zero; LSP counts lines and characters from zero and has no offsets
/// at all. So every answer this server sends is a subtraction away from something the compiler
/// said, and every question it receives is an addition away from something the compiler can be
/// asked -- which is exactly the kind of arithmetic that is right in four places and wrong in the
/// fifth.
/// </para>
/// <para>
/// <b>Both directions here, because they are one rule.</b> A request arrives as a line and a
/// character and has to become an offset before anything in the semantic model will look at it;
/// the answer comes back as a <see cref="SourceSpan"/> and has to become a range. A file that
/// held only one of the two would leave the other to be rediscovered by whoever needed it next.
/// </para>
/// <para>
/// <b>Nothing here re-measures the text.</b> A <see cref="SourceSpan"/> already carries line and
/// column at both ends, so converting one is arithmetic; an offset needs the
/// <see cref="LineMap"/> the document already holds, which is the same converter the rest of the
/// compiler uses. Counting newlines here would be a second opinion about where a line begins,
/// and the first thing a second opinion gets wrong is a lone carriage return.
/// </para>
/// <para>
/// Characters are UTF-16 code units, which is LSP's default, the encoding this server negotiates,
/// and what <see cref="SourcePosition"/> already measures in. A client that accepts neither is
/// warned at <c>initialize</c> rather than silently converted for.
/// </para>
/// </remarks>
public static class EditorPositions
{
    /// <summary>The range an editor draws for a compiler span.</summary>
    /// <remarks>
    /// A span that is nowhere must never go through the subtraction: line 0 minus one is line -1,
    /// which is not a position any client can be given, so it collapses to the start of the
    /// document -- where spec 26.1 requires a locationless diagnostic to be published anyway.
    /// Everything else is clamped at zero as well, because a span this server did not produce is a
    /// span this server does not get to assume about.
    /// </remarks>
    public static Range RangeOf(SourceSpan span)
        => span.IsNone ? DocumentStart : new Range(PositionOf(span.Start), PositionOf(span.End));

    /// <inheritdoc cref="RangeOf"/>
    public static Position PositionOf(SourcePosition position)
        => PositionOf(position.Line, position.Column);

    /// <summary>The editor position a 1-based line and column name.</summary>
    /// <remarks>
    /// The same subtraction, for a caller whose coordinates never went through a
    /// <see cref="SourcePosition"/> at all -- protoc reports a line and a column and no offset, and
    /// fabricating an offset to reuse the overload above would be inventing a fact to satisfy a
    /// signature.
    /// </remarks>
    public static Position PositionOf(int line, int column)
        => new(Math.Max(line - 1, 0), Math.Max(column - 1, 0));

    /// <summary>Where in the document one offset falls.</summary>
    public static Position PositionAt(LineMap lines, int offset)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return PositionOf(lines.PositionOf(offset));
    }

    /// <summary>The range between two offsets, which is how an edit says what it replaces.</summary>
    public static Range Between(LineMap lines, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return new Range(PositionAt(lines, start), PositionAt(lines, end));
    }

    /// <summary>The offset an editor position names, which is what every query is asked in.</summary>
    public static int OffsetOf(LineMap lines, Position position)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(position);

        return lines.OffsetOf(position.Line + 1, position.Character + 1);
    }

    /// <summary>The empty range at the very start of a document.</summary>
    /// <remarks>
    /// Where a diagnostic with no location is published: an unusable include path, a setting being
    /// ignored, a configuration file that was refused. None of them is anywhere in the source and
    /// all of them have to be seen, and the message already names where the value really came from
    /// -- so a placeholder range does not make the diagnostic ambiguous.
    /// </remarks>
    public static Range DocumentStart { get; } = new(new Position(0, 0), new Position(0, 0));
}
