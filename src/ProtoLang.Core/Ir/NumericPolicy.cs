using ProtoLang.Types;

namespace ProtoLang.Ir;

/// <summary>
/// The single place that answers "which behavior does this operation carry?".
/// </summary>
/// <remarks>
/// <para>
/// Every question has exactly one answer today, matching what 10.1 and 10.3 already decide. The
/// point of routing through here anyway is that the answers are about to stop being constants: the
/// compiler is meant to expose arithmetic policy as repository-tracked configuration, so that a
/// project can choose checked or saturating arithmetic without any backend inheriting a native
/// default.
/// </para>
/// <para>
/// The binder stamps the answer onto the IR rather than handing backends a policy object. That
/// keeps <see cref="IrModule"/> the whole contract a backend sees, and it means adding an option to
/// one of these enums is a compile error at every emission site until it has been handled, instead
/// of a silently wrong default.
/// </para>
/// </remarks>
public sealed class NumericPolicy
{
    /// <summary>The behavior the language specifies today.</summary>
    public static NumericPolicy Default { get; } = new();

    /// <summary>Overflow behavior for <c>+</c>, <c>-</c>, and <c>*</c> (spec 10.1).</summary>
    public ArithmeticBehavior ResolveArithmetic(IrBinaryOperator op, ScalarType type)
        => ArithmeticBehavior.Wrap;

    /// <summary>Overflow behavior for unary negation, where negating MIN has no representable result.</summary>
    public ArithmeticBehavior ResolveNegation(ScalarType type) => ArithmeticBehavior.Wrap;

    /// <summary>
    /// Overflow behavior for integer <c>/</c> and <c>%</c>, which is a separate question from the
    /// zero divisor: <c>MIN / -1</c> overflows even though neither operand is zero (spec 10.2).
    /// </summary>
    public ArithmeticBehavior ResolveDivision(IrBinaryOperator op, ScalarType type)
        => ArithmeticBehavior.Wrap;

    /// <summary>Behavior of an explicit conversion whose source value does not fit (spec 10.3).</summary>
    public ConversionBehavior ResolveConversion(ScalarType from, ScalarType to)
        => ConversionBehavior.WrapOrSaturate;
}
