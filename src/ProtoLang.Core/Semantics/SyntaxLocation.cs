using ProtoLang.Syntax;

namespace ProtoLang.Semantics;

/// <summary>Where a position lands in the syntax tree.</summary>
/// <remarks>
/// The two enclosing constructs below are named rather than left to <see cref="NodePath{TNode}.Enclosing{T}"/>
/// because they are asked for constantly -- almost every editor request begins by establishing which
/// method the caret is in -- and a name is worth more than a type argument at a call site that
/// appears in every feature.
/// </remarks>
public sealed record SyntaxLocation : NodePath<SyntaxNode>
{
    internal SyntaxLocation(IReadOnlyList<SyntaxNode> path)
        : base(path)
    {
    }

    /// <summary>The method being written in, or null when the position is outside every method.</summary>
    public MethodDeclaration? Method => Enclosing<MethodDeclaration>();

    /// <summary>
    /// The <c>extend</c> block being written in, or null when the position is outside every one --
    /// in an import, in a test, or in the whitespace between declarations.
    /// </summary>
    public ExtendDeclaration? Extend => Enclosing<ExtendDeclaration>();
}
