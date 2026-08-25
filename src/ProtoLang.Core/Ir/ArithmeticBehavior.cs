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
/// </remarks>
public enum ArithmeticBehavior
{
    /// <summary>
    /// Two's-complement wraparound. The language default: results are reduced modulo 2^N, where
    /// N is the bit width of the operand type.
    /// </summary>
    Wrap,
}

/// <summary>
/// What an explicit numeric conversion does when the source value is not representable in the
/// target type (spec 10.3).
/// </summary>
/// <remarks>
/// One member today, exactly like <see cref="ArithmeticBehavior"/>. The three initial targets
/// disagree here too, and more sharply: an out-of-range floating-point to integer conversion is
/// unspecified in C#, undefined behavior in C++, and floors in Python. Backends therefore state
/// the behavior rather than inheriting it.
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
