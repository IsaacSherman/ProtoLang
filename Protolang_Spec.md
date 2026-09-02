# ProtoLang Specification

Status: Living draft, synchronized with current implementation
Spec version: TBD  
Last updated: 2026-08-30
Target protobuf versions: TBD  
Target language backends: C# and C++ implemented; Python is planned but not present

## 1. Purpose

ProtoLang is a deliberately small language for defining portable behavior over Protocol Buffer message types.

ProtoLang source files define methods and related behavior once, then compile that behavior into target languages. The current implementation emits C# and C++.

The language is intended to describe core semantic behavior, not target-language idioms.

### 1.1 Normative Language Boundary

This specification distinguishes between:

- Normative language semantics: behavior every conforming compiler and backend must preserve.
- Implementation details: compiler, IR, runtime, and backend strategies that may vary.
- Open design questions: unresolved issues requiring explicit decisions before stabilization.

Unless a section is marked "Implementation Note" or "Open Question", its contents are intended to become normative.

## 2. Goals

ProtoLang should:

- Define portable methods over protobuf message types.
- Use protobuf schemas as the source of data types and message structure.
- Provide explicit cross-language semantics.
- Avoid relying on target-language-specific features.
- Compile through a typed intermediate representation before target-language emission.
- Support per-language translation backends.
- Be small enough that generated behavior can be audited and tested across languages.
- Produce predictable generated APIs for C#, C++, and future backends.
- Allow conformance testing from shared source and expected behavior vectors.

## 3. Non-Goals

ProtoLang does not aim to provide:

- Exceptions.
- Inheritance as a language feature.
- Private, protected, or internal methods.
- LINQ-style query syntax.
- Lambdas or anonymous functions.
- Operator overloading.
- Reflection-dependent semantics.
- Threads, locks, async/await, coroutines, or scheduling primitives.
- File, network, console, database, or other I/O.
- Target-language syntactic sugar.
- A replacement for `.proto` schemas.
- A general-purpose application programming language.

## 4. Terminology

Message:
: A protobuf message type defined in a `.proto` schema.

Field:
: A protobuf field belonging to a message.

ProtoLang method:
: A method defined by ProtoLang and associated with a protobuf message.

Receiver:
: The message instance a method operates on, equivalent to `this` or `self` in target languages.

Front end:
: The compiler components that produce the IR: lexer, parser, descriptor binding, name resolution,
  and type checking. The compiler-literature sense of the word, not the web one -- it has nothing to
  do with a user interface.

Backend:
: A compiler component that emits code for a specific target language. Everything after the IR.

IR:
: The typed intermediate representation produced after parsing, name resolution, and type checking.

Normative:
: Required behavior for conforming implementations.

Implementation-defined:
: Behavior that must be documented by each implementation or backend.

Unspecified:
: Behavior that programs must not rely on.

## 5. Source Organization

### 5.1 Files

ProtoLang source files use the extension:

```text
.protolang
```

Decision:

- The extension is `.protolang`. The CLI and tests use this extension, and path-based compilation
  treats the file path as a source identity rather than deriving semantics from any alternate suffix.

### 5.2 Relationship to `.proto`

A ProtoLang file imports one or more protobuf schema files directly.

Example:

```protolang
import proto "inventory.proto";

extend InventoryItem {
    fn total_value() -> int64 {
        return quantity * unit_price;
    }
}
```

Normative Requirements:

- Imports use `import proto "path/to/schema.proto";`.
- The path is resolved against compiler include paths, then against the source file's own directory.
- Well-known protobuf imports may be resolved by the descriptor loader's implicit include paths.
- A file with no `import proto` declaration does not reach binding (`PL0001`).
- ProtoLang does not define an independent package declaration. Message, enum, and field names come
  from protobuf descriptors.
- One file may import schemas whose descriptors contain multiple protobuf packages; each `extend`
  resolves its target message through the descriptor pool.

Open Questions:

- Should descriptor-set input exist in addition to direct `.proto` imports?
- Should ProtoLang ever be embedded directly in `.proto` files?

### 5.3 Compilation Unit

A compilation unit currently consists of:

- One ProtoLang source file.
- The protobuf descriptors referenced by those files.
- Compiler options.
- Backend target configuration.

Implementation Note:

- The `Compilation` object is internally shaped around a source set, but the public implementation
  still binds only one source document. Multi-file binding is intentionally not exposed yet.

## 6. Lexical Structure

This section defines the token-level syntax.

### 6.1 Character Set

- Source files are UTF-8.
- Identifiers use .NET character classification: the first character is `char.IsLetter` or `_`, and
  following characters are `char.IsLetterOrDigit` or `_`.
- String literals support `\n`, `\t`, `\r`, `\\`, and `\"`.
- A string literal ends at the closing quote or at the end of the line. Multi-line string literals
  are not supported.
- Numeric literals use invariant-culture parsing. Integer literals fit in signed 64-bit storage
  before contextual typing; floating literals contain a fractional part and have no exponent syntax.

### 6.2 Comments

```protolang
// line comment

/*
   block comment
*/
```

Normative Requirements:

- `//` starts a line comment.
- `/* ... */` starts a block comment.
- Block comments are not nested.
- An unterminated block comment is `PL0004`.

### 6.3 Identifiers

The implemented rule is:

```text
identifier = (.NET letter | "_") { .NET letter-or-digit | "_" }
```

Identifiers are case-sensitive. ProtoLang does not impose a naming convention on source names.
Backends may map method names to target conventions when emitting public APIs (24).

Open Question:

- Should source names be restricted to ASCII before language stabilization to avoid backend-specific
  identifier edge cases?

### 6.4 Keywords

Reserved keywords:

```text
and
as
arg
bool
break
bytes
case
continue
double
else
enum
expect
extend
fail
false
float
fn
for
has
if
import
in
int32
int64
message
not
on_zero
or
proto
receiver
return
string
switch
test
true
uint32
uint64
var
virtual
void
while
```

Open Question:

- `case`, `enum`, `message`, and `switch` are reserved by the lexer but do not yet have source
  syntax.

## 7. Grammar and Syntax

This section describes the grammar implemented by the parser. The grammar is still summarized rather
than mechanically exhaustive.

### 7.1 Implemented Grammar

```ebnf
source_file       = { import_decl | extend_decl | test_decl };

import_decl       = "import" "proto" string_literal ";";

extend_decl       = "extend" qualified_name "{" { method_decl } "}";

method_decl       = [ "virtual" ] "fn" identifier
                    "(" [ parameter_list ] ")"
                    [ "->" type_ref ]
                    block;

parameter_list    = parameter { "," parameter };
parameter         = identifier ":" type_ref;

block             = "{" { statement } "}";

statement         = var_decl
                  | return_stmt
                  | if_stmt
                  | while_stmt
                  | for_in_stmt
                  | break_stmt
                  | continue_stmt
                  | block
                  | assignment_stmt
                  | expression_stmt;

var_decl          = "var" identifier [ ":" type_ref ] "=" expression ";";
return_stmt       = "return" [ expression ] ";";
if_stmt           = "if" expression block [ "else" ( if_stmt | block ) ];
while_stmt        = "while" expression block;
for_in_stmt       = "for" identifier "in" expression block;
break_stmt        = "break" ";";
continue_stmt     = "continue" ";";
assignment_stmt   = expression "=" expression ";";
expression_stmt   = expression ";";

test_decl         = "test" qualified_name string_literal
                    "{" { receiver_fixture | test_arg | test_expectation } "}";
receiver_fixture  = "receiver" "{" { fixture_field } "}";
test_arg          = "arg" identifier "=" expression ";";
test_expectation  = "expect" ( "return" expression | "fail" ) ";";
```

Normative Requirement:

- The final grammar must be unambiguous.
- Backend code generation must not depend on parser quirks or target-language parsing.
- Semicolons are mandatory after imports, variable declarations, `return`, `break`, `continue`,
  assignment statements, expression statements, scalar fixture fields, test arguments, and test
  expectations.
- Top-level helper functions are not implemented.
- Variable declarations may state an explicit type or infer from the initializer.
- A test declaration must contain a receiver fixture and an expectation. The parser accepts
  `receiver`, `arg`, and `expect` members in any order and reports missing required members after
  the block is parsed.
- Parser recovery synthesizes missing tokens and missing names so later compiler stages can continue
  reporting useful diagnostics and editor tooling can still anchor completion points.

Open Questions:

- Should top-level helper functions be allowed in a later version?
- Should this section be replaced with exact EBNF generated from or checked against the parser?

## 8. Type System

### 8.1 Type Sources

Types come from:

- Protobuf scalar primitive types.
- Protobuf enum types.
- Protobuf message types.

Normative Requirements:

- ProtoLang does not define an independent application type system.
- ProtoLang value types are protobuf scalar primitives, protobuf enums, and protobuf messages.
- ProtoLang does not add non-protobuf numeric types such as `decimal`.
- `void` is a method return marker only; it is not a protobuf value type and cannot be used for fields, variables, or parameters.
- Repeated fields have a compiler type, `repeated <element>`, so they can be iterated, but there is
  no source syntax for declaring a repeated local or parameter.

Open Questions:

- Should type aliases be allowed if they resolve only to protobuf scalar, enum, or message types?
- Should helper/result types ever be allowed, or must error handling also be represented using protobuf-defined messages?

### 8.2 Protobuf Scalar Mapping

ProtoLang accepts all protobuf scalar spellings that name a value domain. Wire-encoding variants
collapse to the decoded value type:

| Protobuf spelling | ProtoLang type |
|---|---|
| `double` | `double` |
| `float` | `float` |
| `int32`, `sint32`, `sfixed32` | `int32` |
| `int64`, `sint64`, `sfixed64` | `int64` |
| `uint32`, `fixed32` | `uint32` |
| `uint64`, `fixed64` | `uint64` |
| `bool` | `bool` |
| `string` | `string` |
| `bytes` | `bytes` |

Normative Requirements:

