namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>A line for the client's log channel.</summary>
public sealed record LogMessageParams(int Type, string Message);

/// <summary>Something the user should be shown without having to open a log.</summary>
/// <remarks>
/// Reserved for the things a user cannot otherwise discover -- protoc missing, so nothing in the
/// workspace can compile. Anything that produces a squiggle is already visible and does not need an
/// interruption as well.
/// </remarks>
public sealed record ShowMessageParams(int Type, string Message);
