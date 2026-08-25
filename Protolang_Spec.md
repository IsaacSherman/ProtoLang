# ProtoLang Specification Template

Status: Draft template  
Spec version: TBD  
Last updated: 2026-08-13  
Target protobuf versions: TBD  
Target language backends: C#, C++, Python, TBD  

## 1. Purpose

ProtoLang is a deliberately small language for defining portable behavior over Protocol Buffer message types.

ProtoLang source files define methods, helper functions, and related behavior once, then compile that behavior into target languages such as C#, C++, Python, and potentially others.

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
- Produce predictable generated APIs for C#, C++, Python, and future backends.
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
: A method defined by ProtoLang and associated with a protobuf message or namespace.

Receiver:
: The message instance a method operates on, equivalent to `this` or `self` in target languages.

Backend:
: A compiler component that emits code for a specific target language.

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

Open Question:

- Should the extension be `.plang`, `.protox`, `.pbehavior`, or something else?

### 5.2 Relationship to `.proto`

A ProtoLang file imports one or more protobuf schema files.

Example:

```protolang
import proto "inventory.proto";

extend InventoryItem {
    fn total_value() -> int64 {
        return quantity * unit_price;
    }
}
```

Open Questions:

- Should imports reference `.proto` files directly, compiled descriptor sets, or both? `Ultimately, I'd like to see this code embedded IN proto files... but I don't know if we can. ~IS`
- Should ProtoLang support packages/namespaces independently of protobuf packages?
- Should one ProtoLang file be allowed to extend messages from multiple protobuf packages?

### 5.3 Compilation Unit

A compilation unit consists of:

- One or more ProtoLang source files.
- The protobuf descriptors referenced by those files.
- Compiler options.
- Backend target configuration.

## 6. Lexical Structure

This section defines the token-level syntax.

### 6.1 Character Set

TBD.

Recommended baseline:

- Source files are UTF-8.
- Identifiers use a restricted ASCII subset initially.
- String literals support explicit escape sequences.

Open Question:

- Should Unicode identifiers be allowed in version 1?

### 6.2 Comments

Candidate syntax:

```protolang
// line comment

/*
   block comment
*/
```

Open Question:

- Are block comments necessary for version 1?

### 6.3 Identifiers

Candidate rule:

```text
identifier = [A-Za-z_][A-Za-z0-9_]*
```

Identifiers are case-sensitive unless otherwise specified.

Open Question:

- Should ProtoLang require a single naming convention, or allow backend-specific casing transformation?

### 6.4 Keywords

Reserved keywords, candidate list. Note this list is missing `import`, `proto`, `double`, `float`,
and `bytes`, all of which the language already uses elsewhere in this document; the implementation
reserves them too, along with `on_zero` and `fail` (10.2.1).

```text
and
bool
break
case
continue
else
enum
extend
false
fn
for
if
in
int32
int64
message
not
or
return
string
switch
true
uint32
uint64
var
virtual
void
while
```

Open Question:

- Should `switch` be included in version 1?

## 7. Grammar and Syntax

This section should eventually contain a complete grammar.

### 7.1 Template Grammar

Illustrative, non-final grammar:

```ebnf
source_file       = { import_decl | extend_decl | function_decl };

import_decl       = "import" "proto" string_literal ";";

extend_decl       = "extend" qualified_name "{" { method_decl } "}";

method_decl       = [ "virtual" ] "fn" identifier
                    "(" [ parameter_list ] ")"
                    [ "->" type_ref ]
                    block;

function_decl     = "fn" qualified_name
                    "(" [ parameter_list ] ")"
                    [ "->" type_ref ]
                    block;

parameter_list    = parameter { "," parameter };
parameter         = identifier ":" type_ref;

block             = "{" { statement } "}";
```

Normative Requirement:

- The final grammar must be unambiguous.
- Backend code generation must not depend on parser quirks or target-language parsing.

Open Questions:

- Should semicolons be mandatory?
- Should variable declarations require explicit types, inferred types, or both?
- Should top-level helper functions be allowed in version 1?

## 8. Type System

### 8.1 Type Sources

Types come from:

- Protobuf scalar primitive types.
- Protobuf enum types.
- Protobuf message types.

Normative Candidate:

- ProtoLang does not define an independent application type system.
- ProtoLang value types are protobuf scalar primitives, protobuf enums, and protobuf messages.
- ProtoLang does not add non-protobuf numeric types such as `decimal`.
- `void` is a method return marker only; it is not a protobuf value type and cannot be used for fields, variables, or parameters.

