using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.Syntax;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>Turns source text into the classification a client paints with.</summary>
/// <remarks>
/// <para>
/// Lexes and nothing more. No parse, no descriptors, no protoc -- which is what lets this answer for a
/// file that does not compile, and answer fast enough to run on every request rather than on a
/// schedule. The diagnostics the lexer produces along the way are discarded here: they reach the user
/// through the compile that publishes them, and reporting them twice from two paths would double
/// every squiggle.
/// </para>
/// <para>
/// The one shape the encoding will not carry is a token that crosses a line, and ProtoLang has
/// exactly one of those: a block comment. It arrives here as a single <see cref="Comment"/> with a
/// multi-line span and leaves as one token per line it touches.
/// </para>
/// </remarks>
public static class SemanticTokenEncoder
{
    /// <summary>Classifies <paramref name="text"/>, ready to send.</summary>
    /// <param name="name">What the text calls itself, for the spans the lexer stamps.</param>
    public static SemanticTokens Encode(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lexer = new Lexer(text, name, new DiagnosticBag());
        var tokens = lexer.Tokenize();

        var classified = new List<Classified>(tokens.Count + lexer.Comments.Count);

        foreach (var token in tokens)
        {
            if (SemanticTokenLegend.IndexOf(token.Kind) is { } type && token.Span.Length > 0)
            {
                classified.Add(Classified.From(token.Span, type));
            }
        }

        foreach (var comment in lexer.Comments)
        {
            AddLineByLine(comment.Span, text, classified);
        }

        classified.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        return new SemanticTokens { Data = Deltas(classified) };
    }

    /// <summary>One classified range, in the coordinates LSP counts in.</summary>
    private readonly record struct Classified(int Offset, int Line, int Character, int Length, int Type)
    {
        /// <remarks>
        /// The 1-based line and column of a <see cref="SourcePosition"/> become LSP's 0-based pair by
        /// subtraction, and the units already agree -- both count UTF-16 code units, which is why no
        /// re-measurement of the text happens anywhere in this file.
        /// </remarks>
        public static Classified From(SourceSpan span, int type)
            => new(span.Start.Offset, span.Start.Line - 1, span.Start.Column - 1, span.Length, type);
    }

    /// <summary>
    /// Adds one comment as one token per line, because the encoding cannot express a token that wraps.
    /// </summary>
    /// <remarks>
    /// Walked in offsets rather than through a line map, so the line and column follow from stepping
    /// over each newline instead of being looked up again. A carriage return before the newline is
    /// left out of the token: it is not part of the line, and a client that highlights it draws a box
    /// past the end of the text. A line that contributes nothing -- a blank line inside a block
    /// comment -- contributes no token, since a zero-length token is not a thing a client can paint.
    /// </remarks>
    private static void AddLineByLine(SourceSpan span, string text, List<Classified> classified)
    {
        var offset = span.Start.Offset;
        var line = span.Start.Line;
        var column = span.Start.Column;

        while (offset < span.End.Offset)
        {
            var newline = text.IndexOf('\n', offset);
            var segmentEnd = newline < 0 || newline >= span.End.Offset ? span.End.Offset : newline;

            var visibleEnd = segmentEnd;
            if (visibleEnd > offset && text[visibleEnd - 1] == '\r')
            {
                visibleEnd--;
            }

            if (visibleEnd > offset)
            {
                classified.Add(
                    new Classified(offset, line - 1, column - 1, visibleEnd - offset, SemanticTokenLegend.CommentIndex));
            }

            offset = segmentEnd + 1;
            line++;
            column = 1;
        }
    }

    /// <summary>
    /// The five-integer form: line delta, character delta, length, type, and a modifier bit set.
    /// </summary>
    /// <remarks>
    /// The character delta is measured from the previous token only when the two share a line, and
    /// from the start of the line otherwise. No modifiers are emitted -- see
    /// <see cref="SemanticTokenLegend"/> for why they are nevertheless declared.
    /// </remarks>
    private static List<int> Deltas(List<Classified> classified)
    {
        var data = new List<int>(classified.Count * 5);

        var previousLine = 0;
        var previousCharacter = 0;

        foreach (var token in classified)
        {
            var lineDelta = token.Line - previousLine;

            data.Add(lineDelta);
            data.Add(lineDelta == 0 ? token.Character - previousCharacter : token.Character);
            data.Add(token.Length);
            data.Add(token.Type);
            data.Add(0);

            previousLine = token.Line;
            previousCharacter = token.Character;
        }

        return data;
    }
}
