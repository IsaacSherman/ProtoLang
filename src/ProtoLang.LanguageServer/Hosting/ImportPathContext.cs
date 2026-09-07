using ProtoLang.Diagnostics;
using ProtoLang.Syntax;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// A cursor sitting inside the path of an <c>import proto</c> declaration: which characters are the
/// path, which directory has been typed so far, and what the file already imports.
/// </summary>
/// <param name="Start">The offset of the first character inside the quotes.</param>
/// <param name="End">
/// The offset one past the last character of the path. Never past the closing quote, and never past
/// the end of the line.
/// </param>
/// <param name="Directory">
/// What has been typed up to the last separator at or before the cursor, which is the directory whose
/// contents can follow. Empty at the top level.
/// </param>
/// <param name="Imported">Every path this file already imports, decoded as the compiler will read it.</param>
/// <remarks>
/// <para>
/// <b>Found by lexing, not by parsing.</b> <c>ImportDeclaration.Span</c> covers the whole declaration
/// and does not carry the path's own span, so the tree cannot say where the quotes are. Worse, the
/// state this is always invoked in -- a half-typed path, an unterminated string, no semicolon yet --
/// is one the parser has recovered from by the time anything can be asked of it. The token stream
/// still has all of it, and lexing costs nothing next to the directory listing that follows.
/// </para>
/// <para>
/// <b>It lives here rather than in the compiler.</b> Where a cursor is inside a buffer is a question
/// about an editor, and the compiler has no cursors. The rule this feeds -- which directories a path
/// resolves against, and what they hold -- is the compiler's, and stays there in
/// <c>SchemaCatalog</c>. Splitting it the other way would put an editor concern in
/// <c>ProtoLang.Core</c> and leave the resolution rule with two implementations.
/// </para>
/// </remarks>
internal sealed record ImportPathContext(
    int Start,
    int End,
    string Directory,
    IReadOnlyList<string> Imported)
{
    /// <summary>
    /// The context at <paramref name="offset"/>, or false when the cursor is not inside an import
    /// path.
    /// </summary>
    /// <remarks>
    /// One pass, because the same walk answers both questions: which string literal the cursor is in,
    /// and what every other import declaration names. Asking the second separately would mean lexing
    /// twice to learn something the first pass had already gone past.
    /// </remarks>
    public static bool TryFind(string text, int offset, out ImportPathContext? context)
    {
        ArgumentNullException.ThrowIfNull(text);

        context = null;

        var tokens = new Lexer(text, SourceIdentity.UnsavedName, new DiagnosticBag()).Tokenize();

        var imported = new List<string>();
        SourceSpan? found = null;
        var editing = -1;

        foreach (var quoted in ImportPaths(tokens))
        {
            if (quoted.Value is string path)
            {
                imported.Add(path);
            }

            if (Covers(text, quoted.Span, offset))
            {
                found = quoted.Span;
                editing = imported.Count - 1;
            }
        }

        if (found is not { } span)
        {
            return false;
        }

        // The declaration being edited does not import anything yet, whatever it currently reads.
        // Counting it would mark the schema already on that line as a duplicate of itself, on the one
        // list where the user is deciding whether to keep it.
        if (editing >= 0)
        {
            imported.RemoveAt(editing);
        }

        var start = span.Start.Offset + 1;
        var end = PathEnd(text, start);
        var written = text[start..Math.Max(start, Math.Min(offset, end))];

        context = new ImportPathContext(start, end, DirectoryOf(written), imported);

        return true;
    }

    /// <summary>The string literal of every well-formed <c>import proto</c> declaration, in order.</summary>
    /// <remarks>
    /// Adjacency rather than anything cleverer: the three tokens are what the grammar says an import
    /// is, and a run of them that does not match is a declaration the author has not finished writing
    /// the front of. Completing inside the string of something that is not an import would offer
    /// schema paths in a place no schema path can go.
    /// </remarks>
    private static IEnumerable<Token> ImportPaths(IReadOnlyList<Token> tokens)
    {
        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (tokens[index].Kind is TokenKind.Import
                && tokens[index + 1].Kind is TokenKind.Proto
                && tokens[index + 2].Kind is TokenKind.StringLiteral)
            {
                yield return tokens[index + 2];
            }
        }
    }

    /// <summary>Whether the cursor is somewhere a path character could be typed.</summary>
    /// <remarks>
    /// Half-open at the front and closed at the back, which is what a caret means rather than what a
    /// range means: immediately after the opening quote is inside, and immediately before the closing
    /// quote is inside, because both are places the next character typed becomes part of the path. On
    /// the opening quote itself is not, and neither is past the closing one.
    /// </remarks>
    private static bool Covers(string text, SourceSpan span, int offset)
    {
        var start = span.Start.Offset + 1;

        return offset >= start && offset <= PathEnd(text, start);
    }

    /// <summary>Where the path stops, whether or not the author has closed the quote.</summary>
    /// <remarks>
    /// <para>
    /// <b>The closing quote decides, and only its absence gives the semicolon a say.</b> A semicolon
    /// is an ordinary character inside a quoted string and a legal one in a directory name, so
    /// treating it as a terminator outright would make <c>odd;name/invoice.proto</c> impossible to
    /// complete -- a real path this feature would then be unable to offer or to replace.
    /// </para>
    /// <para>
    /// It matters only in the unterminated case, and there it matters a lot. The lexer has no choice
    /// but to run an unterminated literal to the end of the line, so the token span of
    /// <c>import proto ";</c> covers the semicolon, and an edit built on that span would delete the
    /// one character of the declaration the author did get right. So: the first quote on the line
    /// ends the path, and the first semicolon does only when the line holds no quote at all.
    /// </para>
    /// <para>
    /// <b>A backslash is a separator here and not an escape</b>, which is the same choice
    /// <see cref="DirectoryOf"/> and the catalog already make, for the same reason: a Windows user
    /// typing the separator their operating system uses is a thing that happens, and a quotation mark
    /// inside a schema path is not -- Windows forbids the character in a file name outright. Reading
    /// the backslash as the lexer does would break the case that is real to protect the case that is
    /// not: an editor auto-closing the quote leaves <c>import proto "billing\"</c>, where the closing
    /// quote is the editor's and the path is what precedes it.
    /// </para>
    /// </remarks>
    private static int PathEnd(string text, int start)
    {
        var semicolon = -1;
        var index = start;

        for (; index < text.Length && text[index] is not ('\n' or '\r'); index++)
        {
            if (text[index] == '"')
            {
                return index;
            }

            if (text[index] == ';' && semicolon < 0)
            {
                semicolon = index;
            }
        }

        return semicolon >= 0 ? semicolon : index;
    }

    /// <summary>The directory part of what has been typed, separator included.</summary>
    private static string DirectoryOf(string written)
    {
        var cut = written.AsSpan().LastIndexOfAny('/', '\\');

        return cut < 0 ? string.Empty : written[..(cut + 1)];
    }
}
