using ProtoLang.Diagnostics;
using ProtoLang.Syntax;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The call a caret is inside the parentheses of, and which argument it is supplying.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by lexing, not by parsing, for the reason <see cref="ImportPathContext"/> is.</b> The
/// state this is always invoked in is one the parser has already recovered from: a call being typed
/// has no closing parenthesis and usually no closing brace either. Worse, even where the tree has an
/// <c>InvocationExpression</c>, it cannot answer the question -- it records no comma positions, no
/// span for the argument list, and no flag saying whether the parenthesis was ever closed. A trailing
/// comma manufactures a phantom argument whose span the parser puts at the end of the file rather
/// than after the comma, so a caret sitting in the slot that phantom stands for is inside no argument
/// node at all.
/// </para>
/// <para>
/// The token stream still has all of it, and lexing costs nothing next to the compile the answer is
/// built from anyway. Where a cursor is inside a buffer is a question about an editor, and the
/// compiler has no cursors.
/// </para>
/// <para>
/// <b>This says where the caret is and nothing about what the name means.</b> Which method
/// <see cref="Callee"/> names is a question for the binder, asked later, of a compilation -- so a
/// misspelt name is found here and resolves to nothing there, which is exactly the right division.
/// </para>
/// </remarks>
/// <param name="Callee">
/// The name written immediately before the open parenthesis. A span rather than the text, because
/// what it is for is asking the binder what was resolved at that position.
/// </param>
/// <param name="ActiveParameter">
/// How many argument separators lie between the open parenthesis and the caret, which is the index of
/// the argument being supplied. It may run past the last parameter a method declares; that is what
/// typing one argument too many looks like, and it is the caller's to decide what to show.
/// </param>
internal sealed record CallSubject(SourceSpan Callee, int ActiveParameter)
{
    /// <summary>
    /// The innermost call whose parentheses are open at <paramref name="offset"/>, if there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stack, because calls nest and the answer is the innermost: in <c>outer(inner(1, |</c> the
    /// caret is supplying an argument to <c>inner</c>, and the commas belonging to <c>outer</c> are
    /// not its to count. A parenthesis with no name before it is grouping rather than a call and is
    /// pushed all the same -- it has to be, or the <c>)</c> that closes it would close a call instead.
    /// </para>
    /// <para>
    /// Tokens are read up to the caret and no further: one that straddles it is the name being typed
    /// right now, and the state that matters is the one before it. A comma or a parenthesis the
    /// author has not finished typing past cannot have changed anything yet.
    /// </para>
    /// <para>
    /// Nothing is suppressed inside a string literal. A string is a single token, so a comma inside
    /// one is not an argument separator and cannot be miscounted -- and a caret inside a string is
    /// still a caret supplying that argument, which is what a reader of the panel wants to know.
    /// </para>
    /// </remarks>
    public static bool TryFind(string text, int offset, out CallSubject? subject)
    {
        ArgumentNullException.ThrowIfNull(text);

        subject = null;

        if (offset < 0 || offset > text.Length)
        {
            return false;
        }

        var tokens = new Lexer(text, SourceIdentity.UnsavedName, new DiagnosticBag()).Tokenize();
        var open = new Stack<Frame>();

        Token? previous = null;

        foreach (var token in tokens)
        {
            if (token.Span.End.Offset > offset)
            {
                break;
            }

            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                    open.Push(new Frame(
                        previous is { Kind: TokenKind.Identifier } name ? name.Span : SourceSpan.None));
                    break;

                case TokenKind.CloseParen when open.Count > 0:
                    open.Pop();
                    break;

                case TokenKind.Comma when open.Count > 0:
                    open.Peek().Supplied++;
                    break;

                default:
                    break;
            }

            previous = token;
        }

        // Outwards until a call is found, because a grouping parenthesis is transparent: a caret in
        // `scaled((a + b|` is still supplying `scaled` its first argument. Only the commas belong to
        // the frame they were written in, which is what keeps `(a, b)` from counting against the call
        // around it.
        while (open.Count > 0)
        {
            var frame = open.Pop();

            if (!frame.Callee.IsNone)
            {
                subject = new CallSubject(frame.Callee, frame.Supplied);
                return true;
            }
        }

        return false;
    }

    /// <summary>One open parenthesis and what has been written since it.</summary>
    private sealed class Frame(SourceSpan callee)
    {
        public SourceSpan Callee { get; } = callee;

        public int Supplied { get; set; }
    }
}
