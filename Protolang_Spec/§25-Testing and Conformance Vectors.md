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
