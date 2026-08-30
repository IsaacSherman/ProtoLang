# ProtoLang architecture

A map for a cold start: what exists, where it lives, and which invariants constrain a change. The
language itself is specified in [Protolang_Spec.md](Protolang_Spec.md); how to write code here is in
[CLAUDE.md](CLAUDE.md); the per-issue process for the editor-support epic is in
[docs/epic-47-workflow.md](docs/epic-47-workflow.md).

ProtoLang compiles small methods written against protobuf messages into equivalent C# and C++.
Behavior is defined once and generated per target, and it has to mean the same thing in each.

## The solution

`ProtoLang.slnx`, five projects, `net10.0`. Settings are central in
[Directory.Build.props](Directory.Build.props): nullable enabled, implicit usings, **warnings as
errors**, and `CheckForOverflowUnderflow=false` on purpose — the compiler must never inherit the
arithmetic behavior it exists to define.

| Project | Role |
|---|---|
| [src/ProtoLang.Core](src/ProtoLang.Core) | Lexer, parser, binder, IR, diagnostics, config. No CLI coupling. |
| [src/ProtoLang.Backend.CSharp](src/ProtoLang.Backend.CSharp) | C# emission, plus generated test projects. |
| [src/ProtoLang.Backend.Cpp](src/ProtoLang.Backend.Cpp) | The same for C++. |
| [src/ProtoLang.Cli](src/ProtoLang.Cli) | `protolangc`: argument parsing, driving a compilation, writing files. |
| [tests/ProtoLang.Tests](tests/ProtoLang.Tests) | One xunit project covering all of it. |

Dependencies run one way. Backends and the CLI reference Core; **Core references nothing in the
repo**. That is what lets a language server consume the compiler without dragging the CLI along, and
it is worth preserving.

## The pipeline

Driven by [`Compilation`](src/ProtoLang.Core/Compilation.cs). Three doors into it: the constructor
(hold it to recompile the same buffer), `Compile(SourceDocument, …)`, and `Compile(string path, …)`.

1. **Source.** [`SourceDocument`](src/ProtoLang.Core/SourceDocument.cs) is text plus a
   `SourceIdentity` — the name diagnostics print, the directory that settles policy and anchors
   imports, and the path, which is `null` for a buffer that was never saved. `ReadFrom` is the only
   place the compiler reads ProtoLang source from disk.
2. **Policy.** The nearest `protolang.config.xml` at or above the source directory
   ([`ProjectConfig.Discover`/`Load`](src/ProtoLang.Core/Config/ProjectConfig.cs)). A config that
   exists and cannot be read **stops** the compilation rather than falling back to defaults.
3. **Lex.** [`Lexer.Tokenize`](src/ProtoLang.Core/Syntax/Lexer.cs) → `List<Token>`. No token spans
   more than one line.
4. **Parse.** [`Parser.ParseCompilationUnit`](src/ProtoLang.Core/Syntax/Parser.cs) → the AST in
   [Ast.cs](src/ProtoLang.Core/Syntax/Ast.cs). Recursive descent, error-recovering, depth-budgeted
   (`MaxNestingDepth`) because a `StackOverflowException` cannot be caught. A name it expected and
   did not find is a [`SyntaxName`](src/ProtoLang.Core/Syntax/SyntaxName.cs) that says so, carrying
   the empty range where the name would go.
5. **No gate.** Parse errors do not stop the pipeline. A buffer being typed into is broken most of
   the time an editor asks anything about it, and what it most often asks — what may follow this
   dot — only the binder can answer.
6. **Descriptors.** Each import is resolved against the search paths into an
   [`ImportResolution`](src/ProtoLang.Core/ImportResolution.cs) — resolved, not found, or never
   written — and the whole list is published on the result. Then
   [`DescriptorLoader`](src/ProtoLang.Core/Binding/DescriptorLoader.cs) shells out to `protoc`
   (located by [`ProtocLocator`](src/ProtoLang.Core/Binding/ProtocLocator.cs)) and returns
   `FileDescriptor`s. The `FileDescriptorSet` is currently discarded — #48 must stop doing that.
7. **Bind.** [`Binder.Bind`](src/ProtoLang.Core/Binding/Binder.cs) resolves names against the
   descriptors and produces typed IR. It does **not** throw on bad input: an unresolved name becomes
   `ErrorType` (`PL0037`) and binding continues, a name the parser never saw resolves to `ErrorType`
   in silence, and a declaration that cannot be resolved is dropped rather than half-built.
8. **Result.** `CompilationResult` carries the IR *even when the file did not parse*, the syntax
   tree, the descriptors, the import outcomes, the diagnostics, the settled config, and the search
   paths that were used. `Module` is null only when the compilation stopped before the binder: an
   unreadable config, an unusable include path, or a schema that could not be found or loaded.
   **`Module` is the partial one. Emit from `EmittableModule`**, which is null unless the
   compilation produced a whole program.
9. **Emit.** Backends consume the IR only.

## Key types