- The spelling may be used in a ProtoLang type reference when protobuf defines it.
- Once decoded, `sint32`, `sfixed32`, and `int32` have the same ProtoLang type; likewise for the
  other encoding families above. Encoding is a schema concern, not a behavior-language concern.
- `bytes` is a valid type and field value type. The language currently has no bytes literal and no
  bytes-specific operators.
- Floating-point behavior is covered by the numeric rules in 10.

Open Question:

- Should version 1 add bytes literals or bytes-specific operations?

### 8.3 Non-Protobuf Types

Normative Requirements:

- ProtoLang does not support additional value types outside the protobuf type universe.
- `decimal` is not supported because Protocol Buffers do not define a decimal scalar type.
- Backends must not silently map a ProtoLang type to target-specific types such as C# `decimal`, Python `Decimal`, or arbitrary-precision numeric classes unless that value is represented by an explicit protobuf message type.

Implementation Note:

- Projects that need decimal-like behavior should define an explicit protobuf message, such as a fixed-scale money or decimal representation, and then define ProtoLang behavior over that message.

Open Questions:

- Should the standard library eventually provide recommended protobuf message shapes for common non-scalar concepts such as money, fixed-scale decimal, dates, durations, or UUIDs?

### 8.4 Nullability and Presence

**Decided: `has <field>` is syntax, and which fields answer it is protobuf's own question.**

Protobuf presence semantics differ between proto2, proto3, optional fields, messages, wrappers, and
repeated fields. ProtoLang does not re-derive those rules; it asks the descriptor.

```protolang
if has customer.email {
    return customer.email;
}
```

Normative Requirements:

- `has <field>` is a prefix expression of type `bool`. It binds at the same precedence as `not`, so
  `has a.b` tests `b` and `has a and has a.b` groups as written.
- Its operand must name a protobuf field. A local, a parameter, a literal, and a method result
  always hold a value, so there is no question to ask about them (`PL0080`).
- Asking about `a.b` reads `a`, so `a` is subject to 13.1 like any other read.
- A field admits the question exactly when protobuf says it has presence. That is one rule covering
  every case in the table below, rather than four rules the compiler could get out of step with a
  schema.
- `has` on a field with no presence is `PL0079`. It is not `false`: the field has no answer, and
  returning one would be a different question silently substituted for the one asked.

| Field kind | Presence | Unset reads as |
|---|---|---|
| Singular message | Yes, always | Nothing -- the read requires a guard (13.1) |
| proto3 singular scalar or enum | **No** | The type's zero, indistinguishable from a set zero |
| proto3 `optional` scalar or enum | Yes | The type's zero, distinguishable by `has` |
| proto2 singular field | Yes | The field's declared default |
| Repeated field | No | An empty collection |
| Map field | No | Not supported at all (14.2) |

- An `optional` scalar set to its zero value, or an `optional` string set to empty, is **set**.
  `has` reports presence, not difference from the default.
- Reading a scalar never needs a guard, under any syntax version. Both targets have always agreed
  about an unset scalar; only message fields diverged.

Open Question:

- `oneof` has presence per case as well as per field, and nothing here addresses the case
  discriminator. No syntax, no IR node, no diagnostic.

## 9. Expressions and Operators

### 9.1 Expression Categories

The language currently includes:

- Integer, floating-point, string, and boolean literals.
- Local, loop-binding, parameter, and implicit receiver field references.
- Field access through `.`.
- Method calls on message receivers.
- Arithmetic, boolean, and comparison expressions.
- Prefix field-presence checks with `has`.
- Explicit numeric conversions with `as`.
- Parenthesized expressions.

Not implemented:

- Top-level function calls.
- Indexing.
- Message literals in ordinary method bodies.
- Bytes literals.

### 9.2 Operators

Implemented operator set:

```text
+  -  *  /  %
== != < <= > >=
and or not
&& || !
has
=
```

`has` is a prefix operator on a field, producing `bool` (8.4). It sits at the same precedence as
`not`, and unlike every other operator its operand is a field rather than a value -- reading the
value is exactly what it must not do.

Normative Requirements:

- Both word and symbolic boolean operators are accepted: `and`/`&&`, `or`/`||`, and `not`/`!`.
- Assignment is a statement only.
- `%` is included and follows the same `on_zero` rule as integer `/`.

### 9.3 Evaluation Order

Normative Requirement:

- The evaluation order of expressions must be explicitly defined.
- Backends must preserve the specified evaluation order.

Current defined subset:

- Method call arguments evaluate left to right.
- Boolean `and` and `or` short-circuit left to right.
- Assignment evaluates the right-hand side before storing the result.

Open Question:

- Should all non-short-circuit binary operators evaluate the left operand before the right operand?
  This only becomes observable when an operand can terminate through `on_zero fail`.

## 10. Numeric Semantics

Numeric behavior is one of the highest-risk portability areas.

The decisions below are collected, alongside the presence rules and everything else the targets
disagree about, in [docs/reference-semantics.md](docs/reference-semantics.md). That table is where
the C# reference behavior for each operation is written down and where each backend's obligation to
reproduce it is stated. This section remains normative; the table is a reading aid.

### 10.1 Integer Overflow

**Decided: wrapping is the default.**

Normative Requirements:

- When the mathematical result of an integer operation falls outside the range of its type, the
  result is reduced modulo 2^N, where N is the bit width of the operand type.
- Operands of a binary arithmetic operator must already have the same type. ProtoLang applies no
  implicit numeric conversion, so overflow is always evaluated in a single, stated width.
- Backends **must** emit each arithmetic operation explicitly rather than relying on the target
  language's default behavior, **even where the target default already matches**. A default is a
  property of the consumer's build; ProtoLang semantics must be a property of the generated code.

The rationale for the third rule is that all three initial targets disagree, and two of them
disagree in ways that are silent:

| Target | Native `int64` overflow | Why the default is not enough |
|---|---|---|
| C# | Wraps. Integer arithmetic is unchecked unless opted in. | A consumer setting `CheckForOverflowUnderflow` in their `.csproj` silently converts wrapping into `OverflowException`. |
| C++ | **Undefined behavior** for signed types. | Not "wraps on most compilers": the optimizer may assume overflow never occurs and delete dependent code. |
| Python | Cannot overflow; integers are arbitrary precision. | Wrapping must be reconstructed by masking and sign-correcting. |

Backend obligations:

- **C#** emits `unchecked(...)` around `+`, `-`, `*`, and unary `-`.
- **C++** routes all integer arithmetic through helpers that perform the operation in the unsigned
  domain, where modular behavior is well-defined, and convert back. Requires C++20, where the
  conversion back to the signed type is defined as two's complement.
- **Python** must mask to the operand width and sign-correct.

Note that `unchecked` in C# does **not** cover division: `long.MinValue / -1` traps at the hardware
level regardless of context. `/` and `%` therefore require helpers in C# as well as in C++.

**Decided: the overflow rule is selectable per project, and wrapping is the default.**

`Arithmetic/Overflow` in `protolang.config.xml` (10.4) selects one of three answers. Every mode is
reproduced identically by every backend; no mode means "whatever this target does natively".

| Mode | Signed `+` `-` `*`, unary `-` | `MIN / -1` | `MIN % -1` |
|---|---|---|---|
| `Wrapping` (default) | Reduced modulo 2^N | `MIN` | `0` |
| `Checked` | Terminal failure: a diagnostic on standard error, then exit code 70, exactly as 10.2.1 | Terminal failure | `0` |
| `Saturating` | Clamps to the bound the true result exceeded | `MAX` | `0` |

The remainder is the same under every mode because `MIN % -1` is `0`, which every type can
represent. Only the quotient is unrepresentable, so only the quotient can fail. `Checked` fails when
the mathematical result does not fit, not when the instruction would trap.

Wrapping remains the default because it is what unmodified C# does. `checked` arithmetic is often
described as C#'s behavior, but a C# author reaches it only through the `checked` keyword or a
`CheckForOverflowUnderflow` build property.

Open Question:

- Whether a non-default behavior should also be declarable per file, per method, or per expression,
  rather than only per project. The typed IR already carries a per-operation annotation, so the
  plumbing exists; what is missing is a syntax worth having.

### 10.2 Division

Defined behavior:

- Integer division truncates toward zero.
- Floating-point division follows IEEE 754: `x / 0.0` is `±inf`, `0.0 / 0.0` is `NaN`. No
  declaration is required, because the operation cannot fail.
- Signed division overflow (`MIN / -1`, `MIN % -1`) wraps per 10.1: the results are `MIN` and `0`.
- **Integer division by zero is not left to the target. The author must state what happens.**

### 10.2.1 The `on_zero` clause

Integer `/` and `%` require an `on_zero` clause. It takes one of two forms: a fallback value, or
`fail`.

```protolang
// A zero divisor has a sensible answer here.
fn mean_item_cents() -> int64 {
    return total_cents() / item_count() on_zero 0;
}

// A zero divisor means the caller handed us something impossible. Stop.
fn strict_rate() -> int64 {
    return counts / live_time on_zero fail;
}
```

Normative Requirements:

- Integer `/` and `%` are a compile error (`PL0054`) without an `on_zero` clause, unless the divisor
  is a literal that is provably non-zero.
- `on_zero <expression>` substitutes that value. It must already have the type the division
  produces; no implicit conversion is applied (10.3).
- `on_zero fail` terminates the program deterministically, with a diagnostic naming the operation
  written to standard error. It is not catchable and not recoverable. The process exit code is
  **70** (`EX_SOFTWARE`) in every backend.
- A backend must terminate with a primitive that cannot be intercepted and does not engage the
  platform's crash reporting: `Environment.Exit` in C#, `std::_Exit` in C++. `Environment.FailFast`
  and `std::abort` are the wrong tools even though they look like the obvious ones. Both are
  crash-reporting primitives, so on Windows they hand the process to Windows Error Reporting and to
  any postmortem debugger registered under `AeDebug`; generated library code must not be able to put
  a dialog on a user's screen or stall a batch run waiting for one. `abort` is also weaker than it
  appears: it raises `SIGABRT`, which a program may catch and resume from, defeating the clause.
