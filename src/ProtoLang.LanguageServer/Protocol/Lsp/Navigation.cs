namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>Which document a request is about, and where in it.</summary>
/// <remarks>
/// The shape LSP calls <c>TextDocumentPositionParams</c> and reuses for most of what an editor asks
/// about a caret. Spelled once here rather than per request, because a second copy is a second place
/// for a member name to be wrong -- and a member name wrong in a deserialized shape is not a
/// compile error, it is a position silently read as line zero.
/// </remarks>
public record TextDocumentPositionParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();

    public Position Position { get; init; } = new(0, 0);
}

/// <summary>How a client should read a string this server sent.</summary>
/// <remarks>
/// Markdown, always, for everything this server renders. The alternative is per-client negotiation
/// over <c>hover.contentFormat</c>, and what it would buy is the ability to send the same words
/// without the punctuation that separates a signature from a comment. A client that cannot render
/// Markdown shows the source of it, which is still the sentence.
/// </remarks>
public static class MarkupKind
{
    public const string PlainText = "plaintext";

    public const string Markdown = "markdown";
}

/// <summary>A string and how to read it.</summary>
public sealed record MarkupContent(string Kind, string Value);

/// <summary>What the editor shows when the pointer rests somewhere.</summary>
/// <param name="Range">
/// What is being described, so the editor can highlight exactly that. Optional in the protocol and
/// always sent here: without it a client highlights whatever it believes the word under the cursor
/// to be, which for <c>protolang.tests.Outer</c> is one segment of a name the card is describing
/// all of.
/// </param>
public sealed record Hover(MarkupContent Contents, Range? Range);

/// <summary>Which document an outline is wanted for.</summary>
public sealed record DocumentSymbolParams
{
    public TextDocumentIdentifier TextDocument { get; init; } = new();
}

/// <summary>What kind of thing a symbol is, as far as the icon beside it is concerned.</summary>
/// <remarks>
/// A deliberate subset, like <see cref="CompletionItemKind"/> and for the same reason: the kinds
/// this server actually produces, declared with the numbers LSP gives them, so that the rest can be
/// added by whoever needs them rather than being carried now for no behavior.
/// </remarks>
public enum SymbolKind
{
    /// <summary>An <c>extend</c> block: a set of methods attached to one message.</summary>
    Class = 5,

    /// <summary>A method declared in an <c>extend</c> block.</summary>
    Method = 6,

    /// <summary>A <c>test</c> declaration.</summary>
    Function = 12,
}

/// <summary>One entry of a document's outline, with whatever it contains.</summary>
/// <param name="Range">
/// The whole construct, which is what an editor reveals and what the breadcrumb bar uses to decide
/// which symbol the caret is inside.
/// </param>
/// <param name="SelectionRange">
/// The name alone, which is what is selected on arrival. LSP requires it to lie inside
/// <paramref name="Range"/>, and a client handed the other way round has no defined behavior.
/// </param>
public sealed record DocumentSymbol
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The short line beside the name: a signature, or what a test is called.</summary>
    public string? Detail { get; init; }

    public SymbolKind Kind { get; init; }

    public Range Range { get; init; } = EditorPositions.DocumentStart;

    public Range SelectionRange { get; init; } = EditorPositions.DocumentStart;

    public IReadOnlyList<DocumentSymbol>? Children { get; init; }
}

/// <summary>One entry of a document's outline for a client that cannot read a tree.</summary>
/// <remarks>
/// LSP's older answer to the same request, and still the one a client gets unless it declared
/// <c>hierarchicalDocumentSymbolSupport</c>. It carries no children and no selection range, so the
/// nesting survives only as <see cref="ContainerName"/> and the whole declaration is both the range
/// shown and the range selected. Sending the newer shape to a client that did not ask for it
/// produces an outline with no <c>location</c> member in it, which such a client discards in
/// silence -- the failure mode "capabilities are honoured, not assumed" exists to prevent.
/// </remarks>
public sealed record SymbolInformation
{
    public string Name { get; init; } = string.Empty;

    public SymbolKind Kind { get; init; }

    public Location Location { get; init; } = new(string.Empty, EditorPositions.DocumentStart);

    /// <summary>What holds this symbol, which is the only trace the flat shape keeps of the tree.</summary>
    public string? ContainerName { get; init; }
}

/// <summary>Where a definition is, for a client that asked for the richer shape.</summary>
/// <param name="OriginSelectionRange">
/// The name that was clicked. A client that has it can animate the jump from the word rather than
/// from the caret, and can underline exactly the text that is a link.
/// </param>
/// <param name="TargetRange">The whole declaration, which is what a peek window shows.</param>
/// <param name="TargetSelectionRange">
/// The declared name inside it, which is what is selected on arrival. Always inside
/// <paramref name="TargetRange"/>.
/// </param>
/// <remarks>
/// The two target ranges are what <see cref="Symbols.DeclarationSite"/> and
/// <see cref="Symbols.SchemaSite"/> have carried since #39 and #41 respectively, and neither is
/// derivable from the other. A client that declared no <c>linkSupport</c> gets a plain
/// <see cref="Location"/> instead, which can only be one of them -- and is the name, because
/// arriving with the whole of a method body selected is worse than arriving with its name selected.
/// </remarks>
public sealed record LocationLink(
    Range? OriginSelectionRange,
    string TargetUri,
    Range TargetRange,
    Range TargetSelectionRange);
