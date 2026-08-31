using ProtoLang.Ir;

namespace ProtoLang.Semantics;

/// <summary>Where a position lands in the typed IR.</summary>
/// <remarks>
/// The IR covers what the binder could bind, which is less of a file than the syntax tree covers: a
/// position between two methods, in an import, or inside a declaration the binder skipped has no IR
/// node and answers null. That is not a failure -- it is the difference between what was written and
/// what has meaning -- and it is why the syntax tree stays the one that can answer anywhere.
/// </remarks>
public sealed record IrLocation : NodePath<IrNode>
{
    internal IrLocation(IReadOnlyList<IrNode> path)
        : base(path)
    {
    }

    /// <summary>
    /// The method whose body this is in, or null inside a <c>test</c> declaration, which is the
    /// other thing a module holds.
    /// </summary>
    public IrMethod? Method => Enclosing<IrMethod>();

    /// <summary>The <c>test</c> declaration this is in, or null when it is in a method body.</summary>
    public IrTest? Test => Enclosing<IrTest>();
}
