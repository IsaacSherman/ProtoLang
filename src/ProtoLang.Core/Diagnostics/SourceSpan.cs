namespace ProtoLang.Diagnostics;

/// <summary>
/// A location in a ProtoLang source file. Spec 22.2 requires the IR to preserve source
/// locations, so this travels all the way from the lexer into backend code generation.
/// </summary>
public readonly record struct SourceSpan(string File, int Line, int Column, int Length)
{
    public static readonly SourceSpan None = new("<none>", 0, 0, 0);

    /// <summary>Formats as <c>file.protolang:line:column</c> per the spec 26 template.</summary>
    public override string ToString() => $"{File}:{Line}:{Column}";
}
