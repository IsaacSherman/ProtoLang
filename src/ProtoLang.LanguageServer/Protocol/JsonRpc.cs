using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProtoLang.LanguageServer.Protocol;

/// <summary>
/// A JSON-RPC request identifier, which the protocol says is a string or a number.
/// </summary>
/// <remarks>
/// A response must echo the identifier back <em>in the form it arrived in</em>: a client that sent
/// <c>"3"</c> and is answered <c>3</c> has an outstanding request forever, because its own table is
/// keyed by the string. So the form is carried rather than normalized, and
/// <see cref="RequestIdConverter"/> is the only place either form is read or written.
/// </remarks>
[JsonConverter(typeof(RequestIdConverter))]
public readonly record struct RequestId
{
    private RequestId(long number, string? text)
    {
        Number = number;
        Text = text;
    }

    /// <summary>The numeric form, meaningful only when <see cref="Text"/> is null.</summary>
    public long Number { get; }

    /// <summary>The string form, or null when this identifier arrived as a number.</summary>
    public string? Text { get; }

    public static RequestId Of(long number) => new(number, null);

    public static RequestId Of(string text) => new(0, text ?? throw new ArgumentNullException(nameof(text)));

    public override string ToString() => Text ?? Number.ToString(CultureInfo.InvariantCulture);
}

/// <inheritdoc cref="RequestId"/>
public sealed class RequestIdConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => RequestId.Of(reader.GetInt64()),
            JsonTokenType.String => RequestId.Of(reader.GetString()!),
            _ => throw new JsonException("A request id must be a string or a number."),
        };

    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.Text is { } text)
        {
            writer.WriteStringValue(text);
            return;
        }

        writer.WriteNumberValue(value.Number);
    }
}

/// <summary>The JSON-RPC and LSP error codes this server produces.</summary>
public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>A request arrived before <c>initialize</c> was answered.</summary>
    public const int ServerNotInitialized = -32002;

    /// <summary>The client withdrew the request, or the server gave up on it.</summary>
    public const int RequestCancelled = -32800;
}

/// <summary>What went wrong with one request.</summary>
public sealed record ResponseError(int Code, string Message)
{
    /// <summary>Anything structured a client or a log might want beyond the sentence.</summary>
    public object? Data { get; init; }
}

/// <summary>
/// A message read off the wire, before anything has decided what kind of message it is.
/// </summary>
/// <remarks>
/// One shape with every field optional, rather than three types and a discriminator, because the
/// discriminator is not a field: a request has a method and an id, a notification has a method and no
/// id, and a response has an id and no method. Reading first and classifying afterwards also means a
/// message that is none of the three is a thing the server can describe and refuse, instead of a
/// deserialization exception a long way from the byte that caused it.
/// </remarks>
public sealed record IncomingMessage
{
    public RequestId? Id { get; init; }

    public string? Method { get; init; }

    /// <summary>The parameters, <c>Undefined</c> when the message carried none.</summary>
    public JsonElement Params { get; init; }

    /// <summary>
    /// The result, <c>Null</c> when it was present and null, and <c>Undefined</c> when it was absent.
    /// </summary>
    /// <remarks>
    /// Not nullable, because a nullable would collapse exactly the distinction JSON-RPC draws here: a
    /// successful response must carry a <c>result</c> member even when its value is null, and an error
    /// response must not carry one at all. Deserializing into <c>JsonElement?</c> makes both of those
    /// arrive as C# null and be indistinguishable.
    /// </remarks>
    public JsonElement Result { get; init; }

    public ResponseError? Error { get; init; }

    /// <summary>Whether this expects an answer.</summary>
    public bool IsRequest => Method is not null && Id is not null;

    /// <summary>Whether this expects nothing back.</summary>
    public bool IsNotification => Method is not null && Id is null;

    /// <summary>Whether this is an answer to something the server asked.</summary>
    public bool IsResponse => Method is null && Id is not null;
}

/// <summary>Something the server sends that expects an answer.</summary>
public sealed record OutgoingRequest(RequestId Id, string Method)
{
    /// <remarks>
    /// Named explicitly because the property naming policy would send <c>jsonRpc</c>, and the member
    /// the protocol defines is <c>jsonrpc</c>. A client is entitled to refuse a message without it.
    /// </remarks>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; } = "2.0";

    public object? Params { get; init; }
}

/// <summary>Something the server sends that expects nothing back.</summary>
public sealed record OutgoingNotification(string Method)
{
    /// <inheritdoc cref="OutgoingRequest.JsonRpc"/>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; } = "2.0";

    public object? Params { get; init; }
}

/// <summary>The server's answer to one request.</summary>
/// <remarks>
/// Written by <see cref="ResponseMessageConverter"/> rather than by the default serializer, because
/// JSON-RPC distinguishes <c>"result": null</c> from no <c>result</c> at all and the default
/// null-ignoring policy cannot say both. A successful <c>shutdown</c> answers with a present, null
/// result; an error answers with no result member whatsoever. Getting that backwards produces a
/// response some clients accept and others treat as malformed, which is the worst kind of bug to
/// find.
/// </remarks>
[JsonConverter(typeof(ResponseMessageConverter))]
public sealed record ResponseMessage
{
    private ResponseMessage(RequestId? id, object? result, ResponseError? error)
    {
        Id = id;
        Result = result;
        Error = error;
    }

    public RequestId? Id { get; }

    public object? Result { get; }

    public ResponseError? Error { get; }

    public static ResponseMessage Success(RequestId? id, object? result) => new(id, result, null);

    public static ResponseMessage Failure(RequestId? id, ResponseError error)
        => new(id, null, error ?? throw new ArgumentNullException(nameof(error)));

    /// <inheritdoc cref="Failure(RequestId?, ResponseError)"/>
    public static ResponseMessage Failure(RequestId? id, int code, string message)
        => Failure(id, new ResponseError(code, message));
}

/// <inheritdoc cref="ResponseMessage"/>
public sealed class ResponseMessageConverter : JsonConverter<ResponseMessage>
{
    public override ResponseMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException(
            "Responses are written, never read: what arrives from a client is an IncomingMessage.");

    public override void Write(Utf8JsonWriter writer, ResponseMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");

        writer.WritePropertyName("id");
        if (value.Id is { } id)
        {
            JsonSerializer.Serialize(writer, id, options);
        }
        else
        {
            // A parse error has no id to answer, and the protocol says to send null rather than to
            // stay silent -- silence is indistinguishable from a server that has stopped reading.
            writer.WriteNullValue();
        }

        if (value.Error is { } error)
        {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, error, options);
        }
        else
        {
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, value.Result, options);
        }

        writer.WriteEndObject();
    }
}