- The clause binds to the single division it follows: `x + a / b on_zero 0` means
  `x + (a / b on_zero 0)`. The fallback parses at unary precedence, so anything more involved than a
  literal, name, or call must be parenthesized.
- `on_zero` is rejected on any other operator, and on floating-point division, where it is
  meaningless (`PL0015`).
- Backends emit a runtime zero check for every integer division except the proven-literal case.

`fail` is deliberately blunt. A catchable exception would let a consumer resume from a state the
author explicitly said has no valid result, and C++ has no equivalent construct under the
free-function design in 24.2. Termination is the only failure mode that means the same thing in
every target, which is the whole point of the section.

This is also why ProtoLang does not need `Result` in order to ship. `Result` remains the better
long-term answer for recoverable failure, but it forces propagation syntax into every expression
containing a division, and that cost multiplies with each new backend rather than amortizing. The
two `on_zero` forms cover the cases that actually arise: there is a sensible substitute, or there
is not.

Rationale: leaving this to the target is not a portability compromise, it is three different
programs. The same source produces a `DivideByZeroException` in C#, a `SIGFPE` crash on x86 in C++,
and a **silent zero** on ARM, where `SDIV`/`UDIV` by zero returns 0 rather than trapping. Requiring
the author to state the behavior costs one clause and removes the divergence entirely.

The literal exception exists so that `count / 2` does not demand a fallback for a branch that can
never be taken; no runtime check is emitted in that case.

Open Questions:

- Should division by zero be a compile-time error when the divisor is a statically known zero
  expression rather than a literal? Today only literal `0` is caught, and only because it fails the
  proven-non-zero test rather than by any dedicated analysis.
- `on_zero fail` gives generated library code the ability to terminate the host process. That is
  intentional, but it is a larger capability than anything else the language permits (20), and a
  server embedding ProtoLang behavior has no way to opt out.
- Python's `/` produces a float and `//` floors, so neither maps to truncating division, and Python
  raises on float division by zero rather than yielding `inf`. The Python backend will need explicit
  helpers for both.

### 10.3 Numeric Conversions

**Decided: no implicit conversions, and an explicit `as` operator.**

Normative Requirements:

- There are **no** implicit numeric conversions. Both operands of a binary arithmetic or comparison
  operator must already have the same type, and a returned value must already have the declared
  return type. This is what makes the overflow rule in 10.1 well-defined: the width the result wraps
  to is never the product of a promotion the author did not write.
- Integer literals are the single exception: a literal adopts the expected type at its use site when
  the value fits, so `var total: int64 = 0;` needs no suffix or conversion.
- An explicit conversion is written `<expression> as <type>`.

```protolang
extend Order {
    // quantity is int32 and unit_price_cents is int64, so one of them has to move.
    fn line_total_cents() -> int64 {
        return quantity as int64 * unit_price_cents;
    }
}
```

- `as` binds tighter than every binary operator and looser than a prefix operator, so
  `a as int64 * b` is `(a as int64) * b`, and `-a as int32` negates in the source type and converts
  the result. Conversions chain left to right.
- The operand of a conversion carries no type expectation into itself. An integer literal in that
  position takes its natural `int64` and a floating-point literal its natural `double`, so
  `3000000000 as int32` is a narrowing conversion that wraps rather than a literal reported as out
  of range.
- Both the source and the target must be numeric scalar types: the four integer types, `float`, and
  `double`. Anything else is `PL0075`, including `bool`, `string`, `bytes`, messages, and enums.
  Whether an enum can convert to or from an integer is left open in 12, and proto3's open enums make
  the reverse direction a question of its own.
- A conversion to the type a value already has is permitted and produces the value unchanged. It
  states nothing new, but it is not an error either.

Conversion behavior:

| Conversion | Result |
|---|---|
| integer to integer | The low bits: the value reduced modulo 2^N, where N is the target width. Consistent with 10.1, and the same rule whether or not signedness changes. |
| integer to floating point | Rounded to nearest, ties to even. |
| `float` to `double` | Exact. |
| `double` to `float` | Rounded to nearest, ties to even; a magnitude too large to represent becomes an infinity. |
| floating point to integer | Truncated toward zero, consistent with 10.2's division rule. A value outside the target's range clamps to that bound rather than wrapping, and NaN becomes zero. |

The last row is the one that costs something, and the reason it is stated rather than inherited is
that no two targets agree:

| Target | Native out-of-range float to integer | Why the default is not enough |
|---|---|---|
| C# | Unspecified by the language. Saturates on current .NET, but throws under a checked context. | A consumer setting `CheckForOverflowUnderflow` converts a deliberate conversion into an `OverflowException`, and the language guarantees nothing about the unchecked result. |
| C++ | **Undefined behavior.** | Not "whatever the hardware does": the optimizer may assume the value is in range. |
| Python | Floors rather than truncating, and raises on NaN. | Neither the rounding direction nor the failure mode matches. |

Saturation is chosen over wrapping because it is total, cheap to check, and produces the answer a
reader expects from a value that is simply too large; wrapping a magnitude beyond 2^64 is also not
meaningfully defined without arbitrary-precision arithmetic that no target has.

Backend obligations:

- **C#** emits `unchecked((T)x)` for integer targets, so neither the wrapping nor a consumer's
  compiler flags are in question. Conversions producing a floating-point type are fully defined in
  C# and unaffected by checked context, so a plain cast states everything. Floating point to integer
  routes through a runtime helper that clamps explicitly.
- **C++** emits `static_cast<T>(x)` for integer targets and for widening to floating point: C++20
  defines the conversion to a signed type as two's complement (P0907R4), which the generated runtime
  header already asserts. The two directions C++ leaves undefined -- floating point to integer, and
  `double` to `float` -- route through runtime helpers. The `double` to `float` threshold is the
  smallest magnitude that rounds to infinity rather than `FLT_MAX`, because doubles between the two
  round down to `FLT_MAX`.
- **Python** will need helpers for every row: it has no fixed-width integers, and its float to
  integer conversion floors.

Each conversion carries a behavior annotation in the typed IR, resolved by a single compile-time
policy rather than hard-coded at each site, so the alternatives in the open question below are a
front-end change and a backend change with no new plumbing.

`Arithmetic/Conversion` in `protolang.config.xml` (10.4) names this behavior. It has one legal
value today, `WrapOrSaturate`, which is the table above. It is stated rather than left implicit so
that the whole language-dependent contract is readable in one file, and so that a second value is
an addition rather than a discovery.

Open Question:

- Whether a second conversion behavior is worth having. A checked conversion -- terminating rather
  than clamping, matching 10.1's `Checked` -- is the obvious candidate, and nothing above rules it
  out.

### 10.4 Compile-Time Policy

**Decided: language-dependent preferences live in a repository-tracked file, and the file wins.**

Some questions in this specification have more than one defensible answer, and which one a project
wants is a property of the project rather than of the language. Those answers live in
`protolang.config.xml`, next to the code they govern.

```xml
<?xml version="1.0" encoding="utf-8"?>
<ProtoLang>
  <Arithmetic>
    <Overflow>Wrapping</Overflow>            <!-- Wrapping | Checked | Saturating (10.1) -->
    <Conversion>WrapOrSaturate</Conversion>  <!-- WrapOrSaturate (10.3) -->
    <DivideByZero>RequireOnZero</DivideByZero><!-- RequireOnZero (10.2.1) -->
  </Arithmetic>
  <Presence>
    <UnsetMessageRead>RequireGuard</UnsetMessageRead><!-- RequireGuard (13.1) -->
  </Presence>
</ProtoLang>
```

Normative Requirements:

- The compiler searches for `protolang.config.xml` in the source file's directory and every
  directory above it, nearest first, the way `.editorconfig` is found. A project states its policy
  once; a subdirectory may state a different one.
- A setting absent from the file takes its default. A file absent entirely is the same as a file
  stating nothing.
- Values are matched exactly, including case. An unknown element (`PL2001`), an unknown value
  (`PL2002`), a malformed file (`PL2003`), or a setting stated twice (`PL2004`) is an error, and the
  compilation stops. A project that states a policy and is then silently ignored is worse off than
  one that states nothing.
- **The file wins.** A command-line flag that contradicts a setting the file states is refused, not
  applied. An explicit override flag lifts the refusal, so trying another policy stays one command
  away while leaving a trace nobody can mistake for the project's own answer.
- A flag may set what the file does not state, since a default left in place is not an answer the
  project gave.
- Every mode of every setting must produce identical observable behavior in every backend. A mode
  must never mean "use whatever this target does", unless that target behavior has been specified
  here and reproduced everywhere else.
- **Every generated file states the policy it was produced under, in its header.** The settings that
  shape the emitted code are named there, so a reader can tell why the code in front of them does
  what it does without re-running the compiler to find out. Every backend states the same facts
  about the same build, and no path is included: an absolute path would make otherwise identical
  output differ between machines.

Settings with a single legal value are listed anyway. The file's purpose is to enumerate every
language-dependent preference, including the settled ones, so the whole contract is readable in one
place. `docs/reference-semantics.md` is the companion table: what each value means in each backend,
and which of them are C#'s own behavior rather than something ProtoLang invented.

Open Questions:

- Whether a language version (27.1) belongs in this file rather than in each source file.
- Whether a backend may add settings of its own, and if so how a third-party backend's settings
  avoid colliding with the language's.

## 11. Strings

### 11.1 String Model

Strings are Unicode text corresponding to protobuf `string`. The current language supports string
literals, assignment to string-typed locals, return values, parameters, field reads, and equality
or inequality against another string value of the same type.

Normative Requirements:

- No locale-sensitive operations are available.
- Ordered string comparison is not supported.
- String indexing, length, case conversion, and normalization operations are not implemented.

Open Questions:

- Should string indexing be supported at all?
- Should normalization be specified?
- Should string equality be specified in terms of Unicode scalar values, UTF-16 code units, or a
  protobuf-runtime guarantee?
- Should string comparison beyond equality be supported?