Open Questions:

- Should type aliases be allowed if they resolve only to protobuf scalar, enum, or message types?
- Should helper/result types ever be allowed, or must error handling also be represented using protobuf-defined messages?

### 8.2 Protobuf Scalar Mapping

The spec must define exact semantics for:

- `double`
- `float`
- `int32`
- `int64`
- `uint32`
- `uint64`
- `sint32`
- `sint64`
- `fixed32`
- `fixed64`
- `sfixed32`
- `sfixed64`
- `bool`
- `string`
- `bytes`

Open Questions:

- Should ProtoLang expose all protobuf scalar types directly?
- Should `bytes` be supported in version 1?
- Should `float` and `double` follow IEEE 754 target behavior exactly, or define stricter portable behavior?

### 8.3 Non-Protobuf Types

Normative Candidate:

- ProtoLang does not support additional value types outside the protobuf type universe.
- `decimal` is not supported because Protocol Buffers do not define a decimal scalar type.
- Backends must not silently map a ProtoLang type to target-specific types such as C# `decimal`, Python `Decimal`, or arbitrary-precision numeric classes unless that value is represented by an explicit protobuf message type.

Implementation Note:

- Projects that need decimal-like behavior should define an explicit protobuf message, such as a fixed-scale money or decimal representation, and then define ProtoLang behavior over that message.

Open Questions:

- Should the standard library eventually provide recommended protobuf message shapes for common non-scalar concepts such as money, fixed-scale decimal, dates, durations, or UUIDs?
- Should ProtoLang have a distinct nullable type, or should all presence and absence semantics come directly from protobuf field presence?

### 8.4 Nullability and Presence

Protobuf presence semantics differ between proto2, proto3, optional fields, messages, wrappers, and repeated fields.

The spec must define:

- How field presence is tested.
- Whether absent scalar fields are distinguishable from default values.
- How optional fields are read.
- How optional fields are assigned.
- How message-valued fields are initialized.

Candidate syntax:

```protolang
if has customer.email {
    return customer.email;
}
```

Open Questions:

- Should `has field` be syntax, a built-in function, or generated method access?
- Should implicit default values be allowed in expressions?

## 9. Expressions and Operators

### 9.1 Expression Categories

The language may include:

- Literals.
- Variable references.
- Field access.
- Method calls.
- Function calls.
- Arithmetic expressions.
- Boolean expressions.
- Comparisons.
- Collection indexing or iteration.

### 9.2 Operators

Candidate operator set:

```text
+  -  *  /  %
== != < <= > >=
and or not
=
```

Open Questions:

- Should boolean operators use words (`and`, `or`, `not`) or symbols (`&&`, `||`, `!`)?
- Should assignment be an expression or statement only?
- Should modulo be included in version 1?

### 9.3 Evaluation Order

Normative Requirement:

- The evaluation order of expressions must be explicitly defined.
- Backends must preserve the specified evaluation order.

Candidate:

- Function and method call arguments evaluate left to right.
- Boolean `and` and `or` short-circuit left to right.
- Assignment evaluates the right-hand side before storing the result.

Open Question:

- Should all binary operators evaluate left operand before right operand?

## 10. Numeric Semantics

Numeric behavior is one of the highest-risk portability areas.

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

Open Question:

- The syntax for declaring a non-default arithmetic behavior per file, per method, or per
  expression is not yet designed. The typed IR already carries a per-operation behavior
  annotation, so adding `Checked` or `Saturate` is a front-end and backend change only.

### 10.2 Division

The spec must define:

- Integer division rounding.
- Division by zero.
- Signed division edge cases.
- Floating-point division by zero.

**Decided:**

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

The spec must define:

- Implicit numeric widening.
- Explicit casts.
- Narrowing conversions.
- Signed/unsigned conversions.
- Float/integer conversions.

Recommendation:

- Keep implicit conversions minimal.
- Require explicit conversion where precision, sign, or range may change.

Decided:

- There are **no** implicit numeric conversions. Both operands of a binary arithmetic or comparison
  operator must already have the same type, and a returned value must already have the declared
  return type. This is what makes the overflow rule in 10.1 well-defined: the width the result wraps
  to is never the product of a promotion the author did not write.
- Integer literals are the single exception: a literal adopts the expected type at its use site when
  the value fits, so `var total: int64 = 0;` needs no suffix or cast.

Open Question:

- Explicit cast syntax is not yet designed. Until it exists, mixed-width arithmetic cannot be
  written at all, which is a real gap: `int32 * int64` is currently a compile error with no
  workaround.

