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
- Name resolution and type checking against real descriptors, including enum types and values
- Typed IR carrying resolved types, source locations, and per-operation arithmetic behavior
- Control flow: `if` / `else if` / `else`, `while`, `break`, `continue`, and `for`-`in`
- Explicit numeric conversions, `x as int64`, which is what makes mixed-width arithmetic writable
- C# backend (extension methods) and C++ backend (header-only free functions)
- Author-written `test` declarations, generated into xUnit tests and a C++ test executable
- A cross-language conformance suite that runs the same vectors in both backends

Not implemented: maps, presence, mutation, virtual methods, `Result` types, `switch`, and the Python
backend. Backends reject these rather than emitting something whose semantics differ from the spec.

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

### Generation Commands

The ProtoLang compiler generates behavior and test artifacts. Protobuf message classes are still
generated by `protoc`.

| Artifact | Command |
|---|---|
| C# and C++ ProtoLang behavior | `dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -o generated` |
| C# ProtoLang behavior only | `dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -t csharp -o generated` |
| C++ ProtoLang behavior only | `dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -t cpp -o generated` |
| C# behavior plus generated xUnit tests | `dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -t csharp -o generated --test-out generated/tests` |
| C++ behavior plus generated standalone tests | `dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -t cpp -o generated --test-out generated/tests` |
| All current ProtoLang behavior and tests | `dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -o generated --test-out generated/tests` |

Output layout:

| Path | Contents |
|---|---|
| `generated/csharp/ProtoLangArithmetic.g.cs` | C# runtime helpers for ProtoLang arithmetic semantics |
| `generated/csharp/simpleScript.g.cs` | C# extension methods generated from ProtoLang behavior |
| `generated/cpp/protolang_runtime.h` | C++ runtime helpers for ProtoLang arithmetic semantics |
| `generated/cpp/simpleScript.pl.h` | C++ header-only free functions generated from ProtoLang behavior |
| `generated/tests/csharp/simpleScript.tests.g.cs` | C# xUnit tests generated from ProtoLang `test` declarations |
| `generated/tests/csharp/ProtoLangTestSupport.g.cs` | C# support for `expect fail` tests. Emitted only when the source has one |
| `generated/tests/cpp/simpleScript.tests.cc` | C++ standalone test executable source generated from ProtoLang `test` declarations |

The compiler options used in those commands are:

| Option | Meaning |
|---|---|
| `-I`, `--proto_path <dir>` | Directory searched for imported `.proto` files. May be repeated. |
| `-o`, `--out <dir>` | Root directory for generated behavior artifacts. Each backend writes below `<dir>/<target>/`. |
| `--test-out <dir>` | Root directory for generated test artifacts. Each test backend writes below `<dir>/<target>/`. |
| `-t`, `--target <list>` | Comma-separated backend list: `csharp`, `cpp`. Defaults to all current backends. |

To generate the protobuf message classes consumed by generated ProtoLang code, run `protoc`
separately. For example:

```bash
protoc -I examples/protos --csharp_out generated/protobuf/csharp examples/protos/invoice.proto
protoc -I examples/protos --cpp_out generated/protobuf/cpp examples/protos/invoice.proto
```

The C# generated ProtoLang behavior and tests must compile in a project that also includes the
C# protobuf output. The C++ generated ProtoLang behavior and tests must compile with the C++
protobuf output, protobuf headers, and protobuf libraries.

### Generating ProtoLang Unit Tests

ProtoLang source can include declarative test blocks. The example script includes an Invoice test:

