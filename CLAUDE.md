# Working in this repository

ProtoLang compiles methods written against protobuf messages into equivalent C# and C++.

Read [ARCHITECTURE.md](ARCHITECTURE.md) for the lay of the land, [Protolang_Spec.md](Protolang_Spec.md)
for the language, and [docs/epic-47-workflow.md](docs/epic-47-workflow.md) for the per-issue process
of the editor-support epic.

## Commands

```bash
dotnet build ProtoLang.slnx
```

```bash
dotnet test ProtoLang.slnx
```

The full suite takes about 90 seconds because it builds and runs real generated projects. Filter
while iterating (`--filter "FullyQualifiedName~LexerTests"`), but the unfiltered run is the gate —
there is no CI. `protoc`, the .NET SDK, and a C++ toolchain must be on the machine.

## How to write code here

**DRY, religiously.** If a rule is expressed twice, the two copies will disagree eventually. When a
value is derived in more than one place, give it one home and have both callers ask. The
`SourceIdentity` type exists because a source path was being decomposed four separate ways.

**A function reads like a paragraph.** Names carry the meaning; the body says what happens, in order,
at one level of abstraction. A function that resolves a path, parses XML, and formats a message is
three functions. If you need a comment to explain *what* a line does, the line or its names are
wrong.

**Comment the why, never the what.** The house pattern is XML docs, not inline comments:

- `<summary>` — what this is, in a sentence or two.
- `<remarks>` — **why it is this way**: the alternative considered and rejected, the failure being
  guarded against, the constraint that forced the shape. This is where the real documentation lives
  in this codebase, and it is often several paragraphs. It is what a reader six months out needs.
- Inline `//` comments are for the non-obvious *decision* at a specific line — why a check is
  ordered before another, why a value is clamped. Not for narrating the code.

**Keep abstraction levels consistent inside a function.** Do not mix "settle the policy" with
"index into a char array" in one body.

**Errors are diagnostics, not exceptions.** The compiler must survive anything a user or an editor
buffer can hand it. Collect into a `DiagnosticBag` and keep going; the binder deliberately continues
past an unresolved name with `ErrorType`. Throwing is for programmer error (`ArgumentNullException`
on a null argument), never for bad input.

**Prefer additive change.** New types over reshaped ones, especially anywhere the epic touches.

## Current patterns

Observed across the codebase; match them rather than introducing a second style.

- **Records for data, `sealed` by default.** `sealed record` for reference data, `readonly record
  struct` for small values on hot paths (`SourceSpan`, `SourcePosition`). Positional syntax when the
  members cannot be inconsistent with each other; an explicit constructor when construction has to
  validate.
- **File-scoped namespaces**, `var` throughout, collection expressions (`[]`, `[.. items]`), target
  typed `new()`, pattern matching over branching.
- **Init-only properties instead of growing parameter lists** where new knobs are expected
  (`CompilationOptions` says so out loud).
- **`Try*` with `out`** for parse-or-report, returning `bool`.
- **Guard clauses and early return**; the happy path stays at the left margin.
- **Nullable is on and meaningful.** `null` means "there is none" (a buffer with no directory), and
  every consumer asks one question rather than `is null` in one place and `IsNullOrEmpty` in another.
- **Diagnostics** get a `PL####` code, a lowercase title, a full-sentence message, a span, and —
  wherever a reader could act on it — a `Help` string that says what to do. Help text is part of the
  product; #61 turns it into quick fixes.
- **Public API stability is deliberate.** Existing constructor signatures and rendering are kept
  working when a type changes shape underneath them.

## Tests must have teeth

A test that cannot fail is worse than no test: it costs a run and buys confidence it has not earned.

- **Name the property, not the method.** `RangesAreHalfOpen`,
  `AnEmptyRangeIsDistinguishableFromAOneCharacterRange`,
  `TheSameTextDiagnosesIdenticallyWhicheverDoorItCameIn`. A full sentence in PascalCase saying what
  is true. Never `Method_Condition_Result`.