## 11. Strings

### 11.1 String Model

The spec must define:

- Encoding model.
- Length semantics.
- Indexing semantics, if indexing is supported.
- Comparison semantics.
- Case conversion, if supported.

Candidate baseline:

- Strings are Unicode text corresponding to protobuf `string`.
- Equality is code-unit or code-point exact TBD.
- No locale-sensitive operations in version 1.

Open Questions:

- Should string indexing be supported at all?
- Should normalization be specified?
- Should string comparison beyond equality be supported?

## 12. Enums

The spec must define:

- How protobuf enum values are referenced.
- How unknown enum numeric values are handled.
- Whether enums can be compared to integers.
- Whether switch/case over enums is supported.

Candidate syntax:

```protolang
if order.status == OrderStatus.SHIPPED {
    return true;
}
```

Open Questions:

- Should enum exhaustiveness be checked?
- Should unknown enum values be representable?

## 13. Messages

### 13.1 Field Access

Candidate syntax:

```protolang
customer.name
order.customer.address.city
```

The spec must define behavior for:

- Missing message fields.
- Default scalar values.
- Optional fields.
- Oneof fields.
- Repeated fields.
- Map fields.

### 13.2 Message Construction

Open Questions:

- Should ProtoLang be able to create new message instances?
- Should object initializer syntax exist?
- Should construction be limited to backend helper APIs?

### 13.3 Equality

Open Questions:

- Does message equality mean identity, field-wise equality, or backend-defined equality?
- Is deep equality part of version 1?

## 14. Repeated Fields and Collections

### 14.1 Supported Operations

Candidate minimal operation set:

- Read length.
- Iterate in order.
- Read by index.
- Append value.
- Clear collection.
- Assign element by index, if mutable.

Candidate syntax:

```protolang
var total: int64 = 0;

for item in invoice.items {
    total = total + item.amount;
}

return total;
```

Open Questions:

- Should filtering, mapping, sorting, or aggregation helpers exist? `No. Basics only. ~IS`
- Should repeated field mutation be allowed inside iteration? `My vote? Banned. ~IS`
- Should collection indexing be bounds-checked with explicit error results?  `Ugh... probably. I really want to say no, but... I have a feeling that not doing this could lead to security concerns in some language or another. ~IS`

### 14.2 Maps

Protobuf maps are repeated key-value structures with language-specific APIs.

Open Questions:

- Are protobuf maps supported in version 1?
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

Version 1 has two loop forms:

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

Candidate:

```protolang
extend DetectorReading {
    fn calculate_rate() -> double {
        return counts / live_time;
    }
}
```

Normative Requirements:

- All ProtoLang-defined methods are public.
- Method behavior must not depend on target-language inheritance.

Open Questions:

- Should method names share a namespace with protobuf fields?
- How should we implement overloading?  Operator overloading is banned, but what about regular overloads? `Banned outright is my vote ~IS`
- Should methods be allowed to mutate the receiver?

### 16.2 Parameters and Returns

The spec must define:

- Parameter passing semantics.
- Return value semantics.
- Void returns.
- Multiple return values, if any.
- Error-return conventions.

Recommendation:

- Start with single return values only.
- Use explicit result types or status patterns for recoverable errors.

## 17. Virtual and Override Semantics

ProtoLang may support overridable behavior, but must not define inheritance as a language feature.



### 17.1 Design Principle

Normative candidate:

- `virtual` marks behavior as backend-overridable.
- `virtual` does not define a ProtoLang subclass model.
- ProtoLang source cannot declare subclasses.
- Each backend must document how virtual methods may be overridden or replaced.

```protolang
extend DetectorReading {
	double real_time;
	virtual fn overridable_count_rate() -> double{
		return counts / real_time;
	}
}
```

Overridable functions don't behave like classical virtual functions- protobuf compiles into sealed classes in C# so inheritance is impossible.  They'll function something like this by necessity: 

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
- Should generated protobuf message subclasses be explicitly discouraged or forbidden? `Forbidden. ~IS`

## 18. Mutability

The spec must define whether methods can:

- Read receiver fields.
- Assign receiver fields.
- Modify repeated fields.
- Modify nested messages.
- Allocate new messages.
- Mutate parameters.

Candidate method modifiers:

```protolang
fn total() -> int64 { ... }          // read-only by default? TBD
mut fn normalize() -> void { ... }   // mutation allowed? TBD
```

Open Questions:

