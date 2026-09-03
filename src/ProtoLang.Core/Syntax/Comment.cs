using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

/// <summary>
/// A comment the lexer walked past: where it was, and which of the two forms it was written in.
/// </summary>
/// <remarks>
/// <para>
/// Not a token. A comment has no place in the grammar and the parser never sees one; what it has is a
/// range, and a range is enough for the two things that ask. A host colors it (spec 6.5), and #41
/// will read the text back out to attach a declaration's documentation to it.
/// </para>
/// <para>
/// It is recorded here rather than recognized again elsewhere because there is only one definition of
/// what a comment is. A client-side grammar written in regular expressions is the obvious second
/// definition, and it disagrees with this one the first time somebody writes <c>/* /* */</c> -- which
/// spec 6.2 says closes at the first <c>*/</c>, because block comments do not nest. The lexer is also
/// the party that reports <c>PL0004</c>, so a reader who is told a comment is unterminated and then
/// watches the rest of the file get colored as code is being told two different stories by two
/// different components.
/// </para>
/// <para>
/// <see cref="Span"/> covers the delimiters as well as the text between them, and a block comment's
/// span crosses lines where the comment does. That is the one place a lexer range is not
/// single-line, which is why this is a <see cref="SourceSpan"/> built from both of its ends rather
/// than through <see cref="SourceSpan.SingleLine"/>. An unterminated block comment ends where the
/// text does.
/// </para>
/// </remarks>
/// <param name="Span">The whole comment, delimiters included.</param>
/// <param name="IsBlock">Whether it was written <c>/* */</c> rather than <c>//</c>.</param>
public readonly record struct Comment(SourceSpan Span, bool IsBlock);
