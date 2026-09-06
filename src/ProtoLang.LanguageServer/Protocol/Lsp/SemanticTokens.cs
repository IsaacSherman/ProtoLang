namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>Which document to classify.</summary>
public sealed record SemanticTokensParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();
}

/// <summary>A whole document's classification, in LSP's five-integers-per-token encoding.</summary>
/// <remarks>
/// Each token is five numbers: line delta from the previous token, character delta (from the previous
/// token when on the same line, from the start of the line otherwise), length, an index into the
/// legend's token types, and a bit set of legend modifiers. A token may not span a line, which is why
/// a block comment arrives here already split.
/// </remarks>
public sealed record SemanticTokens
{
    public IReadOnlyList<int> Data { get; init; } = [];
}