- Are methods read-only by default? `I lean toward no.  Methods can mutate receiver members by default. They should be declared const if they're to be read only. ~IS`
- Should mutation require an explicit marker? `No, but const methods should. ~IS`
- Should immutable and mutable methods generate different APIs?

## 19. Error Handling Without Exceptions

ProtoLang has no exceptions.

The spec must define how operations report failure.

Potential approaches:

- Compile-time rejection where possible.
- Explicit `Result<T, E>` style return type.
- Boolean success return plus out parameter. Not recommended unless target language constraints require it.
- Generated diagnostic/status type.
- Runtime trap/panic. Not recommended for portable business logic unless tightly scoped.

Candidate:

```protolang
fn safe_rate() -> Result<double, CalculationError> {
    if live_time == 0 {
        return error CalculationError.DivideByZero;
    }

    return ok counts / live_time;
}
```

Open Questions:

- Does ProtoLang define a built-in `Result` type? `I think we should- it doesn't have to be used, but it should be there for ease of use. ~IS`
- Are arithmetic errors expressible in the type system? `Yes, it should be a fairly expansive enum. ~IS`
  Note: overflow is no longer one of them. Since 10.1 defines overflow as wrapping, it is a defined
  result rather than a failure, and nothing about it needs to reach the type system.
- Can methods be declared as total, meaning they cannot fail at runtime? `I lean toward no, but this might be a nice optimization at some point. ~IS`
- How do target backends map error results idiomatically while preserving semantics?  `Error handling is heavily on the user to define; we'll provide the Result type with primitive error enums for primitive "exceptions" such as dividing by zero. More advanced stuff?  Users can (and should!) tailor that to their specific needs. ~IS`

## 20. I/O, Threading, and Side Effects

Normative Candidate:

- ProtoLang has no I/O primitives.
- ProtoLang has no threading or concurrency primitives.
- ProtoLang has no clock, randomness, environment, filesystem, network, console, or process APIs.
- Backends must not silently introduce observable I/O or concurrency behavior into generated method bodies.

Open Questions:

- Should deterministic pure helper functions be explicitly marked? `Seems unnecessary. ~IS`
- Should generated code be allowed to call user-provided external functions? If yes, how are purity and portability enforced? `Hard no.  The only functions available to ProtoLang methods are those defined in ProtoLang. ~IS`

## 21. Interoperability With Protobuf

### 21.1 Descriptor Input

The compiler should consume protobuf descriptors rather than reparsing `.proto` files independently.

Implementation Note:

- This aligns ProtoLang with `protoc`, Buf, and plugin-based code generation workflows.

Open Questions:

- Should the compiler run as a `protoc` plugin?  `Eventually. I don't think it's necessary right away, but should happen before v1. ~IS`
- Should it also support Buf plugin workflows?
- Should direct `.proto` parsing be supported for developer tooling only?

### 21.2 Generated Code Integration

The spec must define, or require each backend to define:

- Whether generated behavior is emitted into partial classes, extension methods, free functions, wrappers, mixins, or helper modules.
- How generated method names map to target-language conventions.
- How namespace/package mapping works.
- How generated code is imported by user code.

### 21.3 Protobuf Editions and Syntax Versions

Open Questions:

- Which protobuf syntax versions are supported: proto2, proto3, Editions?
- How does ProtoLang handle explicit presence in newer protobuf versions?

## 22. IR and Compiler Architecture

This section is implementation-facing, not source-language syntax.

### 22.1 Suggested Pipeline

```text
ProtoLang source
    -> lexer/parser
    -> AST
    -> protobuf descriptor binding
    -> name resolution
    -> type checking
    -> typed IR
    -> optimization/lowering
    -> backend code generation
    -> target-language source
    -> target-language tests/conformance
```

### 22.2 Typed IR Requirements

The IR should preserve:

- Source locations for diagnostics.
- Resolved protobuf type references.
- Exact numeric operation kinds.
- Presence checks.
- Field access semantics.
- Mutability intent.
- Error-result behavior.
- Evaluation order.
- Virtual/overridable annotations.

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

Template:

Status as of the first working compiler. "No" means the backend rejects the feature at compile
time rather than emitting something whose semantics differ.

