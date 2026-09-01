using ProtoLang.Diagnostics;

namespace ProtoLang.Syntax;

/// <summary>
/// An identifier as the author wrote it, or the place where one was expected and has not been
/// written yet.
/// </summary>
/// <remarks>
/// <para>
/// A parser that recovers has to put <em>something</em> where a name belongs, and the something it
/// used to put there was an empty string carrying the offending token's span. That loses the two
/// facts an editor needs. It cannot tell a name that is absent from a name that is present and
/// happens to be empty -- so the binder went on to report "no field named ''" on top of the syntax
/// error that had already been reported at the same spot. And it points at whatever token followed,
/// which for <c>line.</c> at the end of a line is the brace on the next one: nowhere near the caret
/// the completion list has to open under.
/// </para>
/// <para>
/// Both are fixed by making absence a property of the name rather than a value it can hold.
/// <see cref="IsMissing"/> is the question every resolution site asks before looking a name up, and
/// answering yes means the parser has already diagnosed this and nothing further should be said.
/// <see cref="Span"/> on a missing name is the empty range where the name would go, which is
/// representable only because spans became half-open with an end of their own.
/// </para>
/// <para>
/// A struct because it sits on nearly every declaration node and on two expression nodes, and
/// because it is a string and a span -- the same size the string alone was.
/// </para>
/// </remarks>
public readonly record struct SyntaxName
{
    /// <remarks>
    /// Nullable behind the property rather than in it, because <c>default(SyntaxName)</c> exists
    /// whether or not anything means to construct one, and a consumer that reaches it should find
    /// an empty name rather than a null reference.
    /// </remarks>
    private readonly string? _text;

    /// <summary>A name that was written.</summary>
    public SyntaxName(string text, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text;
        Span = span;
        IsMissing = false;
    }

    private SyntaxName(SourceSpan at)
    {
        _text = string.Empty;
        Span = at;
        IsMissing = true;
    }

    /// <summary>A name that was expected and not written.</summary>
    /// <param name="at">
    /// The empty range where the name would be written -- the position immediately after the token
    /// that called for it, which is where an editor anchors a completion list.
    /// </param>
    public static SyntaxName Missing(SourceSpan at) => new(at);

    /// <summary>The identifier, or the empty string when there is none.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>
    /// Where the name is, or -- when it <see cref="IsMissing"/> -- the empty range where it would go.
    /// </summary>
    public SourceSpan Span { get; }

    /// <summary>Whether a name was expected here and not written.</summary>
    public bool IsMissing { get; }

    /// <remarks>
    /// So that a diagnostic message interpolating a name reads exactly as it did when names were
    /// bare strings. The rendered text of every existing diagnostic depends on this.
    /// </remarks>
    public override string ToString() => Text;
}
