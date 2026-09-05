namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>LSP's four severities. The compiler has the first two and does not pretend to more.</summary>
/// <remarks>
/// Spec 26 gives the compiler <c>Warning</c> and <c>Error</c>. Mapping something onto
/// <see cref="Information"/> or <see cref="Hint"/> would be this server inventing a distinction the
/// language does not make; if those are wanted, that is a change to the compiler's own severity set
/// and belongs in an issue of its own. They are declared here because they are part of the protocol,
/// not because anything produces them.
/// </remarks>
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>A second place worth looking, with something to say about it.</summary>
public sealed record DiagnosticRelatedInformation(Location Location, string Message);

/// <summary>One squiggle.</summary>
public sealed record Diagnostic
{
    public Range Range { get; init; } = new(new Position(0, 0), new Position(0, 0));

    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>The <c>PL####</c> code, or null for a diagnostic this compiler did not write.</summary>
    /// <remarks>
    /// Null is protoc's case. Its messages have no code in this compiler's numbering, and giving them
    /// one would be ProtoLang inventing a taxonomy for another tool's output. <see cref="Source"/>
    /// says who is speaking instead.
    /// </remarks>
    public string? Code { get; init; }

    /// <summary>Who produced this: <c>protolang</c>, or <c>protoc</c>.</summary>
    public string? Source { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<DiagnosticRelatedInformation>? RelatedInformation { get; init; }

    /// <summary>
    /// What a client hands back when it asks for a code action on this diagnostic.
    /// </summary>
    /// <remarks>
    /// Carries the help text structurally, whatever was done to render it for a human. #61 turns help
    /// into quick fixes, and it should read the string the compiler wrote rather than recover it from
    /// prose that a rendering decision may have reflowed.
    /// </remarks>
    public object? Data { get; init; }
}

/// <summary>Everything now wrong with one document.</summary>
/// <remarks>
/// The whole set, every time: LSP has no way to add or remove one diagnostic, so publishing an empty
/// list is how a file is cleared.
/// </remarks>
public sealed record PublishDiagnosticsParams
{
    public string Uri { get; init; } = string.Empty;

    /// <summary>Which version of the document these were computed against, when it is known.</summary>
    /// <remarks>
    /// Null for a file the server has diagnostics about but the client has not opened -- a
    /// <c>.proto</c> that protoc refused. There is no version because there is no buffer.
    /// </remarks>
    public int? Version { get; init; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}
