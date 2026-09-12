using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>What the editor shows while a call's arguments are being typed.</summary>
/// <remarks>
/// <para>
/// A list, an index into it, and an index into that signature's parameters. ProtoLang has no
/// overloading (spec 16.1), so the list is always one long and <see cref="ActiveSignature"/> is
/// always zero -- which is the whole reason this feature is small enough to be worth having: none of
/// the ranking a language with overloads has to do applies.
/// </para>
/// <para>
/// The shape is still the list, because it is what the protocol asks for and a server that sent a
/// bare signature would be inventing a dialect.
/// </para>
/// </remarks>
public sealed record SignatureHelp
{
    public IReadOnlyList<SignatureInformation> Signatures { get; init; } = [];

    public int ActiveSignature { get; init; }

    /// <summary>Which parameter the caret is currently supplying.</summary>
    /// <remarks>
    /// Carried here rather than on the signature because that is where every client reads it from.
    /// <b>It must be in range.</b> LSP 3.17 defines a value outside the signature's parameters as
    /// falling back to zero, so an index that honestly says "past the last one" arrives as a highlight
    /// on the first -- see <c>SignatureHelpProvider.Highlighted</c> for what is sent instead and why.
    /// </remarks>
    public int ActiveParameter { get; init; }
}

/// <summary>One signature, as a line of text and the parameters inside that line.</summary>
/// <remarks>
/// <see cref="Label"/> is <c>IrMethodSignature.DisplayName</c> -- the one spelling of a signature in
/// this repository, which a hover already shows. A second rendering here would be the same fact in
/// two voices, and the reader would be the one reconciling them.
/// </remarks>
public sealed record SignatureInformation
{
    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<ParameterInformation> Parameters { get; init; } = [];
}

/// <summary>Which part of the signature line one parameter occupies.</summary>
/// <remarks>
/// <para>
/// <b>Two forms, and which one is sent is the client's to decide.</b> LSP spells the label either as
/// a substring of the signature -- which the client finds for itself -- or as a pair of offsets into
/// it. The offsets are strictly better and are what this server would always send given the choice,
/// because ProtoLang lets a method declare one parameter name twice and a client told to find
/// <c>same: int64</c> highlights the first of the two whichever one the caret is supplying.
/// </para>
/// <para>
/// It is not given the choice. The offset form is gated on the client declaring
/// <c>labelOffsetSupport</c>, and a client that did not declare it is entitled to a string and will
/// mis-read an array. So the capability is read and the answer follows it, which leaves the
/// duplicate-name ambiguity with the clients that chose the form it lives in.
/// </para>
/// <para>
/// One member on the wire and two in the type, because the alternative is an <c>object</c> that every
/// reader has to test the runtime type of. The converter below is what makes them one again.
/// </para>
/// </remarks>
[JsonConverter(typeof(ParameterInformationConverter))]
public sealed record ParameterInformation
{
    /// <summary>The parameter as it reads, for a client that did not negotiate offsets.</summary>
    public string? Written { get; init; }

    /// <summary>
    /// Start and end, counted in UTF-16 code units into <see cref="SignatureInformation.Label"/> --
    /// the same units the rest of this protocol counts columns in.
    /// </summary>
    public IReadOnlyList<int>? Offsets { get; init; }
}

/// <inheritdoc cref="ParameterInformation"/>
public sealed class ParameterInformationConverter : JsonConverter<ParameterInformation>
{
    public override ParameterInformation? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var parameter = JsonDocument.ParseValue(ref reader);

        if (!parameter.RootElement.TryGetProperty("label", out var label))
        {
            return new ParameterInformation();
        }

        return label.ValueKind switch
        {
            JsonValueKind.String => new ParameterInformation { Written = label.GetString() },
            JsonValueKind.Array => new ParameterInformation
            {
                Offsets = [.. label.EnumerateArray().Select(offset => offset.GetInt32())],
            },
            _ => new ParameterInformation(),
        };
    }

    public override void Write(
        Utf8JsonWriter writer, ParameterInformation value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WritePropertyName("label");

        if (value.Offsets is { } offsets)
        {
            writer.WriteStartArray();

            foreach (var offset in offsets)
            {
                writer.WriteNumberValue(offset);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStringValue(value.Written ?? string.Empty);
        }

        writer.WriteEndObject();
    }
}

/// <summary>When a client should ask for signature help, and when it should ask again.</summary>
/// <remarks>
/// An options object rather than a bare <c>true</c>, because the trigger characters live in it and
/// there is nowhere else to put them -- the same reason <see cref="CompletionOptions"/> is one. The
/// comma appears in both lists deliberately: the first opens the panel on the first argument, the
/// second moves it along on every argument after that, and a client consults a different list for
/// each.
/// </remarks>
public sealed record SignatureHelpOptions
{
    public IReadOnlyList<string> TriggerCharacters { get; init; } = [];

    public IReadOnlyList<string> RetriggerCharacters { get; init; } = [];
}
