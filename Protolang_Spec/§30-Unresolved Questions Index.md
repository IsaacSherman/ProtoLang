## 30. Unresolved Questions Index

This section should be maintained as the authoritative list of open decisions.

- ~~File extension.~~ Decided: `.protolang` (5.1).
- ~~Direct import model.~~ Decided: `import proto "file.proto";` resolves `.proto` files through
  include paths and the source directory (5.2). Descriptor-set input remains open.
- ~~Package and namespace model for current source.~~ Decided: no independent ProtoLang package
  declaration; names come from protobuf descriptors (5.2). Future embedded-in-proto design remains
  open.
- ~~Semicolon requirement.~~ Decided: semicolons are mandatory for declarations/statements listed
  in 7.1.
- ~~Type inference policy.~~ Decided: local variables may state an explicit type or infer from the
  initializer (7.1, 8).
- Helper functions and whether top-level functions belong in the language.
- ~~Complete scalar type support.~~ Decided: all protobuf scalar spellings map into the supported
  ProtoLang value domains (8.2).
- Decimal support.
- ~~Nullability and presence syntax.~~ Decided: `has <field>`, with the field's own presence
  rules taken from the protobuf descriptor (8.4).
- ~~What reading an unset message field means.~~ Decided: it requires an established presence
  test, so the two backends have nothing to disagree about (13.1).
- ~~Which protobuf syntax versions are supported.~~ Decided: proto2, proto3, and editions, with
  no version check in the compiler (21.3).
- ~~Whether arithmetic behavior is selectable per project.~~ Decided: `protolang.config.xml`,
  with the file winning over command-line flags (10.4).
- ~~Boolean operator spelling.~~ Decided: both word and symbolic forms are accepted (9.2).
- ~~Assignment expression vs statement.~~ Decided: assignment is statement-only (9.2).
- Evaluation order details for non-short-circuit binary operators.
- ~~Integer overflow model.~~ Decided: wrapping (10.1).
- ~~Division and modulo by zero.~~ Decided: mandatory `on_zero` clause, with `fail` for the case
  where no substitute value is correct (10.2.1). `Result` (19) is explicitly deferred, not blocked.
- ~~Explicit cast syntax.~~ Decided: `x as int64`, numeric scalars only (10.3).
- ~~Numeric conversion rules.~~ Decided: integer targets wrap, floating point to integer
  truncates and saturates with NaN mapping to zero (10.3).
- String indexing and comparison semantics.
- ~~How protobuf enum values are referenced.~~ Decided: `EnumType.VALUE_NAME` (12).
- Enum unknown-value behavior, and whether an enum converts to or from an integer.
- Message construction support.
- Message equality semantics.
- ~~Repeated field mutation rules for current implementation.~~ Decided: no repeated mutation;
  only locals can be assigned (14, 18). Future mutation syntax remains open.
- Map support and map iteration order.
- Switch support.
- ~~Method overloading.~~ Decided: not supported (16.1).
- Receiver mutation and possible const/mut method split.
- Virtual method inclusion in version 1.
- Portable override registration model.
- Error result model.
- ~~External function support.~~ Decided: hard no for current language; methods call only ProtoLang
  methods (20).
- `protoc` plugin and Buf integration strategy.
- ~~ProtoLang unit test declaration syntax.~~ Decided: `test` declarations in `.protolang` files
  (25.3). Separate `.protolangtest` files remain open.
- Generated test output framework options and future `protoc` plugin flag names.
- Stable IR format.
- Third-party backend support.
- ~~Generated API shape for implemented backends.~~ Decided: C# extension methods and C++ header-only
  free functions (24). Python remains open because no backend exists.
- Diagnostic compatibility.
- Language version declaration.
- Generated API compatibility.
- Recursion and resource limits.
- Partial semantic model policy after descriptor-load failures and unresolved imports. Current
  implementation does not bind without usable descriptors; whether to produce a lighter semantic
  model for unresolved imports remains open.
