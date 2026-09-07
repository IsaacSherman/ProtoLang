namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>What kind of thing an item is, as far as the icon beside it is concerned.</summary>
/// <remarks>
/// A deliberate subset, like <see cref="ClientCapabilities"/>: the two kinds import completion
/// produces, declared with the numbers LSP gives them so the rest can be added by whoever needs them
/// rather than being carried now for no behavior.
/// </remarks>
public enum CompletionItemKind
{
    File = 17,
    Folder = 19,
}

/// <summary>How the text of an item is to be read.</summary>
/// <remarks>
/// Plain text, always, and stated rather than left to the client's default -- which is also plain
/// text, but a schema path is full of characters a snippet grammar would claim. A path containing a
/// dollar sign inserted as a snippet is a path with something else in it.
/// </remarks>
public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2,
}

/// <summary>Replacing one range of a document with one string.</summary>
public sealed record TextEdit(Range Range, string NewText);

/// <summary>Why the client asked.</summary>
/// <remarks>
/// Read but not acted on. Whether the user pressed a key or typed a trigger character does not change
/// what is importable at that position, and an answer that differed between the two would be a
/// feature that works only when invoked one way. Deserialized so the shape is documented and so a
/// later context that does care -- #43's member completion, where the dot matters -- has somewhere to
/// look rather than a parameter list to change.
/// </remarks>
public sealed record CompletionContext
{
    /// <summary>1 invoked, 2 a trigger character, 3 re-triggered while the list was open.</summary>
    public int TriggerKind { get; init; } = 1;

    public string? TriggerCharacter { get; init; }
}

/// <summary>Which document, and where in it.</summary>
public sealed record CompletionParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();

    public Position Position { get; init; } = new(0, 0);

    public CompletionContext? Context { get; init; }
}

/// <summary>One thing the user could pick.</summary>
public sealed record CompletionItem
{
    public string Label { get; init; } = string.Empty;

    public CompletionItemKind? Kind { get; init; }

    /// <summary>The short line beside the label: where this came from, or what is odd about it.</summary>
    public string? Detail { get; init; }

    /// <summary>The longer note, for what does not fit beside the label.</summary>
    public string? Documentation { get; init; }

    /// <summary>What the client matches the user's typing against.</summary>
    /// <remarks>
    /// The whole path rather than the label, because the label is one segment and the user has by
    /// then typed several. A client filtering a segment against a multi-segment word discards every
    /// item the moment a separator is typed.
    /// </remarks>
    public string? FilterText { get; init; }

    /// <summary>What the list is ordered by, when the order is not the labels' own.</summary>
    public string? SortText { get; init; }

    public InsertTextFormat? InsertTextFormat { get; init; }

    /// <summary>
    /// The edit to apply, range and all, rather than a string dropped at the cursor.
    /// </summary>
    /// <remarks>
    /// Always present on the items this server produces, and that is not a stylistic preference.
    /// Without a range a client inserts at whatever it believes the current word to be, and clients
    /// disagree about whether a slash ends one. The disagreement shows up as
    /// <c>billing/billing/invoice.proto</c> in some editors and not in others, which is the worst
    /// possible way for it to show up. Saying which characters to replace leaves nothing to disagree
    /// about.
    /// </remarks>
    public TextEdit? TextEdit { get; init; }
}

/// <summary>Everything that could be picked, and whether asking again would give more.</summary>
public sealed record CompletionList
{
    /// <summary>
    /// Whether this list was cut short by where the cursor is, so typing more should ask again.
    /// </summary>
    /// <remarks>
    /// True for every list here. Candidates are enumerated one directory at a time, so typing a
    /// separator moves the question to a different directory and the previous answer no longer
    /// describes it. A client that cached and filtered instead would show the parent directory's
    /// contents inside a subdirectory.
    /// </remarks>
    public bool IsIncomplete { get; init; } = true;

    public IReadOnlyList<CompletionItem> Items { get; init; } = [];
}

/// <summary>What kinds of completion request the server answers.</summary>
public sealed record CompletionOptions
{
    /// <summary>The characters that should make a client ask without being asked to.</summary>
    public IReadOnlyList<string> TriggerCharacters { get; init; } = [];
}
