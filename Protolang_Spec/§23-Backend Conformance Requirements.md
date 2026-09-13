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
| Checked integer arithmetic | Yes | Yes | — | Terminates with exit code 70 on overflow. Selected by `Arithmetic/Overflow` ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). |
| Saturating integer arithmetic | Yes | Yes | — | Clamps to the exceeded bound. Selected by `Arithmetic/Overflow` ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). |
| Compile-time policy file | Yes | Yes | — | `protolang.config.xml`, found by walking up from the source ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). |
| Checked division (`on_zero`) | Yes | Yes | — | Runtime zero check in both; see 10.2.1. |
| `on_zero fail` | Yes | Yes | — | `Environment.Exit(70)` / `std::_Exit(70)`, after a diagnostic on stderr ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). |
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
| Conditionals and `while` | Yes | Yes | — | `if` / `else if` / `else`, `while`, `break`, `continue` ([15](./§15-Control%20Flow.md#15-control-flow)). |
| Field presence (`has`) | Yes | Yes | — | `x != null` or `HasX` in C#; `has_x()` in C++ ([8.4](./§8-Type%20System.md#84-nullability-and-presence)). |
| Unset message-field guard | Yes | Yes | — | Compile-time (`PL0078`), so neither backend emits a runtime check ([13.1](./§13-Messages.md#131-field-access)). |
| Proto2 presence | Yes | Yes | — | Via `FieldDescriptor.HasPresence`; no syntax-version branch ([21.3](./§21-Interoperability%20With%20Protobuf.md#213-protobuf-editions-and-syntax-versions)). |
| Proto3 optional | Yes | Yes | — | Same mechanism. |
| Editions | Yes | Yes | — | Same mechanism; presence is a resolved feature ([21.3](./§21-Interoperability%20With%20Protobuf.md#213-protobuf-editions-and-syntax-versions)). |
| Oneof | No | No | — | Blocked on the open question in 8.4. |
| ProtoLang `test` declarations | Yes | Yes | — | Both backends emit generated tests. |
| Test project scaffolding | Yes | Yes | — | C# `.csproj`; C++ `CMakeLists.txt`. |
| Partial semantic model after parse errors | Yes | Yes | — | Front-end feature; emitters use only `EmittableModule`. |
| Declaration sites and symbol IDs | Yes | Yes | — | Front-end/IR feature for editor tooling and stable references. |
