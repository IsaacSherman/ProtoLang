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
- **Every place a name was written, and which symbol it resolved to**, spanning the name alone rather
  than the construct around it. Recorded as the binder resolves, because that is the only point
  holding both the identity and the range of the name: a type reference resolves to a type and leaves
  no node behind, and the spans a node does carry are extents. A reference that did not resolve is
  not recorded; it refers to nothing.
- **What was in scope at each point of a method body**: one entry per name that entered a scope, with
  the range it can be written over and the offset it starts resolving from. Recorded where each name
  is declared, because whether a name won is decided there and nowhere else.
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

### 22.3 What a Compilation Answers

**Decided: the model is addressable by position and by identity, and a schema element is answerable
from either side of the file boundary.**

22.2 says what the IR keeps. This says what a caller may ask of it, because a host that cannot ask is
in the same position as one handed nothing. Every question here is answered by walking what the
binder already produced; none of it is cached, and a keystroke produces a new compilation and a new
set of answers over it.

Normative Requirements:

- **What is at this offset**, in the syntax tree and in the typed IR, with the chain of nodes above
  the answer and a correspondence between the two trees by span. Containment includes both ends, so a
  caret that has just finished typing a name still finds it.
- **What a bare identifier written here could mean**: the names in scope, with their types and
  declarations, and the receiver they are looked up against. Everything offered binds and nothing that
  binds is missing, which is what makes the answer safe to accept without re-checking.
- **Which symbol a name means, where it is declared, and everywhere it is used.** Identity is the
  declaration, never a spelling: two locals of one name in sibling blocks are two symbols, and one
  field reached bare and through a receiver is one.
- **Every name a file resolved, as one sequence in source order**, each spanning the name alone and
  saying what it resolved to and whether that use declared, read or wrote it. The same facts as the
  question above, asked of the file instead of of a symbol, because a caller describing the whole
  file -- classifying it ([6.5](./§6-Lexical%20Structure.md#65-source-classification)) -- would
  otherwise ask about a position once per name in it and scan the same answer each time.
- **Where a schema element is declared and what was written about it**, reachable both from the
  descriptor and from the identity the IR carries for it. The second is not a convenience: a name in
  type position leaves no IR node, so an identity is the only handle a caret there produces.
- A declaration answers with **two ranges** -- the whole construct and the name inside it -- on both
  sides of the file boundary, because an editor asks for both and derives neither. The name always
  lies inside the construct.
- **Absence is ordinary and is not an error.** A schema with no comments, a descriptor set built
  without source info, a well-known type `protoc` resolved from descriptors compiled into itself, a
  `.proto` that cannot be read, one edited since the descriptors were built: each yields a
  declaration with no site, or no documentation, or both. A caller that wants to navigate asks about
  the site and a caller that wants to explain asks about the documentation.

Open Questions:

- Should the IR be serialized as JSON, protobuf, or an internal compiler structure?
- Should backends consume a stable IR format?
- Should third-party backends be supported in version 1?
