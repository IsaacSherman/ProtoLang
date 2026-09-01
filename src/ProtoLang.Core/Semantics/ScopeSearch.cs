using Google.Protobuf.Reflection;
using ProtoLang.Ir;
using ProtoLang.Symbols;
using ProtoLang.Types;

namespace ProtoLang.Semantics;

/// <summary>What a bare name resolves to at an offset: the rule, applied to what the binder published.</summary>
/// <remarks>
/// <para>
/// Shaped like <see cref="PositionSearch"/> and for the same reason -- no state, nothing built, one
/// function of a module and an offset. Every question here is positional and the answer is a filter
/// over one flat list, so an index would be a structure to invalidate in exchange for a scan that is
/// already linear in the method body. #57 is the issue that would measure a need for more.
/// </para>
/// <para>
/// <b>It applies a rule; it does not re-derive one.</b> Which names entered scope, over what range,
/// and from what point are three facts <see cref="IrModule.Scope"/> carries because the binder wrote
/// them down as it declared. What is left here is two comparisons and the hiding rule -- and the
/// hiding rule is the one place this file decides anything, which is why it is the thing to read
/// against <c>Binder.BindName</c>.
/// </para>
/// </remarks>
internal static class ScopeSearch
{
    /// <inheritdoc cref="SemanticModel.ScopeAt"/>
    public static ScopeAtPosition? At(IrModule module, int offset)
    {
        // The containment and tie-break rules are PositionSearch's, borrowed rather than restated so
        // that one caret cannot be told it is inside a method by one query and outside it by this.
        // Methods and tests do not nest, so the path this returns is one node deep.
        var enclosing = PositionSearch.Find(
            IrWalk.RootsOf(module),
            offset,
            node => node.Span,
            _ => []);

        if (enclosing is null)
        {
            return null;
        }

        var declared = InScopeAt(module, offset);

        // The two roots differ in exactly what the binder's AllowImplicitReceiverFields says: a
        // method body reaches its receiver's fields by bare name, and everything inside a test binds
        // against a scope holding nothing. Reading it off the shape rather than restating the policy
        // is what keeps the two from drifting apart.
        return enclosing[0] switch
        {
            IrMethod method => new ScopeAtPosition(
                new MessageType(method.Signature.Receiver),
                [.. declared, .. ReachableFields(method.Signature.Receiver, declared)]),

            IrTest test => new ScopeAtPosition(new MessageType(test.Target.Receiver), declared),

            _ => null,
        };
    }

    /// <summary>Every name the binder declared that can be written at <paramref name="offset"/>.</summary>
    /// <remarks>
    /// Ordered by where each was declared, so a caller sees the enclosing method's parameters before
    /// the locals of the block the caret is in. The identity breaks the tie that cannot arise, for
    /// the reason <see cref="SymbolReference.InSourceOrder"/> gives: a total order is what makes two
    /// compilations of unchanged text answer identically.
    /// </remarks>
    private static IReadOnlyList<VisibleName> InScopeAt(IrModule module, int offset) =>
    [
        .. module.Scope
            .Where(entry => PositionSearch.Contains(entry.Region, offset) && offset >= entry.VisibleFrom)
            .OrderBy(entry => entry.Declaration.Name.Span.Start.Offset)
            .ThenBy(entry => entry.Declaration.Id.Key, StringComparer.Ordinal)
            .Select(entry => new VisibleName(
                entry.Declaration.Id,
                entry.Declaration.Name.Text,
                entry.Type,
                entry.Declaration)),
    ];

    /// <summary>
    /// The fields of <paramref name="receiver"/> a bare name reaches, given the names already taken
    /// by <paramref name="declared"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two exclusions, both of them <c>Binder.BindName</c>'s. A local or parameter of the same
    /// spelling wins, so the field is not offered at all: it is unreachable by that name, and an
    /// entry saying otherwise would be a completion that binds to something else the moment it is
    /// accepted. And a map field is never offered, because reading one is <c>PL0038</c> -- this
    /// compiler version does not support them -- so it is a name that resolves and then refuses.
    /// </para>
    /// <para>
    /// A field whose presence has not been established is <em>not</em> excluded. <c>PL0078</c> is a
    /// diagnostic about the value, reported on a name that resolved perfectly well, and the way out
    /// of it is to write the name inside a guard. Withholding it would hide the field from the
    /// author who needs to guard it.
    /// </para>
    /// </remarks>
    private static IEnumerable<VisibleName> ReachableFields(
        MessageDescriptor receiver,
        IReadOnlyList<VisibleName> declared)
    {
        var taken = declared.Select(name => name.Name).ToHashSet(StringComparer.Ordinal);

        return receiver.Fields.InDeclarationOrder()
            .Where(field => !field.IsMap && !taken.Contains(field.Name))
            .Select(field => new VisibleName(
                SymbolId.ForField(field),
                field.Name,
                TypeFactory.FromField(field),
                Declaration: null));
    }
}
