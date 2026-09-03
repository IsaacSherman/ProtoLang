namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>Every LSP method this server sends or answers, spelled once.</summary>
/// <remarks>
/// A method name is a string a client and a server have to agree on exactly, and a typo in one is a
/// feature that silently does not exist. Registration and dispatch both read from here.
/// </remarks>
public static class Methods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string CancelRequest = "$/cancelRequest";
    public const string SetTrace = "$/setTrace";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";
    public const string DidSave = "textDocument/didSave";
    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string SemanticTokensFull = "textDocument/semanticTokens/full";

    public const string DidChangeConfiguration = "workspace/didChangeConfiguration";
    public const string DidChangeWorkspaceFolders = "workspace/didChangeWorkspaceFolders";
    public const string Configuration = "workspace/configuration";

    public const string LogMessage = "window/logMessage";
    public const string ShowMessage = "window/showMessage";
}
