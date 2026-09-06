using System.Text.Json;

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
public sealed record SemanticTokensClientCapabilities
{
    public IReadOnlyList<string>? TokenTypes { get; init; }

    public IReadOnlyList<string>? TokenModifiers { get; init; }
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

    public bool Full { get; init; } = true;
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
