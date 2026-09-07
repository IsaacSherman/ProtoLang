using ProtoLang.Diagnostics;
using ProtoLang.Syntax;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>What a caret is sitting in, and everything about the buffer that deciding it settled.</summary>
/// <remarks>
/// <para>
/// A completion request is answered in two halves, and this is what crosses between them:
/// <see cref="CompletionProvider.Read"/> decides which context the caret is in while nothing else can
/// race it, and the half that produces candidates reads the answer off this object rather than
/// looking at the buffer again. So every fact here is one that had to be settled before the next
/// message was dequeued.
/// </para>
/// <para>
/// One base type rather than one entry point per context, because a caret is in exactly one context
/// and deciding which is a single question about a single position. Two entry points would each have
/// to know when the other one applies.
/// </para>
/// </remarks>
internal abstract record CompletionSubject
{
    /// <summary>Which context this is, in a form a caller outside this assembly can read.</summary>
    /// <remarks>
    /// Declared here and answered by each subject rather than decided by a switch over their types,
    /// so a context added later cannot forget to say what it is -- the compiler asks it.
    /// </remarks>
    public abstract CompletionContextKind Kind { get; }
}

/// <summary>The kinds of place a caret can be that completion has something to say about.</summary>
/// <remarks>
/// Published because what the caret resolved to is otherwise unobservable from outside, and the two
/// readers that need it cannot get at it any other way: a test asserting that a caret in a comment is
/// in no context at all, and #58's report of what the server thinks it is looking at. The counters on
/// <see cref="CompletionProvider"/> are published for the same reason -- a fact that decides
/// behaviour and that nothing can see is a fact nothing can hold in place.
/// </remarks>
public enum CompletionContextKind
{
    /// <summary>Inside the quotes of an <c>import proto</c> declaration.</summary>
    ImportPath,

    /// <summary>Somewhere a name from the schema or from the current scope could be written.</summary>
    Schema,
}

/// <summary>
/// A caret somewhere a schema name could go: after a dot, on a bare identifier, in a type position,
/// after <c>extend</c>, or naming a fixture field or argument inside a <c>test</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by lexing, like <see cref="ImportPathContext"/>, and for the same reason.</b> This fires
/// in a buffer that does not parse -- the moment the dot is typed, <c>line.</c> has no member name
/// and usually no closing brace either -- so the questions asked here are the ones the token stream
/// can answer about a half-written file. What the caret <em>means</em> is a question for the binder,
/// and is asked later, of a compilation.
/// </para>
/// <para>
/// <b>Why the three token facts are carried rather than re-derived.</b> Each of them describes a
/// state the tree cannot: <c>extend |</c> and <c>arg |</c> are states the parser has already
/// recovered from, leaving no node at the caret to ask about. <see cref="PrecededByDot"/> is the
/// load-bearing one, and it is about correctness rather than classification -- after a dot, a
/// receiver whose type cannot be determined must produce <em>nothing</em>, and without knowing a dot
/// was there, failing to find the receiver would fall through to the bare-identifier list and offer
/// names that cannot go where the caret is.
/// </para>
/// <para>
/// <b>The range is the whole identifier, not the prefix typed so far.</b> A schema list does not
/// change as the user types -- after <c>line.</c> the answer is that message's fields, whatever is
/// typed next -- so it is offered as a complete list and the client filters it locally, re-applying
/// an item it already holds. An edit range covering only what had been typed when the list was built
/// would then insert the rest of the identifier a second time.
/// </para>
/// </remarks>
internal sealed record SchemaSubject(
    int Offset,
    int Start,
    int End,
    bool PrecededByDot,
    bool PrecededByExtend,
    bool PrecededByArg) : CompletionSubject
{
    public override CompletionContextKind Kind => CompletionContextKind.Schema;

    /// <summary>Whether <paramref name="offset"/> is somewhere a schema name could be written.</summary>
    /// <remarks>
    /// False inside a comment and inside a string literal. Both are places where an identifier is not
    /// an identifier, and offering there is the failure that gets completion switched off: a list the
    /// user has to dismiss on every keystroke while writing prose.
    /// </remarks>
    public static bool TryFind(string text, int offset, out SchemaSubject? subject)
    {
        ArgumentNullException.ThrowIfNull(text);

        subject = null;

        if (offset < 0 || offset > text.Length)
        {
            return false;
        }

        // One lex answers all of it. The comments come back from the same scan that skipped them, so
        // "is the caret in a comment" is not a second definition of what a comment is -- which it
        // would have to be, since comments are trivia and leave no token behind to find.
        var lexer = new Lexer(text, SourceIdentity.UnsavedName, new DiagnosticBag());
        var tokens = lexer.Tokenize();

        if (lexer.Comments.Any(comment => Covers(comment.Span, offset)))
        {
            return false;
        }

        if (tokens.Any(token => token.Kind is TokenKind.StringLiteral && Covers(token.Span, offset)))
        {
            return false;
        }

        var (start, end) = WordAt(text, offset);
        var preceding = Preceding(tokens, start);

        subject = new SchemaSubject(
            offset,
            start,
            end,
            PrecededByDot: preceding is TokenKind.Dot,
            PrecededByExtend: preceding is TokenKind.Extend,
            PrecededByArg: preceding is TokenKind.Arg);

        return true;
    }

    /// <summary>Both ends inclusive, so a caret that has just finished typing is still inside.</summary>
    private static bool Covers(SourceSpan span, int offset)
        => offset >= span.Start.Offset && offset <= span.End.Offset;

    /// <summary>The whole identifier the caret is in or beside, as a half-open range.</summary>
    /// <remarks>
    /// Walked over the text rather than found among the tokens, because the caret is often not in a
    /// token at all: the position right after a dot is where the member name is going to go, and
    /// nothing has been typed there yet. An empty range at the caret is the ordinary answer, not a
    /// failure.
    /// </remarks>
    private static (int Start, int End) WordAt(string text, int offset)
    {
        var start = offset;

        while (start > 0 && IsWordCharacter(text[start - 1]))
        {
            start--;
        }

        var end = offset;

        while (end < text.Length && IsWordCharacter(text[end]))
        {
            end++;
        }

        return (start, end);
    }

    private static bool IsWordCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>The kind of the last token that ends at or before <paramref name="start"/>.</summary>
    /// <remarks>
    /// Measured from the start of the identifier rather than from the caret, so that a caret in the
    /// middle of a name already written -- <c>line.to|tal</c>, where completion is invoked rather than
    /// triggered -- finds the dot in front of the name rather than the name itself.
    /// </remarks>
    private static TokenKind? Preceding(IReadOnlyList<Token> tokens, int start)
    {
        TokenKind? found = null;

        foreach (var token in tokens)
        {
            if (token.Kind is TokenKind.EndOfFile || token.Span.End.Offset > start)
            {
                break;
            }

            found = token.Kind;
        }

        return found;
    }
}
