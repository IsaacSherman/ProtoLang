using Google.Protobuf.Reflection;
using ProtoLang.Symbols;

namespace ProtoLang.Binding;

/// <summary>
/// Every element one <c>.proto</c> declares, as the identity the IR carries for it and the
/// <c>SourceCodeInfo</c> path its declaration sits at.
/// </summary>
/// <remarks>
/// <para>
/// <b>One walk, because "what does a schema declare" is one rule and two readers.</b>
/// <see cref="SchemaSourceIndex"/> asks it to find where each element is written and what was
/// said about it; <see cref="DescriptorBundle"/> asks it to find which file declares an identity,
/// so that a caret holding nothing but a <see cref="SymbolId"/> is answerable without opening
/// every schema in the closure. The two want different halves of one pair, and what they share is
/// the part that is easy to get quietly wrong: an enum nested in a message is reachable only by
/// descending, an extension is a field declared somewhere other than the message it extends, and
/// a walk that omits either answers null where it should answer.
/// </para>
/// <para>
/// <b>Downward, so an index is a loop variable.</b> A source-info path is a sequence of
/// <c>descriptor.proto</c> field numbers and positions -- a field of a message nested in a message
/// is <c>[4, i, 3, j, 2, k]</c> -- and each position is the element's index in its parent.
/// Descending hands that index over for free; searching a parent for a child afterwards would
/// reconstruct it, and reconstruct it wrong the first time two elements compare equal. The
/// position in <c>Fields.InDeclarationOrder()</c>, <c>NestedTypes</c> and <c>EnumTypes</c>
/// <em>is</em> the index into the corresponding repeated field of the proto, because the runtime
/// built those lists from that proto in order.
/// </para>
/// <para>
/// The path is yielded although only one of the two readers uses it, because the walk is carrying
/// it anyway and a second walk that dropped it would be a second walk. A reader that wants only
/// identities discards it.
/// </para>
/// </remarks>
internal static class SchemaSymbols
{
    /// <summary>Field numbers in <c>descriptor.proto</c>: what a source-info path is made of.</summary>
    /// <remarks>
    /// Named rather than written into the walk, because <c>[4, i, 3, j, 2, k]</c> is unreadable and
    /// unverifiable at the point of use. Each is checked against the <c>descriptor.proto</c> that
    /// ships beside the bundled protoc.
    /// </remarks>
    public static class ProtoField
    {
        /// <summary>
        /// <c>name</c>, which is field 1 of a message, an enum, a field and an enum value alike --
        /// one constant because it is one rule, not four that happen to agree.
        /// </summary>
        public const int Name = 1;

        public const int FileMessageType = 4;
        public const int FileEnumType = 5;
        public const int FileExtension = 7;
        public const int MessageField = 2;
        public const int MessageNestedType = 3;
        public const int MessageEnumType = 4;
        public const int MessageExtension = 6;
        public const int EnumValue = 2;
    }

    /// <summary>
    /// Every message, enum, field and enum value <paramref name="file"/> declares, nested ones and
    /// extensions included, in declaration order.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static IEnumerable<(SymbolId Symbol, int[] Path)> In(FileDescriptor file)
    {
        ArgumentNullException.ThrowIfNull(file);

        for (var index = 0; index < file.MessageTypes.Count; index++)
        {
            var message = file.MessageTypes[index];

            foreach (var found in InMessage(message, [ProtoField.FileMessageType, index]))
            {
                yield return found;
            }
        }

        for (var index = 0; index < file.EnumTypes.Count; index++)
        {
            foreach (var found in InEnum(file.EnumTypes[index], [ProtoField.FileEnumType, index]))
            {
                yield return found;
            }
        }

        foreach (var found in InExtensions(file.Extensions, [], ProtoField.FileExtension))
        {
            yield return found;
        }
    }

    private static IEnumerable<(SymbolId Symbol, int[] Path)> InMessage(
        MessageDescriptor message, int[] path)
    {
        yield return (SymbolId.ForType(message), path);

        var fields = message.Fields.InDeclarationOrder();
        for (var index = 0; index < fields.Count; index++)
        {
            yield return (SymbolId.ForField(fields[index]), [.. path, ProtoField.MessageField, index]);
        }

        for (var index = 0; index < message.NestedTypes.Count; index++)
        {
            int[] nested = [.. path, ProtoField.MessageNestedType, index];

            foreach (var found in InMessage(message.NestedTypes[index], nested))
            {
                yield return found;
            }
        }

        for (var index = 0; index < message.EnumTypes.Count; index++)
        {
            int[] nested = [.. path, ProtoField.MessageEnumType, index];

            foreach (var found in InEnum(message.EnumTypes[index], nested))
            {
                yield return found;
            }
        }

        foreach (var found in InExtensions(message.Extensions, path, ProtoField.MessageExtension))
        {
            yield return found;
        }
    }

    private static IEnumerable<(SymbolId Symbol, int[] Path)> InEnum(
        EnumDescriptor enumType, int[] path)
    {
        yield return (SymbolId.ForType(enumType), path);

        for (var index = 0; index < enumType.Values.Count; index++)
        {
            var value = enumType.Values[index];

            yield return (SymbolId.ForEnumValue(value), [.. path, ProtoField.EnumValue, index]);
        }
    }

    /// <summary>The fields an <c>extend</c> block declares, which belong to a message elsewhere.</summary>
    /// <remarks>
    /// An extension is a <c>FieldDescriptor</c> like any other and is asked about the same way, so
    /// leaving them out of the walk made every question about one answer null -- not "no location
    /// recorded" but "no such thing", which is the answer reserved for a file no bundle has ever
    /// heard of. They are listed apart from the fields because the proto tree lists them apart: an
    /// extension of a message written at file scope is a child of the <em>file</em>, numbered in
    /// its own sequence, and its position in that sequence is the path element. The collection is
    /// in declaration order despite its name, which its own documentation says.
    /// </remarks>
    private static IEnumerable<(SymbolId Symbol, int[] Path)> InExtensions(
        ExtensionCollection extensions, int[] path, int fieldNumber)
    {
        var declared = extensions.UnorderedExtensions;

        for (var index = 0; index < declared.Count; index++)
        {
            yield return (SymbolId.ForField(declared[index]), [.. path, fieldNumber, index]);
        }
    }
}