- **Assert the real answer, not the implementation's answer.** Compute the expectation from the
  input where you can — `text.IndexOf("extend")` beats a hardcoded `31`, because it still means
  something after the fixture is edited.
- **One property per test**, with a private static helper at the top of the class doing the setup
  (`Tokenize`, `Parse`, `Load` are the existing ones). `out var diagnostics` and
  `Assert.Empty(diagnostics)` is the standing idiom.
- **Prefer a sweep to a sample** when a property should hold everywhere: asserting it for *every*
  token in a fixture costs one loop and covers every path at once.
- **Say why in the assertion.** `Assert.True(cond, "the span must not end past the end of the text")`
  — a failure should diagnose itself.
- **Group long classes** with `// ------- section name` separators, as the existing suites do.
- Semantic behavior belongs in the **conformance corpus**
  ([tests/conformance/vectors](tests/conformance/vectors)), where it is compiled and executed in both
  backends. Unit tests cover the layer above that.

## Keep the spec current

[Protolang_Spec.md](Protolang_Spec.md) is the language, not a description of it. Anything that
changes what an author can write, what it means, or what the compiler tells them about it changes
the spec too, and that edit belongs in the **same commit as the code**. A spec that lags is still
consulted, and being trusted is exactly what makes a stale one expensive.

What counts:

- **Syntax, semantics, or the type system.** A new construct, a rule that moved, a case that used to
  be an error and is not.
- **A diagnostic.** §26 governs codes and rendering, and both are published output.
- **What the IR preserves.** §22.2 states the contract, and is currently behind it: it lists
  declaration sites and resolved type references, but not where each name was *used* (#40) or what
  was in scope at a position (#49), both of which the IR now carries. That gap is the worked example
  for this whole section — two issues shipped, neither said so, and the contract has to be read from
  the code instead.
- **An open question, once it is settled.** §30 is the authoritative list: strike the entry through,
  say what was decided, and name the section that decides it. §31 gets a dated row with the
  rationale. A decision argued out in a PR body and recorded nowhere else is one the next reader
  re-litigates from scratch.

What does not count is an internal with no observable surface. `BlockStatement.IsClosed` is a fact
about the parser rather than about the language, and the spec is not where it goes.

Say what moved under the PR's `## What`. If a change *should* move the spec and deliberately does
not — the wording is contested, or another branch owns that section — say that as well, so the
omission reads as a decision rather than an oversight.

## Never

- Move generated output or rendered diagnostics without saying so explicitly and diffing to prove
  the scope of the move.
- Let a backend see the AST, or branch on policy. Emission comes from IR behavior annotations.
- Add a CLI, editor, or file-system dependency to `ProtoLang.Core`.
- Bake one-file-per-compilation into a new API (#27 is coming).
- Add docs, changelogs, formatting passes, or coverage the task did not ask for. The spec is the
  exception, and only where the change actually reached the language; see above.

## Commits and pull requests

Subject line in the imperative, describing the change in the language of the problem, not the patch:
*"Give a span both of its ends"*, *"Hand the compiler text instead of a path"*, *"Survive malformed
input instead of taking the process down"*.

The body is prose wrapped near 80 columns — a few paragraphs on the defect, the shape of the fix, the
alternative rejected, and what did **not** move. Bullets only for a genuine list. Close with
`Closes #N. Part of #M.` and the `Co-Authored-By` trailer.

PR bodies follow the same voice with `## Why`, `## What`, `## Compatibility`, `## Tests` headings.
Compatibility is not optional: say what stayed byte-for-byte identical and how that was checked.

## Tooling notes

- Write and edit `.cs` files with the Write/Edit tools. Bash heredocs in this environment break on
  apostrophes in the body, which C# prose comments are full of.
- `perl -0777 -pi -e` is reliable for surgical multi-line replacements in existing files.
- `TreatWarningsAsErrors` is on, so an unused field or parameter fails the build. That is the check
  that a refactor left nothing dangling.
