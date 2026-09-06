namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>A point in a document, as LSP counts: both numbers 0-based.</summary>
/// <remarks>
/// <see cref="Character"/> counts UTF-16 code units, which is the default position encoding and the
/// one this server negotiates. <see cref="Diagnostics.SourcePosition"/> counts the same units for the
/// same reason, so the conversion is arithmetic and never a re-measurement of the text.
/// </remarks>
public sealed record Position(int Line, int Character);

/// <summary>A half-open range, <see cref="Start"/> inclusive and <see cref="End"/> exclusive.</summary>
public sealed record Range(Position Start, Position End);

/// <summary>A range in a named document.</summary>
public sealed record Location(string Uri, Range Range);

/// <summary>Which document a request is about.</summary>
public sealed record TextDocumentIdentifier
{
    public string Uri { get; init; } = string.Empty;
}

/// <summary>Which document, and which of its versions.</summary>
public sealed record VersionedTextDocumentIdentifier
{
    public string Uri { get; init; } = string.Empty;

    public int Version { get; init; }
}

/// <summary>A document the client has just opened, text and all.</summary>
public sealed record TextDocumentItem
{
    public string Uri { get; init; } = string.Empty;

    public string LanguageId { get; init; } = string.Empty;

    public int Version { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>Which outstanding request the client no longer wants.</summary>
public sealed record CancelParams
{
    public RequestId? Id { get; init; }
}

/// <summary>How much the client wants to be told.</summary>
public sealed record SetTraceParams
{
    /// <summary><c>off</c>, <c>messages</c>, or <c>verbose</c>.</summary>
    public string Value { get; init; } = "off";
}
