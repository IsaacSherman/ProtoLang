using ProtoLang.Config;
using ProtoLang.Types;

namespace ProtoLang.Ir;

/// <summary>
/// The single place that answers "which behavior does this operation carry?".
/// </summary>
/// <remarks>
/// <para>
/// The answers come from the project's <c>protolang.config.xml</c> (spec 10.4), so a project can
/// choose checked or saturating arithmetic without any backend inheriting a native default. A mode
/// never means "whatever this target does"; every mode is reproduced identically everywhere.
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
    private readonly ProjectConfig _config;

    public NumericPolicy(ProjectConfig config) => _config = config;

    /// <summary>The behavior a project gets when it configures nothing.</summary>
    public static NumericPolicy Default { get; } = new(ProjectConfig.Default);

    /// <summary>Overflow behavior for <c>+</c>, <c>-</c>, and <c>*</c> (spec 10.1).</summary>
    public ArithmeticBehavior ResolveArithmetic(IrBinaryOperator op, ScalarType type)
        => FromOverflow();

    /// <summary>Overflow behavior for unary negation, where negating MIN has no representable result.</summary>
    public ArithmeticBehavior ResolveNegation(ScalarType type) => FromOverflow();

    /// <summary>
    /// Overflow behavior for integer <c>/</c> and <c>%</c>, which is a separate question from the
    /// zero divisor: <c>MIN / -1</c> overflows even though neither operand is zero (spec 10.2).
    /// </summary>
    public ArithmeticBehavior ResolveDivision(IrBinaryOperator op, ScalarType type)
        => FromOverflow();

    /// <summary>Behavior of an explicit conversion whose source value does not fit (spec 10.3).</summary>
    public ConversionBehavior ResolveConversion(ScalarType from, ScalarType to) => _config.Conversion switch
    {
        ConversionPolicy.WrapOrSaturate => ConversionBehavior.WrapOrSaturate,
        _ => throw new ArgumentOutOfRangeException(
            nameof(from), _config.Conversion, "Unhandled conversion policy."),
    };

    private ArithmeticBehavior FromOverflow() => _config.Overflow switch
    {
        OverflowPolicy.Wrapping => ArithmeticBehavior.Wrap,
        OverflowPolicy.Checked => ArithmeticBehavior.Check,
        OverflowPolicy.Saturating => ArithmeticBehavior.Saturate,
        _ => throw new ArgumentOutOfRangeException(
            nameof(_config.Overflow), _config.Overflow, "Unhandled overflow policy."),
    };
}
