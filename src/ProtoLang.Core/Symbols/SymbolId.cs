using Google.Protobuf.Reflection;
using ProtoLang.Diagnostics;

namespace ProtoLang.Symbols;

/// <summary>
/// What makes two references the same symbol: an identity that can be compared, hashed, and used as
/// a dictionary key.
/// </summary>
/// <remarks>
/// <para>
/// Identity used to rest entirely on object identity. Two locals named <c>total</c> in sibling
/// blocks were different symbols only because the binder happened to allocate two
/// <see cref="Ir.IrLocal"/> instances -- and being records, those two were in fact
/// <see cref="object.Equals(object?)"/>. That is enough to bind a method body and nothing more.
/// Highlighting occurrences, indexing references, and caching anything across a keystroke all need
/// to ask whether two things are the same symbol without holding both objects at once.
/// </para>
/// <para>
/// <b>Identity is semantic, not textual.</b> A name is not an identity: field names collide across
/// messages constantly, and two blocks may each declare <c>total</c>. What identifies a symbol is
/// the declaration it came from. So for anything ProtoLang declares, this is <em>where</em> the name
/// was written -- the file and the offset of the declaring name, which is unique because no two
/// names start at one offset, and stable across a recompilation of unchanged text because lexing is
/// deterministic. For anything the schema declares, there is no ProtoLang declaration to point at,
/// and identity comes from the descriptor's fully qualified name instead.
/// </para>
/// <para>
/// A single composed key rather than a discriminated shape, because every consumer wants the same
/// three things from it and no consumer wants to take it apart: compare it, key on it, and print it
/// when a test fails. <see cref="Ir.IrTest.Identity"/> settled the same question the same way.
/// </para>
/// <para>
/// A struct because it hangs off every declaration, and a <c>record struct</c> so equality and
/// hashing are the compiler's problem rather than this file's.
/// </para>
/// </remarks>
public readonly record struct SymbolId
{
    /// <remarks>
    /// Nullable behind the property for the reason <see cref="Syntax.SyntaxName"/> gives: a
    /// <c>default(SymbolId)</c> exists whether or not anything means to construct one, and a
    /// consumer that reaches it should find an empty key rather than a null reference.
    /// </remarks>
    private readonly string? _key;

    private SymbolId(SymbolKind kind, string key)
    {
        Kind = kind;
        _key = key;
    }

    /// <summary>The identity of something declared in ProtoLang source.</summary>
    /// <param name="nameSpan">
    /// The range of the declaring name -- <see cref="Syntax.SyntaxName.Span"/>, which is the empty
    /// insertion point when the author has not written the name yet. Two half-typed declarations
    /// still get two identities, because the parser anchors each hole after a different token.
    /// </param>
    public static SymbolId ForDeclaration(SymbolKind kind, SourceSpan nameSpan)
        => new(kind, $"{nameSpan.File}@{nameSpan.Start.Offset}");

    /// <summary>The identity of a protobuf message field.</summary>
    /// <remarks>
    /// <see cref="FieldDescriptor.FullName"/> is <c>package.Message.field</c>, so two fields with
    /// the same name on different messages are two symbols. That case is the whole reason schema
    /// identity cannot be a name.
    /// </remarks>
    public static SymbolId ForField(FieldDescriptor field)
        => new(SymbolKind.Field, field.FullName);

    /// <summary>The identity of a protobuf enum constant.</summary>
    /// <remarks>
    /// The one full name here that was checked rather than assumed. Protobuf's own scoping rules put
    /// an enum's constants in the enum's <em>parent</em>, following C++, which would make two
    /// same-named constants in sibling enums one identity -- but the C# runtime does not do that,
    /// and reports <c>package.Message.Enum.CONSTANT</c>. A test pins that, because this factory has
    /// no other way to notice if it ever changes.
    /// </remarks>
    public static SymbolId ForEnumValue(EnumValueDescriptor value)
        => new(SymbolKind.EnumValue, value.FullName);

    /// <summary>The identity of a protobuf message type.</summary>
    public static SymbolId ForType(MessageDescriptor message)
        => new(SymbolKind.MessageType, message.FullName);

    /// <summary>The identity of a protobuf enum type.</summary>
    public static SymbolId ForType(EnumDescriptor enumType)
        => new(SymbolKind.EnumType, enumType.FullName);

    /// <summary>What kind of thing this identifies.</summary>
    public SymbolKind Kind { get; }

    /// <summary>
    /// What distinguishes this symbol from every other of its kind: a declaration site for anything
    /// ProtoLang declares, a fully qualified protobuf name for anything the schema declares.
    /// </summary>
    /// <remarks>
    /// Opaque. Its shape is an implementation detail of the factories above and nothing should parse
    /// it; it is a member so that a diagnostic, a log line, or a failing assertion can show it.
    /// </remarks>
    public string Key => _key ?? string.Empty;

    /// <summary>Whether this identifies nothing -- a default value rather than a symbol.</summary>
    public bool IsNone => _key is null;

    /// <summary>Reads as <c>Local:buffer.protolang@142</c>, so a failing assertion diagnoses itself.</summary>
    public override string ToString() => $"{Kind}:{Key}";
}
