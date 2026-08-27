# Reference Semantics

Every question of the form "what does this operation actually do?" has to be answered somewhere, and
answered the same way by every backend. This file is where the answers are collected.

**C# is the reference.** Where the three initial targets disagree about an operation, ProtoLang
adopts what C# does and every other backend emulates it. That is a deliberate choice rather than a
neutral one: C#'s answers are the ones written down in a language standard, they are the ones a
reader is most likely to already hold, and the .NET runtime is the baseline the numeric conversion
rules were derived from. Where ProtoLang does *not* take C#'s answer, the row says so and the
[Departures](#departures-from-c) section says why.

This file is a reference table, not a normative document. [`Protolang_Spec.md`](../Protolang_Spec.md)
is normative; each row cites the section that decides it. Where the two disagree, the spec wins and
this file has a bug.

## How to read a row

| Column | Means |
|---|---|
| **Area** | The operation or situation being pinned. |
| **C# behavior** | What C# and the .NET runtime do, unaided. |
| **ProtoLang rule** | What ProtoLang guarantees, in every backend, regardless of the consumer's build. |
| **C++ must emulate** | What the C++ backend emits to produce that guarantee, and why the native behavior is not enough. |
| **Config key** | The `protolang.config.xml` element that selects it, or `--` when the behavior is fixed. |
| **Spec** | The normative section. |

A row whose **ProtoLang rule** is marked *Not yet pinned* is a known gap: the behavior is not
specified, the backends may already disagree, and nothing in the conformance corpus covers it. Those
rows are the working list.

## Integer arithmetic

| Area | C# behavior | ProtoLang rule | C++ must emulate | Config key | Spec |
|---|---|---|---|---|---|
| Signed `+` `-` `*` overflow | Wraps two's complement. Integer arithmetic is `unchecked` unless the consumer sets `CheckForOverflowUnderflow`. | Wraps: the result is reduced modulo 2^N for the operand width N. Emitted as explicit `unchecked(...)` so a consumer's build flags cannot change it. | Signed overflow is **undefined behavior** -- not "wraps on most compilers". The optimizer may assume it never happens and delete dependent code. Routes through helpers that operate in the unsigned domain and convert back, which C++20 defines as two's complement. | `Arithmetic/Overflow` | 10.1 |
| Unsigned `+` `-` `*` overflow | Wraps. Still subject to a checked context. | Wraps. | Already modular natively, but still emitted explicitly, for the same reason as the signed case. | `Arithmetic/Overflow` | 10.1 |
| Unary `-` of MIN | Wraps back to MIN under `unchecked`; `OverflowException` under `checked`. | Wraps to MIN. | `static_cast<S>(0u - static_cast<U>(a))`. | `Arithmetic/Overflow` | 10.1 |
| Unary `-` of an unsigned value | `-uint` promotes to `long`; `-ulong` is a compile error. | Rejected outright (`PL0052`). ProtoLang has no implicit promotion to fall back on. | Never emitted. | `--` | 10.1, 10.3 |
| `MIN / -1` | **Throws `OverflowException` regardless of `unchecked`** -- it is a hardware trap, not a language-level check. | Wraps to MIN. | The same hardware trap (`SIGFPE` on x86). The helper special-cases a `-1` divisor before dividing. | `Arithmetic/Overflow` | 10.2 |
| `MIN % -1` | Throws, same as above. | `0`. | Same trap; the helper returns `0` for a `-1` divisor. | `Arithmetic/Overflow` | 10.2 |
| Integer division rounding | Truncates toward zero. | Truncates toward zero. | Matches natively since C++11. | `--` | 10.2 |
| Integer `/` by zero | `DivideByZeroException`. | **Not inherited.** The author must write an `on_zero` clause; `PL0054` without one, unless the divisor is a provably non-zero literal. | `SIGFPE` on x86 but a silent `0` on ARM, where `SDIV` by zero returns zero rather than trapping. A runtime zero check is emitted for every division except the proven-literal case. | `Arithmetic/DivideByZero` | 10.2.1 |
| Integer `%` by zero | `DivideByZeroException`. | Same as `/`: an `on_zero` clause is required. | Same as `/`. | `Arithmetic/DivideByZero` | 10.2.1 |
| `on_zero fail` | No equivalent construct. | A diagnostic naming the operation on standard error, then deterministic termination with exit code **70** (`EX_SOFTWARE`). Uncatchable and unrecoverable. | `std::_Exit(70)`. Not `abort` -- that raises `SIGABRT`, which a program may catch and resume from, and it engages the platform's crash reporting. C# uses `Environment.Exit(70)` for the same reason, not `FailFast`. | `--` | 10.2.1 |
| Operand evaluation order | Left to right, guaranteed. | Left to right. | *Not yet pinned.* C++ leaves the operands of `+`, `-`, `*`, `/`, and `%` **unsequenced**, even in C++17. Observable today only through which of two `on_zero fail` divisions terminates first, because method bodies are otherwise free of side effects (spec 20). | `--` | 9.3 |
| `and` / `or` short-circuit | `&&` and `\|\|` short-circuit left to right. | Short-circuits left to right. | Matches natively. | `--` | 9.3 |

### Overflow modes

`Arithmetic/Overflow` selects among three answers to the same question. Every mode is reproduced
identically by every backend; none of them means "whatever this target does natively".

| Mode | Signed `+` `-` `*`, unary `-` | `MIN / -1` |
|---|---|---|
| `Wrapping` (default) | Reduced modulo 2^N. | `MIN`, and `0` for `%`. |
| `Checked` | Terminal failure: diagnostic on stderr, exit 70. Same mechanism as `on_zero fail`. | Terminal failure. |
| `Saturating` | Clamps to the bound the true result exceeded. | `MAX`. |

`Wrapping` is the default because it is what C# does natively. Note that this is the opposite of the
guess in [#20](https://github.com/IsaacSherman/ProtoLang/issues/20), which read "C# standard
behavior" as checked arithmetic: `checked` in C# is opt-in, reached only through the `checked`
keyword or a `CheckForOverflowUnderflow` build property. Wrapping is the behavior a C# author gets
without asking.

## Floating point

| Area | C# behavior | ProtoLang rule | C++ must emulate | Config key | Spec |
|---|---|---|---|---|---|
| `x / 0.0` | IEEE 754: `+-inf`. No exception. | `+-inf`. No `on_zero` clause is permitted, because the operation cannot fail (`PL0015`). | Matches natively. | `--` | 10.2 |
| `0.0 / 0.0` | `NaN`. | `NaN`. | Matches natively. | `--` | 10.2 |
| Comparisons involving `NaN` | Every ordered comparison is false, and `NaN == NaN` is false. | The same. | Matches natively. | `--` | 10.2 |
| Signed zero | IEEE 754: `-0.0 == 0.0` is true; `1.0 / -0.0` is `-inf`. | The same. | Matches natively. | `--` | 10.2 |
| `+` `-` `*` `/` rounding | IEEE 754, round to nearest, ties to even, at the operand's own precision. No implicit widening to a larger evaluation format, and no contraction into a fused multiply-add. | *Not yet pinned.* ProtoLang states IEEE 754 for division only. | *Not yet pinned.* C++ compilers may contract `a * b + c` into an FMA by default -- `-ffp-contract=fast` is GCC's and Clang's default -- which changes the result. Nothing currently prevents it. | `--` | 8.2 |

## Numeric conversions

ProtoLang has **no implicit numeric conversions** (a departure -- see below). Every conversion below
was written by the author as `x as int64`.

| Area | C# behavior | ProtoLang rule | C++ must emulate | Config key | Spec |
|---|---|---|---|---|---|
| Integer to integer, in range | Exact. | Exact. | `static_cast<T>(x)`. | `Arithmetic/Conversion` | 10.3 |
| Integer to integer, out of range | `unchecked` takes the low bits; `OverflowException` under `checked`. | The low bits: reduced modulo 2^N for the target width. The same rule whether or not signedness changes, and consistent with the overflow rule. | `static_cast<T>(x)`, which C++20 defines as two's complement (P0907R4). The generated runtime header asserts C++20. | `Arithmetic/Conversion` | 10.3 |
| Integer to `float` / `double` | Rounds to nearest, ties to even. Fully defined; unaffected by checked context. | Rounds to nearest, ties to even. | `static_cast<T>(x)`. | `Arithmetic/Conversion` | 10.3 |
| `float` to `double` | Exact. | Exact. | `static_cast<double>(x)`. | `Arithmetic/Conversion` | 10.3 |
| `double` to `float`, in range | Rounds to nearest, ties to even. | Rounds to nearest, ties to even. | Undefined behavior when the magnitude is out of range, so this routes through a helper. The overflow threshold is the smallest magnitude that rounds *to infinity* rather than to `FLT_MAX`, because doubles between the two round down. | `Arithmetic/Conversion` | 10.3 |
| `double` to `float`, too large | Becomes an infinity. Fully defined. | Becomes an infinity. | See above. | `Arithmetic/Conversion` | 10.3 |
| Floating point to integer, in range | Truncates toward zero. | Truncates toward zero, consistent with integer division. | Matches natively when in range, but the out-of-range case is undefined, so the whole conversion routes through one helper rather than branching at the emission site. | `Arithmetic/Conversion` | 10.3 |
| Floating point to integer, out of range | **Unspecified by the language.** Saturates on current .NET; throws under `checked`. | Clamps to the target's bound. Saturation is chosen over wrapping because it is total, cheap to check, and is the answer a reader expects from a value that is simply too large; wrapping a magnitude beyond 2^64 is not meaningfully defined without arbitrary-precision arithmetic no target has. | **Undefined behavior** -- not "whatever the hardware does". The optimizer may assume the value is in range. Routes through a clamping helper. | `Arithmetic/Conversion` | 10.3 |
| `NaN` to integer | Unspecified; `0` on current .NET. | `0`. | Undefined behavior; the helper tests `v != v` first. | `Arithmetic/Conversion` | 10.3 |
| Integer literal typing | A literal takes its type from context, with suffixes available. | A literal adopts the expected type at its use site when the value fits, so `var total: int64 = 0;` needs no suffix. The operand of a conversion carries no expectation into itself, so `3000000000 as int32` is a narrowing conversion that wraps rather than a literal reported as out of range. | Emitted with an explicit suffix. | `--` | 10.3 |
| Non-numeric conversion | Many are legal (`int` to `char`, boxing, user-defined). | `PL0075`. Both source and target must be a numeric scalar. `bool`, `string`, `bytes`, messages, and enums are all rejected, which keeps the open enum questions separate rather than answering them by accident. | Never emitted. | `--` | 10.3, 12 |

## Presence and unset fields

Presence is the other place the two backends disagree by default, and it disagrees more quietly than
arithmetic does: reading an unset message field is a `NullReferenceException` in C# and a zero in
C++, from the same source, with nothing in either generated file that looks wrong.

| Area | C# behavior | ProtoLang rule | C++ must emulate | Config key | Spec |
|---|---|---|---|---|---|
| Singular message field, unset -- read through | `NullReferenceException`. | **Compile error (`PL0078`)** unless presence has been established. See [Departures](#departures-from-c). | Nothing: the situation is unrepresentable, so a guarded read emits the plain accessor chain in both targets. | `Presence/UnsetMessageRead` | 13.1 |
| Singular message field, unset -- used as a value | Propagates `null` into the local, argument, or receiver. | `PL0078`. The rule is about the *value*, not only about reading through it: assigning it to a local or passing it as an argument launders exactly the same divergence. | Would otherwise propagate the default instance. | `Presence/UnsetMessageRead` | 13.1 |
| Singular message field -- presence test | `self.Foo != null`. protoc's C# generator emits no `HasFoo` for a message-typed field. | `has foo`. | `self.has_foo()`. The two spellings are unrelated; neither is derivable from the other. | `--` | 8.4 |
| proto3 implicit-presence scalar, unset | Returns the type's zero value. | Returns the zero value, and is indistinguishable from a field explicitly set to zero. That is proto3's design, not an omission. No guard is required. | Matches natively. | `--` | 8.4 |
| proto3 implicit-presence scalar -- presence test | No `HasFoo` is generated. | `PL0079`. The diagnostic exists because the question has no answer on the wire, not because it is hard to implement. | No `has_foo()` is generated. | `--` | 8.4 |
| proto3 `optional` scalar, unset | Returns the zero value; `HasFoo` is false. | Returns the zero value; no guard required. `has foo` distinguishes unset from a set zero. | `foo()` returns the zero value; `has_foo()` is false. | `--` | 8.4, 21.3 |
| proto2 optional scalar, unset | Returns the field's **declared default**, not the type's zero. | The declared default. This was never part of the message-field divergence: both targets already agree. | Matches natively. | `--` | 21.3 |
| Repeated field, empty | An empty `RepeatedField<T>`. Never null. | Iterates zero times. | An empty `RepeatedField` / `RepeatedPtrField`. | `--` | 14.1 |
| Repeated field -- presence test | No `HasFoo`; repeated fields have no presence. | `PL0079`. | No `has_foo()`. | `--` | 14.1 |
| Repeated field element | Always present. | Always present. A `for` loop binding needs no guard, because it is an element rather than a field. | Always present. | `--` | 14.1 |
| Receiver, parameters, locals | Always present. | Always present; never guarded. The only way to obtain a message value is the receiver, a parameter, a loop binding, or a guarded field read, and all four are present by construction. | Always present. | `--` | 13.1 |
| Map field | Supported. | Unsupported: `PL0038` on access, `PL0060` in a test fixture. | Unsupported. | `--` | 14.2 |
| `oneof` | A `FooCase` enum plus per-case accessors. | *Not yet pinned.* No syntax, no IR node, no diagnostic. | *Not yet pinned.* | `--` | 8.4 |
| Message equality | Field-wise `Equals`. | *Not yet pinned.* `==` on two message values is not supported. | *Not yet pinned.* | `--` | 13.3 |

## Departures from C#

Four rows above do not take C#'s answer. Each is deliberate.

**No implicit numeric conversions.** C# widens freely -- `int` to `long`, `int` to `float`, and so
on. ProtoLang has none, and requires `x as int64` instead. This is what makes the overflow rule
well-defined: the width a result wraps to is never the product of a promotion the author did not
write. It is also the only one of these four departures that makes ProtoLang stricter than C# at no
runtime cost.

**Integer division by zero is not an exception.** C#'s answer is `DivideByZeroException`, and
ProtoLang has no exceptions to inherit it into. More to the point, C# is the only one of the three
targets with a usable answer at all: the same program is a `DivideByZeroException` in C#, a `SIGFPE`
crash on x86 in C++, and a **silent zero** on ARM. Requiring the author to state the behavior costs
one clause and removes the divergence entirely.

**Reading an unset message field is a compile error, not a fault.** C# throws
`NullReferenceException`, and C++ cannot reproduce that: dereferencing a null pointer is undefined
behavior, not a trap, so "do what C# does" is not something the C++ backend can be asked to deliver.
The alternatives were to fault deterministically in both targets, or to make the situation
unrepresentable. ProtoLang takes the second, for the same reason `on_zero` exists -- the dangerous
case should not be writable by accident. So ProtoLang keeps C#'s *semantic* position, that an unset
message is not a zero-filled one, and enforces it where the cost is zero rather than where it costs
a runtime check in every backend.

**The overflow default is C#'s real default, not its opt-in one.** `checked` arithmetic is often
described as the C# behavior, but a C# author only gets it by writing `checked` or by setting
`CheckForOverflowUnderflow` in the project file. Unmodified C# wraps, so wrapping is what ProtoLang
adopts. `Checked` is available, but as a mode the project selects rather than as the baseline.

## Adding a row

A row earns its place here when a behavior is *decided* -- when the spec pins it and both backends
implement it. Before then it belongs in the spec's open questions, or in this file as a *Not yet
pinned* row if the gap is worth tracking.

1. Write the normative rule in `Protolang_Spec.md` first, and record the decision in section 31.
2. Add the row here, citing that section. Fill in the **C# behavior** column from what C# actually
   does, not from what it is commonly said to do -- the overflow row above is there because the two
   differ.
3. Say in **C++ must emulate** *why* the native behavior is not enough. A row that reads "matches
   natively" is fine, but only after checking; three of the rows above look like they should say
   that and do not.
4. Add a conformance vector, so the row is a claim the suite checks rather than a claim this file
   makes. `tests/conformance/README.md` describes the format.
5. If the behavior is selectable, add the key to `protolang.config.xml` and name it in the
   **Config key** column. If it is fixed, write `--` and mean it.
