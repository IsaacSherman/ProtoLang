using ProtoLang.Ir;
using ProtoLang.Symbols;
using ProtoLang.Syntax;

namespace ProtoLang.Semantics;

/// <summary>
/// What a compilation knows, asked about by position: what is at this offset, what stands above it,
/// and which node of one tree corresponds to a node of the other.
/// </summary>
/// <remarks>
/// <para>
/// The compiler has had a typed, spanned, error-tolerant model since before any editor asked for
/// one. What it lacked was a way in. This is the way in, and it adds nothing to the model itself: it
/// walks what the binder already produced, holds no state beyond the result it was given, and
/// answers every query from scratch. Nothing here has to be invalidated when the buffer changes,
/// because there is nothing to invalidate -- a keystroke produces a new compilation and a new model
/// over it.
/// </para>
/// <para>
/// <b>Offsets, not lines and columns.</b> Containment is an integer comparison, and the two
/// coordinate systems a <see cref="Diagnostics.SourcePosition"/> carries would be two doors to keep
/// in agreement. A caller holding an editor position converts it once with
/// <see cref="Diagnostics.LineMap"/>, which is the same converter the rest of the compiler uses.
/// </para>
/// <para>
/// <b>One source, for now.</b> A <see cref="CompilationResult"/> carries one syntax tree, so a
/// position is enough to name a place in it. When a compilation holds several (#27), these queries
/// take a document as well; that is an added parameter rather than a different shape, which is why
/// the model is an object over the whole result rather than a function of a tree.
/// </para>
/// </remarks>
public sealed class SemanticModel
{
    private readonly CompilationUnit? _syntaxTree;
    private readonly IrModule? _module;

    /// <remarks>
    /// Lazy because a model is built per request and most requests never ask. Position queries walk
    /// what the binder produced and build nothing; a reference index is the first thing here that
    /// does, and making <see cref="For"/> pay for it would charge hover and completion for something
    /// only occurrence highlighting wants.
    /// </remarks>
    private readonly Lazy<ReferenceIndex?> _references;

    private SemanticModel(CompilationUnit? syntaxTree, IrModule? module)
    {
        _syntaxTree = syntaxTree;
        _module = module;
        _references = new Lazy<ReferenceIndex?>(
            () => module is null ? null : new ReferenceIndex(module));
    }

    /// <summary>Opens a compilation to position queries.</summary>
    /// <remarks>
    /// Takes the partial <see cref="CompilationResult.Module"/> deliberately, not
    /// <see cref="CompilationResult.EmittableModule"/>. A buffer being typed into is broken most of
    /// the time an editor asks anything about it, and the module bound from a broken file is exactly
    /// what it came for. Emission is the one consumer that must never see that one.
    /// </remarks>
    public static SemanticModel For(CompilationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SemanticModel(result.SyntaxTree, result.Module);
    }

    /// <summary>What is at <paramref name="offset"/> in the syntax tree, or null when nothing is.</summary>
    /// <remarks>
    /// <para>
    /// <b>The rule.</b> A node covers a position when the position is at or after its start and at or
    /// before its end -- both ends inclusive, so a caret that has just finished typing an identifier
    /// still finds that identifier, which is where the caret is when completion is requested. Among
    /// covering nodes the shortest wins, and among equally short ones the outermost. Length is all
    /// that is compared before order, so where two nodes of different lengths meet at the caret the
    /// shorter one wins rather than the earlier; see <see cref="PositionSearch.Find"/>.
    /// </para>
    /// <para>
    /// Whitespace and comments <em>between</em> two tokens are inside something -- the innermost
    /// construct spanning them, which is a block, a declaration, or the compilation unit -- so a
    /// position past the end of a line is an ordinary position rather than a special case. Trivia
    /// before the first token of the file is the exception, and answers null: the compilation unit
    /// starts at that token, because its range is what a diagnostic about the file as a whole is
    /// reported against, and stretching it to cover a leading comment would move that.
    /// </para>
    /// <para>
    /// Null also means the offset is outside the file, or that the compilation stopped before it
    /// parsed anything at all.
    /// </para>
    /// </remarks>
    public SyntaxLocation? SyntaxAt(int offset)
    {
        if (_syntaxTree is null)
        {
            return null;
        }

        var path = PositionSearch.Find<SyntaxNode>(
            [_syntaxTree],
            offset,
            node => node.Span,
            SyntaxWalk.ChildrenOf);

        return path is null ? null : new SyntaxLocation(path);
    }

    /// <summary>What is at <paramref name="offset"/> in the typed IR, or null when nothing is.</summary>
    /// <remarks>
    /// The same rule as <see cref="SyntaxAt"/>, over what the binder produced. Null is a great deal
    /// more common here and means what it says: the IR covers method bodies and test declarations,
    /// so an offset between them, in an import, or inside something the binder could not bind has no
    /// answer in this tree. Ask <see cref="SyntaxAt"/> instead -- it can answer anywhere in the file.
    /// </remarks>
    public IrLocation? IrAt(int offset)
    {
        if (_module is null)
        {
            return null;
        }

        var path = PositionSearch.Find(
            IrWalk.RootsOf(_module),
            offset,
            node => node.Span,
            IrWalk.ChildrenOf);

        return path is null ? null : new IrLocation(path);
    }

