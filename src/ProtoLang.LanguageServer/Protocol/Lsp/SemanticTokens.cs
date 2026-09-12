namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>Which document to classify.</summary>
public sealed record SemanticTokensParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();
}

/// <summary>
/// Which document to classify, and which answer the client already holds for it.
/// </summary>
/// <remarks>
/// <see cref="PreviousResultId"/> is the <see cref="SemanticTokens.ResultId"/> of the last answer this
/// client was given. A server that no longer holds that answer -- it restarted, the document was
/// closed and reopened, a newer answer replaced it -- may reply with a whole
/// <see cref="SemanticTokens"/> instead of a delta, which is the only honest reply when there is
/// nothing to compute a difference against.
/// </remarks>
public sealed record SemanticTokensDeltaParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();

    public string PreviousResultId { get; init; } = string.Empty;
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
    /// <summary>How many integers one token occupies in <see cref="Data"/>.</summary>
    /// <remarks>
    /// Named because two places count in it -- the encoder building the array and the diff splicing
    /// it -- and a literal five in both is a number that means the same thing twice.
    /// </remarks>
    public const int IntegersPerToken = 5;

    /// <summary>What a later delta request names to say which answer it is building on.</summary>
    /// <remarks>
    /// Null when the server keeps nothing to diff against, which is how a client is told not to ask
    /// for a delta rather than being refused one later.
    /// </remarks>
    public string? ResultId { get; init; }

    public IReadOnlyList<int> Data { get; init; } = [];
}

/// <summary>What changed in <see cref="SemanticTokens.Data"/> since the answer the client holds.</summary>
public sealed record SemanticTokensDelta
{
    /// <inheritdoc cref="SemanticTokens.ResultId"/>
    public string? ResultId { get; init; }

    public IReadOnlyList<SemanticTokensEdit> Edits { get; init; } = [];
}

/// <summary>One splice into the previous answer's integers.</summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> and <see cref="DeleteCount"/> index the flat integer array rather than tokens,
/// so both are multiples of five in anything this server sends: an edit that began or ended inside a
/// token's five numbers would leave the client holding a stream it cannot decode.
/// </para>
/// <para>
/// <see cref="Data"/> is absent for a pure deletion, which is what the protocol's optionality is for.
/// </para>
/// </remarks>
public sealed record SemanticTokensEdit
{
    public int Start { get; init; }

    public int DeleteCount { get; init; }

    public IReadOnlyList<int>? Data { get; init; }
}
