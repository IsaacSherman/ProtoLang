using ProtoLang.Binding;

namespace ProtoLang;

/// <summary>
/// Why a compilation never got its protobuf schemas, in the shape protoc reported it rather than as
/// prose.
/// </summary>
/// <remarks>
/// <para>
/// The <c>PL0003</c> diagnostic says what went wrong in one sentence, against the import line, and
/// that is the right answer for a build log. It is the wrong answer for an editor: protoc blames a
/// <c>.proto</c>, at a line and a column inside it, and a client that wants to put a squiggle there
/// cannot get back to those numbers from a rendered English sentence. Preserving the structure in
/// <see cref="DescriptorLoadException"/> and then dropping it at the pipeline boundary would mean the
/// only caller that ever sees it is one that catches the exception itself -- which the compilation
/// exists to stop callers having to do.
/// </para>
/// <para>
/// Present on the result whenever a schema load failed, including when it failed before protoc ran at
/// all. A protoc that could not be found reports an empty <see cref="Output"/>: nothing said anything
/// about a schema, and saying so is different from having nothing to say.
/// </para>
/// </remarks>
/// <param name="Output">
/// protoc's report, one entry per line it wrote, with the file and position each line names kept
/// separate from its message. Empty when the failure happened before protoc did.
/// </param>
/// <param name="RawOutput">Its standard error exactly as it arrived, for a status report to quote.</param>
public sealed record SchemaLoadFailure(IReadOnlyList<ProtocDiagnostic> Output, string RawOutput)
{
    /// <summary>What the pipeline caught, kept rather than flattened.</summary>
    public static SchemaLoadFailure From(DescriptorLoadException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new SchemaLoadFailure(exception.Output, exception.RawOutput);
    }
}