## 12. Enums

**Decided: enum types and enum values are both named through the protobuf type universe.**

An enum type can be named wherever a type is expected, and an enum value is named
`<enum type>.<VALUE_NAME>`:

```protolang
extend Order {
    fn is_shipped() -> bool {
        return status == OrderStatus.SHIPPED;
    }

    fn shipped() -> OrderStatus {
        return OrderStatus.SHIPPED;
    }
}
```

Normative Requirements:

- Both the type and the value are resolved by full name or by an unambiguous simple name, including
  enums nested in messages. A simple name matching more than one type is `PL0074`; a name that is
  not a value of the named enum is `PL0076`.
- The value name is the one the `.proto` file declares, exactly as written. ProtoLang does not
  re-spell it, even though both backends do.
- A name that is in scope as a value wins over an enum type spelled the same way, so
  `something.field` stays a field access. Adding an enum to a schema must not silently change what
  an existing expression means.
- Enum values are ordinary expressions, so they are equally available in a `test` fixture and in an
  expectation. A fixture sets an enum field from a named value rather than from a nested block,
  which is reserved for message fields.
- Enums compare only for equality. Ordered comparison is rejected, because the numbers behind the
  values are a wire detail rather than a ranking the schema author asked for.

Backend obligations, because the two targets name a value in unrelated ways and neither spelling is
derivable from the other:

| ProtoLang | C# | C++ |
|---|---|---|
| `TopLevelStatus.TOP_LEVEL_STATUS_OK` | `TopLevelStatus.Ok` | `TOP_LEVEL_STATUS_OK` |
| `Outer.Nested.NESTED_SOME` | `Outer.Types.Nested.Some` | `Outer_Nested_NESTED_SOME` |
| `Outer.Inner.Deep.DEEP_NONE` | `Outer.Types.Inner.Types.Deep.None` | `Outer_Inner_Deep_DEEP_NONE` |

- **C#** strips the enum's own name from the front of the value, ignoring case and underscores, and
  PascalCases what is left. A value that does not carry the prefix keeps its whole name, and one
  where stripping would leave a leading digit gains an underscore. Backends must reproduce this
  exactly rather than approximate it: a near-miss names an identifier that does not exist, which
  fails in the consumer's build rather than in this compiler.
- **C++** keeps the declared spelling but places values at namespace scope, prefixing a nested
  enum's values with the flattened enum type name and leaving a top-level enum's values bare.
  protoc also emits a `static constexpr` member on the containing class, but the namespace-scope
  constant is the one every enum has.
- protobuf C++ enums additionally carry `_INT_MIN_SENTINEL_DO_NOT_USE_` values that are not part of
  the schema and must never be emitted. They will matter again for `switch`.

Open Questions:

- Should enum exhaustiveness be checked? proto3 enums are open -- a field may legally hold a number
  with no declared value -- so no switch over one is exhaustive at runtime regardless of the schema.
- Should unknown enum values be representable?
- Should an enum be convertible to or from an integer? 10.3 rejects the conversion for now.

## 13. Messages

### 13.1 Field Access

```protolang
customer.name
order.customer.address.city
```

**Decided: using the value of a singular message field requires established presence.**

This is the one place the initial backends disagreed silently. Reading an unset `Timestamp` field
raises `NullReferenceException` in C# and returns the default instance -- so, zero -- in C++. Both
are the correct idiomatic translation for their runtime. Neither can be made to match the other
without a runtime check in every target, so the situation is made unrepresentable instead, which is
the same choice `on_zero` makes for a zero divisor (10.2.1).

Normative Requirements:

- Using the **value** of a singular message-typed field is an error (`PL0078`) unless its presence
  has been established on every path reaching the use.
- "Using the value" is reading a field through it, calling a method on it, passing it as an
  argument, or binding it to a local. Each launders the same divergence, so the rule is stated once
  about the value rather than four times about its uses.
- Presence is established by `has` (8.4), in any of these shapes:
  - inside `if has f { ... }`;
  - in the `else` of `if not has f { ... } else { ... }`;
  - after `if not has f { return ...; }`, or any guard whose branch cannot complete normally;
  - in the right operand of `and` when the left proved it, and after `or` on the false side.
- A fact, once established, holds for the remainder of the method. ProtoLang cannot assign to a
  field (18), so nothing shown to be set can become unset. A guard before a loop therefore holds
  inside it.
- A message field reached through a value that has no name -- a method result -- cannot be guarded,
  and is `PL0078`. Binding the intermediate to a local first gives it the name a guard needs.
- The receiver, parameters, locals, and `for` bindings are present by construction and are never
  guarded. Every message value in the language comes from one of those or from a guarded read.
- Reading a **scalar** field never requires a guard. An unset proto3 scalar reads as the type's
  zero, an unset proto2 scalar as its declared default, and both targets have always agreed.
- Reading a **repeated** field never requires a guard. An unset one is empty.
- Because the guard is a compile-time requirement, a guarded read emits the plain accessor chain in
  every backend. The rule costs nothing at runtime.

`Presence/UnsetMessageRead` in `protolang.config.xml` (10.4) names this behavior. It has one legal
value today, `RequireGuard`.

Open Questions:

- Oneof fields, which have a case discriminator this says nothing about.
- Map fields, which are not supported at all (14.2).

### 13.2 Message Construction

Open Questions:

- Should ProtoLang be able to create new message instances?
- Should object initializer syntax exist?
- Should construction be limited to backend helper APIs?

### 13.3 Equality

Open Questions:

- Does message equality mean identity, field-wise equality, or backend-defined equality?
- Is deep equality part of version 1?
- Should the binder reject equality on message and repeated values until this is settled? The
  current type rule permits equality for operands of the same type, while the semantic meaning for
  messages and repeated collections is not specified here.

## 14. Repeated Fields and Collections

### 14.1 Supported Operations

Implemented operation set:

- Iterate in order with `for <name> in <repeated expression> { ... }`.
- Read the repeated field as a collection value for iteration.

Not implemented:

- Length.
- Indexing.
- Append.
- Clear.
- Element assignment.

Example syntax:

```protolang
var total: int64 = 0;

for item in invoice.items {
    total = total + item.amount;
}

return total;
```

Normative Requirement:

- `for` may iterate only a protobuf repeated field value. Iterating anything else is `PL0033`.
- Iteration order is the protobuf repeated-field order.

Open Questions:

- Should filtering, mapping, sorting, or aggregation helpers exist? `No. Basics only. ~IS`
- Should repeated field mutation ever be allowed? Current implementation says no by absence: only
  locals can be assigned.
- Should collection indexing be bounds-checked with explicit error results if indexing is added?
  `Ugh... probably. I really want to say no, but... I have a feeling that not doing this could lead to security concerns in some language or another. ~IS`

### 14.2 Maps

Protobuf maps are repeated key-value structures with language-specific APIs.

Current Status:

- Maps are not supported. A map field is rejected rather than treated as an ordinary repeated
  key-value message.

Open Questions:

- Is map iteration order specified or explicitly unspecified?
- What map operations are portable?

## 15. Control Flow

### 15.1 Conditional Statements

```protolang
if condition {
    ...
} else if other_condition {
    ...
} else {
    ...
}
```

Normative Requirements:

- The condition is an expression of type `bool`. There is no truthiness: a numeric, string, or
  message value in condition position is a diagnostic (PL0071), not a shorthand for a comparison.
- The condition is unparenthesized and every branch is braced. There is no single-statement form,
  so no statement can dangle off an `if`.
- An `else` binds to the nearest unmatched `if`. `else if` is a chain rather than a block
  containing a nested `if`, and generated code preserves that shape.
- A method that declares a return type must not be able to reach the end of its body. An `if`
  guarantees that only when it has an `else` and every branch guarantees it.

### 15.2 Loops

The current implementation has two loop forms:

```protolang
while condition {
    ...
}

for item in collection {
    ...
}
```

Normative Requirements:

- A `while` condition is an expression of type `bool`, under the same rule as 15.1.
- `for` iterates a protobuf repeated field in field order (14).
- `break` exits the innermost enclosing loop and `continue` advances it to its next iteration.
  Either one outside a loop is a diagnostic (PL0072, PL0073).
- The compiler performs no termination analysis. `while true` is legal, and a method whose only
  exit is a `return` inside `while true` satisfies the missing-return check, because control
  cannot reach the end of the body. A `break` that can leave that loop makes the end reachable
  again, and the method then needs a return after it.

Open Questions:

- ~~Should numeric `for` loops exist?~~ Decided: yes. `Yes. ~IS` Not yet implemented; `for`-`in`
  remains the only `for` form.
- ~~Should `break` and `continue` be supported?~~ Decided: yes, and implemented. `Yes. ~IS`
- ~~Should loops require static termination checks?~~ Decided: no. `No. ~IS`

### 15.3 Switch

Open Question:

- Should `switch` be included, or should version 1 use only `if` / `else if`? `Yes.  Support switch, including enums. ~IS`

## 16. Methods

### 16.1 Method Attachment

Methods are attached to protobuf message types using `extend`.

```protolang
extend DetectorReading {
    fn calculate_rate() -> double {
        return counts / live_time on_zero fail;
    }
}
```

Normative Requirements:

- All ProtoLang-defined methods are public.
- Method behavior must not depend on target-language inheritance.
- Method names share a namespace with protobuf fields on the receiver. A method whose name collides
  with a field is `PL0023`.
- Overloading is not supported. Two methods with the same name on the same receiver are `PL0022`,
  even if their parameter lists differ.
- A method whose declaration is syntactically incomplete may still be bound into a partial semantic
  model for editor use, but it is not callable when it lacks a usable declaration name.

Open Questions:

- Should methods ever be allowed to mutate the receiver?

### 16.2 Parameters and Returns

Normative Requirements:

- Parameters are named and typed: `name: type`.
- `void` is allowed only as an omitted or explicit method return type. It is not allowed for
  parameters or variables (`PL0024`).
