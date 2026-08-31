using ProtoLang.Ir;
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

    private SemanticModel(CompilationUnit? syntaxTree, IrModule? module)
    {
        _syntaxTree = syntaxTree;
        _module = module;
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
    /// covering nodes the shortest wins; among equally short ones the outermost wins, and between two
    /// adjacent ones the caret belongs to the node it ends rather than the one it begins.
    /// </para>
    /// <para>
    /// Whitespace and comments are inside something: the innermost construct that spans them, which
    /// is a block, a declaration, or the compilation unit. A position past the end of a line is
    /// therefore an ordinary position and not a special case. Null means the offset is outside the
    /// file, or that the compilation stopped before it parsed anything at all.
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
}
