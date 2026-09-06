namespace ProtoLang.Binding;

/// <summary>Which way a descriptor load failed, where the difference changes what to do about it.</summary>
/// <remarks>
/// <para>
/// Two values, because two is what can be told apart from the outside and acted on differently. A
/// schema protoc read and rejected is the user's problem and the message names the line; a protoc
/// that never finished reading is the machine's problem and the schema may be perfectly good. Told
/// as one kind of failure, the second reads as the first -- a report about a file with nothing wrong
/// in it -- which is the single most misleading thing this compiler can say about a schema.
/// </para>
/// <para>
/// A kind rather than a second exception type, because every caller handles both the same way and
/// only the reporting differs. It is also what lets a status report (#58) count expiries without
/// matching on the wording of a message.
/// </para>
/// </remarks>
public enum DescriptorLoadFailureKind
{
    /// <summary>protoc ran and said no, or could not be started or built from at all.</summary>
    Failed,

    /// <summary>protoc was still running when its budget ran out, and was stopped.</summary>
    TimedOut,
}