- Methods have either one return value or no return value. Multiple return values are not supported.
- A non-void method must return a value on every path that can reach the end of the body.
- A void method may use `return;`.
- Parameters are not assignable. The only assignment target currently supported is a local variable.

Open Questions:

- Should recoverable errors be represented through user-defined protobuf messages, a future standard
  library `Result`, or some other convention?

## 17. Virtual and Override Semantics

ProtoLang parses `virtual`, but the implemented backends reject virtual methods.

### 17.1 Design Principle

Current Status:

- `virtual` is accepted by the parser and carried in the IR.
- The C# and C++ backends reject virtual methods at compile time because portable override semantics
  are not defined.
- `virtual` does not define a ProtoLang subclass model.
- ProtoLang source cannot declare subclasses.
- Generated protobuf message subclasses are forbidden as a portability strategy.

```protolang
extend DetectorReading {
    virtual fn overridable_count_rate() -> int64 {
        return counts / live_time on_zero fail;
    }
}
```

Possible future shape. Overridable functions cannot behave like classical virtual functions:
protobuf compiles into sealed classes in C# so inheritance is impossible. They would need something
like this by necessity:

```csharp
	public partial class DetectorReading{
	public delegate double DetectorReadingCountRateOverride();  //We can also just use Func<double> here.
	public static DetectorReadingCountRateOverride CountRateOverride {get;set;} //Note: Proto files are nullable agnostic
	public double RealTime {get;set;}
	public double CountRate(){
		if(CountRateOverride != null)
			return CountRateOverride();
		else
			return counts / RealTime;
	}
}
```

`That said, I'm really not convinced they're a good idea at all. Again, we're writing this because *we don't want to have more than 1 source of truth for behavior*.  Virtual functions are antithetical to that.  But they might be a necessary workaround for some people in some scenarios- I just don't know what they might be. ~IS`

### 17.2 Backend Strategies

Possible C# strategies:

- Generate a static, settable delegate field in the class.  
- Generate partial method hooks.
- Generate an adapter/wrapper class.

Possible C++ strategies:

- Generate free functions plus overridable policy objects.
- Use protobuf generator insertion points where appropriate.
- Generate wrapper/adaptor classes rather than subclass protobuf messages.

Possible Python strategies:

- Generate regular methods.
- Generate mixins or monkey-patch registration helpers.
- Generate wrapper classes.

Open Questions:

- Is `virtual` part of version 1?  `I think yes.  But it's low on the list. We're doing this because we DON'T want to write functions for every language. ~IS`
- Must all backends support virtual behavior, or may some reject it? `If we're going to do it... we should do it for every language. All languages should be able to support something like this, even if it's not explicitly supported. ~IS`
- Should there be a portable override registration mechanism? `Out of scope for v1. ~IS`

## 18. Mutability

The current language is read-only with respect to protobuf data. It can mutate local variables only.

Normative Requirements:

- Methods may read receiver fields.
- Methods may read fields through parameters, locals, loop bindings, and other message-valued
  expressions subject to the presence rule in 13.1.
- Methods may assign local variables declared with `var`.
- Methods may not assign receiver fields, nested message fields, repeated fields, parameters, or
  loop bindings. An assignment target that is not a local is `PL0034`.
- Methods cannot allocate new protobuf messages in ordinary method bodies.

Open Questions:

- Should receiver mutation be added later, and if so should read-only or mutable behavior be the
  default? `I lean toward no.  Methods can mutate receiver members by default. They should be declared const if they're to be read only. ~IS`
- Should mutation require an explicit marker? `No, but const methods should. ~IS`
- Should immutable and mutable methods generate different APIs?

## 19. Error Handling Without Exceptions

ProtoLang has no exceptions. The implemented failure model is:

- Compile-time rejection where portability cannot be guaranteed.
- `on_zero <fallback>` for recoverable integer division and modulo by zero.
- `on_zero fail` for deterministic terminal failure with exit code 70.

No built-in `Result` type, generated status type, or catchable runtime error model exists today.

Open Questions:

- Does ProtoLang define a built-in `Result` type? `I think we should- it doesn't have to be used, but it should be there for ease of use. ~IS`
- Are arithmetic errors expressible in the type system? `Yes, it should be a fairly expansive enum. ~IS`
  Note: overflow is no longer one of them. Since 10.1 defines overflow as wrapping, it is a defined
  result rather than a failure, and nothing about it needs to reach the type system.
- Can methods be declared as total, meaning they cannot fail at runtime? `I lean toward no, but this might be a nice optimization at some point. ~IS`
- How do target backends map error results idiomatically while preserving semantics?  `Error handling is heavily on the user to define; we'll provide the Result type with primitive error enums for primitive "exceptions" such as dividing by zero. More advanced stuff?  Users can (and should!) tailor that to their specific needs. ~IS`

## 20. I/O, Threading, and Side Effects

Normative Requirements:

- ProtoLang has no I/O primitives.
- ProtoLang has no threading or concurrency primitives.
- ProtoLang has no clock, randomness, environment, filesystem, network, console, or process APIs.
- Backends must not silently introduce observable I/O or concurrency behavior into generated method bodies.
- Generated terminal-failure paths are the exception: `on_zero fail` writes a diagnostic to standard
  error and terminates the process as specified in 10.2.1.
- ProtoLang methods may call only ProtoLang methods resolved by the compiler.

Open Questions:

- Should deterministic pure helper functions be explicitly marked? `Seems unnecessary. ~IS`

## 21. Interoperability With Protobuf

### 21.1 Descriptor Input

The compiler consumes protobuf descriptors produced from the `.proto` files named by `import proto`
declarations. The default descriptor loader invokes `protoc` with include paths and asks for
transitive imports.

Implementation Note:

- This aligns ProtoLang with `protoc`, Buf, and plugin-based code generation workflows.
- `CompilationResult.Imports` records every import declaration and whether it resolved, was not
  found, or was syntactically unwritten. Descriptor-load failures preserve this resolved-import list
  rather than replacing it with an empty one.
- A descriptor load produces the whole of what `protoc` emitted, not only the descriptors built from
  it: the `FileDescriptorSet` with the source info `--include_source_info` requests, and the file
  each schema in the transitive closure was read from. `CompilationResult.Schema` carries it. Source
  info is where a schema's declaration sites and doc comments live, so discarding it meant paying
  `protoc` to produce the one thing the compiler then threw away.
- A descriptor-load failure preserves `protoc`'s own report line by line, with the file and position
  each line names kept separate from its message, rather than only as prose inside a `PL0003`
  message. Publishing a schema error against the schema is only possible if that structure survives.
- Loading may be cached. Correctness is defined against the located `protoc`, the ordered include
  paths, and the content of every file in the transitive closure -- not against the files the
  compilation named, which do not determine the result. Caching is never observable: a cached load
  produces what a cold load would have produced, and a load that failed is not cached at all.

Open Questions:

- Should the compiler run as a `protoc` plugin?  `Eventually. I don't think it's necessary right away, but should happen before v1. ~IS`
- Should it also support Buf plugin workflows?
- Should descriptor-set input be accepted directly, and how would it report per-import diagnostics?

### 21.2 Generated Code Integration

The current implementation defines:

- C# behavior is emitted as extension methods in generated static classes.
- C++ behavior is emitted as header-only free functions in the protobuf namespace.
- Target method names are mapped by the backend's protobuf naming convention helpers.
- Namespace/package mapping follows the generated protobuf target's conventions.

Open Question:

- Python and any future backend must define its generated API shape before it can be conforming.

### 21.3 Protobuf Editions and Syntax Versions

**Decided: proto2, proto3, and editions are all supported, and the compiler does not branch on the
version.**

The version mattered because presence rules differ by it. Once presence is a first-class question
(8.4), the compiler asks the descriptor rather than the syntax version, and
`FieldDescriptor.HasPresence` answers correctly for every one of them -- including editions, where
presence is a resolved feature rather than a property of the syntax line. A version check would be
a second, worse copy of a rule the protobuf runtime already implements.

Both non-proto3 cases were checked rather than assumed: protoc 31.1 generates C# for a proto2 file
and for an `edition = "2023"` file. The C# generator historically refused proto2, which is the only
reason this was ever in doubt.

Implementation Note:

- `FileDescriptor.Syntax` is deprecated in current protobuf runtimes, and its deprecation note says
  to use feature resolution instead. That is exactly what `HasPresence` consults, so taking the
  version out of the compiler follows the runtime's own advice rather than merely avoiding a
  warning.

Open Question:

- Whether a future edition could change a rule this specification states, and how the compiler would
  notice. Nothing here reads edition-specific features other than through `HasPresence`.

## 22. IR and Compiler Architecture

This section is implementation-facing, not source-language syntax.

### 22.1 Implemented Pipeline

```text
ProtoLang source
    -> lexer/parser with recovery
    -> syntax tree
    -> import resolution
    -> protobuf descriptor loading
    -> binder: descriptor binding, name resolution, and type checking
    -> typed IR
    -> backend code generation
    -> target-language source
    -> target-language tests/conformance
```

Pipeline gates:

- Configuration-file errors stop before lexing or binding.
- Parse errors do not stop binding. The parser recovers and the binder produces a partial semantic
  model where it has descriptors to bind against.
- Unusable include paths, zero imports, unwritten imports, unresolved imports, missing default
  descriptor loader, and descriptor-load failures stop before binding.
- `CompilationResult.Module` is the partial semantic model. `CompilationResult.EmittableModule` is
  non-null only when the module exists and there are no errors.

### 22.2 Typed IR Requirements

The IR preserves:

- Source locations for diagnostics.
- Declaration sites and stable symbol identities for ProtoLang-declared methods, parameters, locals,
  and loop bindings.
- Descriptor identities for protobuf fields, enum values, message types, and enum types.
- Resolved protobuf type references.
- Exact numeric operation kinds.
- Presence checks. `IrFieldPresence` carries the field descriptor rather than a lowered boolean,
  because the two targets spell the test in unrelated ways.
