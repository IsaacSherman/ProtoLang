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

`Arithmetic/Overflow` in `protolang.config.xml` ([10.4](#104-compile-time-policy)) selects one of three answers. Every mode is
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
  produces; no implicit conversion is applied ([10.3](#103-numeric-conversions)).
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
  intentional, but it is a larger capability than anything else the language permits ([20](./§20-I-O, Threading, and Side Effects.md#20-io-threading-and-side-effects)), and a
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

`Arithmetic/Conversion` in `protolang.config.xml` ([10.4](#104-compile-time-policy)) names this behavior. It has one legal
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

- Whether a language version ([27.1](./§27-Versioning and Compatibility.md#271-language-versioning)) belongs in this file rather than in each source file.
- Whether a backend may add settings of its own, and if so how a third-party backend's settings
  avoid colliding with the language's.

### 10.4.1 Host Configuration

**Decided: a host resolves configuration per document, in one documented order, and may not restate
language policy.**

10.4 settles where language policy lives, for a compiler invoked once over one file. A host that
serves an editor has a question the command line never had: one process holds many documents at
once, over one or more workspace folders, each of which may carry settings of its own and its own
`protolang.config.xml`. Left to each client to answer, that becomes two settings models and a server
receiving both dialects.

Normative Requirements:

- Configuration is resolved **for a document**, not for a session. Two documents open at once may
  legitimately resolve different include paths and different policy.
- The precedence order, most specific first, is: an editor setting for the workspace folder holding
  the document; an editor setting for the workspace; an editor setting at user scope; the
  `PROTOLANG_PROTOC` environment variable; and finally discovery -- `PATH`, then the NuGet package
  cache. A setting beats the environment because a setting is the project's answer and the
  environment is the machine's, and because the setting is the one the user can see in front of
  them.
- **Language policy is not host-configurable.** 10.4 says the file wins, and a host that could
  restate a policy would make a buffer mean one thing on screen and another in the build. A host may
  name a different `protolang.config.xml`, which is what `--config` does for the command line, and
  may not state what is inside one.
- **Anything the user wrote that is not being used is reported**, as a warning naming the scope it
  was written at. A setting stating language policy (`PL2101`), a setting the host does not
  recognize (`PL2102`), a path that is relative with nothing to resolve it against or that is not a
  path at all (`PL2103`), a named configuration file that does not exist (`PL2104`), and a named
  `protoc` that does not exist (`PL2105`). A setting ignored in silence leaves a user unable to tell
  a typo from a refusal from a defect.
- **A named `protoc` that exists and still cannot be run stops the document**, as `PL2107`, an error.
  It is deliberately not `PL2105`: that one is a warning and a fall-through, because the host can go
  on to the next source, and here there is nowhere to fall through to. Falling back to a located
  `protoc` instead would compile against an executable the settings do not name while the resolved
  configuration went on reporting that the setting was in force.
- **A setting that is present and blank states nothing.** An editor writes an unset string setting as
  the empty string rather than leaving it out, so blank is the ordinary shape of "no answer" and
  falls through to the next source without comment.
- **A configuration file that is found and cannot be read stops the document, and says so.**
  `PL2106` is an error, not a warning, and names the file, how it came to be consulted, every problem
  reported inside it with its position, and the fact that nothing is compiled for the document until
  it is fixed. This is 10.4's rule applied to a host: a project that states a policy and is then
  silently ignored is worse off than one that states nothing. The resolved configuration reports such
  a file as *refused*, rather than reporting the defaults beside the file that in fact supplied none.
- A relative path resolves against **the scope that supplied it**: a folder-scope setting against
  that folder, a workspace-scope setting against the workspace -- or against the only open folder,
  when the workspace has no file of its own -- and a user-scope setting against nothing, which is
  reported and ignored. A setting that applies to every workspace on the machine names no one
  directory, and resolving it against whichever folder the document happens to be in would give one
  setting a different meaning in every project.
- Include paths **accumulate** across scopes, most specific first, deduplicated. They are a search
  order rather than a value, so a nearer scope adds to a further one instead of replacing it.
- A document that belongs to no workspace folder resolves against workspace and user scope, and
  discovers its policy file by walking up from its own directory as 10.4 requires. A document with
  no path at all -- a buffer that has never been saved -- belongs to the only open folder when there
  is exactly one, and to none otherwise.
- **Two spellings of one path are one document and one cache entry.** Case where the file system
  ignores case, a percent-encoded drive colon, a forward-slashed Windows path, a trailing separator,
  and a URI against the path it names must all resolve to one identity.
- **Every setting takes effect on the next compilation, and none requires a restart.** Work already
  in flight keeps the configuration it began under and carries the generation of that configuration,
  so a host can tell that a result it is handed was computed under settings that no longer apply.
- The resolved configuration for a document, with the source each value came from, must be
  retrievable.

Implementation Note:

- `WorkspaceConfiguration` in `ProtoLang.LanguageServer` is the model, and `Resolve` is the only
  place the order above is applied. `ConfigurationSource` declares the order, and the resolver walks
  that declaration rather than restating it.
- `DocumentUri` is the single conversion between a URI and a path. Every spelling above is settled
  there and in `PathIdentity`, which is where the compiler asks whether two paths are one path.
- A configuration file named by a setting and then not found is a warning and a fall-through rather
  than a stop, which is where a host deliberately differs from the command line: `--config` naming
  nothing is refused outright, because a build must not quietly produce different code, while an
  editor that went dark over a stale path in a settings file would take away the diagnostics the
  user is trying to read. A file that exists and cannot be *read* still stops the document, exactly
  as 10.4 requires.

Open Question:

- What a repository is allowed to configure in an untrusted workspace, which is a trust question
  rather than a precedence one.
