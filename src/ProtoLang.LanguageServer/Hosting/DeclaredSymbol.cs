using ProtoLang.Symbols;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The name under a caret, what it means, and where whatever it means was declared.
/// </summary>
/// <remarks>
/// <para>
/// Hover and go-to-definition are one question answered two ways, and this is the question. Both
/// start by turning a position into a symbol and then asking where that symbol comes from; what they
/// do afterwards is render it or navigate to it. Asking it twice would be two joins over the same
/// two indexes, and the join is the part with a boundary running through it.
/// </para>
/// <para>
/// <b>The boundary is which compiler owns the declaration.</b> A local, a parameter, a loop binding
/// and a method are declared in the ProtoLang buffer, so
/// <see cref="Semantics.SemanticModel.DeclarationOf"/> answers and says where in this file to go. A
/// field, an enum constant, a message and an enum are declared in a <c>.proto</c> this compiler does
/// not own, so that method answers null by design and
/// <see cref="Binding.DescriptorBundle.DeclarationOf(SymbolId)"/> answers instead, with a range in
/// another file. Exactly one of the two answers for any symbol that resolved; both being null means
/// the schema element is real and the file that declares it is not readable, which is ordinary --
/// a well-known type protoc compiled into itself has no <c>.proto</c> on this machine at all.
/// </para>
/// <para>
/// <b>Only names that resolved.</b> <see cref="Semantics.SemanticModel.ReferenceAt"/> answers from
/// what the binder recorded, so a half-typed name, a misspelt one, an operator and a literal all
/// give null here -- which is the right answer for both features. Navigation to a name that means
/// nothing is a guess, and a hover card describing one is a claim the compiler did not make.
/// </para>
/// </remarks>
internal sealed record DeclaredSymbol(
    SymbolReference Reference,
    DeclarationSite? Site,
    SchemaDeclaration? Schema)
{
    /// <summary>What the name means.</summary>
    public SymbolId Id => Reference.Symbol;

    /// <summary>What kind of thing it is.</summary>
    public SymbolKind Kind => Reference.Symbol.Kind;

    /// <summary>The range of the name alone, which is what an editor highlights.</summary>
    public Diagnostics.SourceSpan Span => Reference.Span;

    /// <summary>The name at <paramref name="offset"/>, or null when the offset is not on one.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="compiled"/> is null.</exception>
    public static DeclaredSymbol? At(DocumentCompilation compiled, int offset)
    {
        ArgumentNullException.ThrowIfNull(compiled);

        if (compiled.Semantics is not { } model || model.ReferenceAt(offset) is not { } reference)
        {
            return null;
        }

        return new DeclaredSymbol(
            reference,
            model.DeclarationOf(reference.Symbol),
            compiled.Result?.Schema?.DeclarationOf(reference.Symbol));
    }
}