- Field access semantics.
- Local assignment intent.
- Terminal-failure behavior for `on_zero fail`.
- Evaluation order.
- Virtual/overridable annotations.
- Error placeholder nodes and types so one failed bind does not necessarily suppress later useful
  diagnostics.

Open Questions:

- Should the IR be serialized as JSON, protobuf, or an internal compiler structure?
- Should backends consume a stable IR format?
- Should third-party backends be supported in version 1?

## 23. Backend Conformance Requirements

A backend is conforming if it:

- Accepts the typed IR format supported by the compiler version.
- Emits target-language code preserving normative ProtoLang semantics.
- Documents all implementation-defined behavior.
- Passes the shared conformance test suite for supported features.
- Rejects unsupported ProtoLang features at compile time.
- Does not silently change numeric, presence, collection, or error semantics.

### 23.1 Backend Feature Matrix

Status as of the first working compiler. "No" means the backend rejects the feature at compile
time rather than emitting something whose semantics differ.

| Feature | C# | C++ | Python | Notes |
|---|---:|---:|---:|---|
| Attached methods | Yes | Yes | — | C#: extension methods. C++: free functions. |
| Wrapping integer arithmetic | Yes | Yes | — | `unchecked(...)` / unsigned round-trip helpers. The default policy. |
| Checked integer arithmetic | Yes | Yes | — | Terminates with exit code 70 on overflow. Selected by `Arithmetic/Overflow` (10.4). |
| Saturating integer arithmetic | Yes | Yes | — | Clamps to the exceeded bound. Selected by `Arithmetic/Overflow` (10.4). |
| Compile-time policy file | Yes | Yes | — | `protolang.config.xml`, found by walking up from the source (10.4). |
| Checked division (`on_zero`) | Yes | Yes | — | Runtime zero check in both; see 10.2.1. |
| `on_zero fail` | Yes | Yes | — | `Environment.Exit(70)` / `std::_Exit(70)`, after a diagnostic on stderr (10.2.1). |
| IEEE 754 float division | Yes | Yes | — | Native in both. Python will need a helper. |
| Repeated iteration | Yes | Yes | — | `foreach` / range-`for` over the protobuf container. |
| Cross-message method calls | Yes | Yes | — | C++ emits all declarations before any definition. |
| Local variables and assignment | Yes | Yes | — | Only locals can be assigned. |
| Mutable methods | No | No | — | Blocked on the open question in 16.1. |
| Virtual methods | No | No | — | Blocked on 17; both backends reject. |
| Maps | No | No | — | Blocked on 14.2. |
| Result/error returns | No | No | — | Blocked on 19. |
| Explicit casts | Yes | Yes | — | `x as int64`; see 10.3 for the per-family rules. |
| Enum types and values | Yes | Yes | — | Named per 12; both targets re-spell values differently. |
| Conditionals and `while` | Yes | Yes | — | `if` / `else if` / `else`, `while`, `break`, `continue` (15). |
| Field presence (`has`) | Yes | Yes | — | `x != null` or `HasX` in C#; `has_x()` in C++ (8.4). |
| Unset message-field guard | Yes | Yes | — | Compile-time (`PL0078`), so neither backend emits a runtime check (13.1). |
| Proto2 presence | Yes | Yes | — | Via `FieldDescriptor.HasPresence`; no syntax-version branch (21.3). |
| Proto3 optional | Yes | Yes | — | Same mechanism. |
| Editions | Yes | Yes | — | Same mechanism; presence is a resolved feature (21.3). |
| Oneof | No | No | — | Blocked on the open question in 8.4. |
| ProtoLang `test` declarations | Yes | Yes | — | Both backends emit generated tests. |
| Test project scaffolding | Yes | Yes | — | C# `.csproj`; C++ `CMakeLists.txt`. |
| Partial semantic model after parse errors | Yes | Yes | — | Front-end feature; emitters use only `EmittableModule`. |
| Declaration sites and symbol IDs | Yes | Yes | — | Front-end/IR feature for editor tooling and stable references. |

## 24. Generated API Strategy

This section captures backend-specific integration choices.

### 24.1 C#

Potential strategies:

- Partial classes.
- Extension methods.
- Generated companion classes.
- Wrapper/adaptor classes.

**Decided for the current implementation: extension methods**, in a
`{Message}ProtoLangExtensions` static class per receiver. This imposes nothing on the protobuf
codegen: the generated messages may live in a different assembly, and nothing depends on their
being partial. Method names are PascalCased to match the C# protobuf generator, so
`line_total_cents` becomes `LineTotalCents` and reads the same as a hand-written member.

This choice may need revisiting if mutation (18) is allowed, since extension methods cannot access
anything the public surface does not already expose.

Questions:

- Are generated protobuf C# classes safe to extend directly?
- Should mutable methods require partial class integration?
- How should virtual behavior be represented?

### 24.2 C++

Potential strategies:

- Free functions.
- Generated helper namespaces.
- Protobuf insertion points.
- Wrapper/adaptor classes.
- Policy-based override hooks.

**Decided for the current implementation: header-only free functions** in the message's own
protobuf namespace, taking the receiver as `const T&`. This subclasses nothing, needs no protoc
insertion points, and behaves the same whether the protobuf codegen is regenerated or vendored.
All declarations are emitted before any definition so methods may call one another in any order.

Const-correctness follows from the read-only method model: every receiver is `const T&` and every
message-typed parameter is `const T&`. If mutation (18) is allowed, that decision has to be
revisited along with the free-function shape.

Questions:

- Should generated methods be added to message classes when insertion points are available?
- What is the ABI compatibility strategy? Header-only inline functions sidestep this for now, at
  the cost of recompiling consumers on every regeneration.

### 24.3 Python

No Python backend exists in the current implementation.

Potential strategies:

- Helper functions.
- Monkey-patched methods.
- Mixins.
- Wrapper classes.

Questions:

- Should generated Python behavior modify generated protobuf classes at import time?
- Should wrappers be preferred for predictability?
- How should type hints be generated?

### 24.4 Future Backends

Future backends must document:

- Type mappings.
- Numeric behavior.
- Presence behavior.
- Collection behavior.
- Error handling mapping.
- Generated API shape.
- Unsupported features.

## 25. Testing and Conformance Vectors

### 25.1 Test Categories

The conformance suite should include:

- Parser tests.
- Type-checker tests.
- IR golden tests.
- Backend source golden tests.
- Cross-language runtime behavior tests.
- Numeric edge-case tests.
- Presence/default-value tests.
- Repeated field and map tests.
- Error handling tests.
- Virtual/override behavior tests, if supported.
- Partial-binding and diagnostic-recovery tests.
- Symbol identity tests for editor-facing semantic data.

### 25.2 Conformance Vector Format

Decided. A conformance vector is not a separate file format at all: it is a ProtoLang `test`
declaration (25.3) in a `.protolang` file, paired with the `.proto` it imports.

- **Format.** The `test` declaration, rather than YAML, JSON, or text format. It is already parsed,
  name-resolved, and type-checked against protobuf descriptors, so a fixture field that does not
  exist, or an expectation whose type does not match the method's return type, is a compile error
  rather than something discovered when generated test code fails to build. A second, untyped way
  to say the same thing would have to re-earn all of that.
- **Expected results.** ProtoLang literals bound to the method's return type. Being bound to the
  IR rather than to a serialization makes them language-independent without a wire format of their
  own. The cost is that values with no ProtoLang literal -- `int64` MIN, `uint64` above `int64`
  MAX, infinity, NaN -- must be written as expressions or asserted through a predicate.
- **Compile and execute, not inspect.** Golden assertions over emitted source only state that a
  backend emits what it emitted last time, one language at a time. The suite compiles the generated
  code with a real compiler and runs it, and requires every backend to have run the same set of
  vectors, identified by a backend-independent test identity that each backend reports.

The reference corpus lives in `tests/conformance/`.

### 25.3 Author-Written ProtoLang Unit Tests

ProtoLang supports author-written unit tests for behavior defined in ProtoLang source. These tests
are distinct from the compiler's own conformance suite:

- Conformance vectors test whether a ProtoLang compiler/backend implements the language correctly.
- ProtoLang unit tests test whether a project's ProtoLang behavior is correct for that project.

Normative Requirements:

- Unit tests are written in a ProtoLang test declaration, either in the same `.protolang` file as
  the behavior or in a companion test file imported by the test command.
- Unit tests are declarative fixtures and expectations, not arbitrary executable ProtoLang code.
- A test names a receiver method, supplies a protobuf receiver value and method arguments, and
  declares the expected return value or expected terminal failure.
- Test declarations are not emitted into production behavior output unless test generation is
  explicitly requested.
- The compiler generates target-language test source files into a user-selected output directory.
- The compiler should not execute tests by default. Execution belongs to the target language's
  normal test runner or build system.

Syntax:

```protolang
import proto "invoice.proto";

extend Invoice {
    fn total_cents() -> int64 {
        var total: int64 = 0;

        for item in items {
            total = total + item.line_total_cents();
        }

        return total;
    }
}

test Invoice.total_cents "sums line totals" {
    receiver {
        items {
            quantity = 2;
            unit_price_cents = 300;
        }

        items {
            quantity = 4;
            unit_price_cents = 125;
        }
    }

    expect return 1100;
}
```

The `receiver` block is a descriptor-bound fixture initializer, not a general ProtoLang message
literal. Each entry names a protobuf field. Scalar fields use `field = expression;`; message fields
use nested blocks. Repeated fields may appear multiple times. The compiler binds field names and
fixture value types against protobuf descriptors, then a backend may lower the fixture to
target-language message construction code. A future test syntax may also accept protobuf text
format, but fixture semantics must still come from protobuf descriptors.

For methods with parameters, the test declaration should name each argument:

```protolang
test InvoiceItem.discounted_total "applies discount" {
    receiver {
        quantity = 2;
        unit_price_cents = 300;
    }

    arg discount_cents = 50;
    expect return 550;
}
```

For methods expected to terminate through `on_zero fail` or another future terminal failure
mechanism:

