namespace ProtoLang.Symbols;

/// <summary>What a reference does with the symbol it names.</summary>
/// <remarks>
/// <para>
/// Three values because three is what the consumers distinguish. LSP's <c>DocumentHighlightKind</c>
/// separates a read from a write so an editor can tint the assignment differently from the uses, and
/// <c>ReferenceContext.includeDeclaration</c> asks whether the declaration itself belongs in the
/// list -- a question that only has an answer if the declaration is in the list and marked.
/// </para>
/// <para>
/// There is deliberately no value for a type reference. A name in type position is read like any
/// other name; nothing renders it differently, and a kind that no consumer branches on is a
/// distinction the index would have to keep correct for nobody.
/// </para>
/// </remarks>
public enum ReferenceKind
{
    /// <summary>Where the name was introduced. See <see cref="DeclarationSite"/>.</summary>
    Declaration,

    /// <summary>The name was used for its value, its type, or the method it calls.</summary>
    Read,

    /// <summary>The name was assigned to.</summary>
    Write,
}
