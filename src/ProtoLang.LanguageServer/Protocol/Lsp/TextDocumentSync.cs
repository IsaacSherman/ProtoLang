namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>A document has been opened, and here is what is in it.</summary>
public sealed record DidOpenTextDocumentParams
{
    public TextDocumentItem TextDocument { get; init; } = new();
}

/// <summary>One edit, or a whole new text.</summary>
/// <remarks>
/// The two forms are told apart by <see cref="Range"/>: absent means <see cref="Text"/> replaces the
/// document entirely. A server that declares incremental sync still has to accept the full form,
/// because a client may send it -- on a change it cannot describe as a range, or simply because it
/// chooses to.
/// </remarks>
public sealed record TextDocumentContentChangeEvent
{
    public Range? Range { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>A document has changed, in the order the changes were made.</summary>
public sealed record DidChangeTextDocumentParams
{
    public VersionedTextDocumentIdentifier TextDocument { get; init; } = new();

    public IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges { get; init; } = [];
}

/// <summary>A document has been closed, so the file on disk is authoritative again.</summary>
public sealed record DidCloseTextDocumentParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();
}

/// <summary>A document has been saved. The server already had the text.</summary>
public sealed record DidSaveTextDocumentParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();
}