```protolang
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

Generate C# behavior and xUnit test source with:

```bash
dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -t csharp -o generated --test-out generated/tests
```

That writes production C# to `generated/csharp/` and generated xUnit tests to
`generated/tests/csharp/`. To make the generated tests appear in Visual Studio Test Explorer, put
that generated test directory under an SDK-style xUnit test project, or link/copy the generated
`*.tests.g.cs` file into one, then build the test project. The project must reference:

- the generated protobuf C# types for the imported `.proto` files
- the generated ProtoLang behavior source
- `xunit.v3` and `xunit.runner.visualstudio`

After the test project builds, Visual Studio discovers the generated `[Fact]` tests normally.

For C++, the same flag generates a small standalone test program:

```bash
dotnet run --project src/ProtoLang.Cli -- examples/simpleScript.protolang -I examples/protos -t cpp -o generated --test-out generated/tests
```

That writes `generated/tests/cpp/simpleScript.tests.cc`. Build it with the generated ProtoLang
header, generated protobuf C++ sources, protobuf headers, and protobuf libraries on your normal C++
compiler command line. The executable prints one line per test and a summary, and returns `0` when
all generated ProtoLang tests pass and non-zero otherwise:

```text
[ok] protolang.examples.Invoice.total_cents: sums line totals
protolang: 12 test(s), 0 failed
```

Passing `--run <name>` runs a single test instead; that is how the driver observes a test that
expects the process to terminate, described next.

### Tests That Expect Failure

`on_zero fail` says no substitute value is correct, so the program stops. A test can assert that:

```protolang
test InvoiceItem.strict_ratio "a zero divisor stops the program" {
    receiver {
        quantity = 0;
        unit_price_cents = 100;
    }

    expect fail;
}
```

A stopped program writes its reason to standard error and exits with code **70** (`EX_SOFTWARE`),
the same in every backend. That is `Environment.Exit` in C# and `std::_Exit` in C++, deliberately
not `Environment.FailFast` or `std::abort`: those are crash-reporting primitives, so on Windows
they hand the process to Windows Error Reporting and to whatever postmortem debugger is registered
-- meaning generated library code could put a dialog on a user's screen. `abort` is also catchable
through `SIGABRT`, so it would not even guarantee the program stops.

Nothing inside a process can observe the process ending, so both backends generate this as an
out-of-process test. The C# backend emits an extra `ProtoLangTestSupport.g.cs` and a module
initializer that lets the test assembly be relaunched for one named test; the generated `[Fact]`
starts that child and checks its exit code. The C++ driver reruns itself with `--run <name>` and
does the same. Neither needs any wiring from you beyond building the generated files as usual.

### Conformance Suite

The compiler's own cross-language test suite lives in
[tests/conformance/](tests/conformance/README.md). Each vector is a `.protolang` file whose `test`
declarations state an expected result once; every backend then compiles, builds, and executes them,
and all backends must agree. It covers the cases where the targets natively disagree: integer
overflow wrapping, `on_zero` and `on_zero fail`, `MIN / -1`, truncating integer division, and IEEE
754 division by zero.

It runs as part of `dotnet test`, and skips with a message naming the missing tool when protoc, a
C++ compiler, or a protobuf C++ install is not available.

```powershell
dotnet test tests\ProtoLang.Tests\ProtoLang.Tests.csproj --filter "FullyQualifiedName~Conformance"
```

### Optional C++ Smoke Test Dependencies

The test suite includes optional C++ smoke tests for generated code:

- a syntax-only test that parses generated ProtoLang headers with generated protobuf headers
- a link-and-run test that builds a tiny executable and verifies generated behavior

They need:

- a C++20 compiler: `clang++`, `g++`, or Visual Studio C++ Build Tools
- protobuf C++ runtime headers containing `google/protobuf/message.h`
- protobuf C++ libraries, for the link-and-run test

The repo includes a vcpkg manifest for the native dependency:

```powershell
vcpkg install
```

If `vcpkg` is not on `PATH`, run it by full path instead, for example:

```powershell
C:\vcpkg\vcpkg.exe install --triplet x64-windows
```

When using vcpkg manifest mode from the repository root, the test looks under
`vcpkg_installed/<triplet>/include`. It also checks `VCPKG_ROOT`, `VCPKG_INSTALLED_DIR`, common
system install paths, and the explicit override:

```powershell
$env:PROTOLANG_PROTOBUF_CPP_INCLUDE = "C:\path\to\protobuf\include"
```

Point that at the `include` directory of a complete install rather than at a bare copy of the
headers. The libraries, runtime binaries, and matching `protoc` are located beside it, and the tests
that build and execute generated C++ need all four. Given headers alone, those tests skip and say
which pieces were missing.

To run only the C++ smoke tests:

```powershell
dotnet test tests\ProtoLang.Tests\ProtoLang.Tests.csproj --filter "FullyQualifiedName~CppSyntaxSmokeTests" --logger "console;verbosity=normal"
```

To run the full suite, including the C++ smoke test when its native prerequisites are available:

```powershell
dotnet test tests\ProtoLang.Tests\ProtoLang.Tests.csproj
```

On Windows, the test can find Visual Studio C++ Build Tools even when `cl.exe` is not already on
`PATH`; it runs MSVC through `VsDevCmd.bat`. The link-and-run test currently targets MSVC with
vcpkg's `x64-windows` protobuf package, using vcpkg's matching `protoc.exe`, headers, import
library, and DLLs. If the C++ compiler or protobuf C++ install is not available, the relevant test
is skipped with a message. A fully active local run should report zero skipped tests.

### Architecture

| Project | Role |
|---|---|
| `src/ProtoLang.Core` | Lexer, parser, descriptor binding, type checker, typed IR |
| `src/ProtoLang.Backend.CSharp` | C# code generation |
| `src/ProtoLang.Backend.Cpp` | C++ code generation |
| `src/ProtoLang.Cli` | `protolangc` command-line driver |
| `tests/ProtoLang.Tests` | Lexer, parser, binder, and backend tests, plus the conformance harness |
| `tests/conformance` | Cross-language conformance vectors ([README](tests/conformance/README.md)) |

Backends depend only on the IR, never on the AST.
