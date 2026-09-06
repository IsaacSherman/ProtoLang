using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProtoLang.LanguageServer.Protocol;

/// <summary>How every message on this connection is serialized, settled once.</summary>
/// <remarks>
/// <para>
/// Camel case because LSP is camel case, and null members omitted because LSP treats an absent member
/// as "not stated" nearly everywhere. The exception is a JSON-RPC response, whose <c>result</c> must
/// be present even when it is null; that one case is why <see cref="ResponseMessage"/> carries a
/// converter instead of trusting this policy.
/// </para>
/// <para>
/// Enumerations are left as numbers. LSP's enumerations <em>are</em> numbers on the wire --
/// <c>DiagnosticSeverity.Error</c> is <c>1</c> -- so adding a string converter would produce a
/// document no client can read.
/// </para>
/// </remarks>
public static class LspJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads request parameters, treating an absent <c>params</c> as an empty object.</summary>
    /// <remarks>
    /// A client may leave <c>params</c> out of a request whose parameters are all optional, and it may
    /// send <c>null</c> for the same request. Both mean the same thing and neither is an error, so the
    /// distinction dies here rather than in every handler.
    /// </remarks>
    public static T? Read<T>(JsonElement? element)
        => element is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value
            ? default
            : value.Deserialize<T>(Options);
}