| Feature | C# | C++ | Python | Notes |
|---|---:|---:|---:|---|
| Attached methods | Yes | Yes | — | C#: extension methods. C++: free functions. |
| Wrapping integer arithmetic | Yes | Yes | — | `unchecked(...)` / unsigned round-trip helpers. |
| Checked division (`on_zero`) | Yes | Yes | — | Runtime zero check in both; see 10.2.1. |
| `on_zero fail` | Yes | Yes | — | `Environment.Exit(70)` / `std::_Exit(70)`, after a diagnostic on stderr (10.2.1). |
| IEEE 754 float division | Yes | Yes | — | Native in both. Python will need a helper. |
| Repeated iteration | Yes | Yes | — | `foreach` / range-`for` over the protobuf container. |
| Cross-message method calls | Yes | Yes | — | C++ emits all declarations before any definition. |
| Mutable methods | No | No | — | Blocked on the open question in 16.1. |
| Virtual methods | No | No | — | Blocked on 17; both backends reject. |
| Maps | No | No | — | Blocked on 14.2. |
| Result/error returns | No | No | — | Blocked on 19. |
| Explicit casts | No | No | — | Blocked on 10.3; no syntax yet. |
| Conditionals and `while` | No | No | — | Parsed keywords are reserved but unimplemented. |
| Proto2 presence | No | No | — | Blocked on 21.3. |
| Proto3 optional | No | No | — | Blocked on 21.3. |

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

### 25.2 Conformance Vector Format

Candidate structure:

```yaml
name: total_value_basic
proto: inventory.proto
source: inventory.protolang
method: InventoryItem.total_value
input:
  quantity: 3
  unit_price: 10
expected:
  ok: 30
```

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

ProtoLang should support author-written unit tests for behavior defined in ProtoLang source.
These tests are distinct from the compiler's own conformance suite:

- Conformance vectors test whether a ProtoLang compiler/backend implements the language correctly.
- ProtoLang unit tests test whether a project's ProtoLang behavior is correct for that project.

Recommended direction:

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

Candidate syntax:

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

Test generation should follow the same shape as normal backend generation:

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

The exact flag names remain open, but the important constraint is that test output is a separate
artifact root. This lets users choose whether generated test code is checked in, compiled only in
CI, copied into an existing test project, or adapted by downstream build tooling.

Backend expectations:

- C# should generate ordinary test source that can live in a user-selected test project. The test
  framework binding is an option; xUnit is a reasonable default for this repository but should not
  be a language-level requirement.
- C++ should initially generate a small standalone test executable source, because this avoids
  requiring GoogleTest/Catch2 before the language has a package story. A backend option can later
  select a test framework.
- Python should generate test functions compatible with the standard `unittest` module by default,
  with pytest-compatible output as an option.

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

Diagnostic template:

```text
PL####: short diagnostic title
file.protolang:line:column
message
optional help text
```

Open Questions:

- Should diagnostic codes be part of the compatibility contract?
- Should warnings exist, or should all portability concerns be errors?

## 27. Versioning and Compatibility

### 27.1 Language Versioning

Each source file may declare a language version.

Candidate:

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

Normative Candidate:

- ProtoLang behavior must be deterministic for a fixed input message and method arguments.
- No source-level access is provided to time, randomness, environment variables, filesystem, network, process state, or threads.
- Generated code must not depend on locale unless explicitly specified.

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

- File extension.
- Import model: `.proto`, descriptor set, or both.
- Package and namespace model.
- Semicolon requirement.
- Type inference policy.
- Helper functions.
- Complete scalar type support.
- Decimal support.
- Nullability and presence syntax.
- Boolean operator spelling.
- Assignment expression vs statement.
- Evaluation order details.
- ~~Integer overflow model.~~ Decided: wrapping (10.1).
- ~~Division and modulo by zero.~~ Decided: mandatory `on_zero` clause, with `fail` for the case
  where no substitute value is correct (10.2.1). `Result` (19) is explicitly deferred, not blocked.
- Explicit cast syntax, needed before mixed-width arithmetic is expressible (10.3).
- Division by zero model.
- Numeric conversion rules.
- String indexing and comparison semantics.
- Enum unknown-value behavior.
- Message construction support.
- Message equality semantics.
- Repeated field mutation rules.
- Map support and map iteration order.
- Switch support.
- Method overloading.
- Receiver mutation.
- Virtual method inclusion in version 1.
- Portable override registration model.
- Error result model.
- External function support.
- `protoc` plugin and Buf integration strategy.
- ProtoLang unit test declaration syntax and file extension.
- Generated test output flags and target test framework options.
- Stable IR format.
- Third-party backend support.
- Generated API shape per target language.
- Diagnostic compatibility.
- Language version declaration.
- Generated API compatibility.
- Recursion and resource limits.

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
