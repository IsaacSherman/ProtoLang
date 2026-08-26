# ProtoLang Conformance Vectors

This directory is the answer to the question in spec 25.2: does every backend produce the *same*
answer for the same input?

Golden tests over emitted source only say that a backend emits what it emitted last time, and they
say it one language at a time. The vectors here are compiled by every backend, built with a real
compiler, and executed. Each one declares its expectation once, in ProtoLang, and every backend has
to agree with it.

```text
tests/conformance/
  protos/conformance.proto     one schema, shared by every vector
  vectors/*.protolang          the vectors themselves
```

The harness lives in [`tests/ProtoLang.Tests/Conformance/`](../ProtoLang.Tests/Conformance) and runs
as part of `dotnet test`.

## Vector format

A vector is a ProtoLang source file whose `test` declarations (spec 25.3) are the vectors:

```protolang
test DivisionCase.quotient "truncates a negative quotient toward zero" {
    receiver {
        numerator = -7;
        divisor = 2;
    }

    expect return -3;
}
```

Spec 25.2 sketched a separate YAML format. This repository uses the `test` declaration instead,
because it is already bound and type-checked against protobuf descriptors: a fixture field that does
not exist, or an expectation whose type does not match the method's return type, is a compile error
rather than something discovered when a generated test fails to build. Expectations are ProtoLang
literals bound to the method's return type, which makes them language-independent without needing a
serialization format of their own.

## Adding a vector

1. Add a message for it to `protos/conformance.proto`. **Give it a message of its own.** Every
   vector is compiled into a single C# assembly, and the C# backend names its extension class after
   the receiver, so two vectors extending the same message would emit that class twice.
   `ConformanceVectorTests.EveryVectorExtendsItsOwnMessage` enforces this.
2. Drop a `.protolang` file into `vectors/`. It is discovered automatically; nothing needs
   registering.
3. Run `dotnet test ProtoLang.slnx`.

Two constraints are worth knowing before writing one:

- **Take divisors from fixture fields, not literals.** A non-zero literal divisor is proof that an
  `on_zero` clause is unreachable, and the compiler warns about it (PL0056). A field-supplied
  divisor keeps both the zero and non-zero paths live.
- **Some values have no literal form.** `int64` MIN cannot be written directly, because its
  magnitude does not fit in a positive 64-bit literal; write it as `-9223372036854775807 - 1`, the
  way `<climits>` does. There is no `inf` or `NaN` literal either, so floating-point edge cases are
  written as `bool`-returning predicates. `floating_point.protolang` shows the pattern.

## What the harness checks

| Test | Checks |
|---|---|
| `ConformanceVectorTests` | Every vector compiles, declares at least one test, and owns its receiver. Needs only protoc, so it always runs |
| `ConformanceTests.CSharpRunsEveryConformanceVector` | The vectors build into one C# project and every test passes |
| `ConformanceTests.CppRunsEveryConformanceVector` | Each vector builds into a C++ executable and every test passes |
| `ConformanceTests.BothBackendsRunTheSameVectors` | The set of tests C# ran, the set C++ ran, and the set declared in the corpus are the same set |

The last one is the one that matters. Each backend passing on its own is not enough: a driver that
ran zero tests also exits 0. Both backends report each test by the same backend-independent
identity — `IrTest.Identity`, which the C# backend uses as the xUnit display name and the C++ driver
prints — so the sets can be compared directly.

When a backend cannot run, its tests skip with a message naming the missing tool rather than
failing. A fully equipped machine should report no skips.

## Coverage

| Vector | Covers |
|---|---|
| `integer_overflow` | Two's complement wrapping for `int32`, `int64`, `uint32`, `uint64` addition, subtraction, multiplication, and negation (spec 10.1) |
| `integer_division` | Truncation toward zero, `on_zero` fallbacks, `on_zero fail`, and `MIN / -1` and `MIN % -1` (spec 10.2, 10.2.1) |
| `floating_point` | IEEE 754 division including by zero: infinities, NaN, and NaN comparison behavior |
| `control_flow` | `if` / `else if` / `else`, `while`, `while true` with `break`, `continue`, and `for`-`in` (spec 15) |
| `strings` | String equality, string returns, and literals containing characters both backends must escape (spec 11) |
| `enum_types` | Enum-typed locals, parameters, and returns, and named enum values in comparisons, returns, branches, and fixtures (spec 12) |
| `casts` | Explicit conversions: mixed-width arithmetic, integer narrowing and signedness, int-to-float rounding, and float-to-integer truncation, saturation, and NaN (spec 10.3) |

## Adding a backend

The harness is written so a third backend is a small addition, not a third copy:

1. Implement `ITestBackend`, as `CSharpBackend` and `CppBackend` do.
2. Report each test by `IrTest.Identity`, so the agreement check can see it. The C# backend uses it
   as the xUnit display name; the C++ driver prints `[ok] <identity>` and `[FAIL] <identity> ...`.
3. Add a workspace type under `tests/ProtoLang.Tests/Harness/` that writes the generated files,
   runs protoc for that language, builds, and executes. `ProcessRunner` and `Toolchain` already
   handle process plumbing and tool discovery.
4. Add a `ConformanceRun` for it in `ConformanceFixture` and a fact in `ConformanceTests`.

## Not covered yet

- **`uint64` literals above `int64` MAX**, and `int64` MIN and `int32` MIN, have no direct literal
  form. Nor do infinities, NaN, or any float needing an exponent: ProtoLang has no exponent syntax,
  so `casts.protolang` builds large doubles by multiplication and checks an infinity by the property
  that identifies one rather than comparing against a literal.
- **Enum values with no declared name.** proto3 enums are open, so a field can hold a number the
  schema does not name, but a fixture can only set a value that exists. What happens to an unknown
  value is undecided (spec 12), so there is nothing to pin.
- **Maps, presence, mutation, and virtual methods** are not implemented in the language, so there is
  nothing to write a vector against.
- **The negative case for `expect fail` is not in the suite.** That a passing `expect fail` really
  does detect a method returning normally was verified by hand, by pointing such a test at a
  non-zero divisor and confirming it fails with "the method returned instead of terminating the
  process". Covering it automatically means building a deliberately wrong corpus alongside the real
  one, which is a second full build of everything; what the suite asserts today is that both
  backends emit the rejection paths. The half of the check that is machine-verified every run is
  strict: the verdict is an equality test against the failure exit code 70, so a child that fell
  over for an unrelated reason fails rather than passing.
