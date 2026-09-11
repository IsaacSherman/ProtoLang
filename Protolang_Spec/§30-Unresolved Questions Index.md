## 30. Unresolved Questions Index

This section should be maintained as the authoritative list of open decisions.

- ~~File extension.~~ Decided: `.protolang` ([5.1](./§5-Source Organization.md#51-files)).
- ~~Direct import model.~~ Decided: `import proto "file.proto";` resolves `.proto` files through
  include paths and the source directory ([5.2](./§5-Source Organization.md#52-relationship-to-proto)). Descriptor-set input remains open.
- ~~Package and namespace model for current source.~~ Decided: no independent ProtoLang package
  declaration; names come from protobuf descriptors ([5.2](./§5-Source Organization.md#52-relationship-to-proto)). Future embedded-in-proto design remains
  open.
- ~~Semicolon requirement.~~ Decided: semicolons are mandatory for declarations/statements listed
  in 7.1.
- ~~Type inference policy.~~ Decided: local variables may state an explicit type or infer from the
  initializer ([7.1](./§7-Grammar and Syntax.md#71-implemented-grammar), [8](./§8-Type System.md#8-type-system)).
- Helper functions and whether top-level functions belong in the language.
- ~~Complete scalar type support.~~ Decided: all protobuf scalar spellings map into the supported
  ProtoLang value domains ([8.2](./§8-Type System.md#82-protobuf-scalar-mapping)).
- Decimal support.
- ~~Nullability and presence syntax.~~ Decided: `has <field>`, with the field's own presence
  rules taken from the protobuf descriptor ([8.4](./§8-Type System.md#84-nullability-and-presence)).
- ~~What reading an unset message field means.~~ Decided: it requires an established presence
  test, so the two backends have nothing to disagree about ([13.1](./§13-Messages.md#131-field-access)).
- ~~Which protobuf syntax versions are supported.~~ Decided: proto2, proto3, and editions, with
  no version check in the compiler ([21.3](./§21-Interoperability With Protobuf.md#213-protobuf-editions-and-syntax-versions)).
- ~~Whether arithmetic behavior is selectable per project.~~ Decided: `protolang.config.xml`,
  with the file winning over command-line flags ([10.4](./§10-Numeric Semantics.md#104-compile-time-policy)).
- ~~Boolean operator spelling.~~ Decided: both word and symbolic forms are accepted ([9.2](./§9-Expressions and Operators.md#92-operators)).
- ~~Assignment expression vs statement.~~ Decided: assignment is statement-only ([9.2](./§9-Expressions and Operators.md#92-operators)).
- Evaluation order details for non-short-circuit binary operators.
- ~~Integer overflow model.~~ Decided: wrapping ([10.1](./§10-Numeric Semantics.md#101-integer-overflow)).
- ~~Division and modulo by zero.~~ Decided: mandatory `on_zero` clause, with `fail` for the case
  where no substitute value is correct ([10.2.1](./§10-Numeric Semantics.md#1021-the-on_zero-clause)). `Result` ([19](./§19-Error Handling Without Exceptions.md#19-error-handling-without-exceptions)) is explicitly deferred, not blocked.
- ~~Explicit cast syntax.~~ Decided: `x as int64`, numeric scalars only ([10.3](./§10-Numeric Semantics.md#103-numeric-conversions)).
- ~~Numeric conversion rules.~~ Decided: integer targets wrap, floating point to integer
  truncates and saturates with NaN mapping to zero ([10.3](./§10-Numeric Semantics.md#103-numeric-conversions)).
- String indexing and comparison semantics.
- ~~How protobuf enum values are referenced.~~ Decided: `EnumType.VALUE_NAME` ([12](./§12-Enums.md#12-enums)).
- Enum unknown-value behavior, and whether an enum converts to or from an integer.
- Message construction support.
- Message equality semantics.
- ~~Repeated field mutation rules for current implementation.~~ Decided: no repeated mutation;
  only locals can be assigned ([14](./§14-Repeated Fields and Collections.md#14-repeated-fields-and-collections), [18](./§18-Mutability.md#18-mutability)). Future mutation syntax remains open.
- Map support and map iteration order.
- Switch support.
- ~~Method overloading.~~ Decided: not supported ([16.1](./§16-Methods.md#161-method-attachment)).
- Receiver mutation and possible const/mut method split.
- Virtual method inclusion in version 1.
- Portable override registration model.
- Error result model.
- ~~External function support.~~ Decided: hard no for current language; methods call only ProtoLang
  methods ([20](./§20-I-O, Threading, and Side Effects.md#20-io-threading-and-side-effects)).
- `protoc` plugin and Buf integration strategy.
- ~~ProtoLang unit test declaration syntax.~~ Decided: `test` declarations in `.protolang` files
  ([25.3](./§25-Testing and Conformance Vectors.md#253-author-written-protolang-unit-tests)). Separate `.protolangtest` files remain open.
- Generated test output framework options and future `protoc` plugin flag names.
- Stable IR format.
- Third-party backend support.
- ~~Generated API shape for implemented backends.~~ Decided: C# extension methods and C++ header-only
  free functions ([24](./§24-Generated API Strategy.md#24-generated-api-strategy)). Python remains open because no backend exists.
- Diagnostic compatibility.
- Language version declaration.
- Generated API compatibility.
- Recursion and resource limits.
- Partial semantic model policy after descriptor-load failures and unresolved imports. Current
  implementation does not bind without usable descriptors; whether to produce a lighter semantic
  model for unresolved imports remains open.
