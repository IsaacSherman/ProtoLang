using Google.Protobuf.Reflection;

namespace ProtoLang.Binding;

/// <summary>One type a ProtoLang author may name, under both spellings that reach it.</summary>
/// <remarks>
/// Two shapes rather than one carrying a pair of nullable descriptors, because a type is a message or
/// an enum and never both, and a record whose members can contradict each other is one a reader has to
/// check before trusting. The consumer has to know which it is in any case: what may follow
/// <c>extend</c> is messages alone, and the two are documented and rendered by different means.
/// </remarks>
public abstract record SchemaTypeName(string FullName, string SimpleName)
{
    /// <summary>Whether this is a message, and so may be a receiver.</summary>
    public abstract bool IsMessage { get; }
}

/// <inheritdoc cref="SchemaTypeName"/>
public sealed record SchemaMessageName(MessageDescriptor Descriptor)
    : SchemaTypeName(Descriptor.FullName, Descriptor.Name)
{
    public override bool IsMessage => true;
}

/// <inheritdoc cref="SchemaTypeName"/>
public sealed record SchemaEnumName(EnumDescriptor Descriptor)
    : SchemaTypeName(Descriptor.FullName, Descriptor.Name)
{
    public override bool IsMessage => false;
}

/// <summary>
/// Every message and enum the imported schemas make nameable, indexed by the two spellings a
/// ProtoLang author may write: the full name, and the simple one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One home for "what types are there, and which spellings reach them".</b> The binder built this
/// index privately and answered three different questions from it -- a receiver name after
/// <c>extend</c>, a type in a type position, an enum in front of a dot -- and each question is a
/// different one about ambiguity, which is the detail that makes a second copy dangerous rather than
/// merely wasteful. A host that offers a type name has to offer exactly what the binder would accept,
/// so it asks this object rather than re-deriving the answer from the descriptors and getting the
/// nested cases wrong.
/// </para>
/// <para>
/// <b>Three lookups, not one, because there are three name spaces.</b> An <c>extend</c> receiver must
/// be a message and is ambiguous only against other messages (<c>PL0020</c>). A type position takes
/// messages and enums together, so a name matching one of each is as ambiguous as one matching two
/// enums (<c>PL0074</c>). An enum in front of a dot is ambiguous only against other enums. Collapsing
/// these into a single "is this name ambiguous" would answer the wrong one at two of the three sites,
/// and the failure would be silent: a name that resolves fine reported as ambiguous, or the reverse.
/// </para>
/// <para>
/// <b>Nesting is why this is a walk and not a projection.</b> A message nested in a message, and an
/// enum nested in either, are reachable only by descending -- <see cref="FileDescriptor.MessageTypes"/>
/// lists the top level alone. Every candidate list a host produces has to come through the same walk,
/// because the first thing a hand-rolled second one omits is the nested enum.
/// </para>
/// <para>
/// Ordinal comparison throughout, matching the dictionaries this replaces and the descriptor pool it
/// indexes: protobuf names are case-sensitive, and a lookup that folded case would resolve a name the
/// compiler would then reject.
/// </para>
/// </remarks>
public sealed class SchemaTypes
{
    private readonly Dictionary<string, MessageDescriptor> _messagesByFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MessageDescriptor>> _messagesBySimpleName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescriptor> _enumsByFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EnumDescriptor>> _enumsBySimpleName = new(StringComparer.Ordinal);

    private SchemaTypes()
    {
    }

    /// <summary>The index over no schemas at all, for a compilation that never loaded any.</summary>
    /// <remarks>
    /// So that a caller holding a result whose schema load failed asks the same questions of the same
    /// shape and is told there are no types, rather than testing for null first. The same reason
    /// <see cref="DescriptorBundle.Empty"/> exists.
    /// </remarks>
    public static SchemaTypes Empty { get; } = new();

    /// <summary>Indexes every type declared in <paramref name="files"/>, nested ones included.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
    public static SchemaTypes From(IEnumerable<FileDescriptor> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var types = new SchemaTypes();

        foreach (var file in files)
        {
            foreach (var message in file.MessageTypes)
            {
                types.IndexMessage(message);
            }

            foreach (var enumType in file.EnumTypes)
            {
                types.IndexEnum(enumType);
            }
        }

        return types;
    }