```protolang
test InvoiceItem.strict_ratio "zero divisor fails" {
    receiver {
        quantity = 2;
        unit_price_cents = 0;
    }

    expect fail;
}
```

Test generation follows the same shape as normal backend generation in the CLI:

```text
protolangc behavior.protolang \
  --target csharp \
  --out generated/src \
  --test-out generated/tests
```

If ProtoLang is run as a `protoc` plugin, test generation should use protoc-style output flags
rather than a special test runner protocol. Since `protoc` passes `.proto` descriptors to
plugins, the ProtoLang behavior/test source file must be named explicitly in plugin options:

```text
protoc \
  --proto_path=protos \
  --protolang_out=generated/src \
  --protolang_opt=source=behavior.protolang,target=csharp \
  --protolang_test_out=generated/tests \
  --protolang_test_opt=source=behavior.protolang,target=csharp \
  invoice.proto
```

`--scaffold` writes a target-specific test project beside generated tests and requires
`--test-out`. Each test backend writes under `<test-out>/<target>/`.

Implemented backend behavior:

- C# generates ordinary test source and can scaffold a `.csproj`.
- C++ generates standalone test executables and can scaffold a CMake project.
- Python has no implementation.

Open Questions:

- Should test declarations live in production `.protolang` files, separate `.protolangtest` files,
  or both?
- Should expected protobuf message values use text format, JSON mapping, binary fixtures, or all
  three?
- Should the compiler embed fixtures in generated source, copy fixture files beside the generated
  tests, or support both?
- Should target test framework selection be a backend option such as
  `--test-opt framework=xunit|standalone|gtest`?
- Should generated tests be stable enough to check in, or treated as build artifacts only?
- What should the eventual `protoc` plugin flag names be?

Decided: `expect fail` runs out of process. A terminal failure cannot be observed from inside the
process it ends, so a backend generates such a test as a driver that relaunches itself for that one
test and inspects how the child ended.

The verdict is the child's exit code, and it is an equality check against the failure code 10.2.1
fixes at 70, not a test for "died somehow". That distinction matters: a child that crashed for an
unrelated reason, or that fell through to an ordinary test run and merely reported failures, must
not be mistaken for a method that terminated. Requiring one exact code across every backend is only
possible because 10.2.1 rules out crash primitives, whose exit codes the host chooses.

## 26. Diagnostics

The compiler should provide deterministic diagnostics for:

- Syntax errors.
- Unknown protobuf types.
- Unknown fields.
- Type mismatches.
- Unsupported backend features.
- Ambiguous method names.
- Non-portable operations.
- Invalid mutation.
- Missing return statements.
- Possible arithmetic errors, where statically detectable.
- Parser recovery without duplicate follow-on diagnostics for the same missing token or name.
- Partial binding diagnostics when a source has parse errors but valid descriptors.

Diagnostic template:

```text
PL####: short diagnostic title
file.protolang:line:column
message
optional help text
```

Code ranges:

| Range | Owner |
|---|---|
| `PL0001`–`PL0999` | The compiler front end: lexer, parser, binder |
| `PL1001`–`PL1099` | The C# backend |
| `PL1101`–`PL1199` | The C++ backend |
| `PL2001`–`PL2999` | The driver and the configuration file (10.4) |

A configuration diagnostic names `protolang.config.xml` and the line and column inside it, rather
than a position in a `.protolang` source.

Open Questions:

- Should diagnostic codes be part of the compatibility contract?
- Warnings exist today, so which warnings are compatibility-stable and which remain advisory?

## 27. Versioning and Compatibility

### 27.1 Language Versioning

No language-version declaration is implemented today.

Possible future syntax:

```protolang
language "1.0";
```

Open Questions:

- Is the version declaration required?
- Can a compilation unit mix language versions?

### 27.2 Compatibility Policy

The project should define:

- Source compatibility rules.
- IR compatibility rules.
- Backend compatibility rules.
- Generated API compatibility rules.
- Runtime behavior compatibility rules.

Candidate policy:

- Patch versions may fix bugs and add diagnostics.
- Minor versions may add backward-compatible syntax or features.
- Major versions may change semantics.

Open Question:

- Is ProtoLang intended to preserve generated API compatibility across compiler versions?

## 28. Security and Determinism

Normative Requirements:

- ProtoLang behavior must be deterministic for a fixed input message and method arguments.
- No source-level access is provided to time, randomness, environment variables, filesystem, network, process state, or threads.
- Generated code must not depend on locale unless explicitly specified.
- Parser nesting is bounded to avoid compiler stack exhaustion on malformed or generated input.
- Runtime loops and recursive method calls are not currently resource-limited.

Open Questions:

- Should resource limits be specified for generated methods?
- Should recursion be allowed?
- Should the compiler reject potentially unbounded recursion?

## 29. Minimal Example

Protobuf schema:

```proto
syntax = "proto3";

message InvoiceItem {
  int64 quantity = 1;
  int64 unit_price_cents = 2;
}

message Invoice {
  repeated InvoiceItem items = 1;
}
```

ProtoLang source:

```protolang
import proto "invoice.proto";

extend InvoiceItem {
    fn line_total_cents() -> int64 {
        return quantity * unit_price_cents;
    }
}

extend Invoice {
    fn total_cents() -> int64 {
        var total: int64 = 0;

        for item in items {
            total = total + item.line_total_cents();
        }

        return total;
    }
}
```

Expected semantic behavior:

- `line_total_cents` returns `quantity * unit_price_cents` with wrapping overflow (10.1).
- `total_cents` iterates over `items` in protobuf repeated-field order.
- The method performs no I/O and uses no target-language-specific collection helpers.

## 30. Unresolved Questions Index

This section should be maintained as the authoritative list of open decisions.

- ~~File extension.~~ Decided: `.protolang` (5.1).
- ~~Direct import model.~~ Decided: `import proto "file.proto";` resolves `.proto` files through
  include paths and the source directory (5.2). Descriptor-set input remains open.
- ~~Package and namespace model for current source.~~ Decided: no independent ProtoLang package
  declaration; names come from protobuf descriptors (5.2). Future embedded-in-proto design remains
  open.
- ~~Semicolon requirement.~~ Decided: semicolons are mandatory for declarations/statements listed
  in 7.1.
- ~~Type inference policy.~~ Decided: local variables may state an explicit type or infer from the
  initializer (7.1, 8).
- Helper functions and whether top-level functions belong in the language.
- ~~Complete scalar type support.~~ Decided: all protobuf scalar spellings map into the supported
  ProtoLang value domains (8.2).
- Decimal support.
- ~~Nullability and presence syntax.~~ Decided: `has <field>`, with the field's own presence
  rules taken from the protobuf descriptor (8.4).
- ~~What reading an unset message field means.~~ Decided: it requires an established presence
  test, so the two backends have nothing to disagree about (13.1).
- ~~Which protobuf syntax versions are supported.~~ Decided: proto2, proto3, and editions, with
  no version check in the compiler (21.3).
- ~~Whether arithmetic behavior is selectable per project.~~ Decided: `protolang.config.xml`,
  with the file winning over command-line flags (10.4).
- ~~Boolean operator spelling.~~ Decided: both word and symbolic forms are accepted (9.2).
- ~~Assignment expression vs statement.~~ Decided: assignment is statement-only (9.2).
- Evaluation order details for non-short-circuit binary operators.
- ~~Integer overflow model.~~ Decided: wrapping (10.1).
- ~~Division and modulo by zero.~~ Decided: mandatory `on_zero` clause, with `fail` for the case
  where no substitute value is correct (10.2.1). `Result` (19) is explicitly deferred, not blocked.
- ~~Explicit cast syntax.~~ Decided: `x as int64`, numeric scalars only (10.3).
- ~~Numeric conversion rules.~~ Decided: integer targets wrap, floating point to integer
  truncates and saturates with NaN mapping to zero (10.3).
- String indexing and comparison semantics.
- ~~How protobuf enum values are referenced.~~ Decided: `EnumType.VALUE_NAME` (12).
- Enum unknown-value behavior, and whether an enum converts to or from an integer.
- Message construction support.
- Message equality semantics.
- ~~Repeated field mutation rules for current implementation.~~ Decided: no repeated mutation;
  only locals can be assigned (14, 18). Future mutation syntax remains open.
- Map support and map iteration order.
- Switch support.
- ~~Method overloading.~~ Decided: not supported (16.1).
- Receiver mutation and possible const/mut method split.
- Virtual method inclusion in version 1.
- Portable override registration model.
- Error result model.
- ~~External function support.~~ Decided: hard no for current language; methods call only ProtoLang
  methods (20).
- `protoc` plugin and Buf integration strategy.
- ~~ProtoLang unit test declaration syntax.~~ Decided: `test` declarations in `.protolang` files
  (25.3). Separate `.protolangtest` files remain open.
- Generated test output framework options and future `protoc` plugin flag names.
- Stable IR format.
- Third-party backend support.
- ~~Generated API shape for implemented backends.~~ Decided: C# extension methods and C++ header-only
  free functions (24). Python remains open because no backend exists.
- Diagnostic compatibility.
- Language version declaration.
- Generated API compatibility.
- Recursion and resource limits.
- Partial semantic model policy after descriptor-load failures and unresolved imports. Current
  implementation does not bind without usable descriptors; whether to produce a lighter semantic
  model for unresolved imports remains open.

## 31. Decision Log

Use this table to record decisions as the language stabilizes.

