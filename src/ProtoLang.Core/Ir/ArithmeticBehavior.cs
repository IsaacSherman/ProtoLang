namespace ProtoLang.Ir;

/// <summary>
/// How an arithmetic operation behaves when the mathematical result leaves the value range of
/// its type.
/// </summary>
/// <remarks>
/// <para>
/// ProtoLang does not inherit the host language's overflow behavior, because the three initial
/// targets disagree: C# wraps by default (integer arithmetic is unchecked unless the consumer
/// opts in), signed overflow in C++ is undefined behavior the optimizer may assume never happens,
/// and Python integers have arbitrary precision and never overflow at all.
/// </para>
/// <para>
/// Every backend therefore emits the operation explicitly rather than relying on a target default,
/// even where the target default already matches. Relying on the default would leave semantics at
/// the mercy of a consumer's compiler flags.
/// </para>
/// <para>
/// Which member an operation carries is chosen once, by the project's configured overflow policy
/// (spec 10.4), and stamped onto the IR by the binder. No member means "whatever this target does".
/// </para>
/// </remarks>
public enum ArithmeticBehavior
{
    /// <summary>
    /// Two's-complement wraparound. The language default: results are reduced modulo 2^N, where
    /// N is the bit width of the operand type. This is also what unmodified C# does.
    /// </summary>
    Wrap,

    /// <summary>
    /// Terminal failure on overflow, through the same path as <c>on_zero fail</c>: a diagnostic on
    /// standard error, then exit code 70. Not an exception, because C++ has no equivalent under the
    /// free-function design in spec 24.2, and a catchable failure would let a consumer resume from
    /// a state the arithmetic says has no valid result.
    /// </summary>
    Check,

    /// <summary>
    /// Clamp to the bound the true result exceeded: <c>MAX</c> when it overflowed above,
    /// <c>MIN</c> when it overflowed below. Total, and cheap to compute in every target.
    /// </summary>
    Saturate,
}

/// <summary>
/// What an explicit numeric conversion does when the source value is not representable in the
/// target type (spec 10.3).
/// </summary>
/// <remarks>
/// One member today, and the reason is worth stating: the three initial targets disagree here more
/// sharply than they do about overflow. An out-of-range floating-point to integer conversion is
/// unspecified in C#, undefined behavior in C++, and floors in Python. Backends therefore state the
/// behavior rather than inheriting it, whatever the project selects.
/// </remarks>
public enum ConversionBehavior
{
    /// <summary>
    /// Integer targets take the low bits, reduced modulo 2^N per 10.1. A floating-point source
    /// truncates toward zero, clamps to the target's bounds, and maps NaN to zero. A
    /// floating-point target rounds to nearest, ties to even. This is what the .NET runtime
    /// produces, which is the baseline every arithmetic-policy question starts from.
    /// </summary>
    WrapOrSaturate,
}