    /// <summary>The message with this exact full name, or null if no schema declares one.</summary>
    public MessageDescriptor? FindMessage(string fullName)
        => _messagesByFullName.GetValueOrDefault(fullName);

    /// <summary>The enum with this exact full name, or null if no schema declares one.</summary>
    public EnumDescriptor? FindEnum(string fullName)
        => _enumsByFullName.GetValueOrDefault(fullName);

    /// <summary>Every message whose simple name is <paramref name="simpleName"/>, in the order the
    /// schemas declared them. More than one means the simple name is ambiguous.</summary>
    /// <remarks>
    /// Declaration order rather than sorted, because the caller that reports the ambiguity sorts the
    /// full names itself and the caller that resolves an unambiguous name reads the only entry. A
    /// list sorted here would be sorted twice at one site and pointlessly at the other.
    /// </remarks>
    public IReadOnlyList<MessageDescriptor> MessagesNamed(string simpleName)
        => _messagesBySimpleName.GetValueOrDefault(simpleName) is { } found ? found : [];

    /// <summary>Every enum whose simple name is <paramref name="simpleName"/>, in the order the
    /// schemas declared them. More than one means the simple name is ambiguous.</summary>
    public IReadOnlyList<EnumDescriptor> EnumsNamed(string simpleName)
        => _enumsBySimpleName.GetValueOrDefault(simpleName) is { } found ? found : [];

    /// <summary>Every type the imported schemas make nameable, nested declarations included.</summary>
    /// <remarks>
    /// In full-name order, so a host offering them produces the same list twice running. Messages and
    /// enums together, because a type position takes either and the caller that wants only messages
    /// says so.
    /// </remarks>
    public IEnumerable<SchemaTypeName> All
        => _messagesByFullName.Values.Select(SchemaTypeName (message) => new SchemaMessageName(message))
            .Concat(_enumsByFullName.Values.Select(SchemaTypeName (enumType) => new SchemaEnumName(enumType)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="simpleName"/> reaches more than one type, and so cannot be written
    /// unqualified in a type position.
    /// </summary>
    /// <remarks>
    /// <b>This is one of three different questions about ambiguity and answers only its own.</b>
    /// Messages and enums share a single name space in a type position, so a name matching one of each
    /// is as ambiguous as one matching two enums -- which is <c>PL0074</c>, and is not the rule for a
    /// receiver after <c>extend</c> (messages alone, <c>PL0020</c>) nor for an enum in front of a dot
    /// (enums alone). It is named for the position it governs so that reaching for it in one of the
    /// others has to be a deliberate act rather than an assumption.
    /// </remarks>
    public bool IsAmbiguousAsATypeName(string simpleName)
        => MessagesNamed(simpleName).Count + EnumsNamed(simpleName).Count > 1;

    /// <summary>
    /// Whether <paramref name="simpleName"/> reaches more than one message, and so cannot be written
    /// unqualified as the receiver of an <c>extend</c> or a <c>test</c>.
    /// </summary>
    /// <remarks>
    /// The second of the three questions, and deliberately not the first. A receiver must be a
    /// message, so an enum sharing the name changes nothing here -- which is exactly why answering
    /// this with <see cref="IsAmbiguousAsATypeName"/> would refuse a name the compiler accepts. The
    /// diagnostic is <c>PL0020</c>, which counts messages and nothing else.
    /// </remarks>
    public bool IsAmbiguousAsAReceiverName(string simpleName)
        => MessagesNamed(simpleName).Count > 1;

    private void IndexMessage(MessageDescriptor message)
    {
        _messagesByFullName[message.FullName] = message;

        if (!_messagesBySimpleName.TryGetValue(message.Name, out var list))
        {
            list = [];
            _messagesBySimpleName[message.Name] = list;
        }

        list.Add(message);

        // Enums nested in a message are only reachable through this walk, so they are indexed here
        // rather than in the top-level loop.
        foreach (var nested in message.EnumTypes)
        {
            IndexEnum(nested);
        }

        foreach (var nested in message.NestedTypes)
        {
            IndexMessage(nested);
        }
    }

    private void IndexEnum(EnumDescriptor enumType)
    {
        _enumsByFullName[enumType.FullName] = enumType;

        if (!_enumsBySimpleName.TryGetValue(enumType.Name, out var list))
        {
            list = [];
            _enumsBySimpleName[enumType.Name] = list;
        }

        list.Add(enumType);
    }
}