| Date | Area | Decision | Rationale | Status |
|---|---|---|---|---|
| 2026-08-13 | Scope | No exceptions, inheritance, I/O, threading, lambdas, LINQ-style syntax, or target-language syntactic sugar | Preserve small portable semantic core | Draft |
| 2026-08-13 | Methods | Methods are public only | Avoid cross-language visibility mismatch | Draft |
| 2026-08-13 | Architecture | Use protobuf data types as foundation and compile through typed IR to per-language backends | Separate language semantics from code generation concerns | Draft |
| 2026-08-13 | Virtual behavior | Consider overridable behavior without committing to inheritance | Preserve extension flexibility while avoiding protobuf inheritance assumptions | Open |
| 2026-08-24 | Control flow | `if` / `else if` / `else`, `while`, `break`, and `continue`, with bool-only conditions and mandatory braces | Smallest branching set that maps one-to-one onto every initial backend, so no target has to emulate it | Draft |
| 2026-08-24 | Missing return | All-paths-return is a reachability question: a method with a return type must not be able to reach the end of its body | Branching makes a trailing-return rule wrong in both directions, and reachability is what the backends' own compilers will check anyway | Draft |
| 2026-08-25 | Type system | Enum types can be named wherever a type is expected, resolved by full name or by an unambiguous simple name, including enums nested in messages | The type universe is the protobuf type universe (8.1); indexing messages but not enums left enum support reachable only through inference | Draft |
| 2026-08-25 | Conformance vectors | A vector is a ProtoLang `test` declaration, not a separate YAML or JSON format (25.2) | It is already bound and type-checked against descriptors, so a malformed vector is a compile error; a second authoring format would have to re-earn that | Draft |
| 2026-08-25 | Conformance vectors | Backends compile and execute generated code, and must be shown to have run the same set of vectors, not merely to have passed | A driver that runs no tests also exits zero, so per-backend success alone does not establish cross-language agreement | Draft |
| 2026-08-25 | Testing | `expect fail` runs out of process, and the verdict is an equality check on the child's exit code (25.3) | A terminal failure cannot be observed from inside the process it ends; requiring one exact code stops an unrelated crash from passing as a deliberate stop | Draft |
| 2026-08-25 | Error reporting | The `on_zero fail` path writes its diagnostic to standard error before terminating, in every backend | Spec 20 bans I/O in method bodies, but this is a fatal-error path rather than behavior, and how a host reports a termination is not something a portable diagnostic can rely on | Draft |
| 2026-08-25 | Error handling | `on_zero fail` terminates through `Environment.Exit` / `std::_Exit` with exit code 70, not `FailFast` / `abort` (10.2.1) | Crash primitives invoke the platform's error reporting, so generated library code could raise a dialog or a debugger prompt; `abort` is also catchable through `SIGABRT` and so does not deliver the guarantee at all. A fixed code also makes the backends observably identical | Draft |
| 2026-08-25 | Numeric conversions | Explicit conversions are written `x as int64`, between numeric scalar types only (10.3) | A postfix keyword is unambiguous against the existing grammar and reads as the plain pseudocode the language is meant to be; the alternatives collide with parenthesized expressions or with future message construction. Restricting to numeric scalars keeps the enum and bool questions separate rather than answering them by accident | Draft |
| 2026-08-25 | Numeric conversions | Integer targets wrap; floating point to integer truncates toward zero, saturates at the target's bounds, and maps NaN to zero (10.3) | Wrapping matches 10.1, so a conversion and an overflow answer the same way. Saturation is total and cheap where wrapping a magnitude beyond 2^64 is not meaningfully defined, and it is the answer a reader expects from a value that is simply too large. Every target disagrees natively -- unspecified in C#, undefined behavior in C++, flooring in Python -- so the result had to be stated rather than inherited | Draft |
| 2026-08-25 | Numeric conversions | Behavior annotations are resolved by one compile-time policy and stamped onto the typed IR, rather than decided at each emission site | Arithmetic policy is meant to become repository-tracked configuration; putting the answer on the IR keeps the module the whole contract a backend sees, and makes a new option a compile error at every emission site instead of a silently wrong default | Draft |
| 2026-08-25 | Enums | Enum values are named `EnumType.VALUE_NAME`, resolved by full name or unambiguous simple name, with a value in scope winning over an enum type of the same name (12) | Enum types alone left the feature decorative: a value could be declared, passed, and returned but never compared against anything. Values winning over types means adding an enum to a schema cannot silently change what an existing expression means | Draft |
| 2026-08-25 | Enums | Backends reproduce protoc's own value naming exactly: prefix-stripped PascalCase for C#, flattened namespace constants for C++ | The two spellings are unrelated and neither is derivable from the other. Approximating either emits an identifier that does not exist, which fails in the consumer's build rather than in this compiler, and only for the schemas nobody tested | Draft |
| 2026-08-25 | Testing | A `test` fixture may set an enum field from a named value; only message fields require a nested block | An enum field is set from a constant, which is an ordinary expression. Rejecting it left enum behavior untestable, so the feature and its test path had to land together | Draft |
| 2026-08-27 | Compile-time policy | Language-dependent preferences live in a repository-tracked `protolang.config.xml`, found by walking up from the source; the file wins over command-line flags unless an explicit override is passed (10.4) | Semantics that depend on who typed the command are not reproducible. Flags stay useful for experiments, but a project's answer has to be the one checked in beside the code it governs. Settings with a single legal value are listed anyway, so the whole contract is readable in one place and a second value is an addition rather than a discovery | Draft |
| 2026-08-27 | Numeric semantics | Integer overflow is selectable per project: `Wrapping` (default), `Checked`, or `Saturating`, each reproduced identically by every backend (10.1) | Wrapping stays the default because it is what unmodified C# does -- `checked` in C# is reached only through the keyword or a build property, so calling checked arithmetic "C# standard behavior" had it backwards. `MIN % -1` is 0 under every mode, because 0 is representable; only the quotient can fail | Draft |
| 2026-08-27 | Presence | `has <field>` is syntax, and which fields answer it comes from `FieldDescriptor.HasPresence` rather than from rules this compiler re-derives (8.4) | One rule covers singular messages, proto3 implicit and `optional` scalars, proto2 fields, repeated fields, and editions. A hand-written version would be a second, worse copy of something the protobuf runtime already implements, and would drift from it | Draft |
| 2026-08-27 | Presence | Using the value of a singular message field requires an established presence test; without one it is PL0078 (13.1) | C# yields null and throws, C++ yields the default instance and returns zero, and neither can be made to match the other without a runtime check in every target. Making the situation unrepresentable is what `on_zero` does for a zero divisor, and it costs nothing at runtime: a guarded read emits the same accessor chain it always did. The rule is on the value rather than on reading through it, because binding to a local or passing as an argument launders the same divergence | Draft |
| 2026-08-27 | Presence | The analysis is a plain set of access paths with no fixpoint, and guard clauses fall out of the existing all-paths-return reachability predicate (13.1) | Presence facts are monotone within a method, because ProtoLang cannot assign to a field, so nothing shown to be set can become unset. If receiver mutation ever arrives (18), that is the assumption to revisit | Draft |
| 2026-08-27 | Protobuf interop | proto2, proto3, and editions are all supported, and the compiler does not branch on the syntax version (21.3) | The version was standing in for presence, and presence now has a first-class answer. `FileDescriptor.Syntax` is deprecated in current runtimes with a note pointing at feature resolution, which is what `HasPresence` consults; protoc 31.1 was checked to generate C# for both proto2 and edition 2023 rather than assumed to | Draft |
| 2026-08-30 | Source organization | ProtoLang source files use `.protolang`, import schemas with `import proto`, and do not declare their own packages (5) | This is the compiler surface that now exists: include paths plus the source directory resolve `.proto` imports, and protobuf descriptors provide the package/type namespace | Draft |
| 2026-08-30 | Grammar | Top-level declarations are `import`, `extend`, and `test`; semicolons are mandatory; local variables may infer from initializers; no top-level helper functions are implemented (7) | The parser has a concrete recovery grammar, and keeping the spec in "template" language hid decisions that tests and callers already rely on | Draft |
| 2026-08-30 | Scalar types | Protobuf scalar wire spellings collapse to ProtoLang value-domain types, including `bytes` as a type without bytes literals or operations (8.2) | Once a field is decoded, `sint32`, `sfixed32`, and `int32` have the same behavior-language type. Preserving the wire spelling in the type system would create distinctions with no expression semantics | Draft |
| 2026-08-30 | Methods | Method names share the receiver namespace with fields, and overloading is rejected (16.1) | Bare receiver-field lookup and method lookup would otherwise disagree or require target-specific overload conventions. Rejecting the collision in the binder keeps calls portable and unambiguous | Draft |
| 2026-08-30 | Mutability | Only local variables can be assigned; receiver fields, parameters, loop bindings, repeated fields, and nested message fields are read-only in the current language (18) | This matches the generated API shapes already chosen: C# extension methods and C++ `const T&` free functions. Receiver mutation remains a future language design, not an accidental backend behavior | Draft |
| 2026-08-30 | Partial compilation | Parse errors no longer stop binding when descriptors are available; callers use `Module` for partial semantic data and `EmittableModule` for generated artifacts (22.1) | Editors need symbol/type answers in broken buffers, while code generation must not accidentally consume a partial model. Expressing the distinction in the result type is safer than asking every caller to remember a diagnostic-bag convention | Draft |
| 2026-08-30 | Import results | Import resolution is returned as per-import outcomes, and descriptor-load failures preserve the resolved import list (21.1, 22.1) | A count or empty list cannot distinguish an unwritten import, a not-found import, and a schema that was found but rejected by protoc. Tooling needs the declaration-to-file mapping even when descriptor loading fails | Draft |
| 2026-08-30 | Symbols | The IR carries declaration sites and stable symbol IDs for ProtoLang declarations, and descriptor-based IDs for schema symbols (22.2) | Editor features, occurrence highlighting, and caching need identities that survive a rebind of unchanged text and do not collapse same-named locals or fields from different scopes/messages | Draft |
| 2026-09-01 | Descriptor input | A load returns the whole descriptor set with its source info and the file each schema came from, and may be cached against the located protoc, the ordered include paths, and the content of the transitive closure (21.1) | Building descriptors and dropping the set paid protoc for source info on every run and then discarded it, which is exactly what resolving a schema declaration or its doc comment needs. Keying a cache on the files a compilation named would be wrong in five ways at once -- a transitively imported schema, a reordered include list, a file appearing in a root that was empty, a deletion, and protoc itself changing -- so correctness is defined over the closure protoc reports rather than over the request | Draft |
