namespace ProtoLang.Symbols;

/// <summary>What kind of thing a symbol is.</summary>
/// <remarks>
/// <para>
/// Finer than the type system needs and finer than the binder needs, because the consumers are
/// editors: a completion list ranks a local above a field of the receiver, and a semantic
/// highlighter colours a parameter differently from a loop binding. Both distinctions are invisible
/// to the compiler -- a loop binding is an <see cref="Ir.IrLocal"/> like any other -- so the kind is
/// recorded at the declaration rather than recovered later by asking which statement holds it.
/// </para>
/// <para>
/// The first four are declared in ProtoLang source. The last four are declared in a
/// <c>.proto</c> file this compiler does not own, which is why their identity comes from a
/// descriptor rather than from a location; see <see cref="SymbolId"/>.
/// </para>
/// </remarks>
public enum SymbolKind
{
    /// <summary>A <c>var</c> declaration.</summary>
    Local,

    /// <summary>A method parameter.</summary>
    Parameter,

    /// <summary>The name a <c>for</c> loop binds each element to.</summary>
    LoopBinding,

    /// <summary>A ProtoLang method, declared in an <c>extend</c> block.</summary>
    Method,

    /// <summary>A protobuf message field.</summary>
    Field,

    /// <summary>A protobuf enum constant.</summary>
    EnumValue,

    /// <summary>A protobuf message type.</summary>
    MessageType,

    /// <summary>A protobuf enum type.</summary>
    EnumType,
}
