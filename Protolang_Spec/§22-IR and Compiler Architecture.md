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