    /// <summary>
    /// The IR node bound from a syntax node, or null where the binder produced nothing for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spans are the bridge, because the IR is built from the AST and does not point back at it.
    /// That the mapping is not one-to-one is a property of the compiler rather than of this method: a
    /// <see cref="BinaryExpression"/> becomes an <see cref="IrBinary"/> or an
    /// <see cref="IrIntegerDivision"/>, a <see cref="NameExpression"/> becomes a local reference, a
    /// parameter reference or a field access on an implicit receiver, and plenty of syntax -- a
    /// block's braces, a type reference, a declaration the binder skipped -- becomes nothing at all.
    /// </para>
    /// <para>
    /// Where several IR nodes share one span, this answers with the outermost, which is the node
    /// standing for what was written; the inner ones are what the binder introduced underneath it.
    /// The reverse direction, <see cref="SourceOf"/>, therefore maps several IR nodes onto one syntax
    /// node, and that is correct rather than lossy.
    /// </para>
    /// </remarks>
    public IrNode? BoundFrom(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return _module is null
            ? null
            : PositionSearch.WithSpan(IrWalk.DescendantsAndSelf(_module), node.Span, ir => ir.Span);
    }

    /// <summary>The syntax an IR node was bound from, or null where there is none.</summary>
    /// <inheritdoc cref="BoundFrom" path="/remarks"/>
    public SyntaxNode? SourceOf(IrNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return _syntaxTree is null
            ? null
            : PositionSearch.WithSpan(SyntaxWalk.DescendantsAndSelf(_syntaxTree), node.Span, syntax => syntax.Span);
    }

    /// <summary>
    /// Every place <paramref name="symbol"/> is written, its declaration included and marked as one,
    /// in source order. Empty for a symbol this compilation never mentions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of what #39 made possible, and what occurrence highlighting, find-all-references
    /// and eventually rename are each made of. Each entry spans the name alone -- never the construct
    /// around it, which is what an IR node spans; see <see cref="SymbolReference"/>.
    /// </para>
    /// <para>
    /// The declaration is in the list rather than beside it, because LSP asks for one list and says
    /// whether the declaration belongs in it. <see cref="ReferenceKind"/> is how a caller tells them
    /// apart, and a symbol the schema declares simply has no entry of that kind.
    /// </para>
    /// <para>
    /// <b>Identity is <see cref="SymbolId"/> and never a spelling.</b> Two locals named <c>total</c>
    /// in sibling blocks are two symbols with disjoint answers here; one field reached bare and
    /// through a receiver is one symbol with both.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SymbolReference> ReferencesTo(SymbolId symbol)
        => _references.Value?.ReferencesTo(symbol) ?? [];

    /// <summary>
    /// Where <paramref name="symbol"/> was declared, or null when this compilation does not declare
    /// it.
    /// </summary>
    /// <remarks>
    /// Null for anything the schema declares -- a field, an enum constant, a message or enum type --
    /// because its declaration is in a <c>.proto</c> this compiler does not own. That is a boundary
    /// rather than a shortfall: ProtoLang reports uses of a schema member and does not edit it. #41
    /// is what will answer with a location in the <c>.proto</c> itself.
    /// </remarks>
    public DeclarationSite? DeclarationOf(SymbolId symbol)
        => _references.Value?.DeclarationOf(symbol);

    /// <summary>
    /// The name written at <paramref name="offset"/> and what it means, or null when the offset is
    /// not on a name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How a caret becomes a symbol, and therefore the first half of every reference question an
    /// editor asks: this, then <see cref="ReferencesTo"/> to highlight, or
    /// <see cref="DeclarationOf"/> to navigate. The whole reference is returned rather than the
    /// identity alone because whether the caret is on the declaration is a question rename asks
    /// before it starts, and the answer is already here.
    /// </para>
    /// <para>
    /// Containment includes both ends, as it does for <see cref="SyntaxAt"/> and for the same reason:
    /// the caret sits immediately after an identifier the moment the author finishes typing it. Null
    /// means the offset is on an operator, on whitespace, inside a literal, or on a name that did not
    /// resolve -- and the last of those is why this is not a way to ask what was written. Ask
    /// <see cref="SyntaxAt"/> for that; it can answer anywhere in the file.
    /// </para>
    /// </remarks>
    public SymbolReference? ReferenceAt(int offset) => _references.Value?.ReferenceAt(offset);

    /// <summary>
    /// What a bare identifier written at <paramref name="offset"/> could mean, or null when the
    /// offset is not inside a method body or a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binder knows what is in scope at every point of a method body and used to discard it as
    /// it descended. This is that set handed back, at a position nobody knew about while binding,
    /// and it is what completion on a bare identifier -- the most common completion in any language
    /// -- is made of.
    /// </para>
    /// <para>
    /// <b>Every name here binds, and nothing that binds is missing.</b> That is the whole contract,
    /// and it is why the set is narrower than "everything nameable": a method resolves only in call
    /// position and a type only in type position, so neither is here. A local hides a field of the
    /// receiver with the same spelling, and only the winner is listed, because a list that offered
    /// both would offer a word that means something other than what it says.
    /// </para>
    /// <para>
    /// Null means the offset is between declarations, in an import, in an <c>extend</c> header, or
    /// outside the file -- places where a bare identifier means nothing rather than nothing being in
    /// scope. Inside a <c>test</c> the answer is an empty list with a receiver, which is a different
    /// statement: there is a message being tested, and no unqualified name resolves against it.
    /// </para>
    /// <para>
    /// A position in <em>type</em> position gets an answer too, and it is the wrong list -- the
    /// names of values, where a type name belongs. Deciding which question a caret is asking is the
    /// caller's, from <see cref="SyntaxAt"/>; this answers only the one it was asked.
    /// </para>
    /// </remarks>
    public ScopeAtPosition? ScopeAt(int offset)
        => _module is null ? null : ScopeSearch.At(_module, offset);
}
