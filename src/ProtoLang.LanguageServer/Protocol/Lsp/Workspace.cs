using System.Text.Json;

namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>One question in a <c>workspace/configuration</c> request.</summary>
/// <remarks>
/// <see cref="ScopeUri"/> names the folder the answer should be scoped to; null asks for the answer
/// with no folder in view, which is the workspace-and-user value.
/// </remarks>
public sealed record ConfigurationItem
{
    public string? ScopeUri { get; init; }

    public string? Section { get; init; }
}

/// <inheritdoc cref="ConfigurationItem"/>
public sealed record ConfigurationParams
{
    public IReadOnlyList<ConfigurationItem> Items { get; init; } = [];
}

/// <summary>Settings have changed. What is in <see cref="Settings"/> depends on the client.</summary>
/// <remarks>
/// A client that supports <c>workspace/configuration</c> may send this with nothing useful in it and
/// expect the server to ask; one that does not support pulling puts its whole settings tree here. Both
/// are handled, because refusing the second would leave a class of clients unable to change a setting
/// at all.
/// </remarks>
public sealed record DidChangeConfigurationParams
{
    public JsonElement? Settings { get; init; }
}

/// <summary>Which folders were added and which were taken away.</summary>
public sealed record WorkspaceFoldersChangeEvent
{
    public IReadOnlyList<WorkspaceFolder> Added { get; init; } = [];

    public IReadOnlyList<WorkspaceFolder> Removed { get; init; } = [];
}

/// <inheritdoc cref="WorkspaceFoldersChangeEvent"/>
public sealed record DidChangeWorkspaceFoldersParams
{
    public WorkspaceFoldersChangeEvent Event { get; init; } = new();
}
