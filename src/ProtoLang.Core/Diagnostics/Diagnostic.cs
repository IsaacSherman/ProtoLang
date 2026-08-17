using System.Collections;
using System.Text;

namespace ProtoLang.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A single compiler message. The <paramref name="Code"/> is a <c>PL####</c> identifier as
/// described in spec 26; whether those codes are part of the compatibility contract is still
/// an open question, so treat them as stable-ish but not yet frozen.
/// </summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Title,
    string Message,
    SourceSpan Span,
    string? Help = null)
{
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Code).Append(": ").Append(Title).AppendLine();
        builder.Append(Span).AppendLine();
        builder.Append(Message);
        if (Help is not null)
        {
            builder.AppendLine();
            builder.Append("help: ").Append(Help);
        }

        return builder.ToString();
    }
}

public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public int Count => _diagnostics.Count;

    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void Error(string code, string title, string message, SourceSpan span, string? help = null)
        => Add(new Diagnostic(code, DiagnosticSeverity.Error, title, message, span, help));

    public void Warning(string code, string title, string message, SourceSpan span, string? help = null)
        => Add(new Diagnostic(code, DiagnosticSeverity.Warning, title, message, span, help));

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
