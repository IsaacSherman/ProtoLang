# ProtoLang

ProtoLang is an experimental language for defining portable behavior over Protocol Buffer messages.

Protocol Buffers are excellent at defining shared data contracts, but they deliberately stop at data. A `.proto` file can tell C#, C++, Python, and other languages what a message looks like, but it cannot define the behavior that should live with that message. In practice, teams often reimplement the same methods in every target language and hope the implementations stay semantically identical.

ProtoLang is an attempt to fill that gap without turning protobuf into a full programming platform.

## Vision

The goal is simple: define behavior once, then transpile it into the languages where the protobuf messages are used.

ProtoLang should let a team write small, explicit methods against protobuf message types:

```protolang
extend InvoiceItem {
    fn line_total_cents() -> int64 {
        return quantity * unit_price_cents;
    }
}
```

That behavior can then be generated for C#, C++, Python, and potentially other languages, with the same core semantics in each target.

This is not meant to be a clever language. It is meant to be deliberately plain: old-fashioned pseudocode with enough type information and control flow to express common domain behavior clearly.

## Scope

ProtoLang is built around a small semantic core:

- Protocol Buffer messages and fields are the foundation of the type system.
- Methods are attached to protobuf message types.
- Methods are public.
- Semantics should be explicit and portable across target languages.
- The compiler should lower source code into a typed intermediate representation before emitting target-language code.
- Each target language should have its own backend with documented conformance requirements.

The first target backends under consideration are:

- C#
- C++
- Python

Other languages may be possible later if the core semantics remain small enough.

## Non-Goals

ProtoLang is intentionally not a general-purpose programming language.

It does not aim to support:

- Exceptions
- Inheritance as a language feature
- Private, protected, or internal methods
- LINQ-style query syntax
- Lambdas or anonymous functions
- Target-language syntactic sugar
- Threading, locks, async, or scheduling primitives
- File, network, console, database, or other I/O
- Reflection-dependent behavior
- A replacement for `.proto` schemas

These omissions are part of the design. The more ProtoLang depends on target-language-specific features, the harder it becomes to guarantee equivalent behavior across C#, C++, Python, and future backends.

## Virtual Behavior

One open design area is overridable behavior.

There is a useful distinction between supporting overridable methods and supporting inheritance. ProtoLang may eventually allow a method to be marked as `virtual`, meaning a backend can expose an appropriate override mechanism for that target language.

That does not necessarily mean ProtoLang should define subclasses or assume generated protobuf classes are good inheritance targets. Some protobuf runtimes make inheritance awkward or unsafe. A portable design may need to express "this behavior can be replaced" without promising "subclass this generated message."

This remains an active design question.

## Compiler Direction

The intended architecture is:

```text
ProtoLang source
    -> parser
    -> protobuf descriptor binding
    -> type checking
    -> typed IR
    -> language-specific backend
    -> generated C# / C++ / Python / ...
```

The typed IR is important. It gives the project a place to define semantics once before target-language code generation begins. Backend output should be judged against the IR and the language specification, not against whatever is convenient in a particular target language.

## Specification

The current draft specification template is in:

[Protolang_Spec.md](Protolang_Spec.md)

That document separates:

- Normative language semantics
- Backend and implementation details
- Open design questions

The project is still early, so the spec intentionally captures unresolved decisions rather than pretending the language is finished.

## Status

There is now a working compiler for a small slice of the language. It takes the example in
[examples/simpleScript.protolang](examples/simpleScript.protolang) all the way to C# and C++ source.

Implemented:

- Lexer and recursive-descent parser
- Protobuf descriptor binding via `protoc`
- Name resolution and type checking against real descriptors
- Typed IR carrying resolved types, source locations, and per-operation arithmetic behavior
- C# backend (extension methods) and C++ backend (header-only free functions)

Not implemented: conditionals and loops other than `for`-`in`, maps, presence, explicit casts,
mutation, virtual methods, `Result` types, and the Python backend. Backends reject these rather
than emitting something whose semantics differ from the spec.

### Building

```bash
dotnet test ProtoLang.slnx
```

### Running the compiler

```bash
dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -o generated
```

That writes `generated/csharp/` and `generated/cpp/`. Pass `-t csharp` or `-t cpp` for one target.

The compiler needs a `protoc` executable, because it consumes protobuf descriptors rather than
reparsing `.proto` files itself (spec 21.1). It looks at `PROTOLANG_PROTOC`, then `PATH`, then a
restored `Grpc.Tools` NuGet package, so a separate protoc install is usually unnecessary.

### Architecture

| Project | Role |
|---|---|
| `src/ProtoLang.Core` | Lexer, parser, descriptor binding, type checker, typed IR |
| `src/ProtoLang.Backend.CSharp` | C# code generation |
| `src/ProtoLang.Backend.Cpp` | C++ code generation |
| `src/ProtoLang.Cli` | `protolangc` command-line driver |
| `tests/ProtoLang.Tests` | Lexer, parser, binder, and backend tests |

Backends depend only on the IR, never on the AST.

