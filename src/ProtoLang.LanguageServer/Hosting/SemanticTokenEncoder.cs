using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.Symbols;
using ProtoLang.Syntax;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>Turns source text into the classification a client paints with.</summary>
/// <remarks>
/// <para>
/// <b>Lexes, then reads what the binder decided.</b> The token stream is the whole of the answer for
/// keywords, literals, comments and operators, and it is where every identifier starts: <c>variable</c>,
/// because from a token stream that is all an identifier is. The refinement is a second sequence laid
/// over the first -- the names the binder resolved, each already carrying the range it was written at
/// and the symbol it turned out to mean -- and an identifier inside one of those takes its category.
/// </para>
/// <para>
/// <b>It transcribes the binder rather than consulting it, and that is the point.</b> Whether
/// <c>Level</c> is an enum type or a field of the receiver spelled the same way is decided by
/// <c>Binder.BindName</c> and <c>Binder.TryResolveEnumReceiver</c>, and the predicate that decides it
/// is private to the binder. Nothing here re-derives that order: what is published is the answer the
/// binder recorded at the moment it resolved the name, so this feature cannot disagree with #43, with
/// go-to-definition, or with the code that actually gets generated.
/// </para>
/// <para>
/// <b>An unresolved name keeps the lexical answer</b>, which is the whole degradation rule. A buffer
/// mid-edit is full of names that resolve to nothing, a buffer whose schema will not load has no
/// resolved names at all, and both are classified completely -- refinement adds colour and never
/// takes any away. Called with no references at all, this is exactly the pass #42 shipped.
/// </para>
/// <para>
/// The one shape the encoding will not carry is a token that crosses a line, and ProtoLang has
/// exactly one of those: a block comment. It arrives here as a single <see cref="Comment"/> with a
/// multi-line span and leaves as one token per line it touches.
/// </para>
/// </remarks>
public static class SemanticTokenEncoder
{
    /// <summary>Classifies <paramref name="text"/> from its tokens alone.</summary>
    /// <param name="name">What the text calls itself, for the spans the lexer stamps.</param>
    /// <remarks>
    /// The answer for a caller that has no compilation and does not want to wait for one. It is the
    /// same answer the refined path gives for a file in which nothing resolved.
    /// </remarks>
    public static SemanticTokens Encode(string text, string name)
        => Encode(text, name, [], ClientLegend.Everything);

    /// <summary>
    /// Classifies <paramref name="text"/>, refining every identifier the binder resolved.
    /// </summary>
    /// <param name="name">What the text calls itself, for the spans the lexer stamps.</param>
    /// <param name="references">
    /// What the binder resolved, in source order -- <see cref="Semantics.SemanticModel.AllReferences"/>.
    /// </param>
    /// <param name="client">What the client at the other end can actually paint.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static SemanticTokens Encode(
        string text, string name, IReadOnlyList<SymbolReference> references, ClientLegend client)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(client);

        var lexer = new Lexer(text, name, new DiagnosticBag());
        var tokens = lexer.Tokenize();

        var classified = new List<Classified>(tokens.Count + lexer.Comments.Count);
        var resolved = new Resolved(references);

        foreach (var token in tokens)
        {
            if (SemanticTokenLegend.IndexOf(token.Kind) is not { } type || token.Span.Length == 0)
            {
                continue;
            }

            classified.Add(
                token.Kind is TokenKind.Identifier && resolved.Covering(token.Span) is { } reference
                    ? Refined(token.Span, type, reference, client)
                    : Classified.From(token.Span, type));
        }

        foreach (var comment in lexer.Comments)
        {
            AddLineByLine(comment.Span, text, classified);
        }

        classified.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        return new SemanticTokens { Data = Deltas(classified) };
    }

    /// <summary>One identifier, coloured by the symbol it turned out to name.</summary>
    private static Classified Refined(
        SourceSpan span, int lexical, SymbolReference reference, ClientLegend client)
    {
        var kind = reference.Symbol.Kind;

        return Classified.From(
            span,
            client.Category(SemanticTokenLegend.IndexOf(kind), lexical),
            client.Modifiers(SemanticTokenLegend.ModifiersOf(kind, reference.Kind)));
    }

    /// <summary>What the binder resolved, read once in the order the names were written.</summary>
    /// <remarks>
    /// <para>
    /// <b>Forward only, because both sequences are in source order.</b> The tokens arrive from the
    /// lexer in the order they were written and the references arrive in
    /// <see cref="SymbolReference.InSourceOrder"/>, so one position into each is enough and the whole
    /// overlay costs one pass rather than a lookup per identifier. The alternative -- asking
    /// <c>SemanticModel.ReferenceAt</c> once per token -- scans the same list once per identifier in
    /// the file.
    /// </para>
    /// <para>
    /// <b>Containment rather than equality, because a name is not always a token.</b> A qualified
    /// name is parsed as one <c>SyntaxName</c> and an enum receiver is recorded over the whole of what
    /// names the enum, so <c>pkg.Level</c> is one reference spanning three tokens. Every identifier
    /// inside it takes the reference's category, and the dots keep getting no token at all -- which is
    /// what spec 6.5 already promises about structural punctuation.
    /// </para>
    /// <para>
    /// <b>One document, which is the same assumption the model it reads from makes.</b> A compilation
    /// carries one ProtoLang source today, so every reference in the sequence is a reference written
    /// in the text being classified. When one carries several (#27) the sequence will be ordered by
    /// document before offset, and this walk will need the document as well as the text -- which is
    /// the same added parameter <see cref="Semantics.SemanticModel"/> says its own queries will take.
    /// </para>
    /// </remarks>
    private sealed class Resolved(IReadOnlyList<SymbolReference> references)
    {
        private int _next;

        /// <summary>The name <paramref name="token"/> is part of, or null when it is part of none.</summary>
        public SymbolReference? Covering(SourceSpan token)
        {
            while (_next < references.Count
                && references[_next].Span.End.Offset <= token.Start.Offset)
            {
                _next++;
            }

            return _next < references.Count && Covers(references[_next].Span, token)
                ? references[_next]
                : null;
        }

        private static bool Covers(SourceSpan name, SourceSpan token)
            => name.Start.Offset <= token.Start.Offset && token.End.Offset <= name.End.Offset;
    }

    /// <summary>One classified range, in the coordinates LSP counts in.</summary>
    private readonly record struct Classified(
        int Offset, int Line, int Character, int Length, int Type, int Modifiers)
    {
        /// <remarks>
        /// The 1-based line and column of a <see cref="SourcePosition"/> become LSP's 0-based pair by
        /// subtraction, and the units already agree -- both count UTF-16 code units, which is why no
        /// re-measurement of the text happens anywhere in this file.
        /// </remarks>
        public static Classified From(SourceSpan span, int type, int modifiers = 0)
        {
            var start = EditorPositions.PositionOf(span.Start);

            return new Classified(
                span.Start.Offset, start.Line, start.Character, span.Length, type, modifiers);
        }
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
                classified.Add(new Classified(
                    offset,
                    line - 1,
                    column - 1,
                    visibleEnd - offset,
                    SemanticTokenLegend.CommentIndex,
                    Modifiers: 0));
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
    /// from the start of the line otherwise.
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
            data.Add(token.Modifiers);

            previousLine = token.Line;
            previousCharacter = token.Character;
        }

        return data;
    }
}
