using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>One folder the client has open.</summary>
public sealed record WorkspaceFolder
{
    public string Uri { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// The parts of the client's capabilities this server acts on.
/// </summary>
/// <remarks>
/// A deliberate subset. Deserializing the whole of <c>ClientCapabilities</c> would be a large amount
/// of type for no behavior, and the members left out are exactly the ones this server does not consult
/// -- which is a fact worth being able to see. Everything here is nullable because a client is
/// entitled to omit any of it, and every consumer treats absent as "not supported" rather than
/// assuming VS Code's answer.
/// </remarks>
public sealed record ClientCapabilities
{
    public WorkspaceClientCapabilities? Workspace { get; init; }

    public TextDocumentClientCapabilities? TextDocument { get; init; }

    public GeneralClientCapabilities? General { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record WorkspaceClientCapabilities
{
    /// <summary>Whether the client will answer <c>workspace/configuration</c>.</summary>
    public bool? Configuration { get; init; }

    /// <summary>Whether the client reports the folders it has open, and changes to them.</summary>
    public bool? WorkspaceFolders { get; init; }

    public DynamicRegistrationCapability? DidChangeConfiguration { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record TextDocumentClientCapabilities
{
    public PublishDiagnosticsClientCapabilities? PublishDiagnostics { get; init; }

    /// <summary>Present when the client will ask for semantic tokens at all.</summary>
    public SemanticTokensClientCapabilities? SemanticTokens { get; init; }

    /// <summary>Present when the client will ask for completion at all.</summary>
    public CompletionClientCapabilities? Completion { get; init; }

    /// <summary>Present when the client will ask for hover at all.</summary>
    public HoverClientCapabilities? Hover { get; init; }

    /// <summary>Present when the client will ask for go-to-definition at all.</summary>
    public DefinitionClientCapabilities? Definition { get; init; }

    /// <summary>Present when the client will ask for a document outline at all.</summary>
    public DocumentSymbolClientCapabilities? DocumentSymbol { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
/// <remarks>
/// Deliberately empty: presence is the whole of what this server consults. LSP's completion
/// capability describes what an <em>item</em> may carry -- snippets, documentation formats, tag
/// support -- and this server sends the same plain item to every client, so declaring members here
/// would be carrying shape for no behavior. What the members would have decided, they decide by
/// their absence: an edit range is always sent because clients disagree about word boundaries, and
/// the insert format is always stated because a path is not a snippet.
/// </remarks>
public sealed record CompletionClientCapabilities;

/// <inheritdoc cref="ClientCapabilities"/>
/// <remarks>
/// Deliberately empty, on the same terms as <see cref="CompletionClientCapabilities"/>. LSP's hover
/// capability says which content formats the client accepts, best first, and every client that
/// implements hover accepts Markdown -- so honouring it would mean carrying a second rendering of
/// every card to serve a client that does not exist. A client that accepts only plain text is shown
/// the Markdown source, which is still the sentence.
/// </remarks>
public sealed record HoverClientCapabilities;

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record DefinitionClientCapabilities
{
    /// <summary>
    /// Whether the client accepts <see cref="LocationLink"/>, which carries the declaration and the
    /// name inside it as separate ranges.
    /// </summary>
    /// <remarks>
    /// Consulted rather than assumed, because the two shapes are not compatible: a client that did
    /// not ask for links and is sent them finds no <c>uri</c> member and navigates nowhere. Both
    /// ranges are produced either way -- <see cref="Symbols.DeclarationSite"/> has carried both
    /// since #39 -- so what this decides is only how much of what is already known survives the
    /// wire.
    /// </remarks>
    public bool? LinkSupport { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record DocumentSymbolClientCapabilities
{
    /// <summary>Whether the client can show an outline that nests.</summary>
    /// <inheritdoc cref="SymbolInformation" path="/remarks"/>
    public bool? HierarchicalDocumentSymbolSupport { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record GeneralClientCapabilities
{
    /// <summary>The position encodings the client accepts, best first.</summary>
    public IReadOnlyList<string>? PositionEncodings { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record DynamicRegistrationCapability
{
    public bool? DynamicRegistration { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
public sealed record PublishDiagnosticsClientCapabilities
{
    /// <summary>Whether a diagnostic may carry secondary locations with messages of their own.</summary>
    /// <remarks>
    /// This server renders a diagnostic's help text through that member when it is available, so the
    /// help stays a separate, readable thing rather than being appended to the message. See
    /// <c>DiagnosticMapper</c> for what happens when it is not.
    /// </remarks>
    public bool? RelatedInformation { get; init; }
}

/// <inheritdoc cref="ClientCapabilities"/>
/// <remarks>
/// <para>
/// <b>These lists are read, and the reading is what keeps a refinement useful.</b> LSP leaves a
/// client free to understand only part of a legend, and one that meets a category it has no rule for
/// paints the token with nothing at all -- so publishing <c>enumMember</c> to a client that never
/// claimed to know the word takes colour away rather than adding it. <c>ClientLegend</c> is where
/// that is applied; a category this client did not declare degrades to the lexical answer it was
/// already being sent.
/// </para>
/// <para>
/// Both are nullable because this server must survive a client that omits them, even though LSP
/// requires them of a client that asks for semantic tokens at all. Absent is read as "did not fill
/// the capability in" rather than as "supports nothing", since the second reading would switch the
/// feature off for every such client; an empty list is read literally.
/// </para>
/// </remarks>
public sealed record SemanticTokensClientCapabilities
{
    public IReadOnlyList<string>? TokenTypes { get; init; }

    public IReadOnlyList<string>? TokenModifiers { get; init; }

    public SemanticTokensRequests? Requests { get; init; }
}

/// <summary>Which of the semantic token requests this client intends to send.</summary>
public sealed record SemanticTokensRequests
{
    public SemanticTokensFullRequest? Full { get; init; }
}

/// <summary>
/// Whether this client wants whole answers only, or wants to be sent the difference between one
/// answer and the next.
/// </summary>
/// <remarks>
/// LSP spells this member as either <c>true</c> or <c>{ "delta": true }</c>, which is the first union
/// of shapes this protocol layer has had to read -- <c>TextDocumentContentChangeEvent</c> tells its
/// two forms apart by a nullable member rather than by a type. The converter below collapses both
/// spellings to one record, so nothing above it has to know there were two.
/// </remarks>
[JsonConverter(typeof(SemanticTokensFullRequestConverter))]
public sealed record SemanticTokensFullRequest
{
    public bool Delta { get; init; }
}

/// <inheritdoc cref="SemanticTokensFullRequest"/>
public sealed class SemanticTokensFullRequestConverter : JsonConverter<SemanticTokensFullRequest>
{
    public override SemanticTokensFullRequest? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // The bare `true` form says the client sends the full request and nothing about deltas, which
        // is the same thing `{ "delta": false }` says.
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return new SemanticTokensFullRequest { Delta = false };
        }

        if (reader.TokenType is JsonTokenType.Null)
        {
            return null;
        }

        using var full = JsonDocument.ParseValue(ref reader);

        return new SemanticTokensFullRequest
        {
            Delta = full.RootElement.ValueKind is JsonValueKind.Object
                && full.RootElement.TryGetProperty("delta", out var delta)
                && delta.ValueKind is JsonValueKind.True,
        };
    }

    /// <remarks>
    /// Only ever written by a test round-tripping the shape; a server states what it offers in
    /// <see cref="SemanticTokensOptions"/>, not in a client capability.
    /// </remarks>
    public override void Write(
        Utf8JsonWriter writer, SemanticTokensFullRequest value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteBoolean("delta", value.Delta);
        writer.WriteEndObject();
    }
}

/// <summary>What the client says when the conversation opens.</summary>
public sealed record InitializeParams
{
    public int? ProcessId { get; init; }

    public string? RootUri { get; init; }

    public ClientCapabilities? Capabilities { get; init; }

    public IReadOnlyList<WorkspaceFolder>? WorkspaceFolders { get; init; }

    public JsonElement? InitializationOptions { get; init; }

    public string? Trace { get; init; }
}

/// <summary>The token categories a server publishes, and the order they are indexed in.</summary>
public sealed record SemanticTokensLegend
{
    public IReadOnlyList<string> TokenTypes { get; init; } = [];

    public IReadOnlyList<string> TokenModifiers { get; init; } = [];
}

/// <summary>What kinds of semantic token request the server answers.</summary>
public sealed record SemanticTokensOptions
{
    public SemanticTokensLegend Legend { get; init; } = new();

    public SemanticTokensFullOptions Full { get; init; } = new();
}

/// <summary>
/// That the server answers for a whole document, and whether it will also answer with a difference.
/// </summary>
/// <remarks>
/// LSP allows this member to be a bare <c>true</c> as well, and that is what this server sent before
/// deltas existed. It is written in the object form unconditionally now rather than switching between
/// two spellings: the object form is as old as semantic tokens themselves, so no client that can ask
/// for them can fail to read it, and one shape on the way out is one shape to keep right.
/// </remarks>
public sealed record SemanticTokensFullOptions
{
    public bool Delta { get; init; }
}

/// <summary>When the client should send text, and how much of it.</summary>
public sealed record TextDocumentSyncOptions
{
    public bool OpenClose { get; init; } = true;

    /// <summary>0 none, 1 full, 2 incremental.</summary>
    public int Change { get; init; } = 2;

    public SaveOptions? Save { get; init; }
}

/// <inheritdoc cref="TextDocumentSyncOptions"/>
public sealed record SaveOptions
{
    /// <remarks>
    /// False: the buffer the client has already sent is the source of truth, and the text on disk is
    /// stale between saves and absent before the first one. Asking for it again on save would invite
    /// the server to compile something other than what is on the screen.
    /// </remarks>
    public bool IncludeText { get; init; }
}

/// <summary>What the server does about workspace folders.</summary>
public sealed record WorkspaceFoldersServerCapabilities
{
    public bool Supported { get; init; } = true;

    /// <summary>True so the client reports folder changes without a separate registration.</summary>
    public bool ChangeNotifications { get; init; } = true;
}

/// <inheritdoc cref="WorkspaceFoldersServerCapabilities"/>
public sealed record WorkspaceServerCapabilities
{
    public WorkspaceFoldersServerCapabilities? WorkspaceFolders { get; init; }
}

/// <summary>What this server can do, as negotiated for this one client.</summary>
public sealed record ServerCapabilities
{
    /// <summary>How ranges are measured. Always <c>utf-16</c>; see <see cref="Position"/>.</summary>
    public string PositionEncoding { get; init; } = "utf-16";

    public TextDocumentSyncOptions? TextDocumentSync { get; init; }

    /// <summary>Null when the client never said it wanted semantic tokens.</summary>
    public SemanticTokensOptions? SemanticTokensProvider { get; init; }

    /// <summary>Null when the client never said it wanted completion.</summary>
    public CompletionOptions? CompletionProvider { get; init; }

    /// <summary>Null when the client never said it wanted hover.</summary>
    /// <remarks>
    /// A bare boolean rather than an options object, here and for the two below. LSP allows either,
    /// and the options shape carries only work-done progress reporting, which this server does not
    /// do for a request that answers in milliseconds.
    /// </remarks>
    public bool? HoverProvider { get; init; }

    /// <summary>Null when the client never said it wanted go-to-definition.</summary>
    /// <inheritdoc cref="HoverProvider" path="/remarks"/>
    public bool? DefinitionProvider { get; init; }

    /// <summary>Null when the client never said it wanted a document outline.</summary>
    /// <inheritdoc cref="HoverProvider" path="/remarks"/>
    public bool? DocumentSymbolProvider { get; init; }

    public WorkspaceServerCapabilities? Workspace { get; init; }
}

/// <summary>Who the server is, for a client's log and about box.</summary>
public sealed record ServerInfo(string Name, string Version);

/// <summary>The server's half of the opening exchange.</summary>
public sealed record InitializeResult
{
    public ServerCapabilities Capabilities { get; init; } = new();

    public ServerInfo? ServerInfo { get; init; }
}