| Concern | Type | File |
|---|---|---|
| Location | `SourceSpan`, `SourcePosition` | [Diagnostics/SourceSpan.cs](src/ProtoLang.Core/Diagnostics/SourceSpan.cs) |
| Written or not-yet-written names | `SyntaxName` | [Syntax/SyntaxName.cs](src/ProtoLang.Core/Syntax/SyntaxName.cs) |
| What became of an import | `ImportResolution` | [ImportResolution.cs](src/ProtoLang.Core/ImportResolution.cs) |
| Offset ↔ line/column | `LineMap` | [Diagnostics/LineMap.cs](src/ProtoLang.Core/Diagnostics/LineMap.cs) |
| Messages | `Diagnostic`, `DiagnosticBag` | [Diagnostics/Diagnostic.cs](src/ProtoLang.Core/Diagnostics/Diagnostic.cs) |
| Type system | `PlType` and friends | [Types/PlType.cs](src/ProtoLang.Core/Types/PlType.cs) |
| Typed IR | `IrModule` … `IrLiteral` | [Ir/Ir.cs](src/ProtoLang.Core/Ir/Ir.cs) |
| Emission behavior | `ArithmeticBehavior`, `ConversionBehavior` | [Ir/ArithmeticBehavior.cs](src/ProtoLang.Core/Ir/ArithmeticBehavior.cs) |
| Policy → behavior | `NumericPolicy` | [Ir/NumericPolicy.cs](src/ProtoLang.Core/Ir/NumericPolicy.cs) |
| Backend contract | `IBackend`, `ITestBackend`, `ITestProjectScaffold` | [Backend/IBackend.cs](src/ProtoLang.Core/Backend/IBackend.cs) |
| Identifier mapping | `NameConventions` | [Backend/NameConventions.cs](src/ProtoLang.Core/Backend/NameConventions.cs) |

### Diagnostics

`Diagnostic` is `(Code, Severity, Title, Message, Span, Help?)` — very nearly the LSP diagnostic
shape already, `Help` included. Codes are `PL####`. Rendering is
`CODE: title` / `file:line:column` / message / optional `help:` line, per spec 26. **That rendering
is published output**; a change to it moves what users see.

Spans are half-open, carry an absolute offset and line/column at both ends, and are 1-based on
line/column, 0-based on offset. `SourceSpan.None` is line 0 — out of band — and `IsNone` is the
question to ask before mapping a span to an editor range.

### Configuration

`protolang.config.xml` (spec 10.4) states the language-dependent policies: overflow, conversion,
divide-by-zero, unset-message reads. Discovery walks up from the source directory, the way
`.editorconfig` does. A command-line flag that contradicts an explicit setting is **refused** unless
`--override-config` is passed — generated code has to mean the same thing however it was built.

### Backends

Per spec 23 a backend consumes only the typed IR, never the AST, and rejects what it cannot support
rather than emitting something that quietly differs. A backend **cannot branch on policy**: how an
operation is emitted comes from the behavior annotation the binder stamped on the IR node. Policy
reaches a backend only as prose for the generated file's header.

## Tests

One project, [tests/ProtoLang.Tests](tests/ProtoLang.Tests), roughly organized by layer:
`LexerTests`, `ParserTests`, `ParserResilienceTests` and `BinderResilienceTests` (fuzz),
`SourceSpanTests`, `CompilationTests`, `InMemoryCompilationTests`, `PartialBindingTests`,
`ImportResolutionTests`, `ProjectConfigTests`, `BackendTests`, `NameMappingTests`, and the
scaffolding and smoke suites.

- **Conformance corpus** — [tests/conformance/vectors](tests/conformance/vectors) holds `.protolang`
  files whose `test` blocks *are* the vectors, compiled and executed in both backends. This is the
  semantic gate: spec 25.2 left the vector format open and this repository answers it with the
  language's own `test` declaration, so a vector with a wrong-typed expectation is a compile error.
- **Harness** — [tests/ProtoLang.Tests/Harness](tests/ProtoLang.Tests/Harness) builds and runs real
  generated projects. Needs `protoc`, the .NET SDK, and a C++ toolchain.
- **Paths** — [TestPaths.cs](tests/ProtoLang.Tests/TestPaths.cs) finds the repository root and the
  fixture protos; use it rather than hand-rolling paths.

There is **no CI**. `dotnet test` locally is the gate.

## Invariants that constrain a change

1. **Generated output is byte-for-byte stable.** Any change that could move it gets diffed against
   the base commit, not asserted about.
2. **Rendered diagnostics are published output.** Format, codes, and positions are user-visible.
3. **Core stays free of CLI and editor coupling**, and dependencies keep running one way.
4. **The compiler does not throw on user input.** Bad source, bad config, and bad include paths are
   diagnostics. A long-lived host must survive all of them, through binding as well as parsing.
   Neither stage may throw, hang, or recurse without bound on any input at all.
5. **Backends see the IR only**, and cannot branch on policy.
6. **Do not assume single-file forever.** #27 proposes multi-file compilation units; `Compilation`
   already holds a *set* of sources for that reason.

## Where the editor-support epic lands

Epic [#47](https://github.com/IsaacSherman/ProtoLang/issues/47) makes the semantic model
*addressable* ("what is at line 12, column 7?") and *durable* (the binder currently discards scope
information as it goes), then builds a language server on top. Only four sub-issues touch existing
compiler code — #35, #36 and #37 (done), and #39. Everything else should be additive: new types,
new projects. Rewriting the binder is the signal to stop and re-scope.
