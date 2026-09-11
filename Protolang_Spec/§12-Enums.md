## 12. Enums

**Decided: enum types and enum values are both named through the protobuf type universe.**

An enum type can be named wherever a type is expected, and an enum value is named
`<enum type>.<VALUE_NAME>`:

```protolang
extend Order {
    fn is_shipped() -> bool {
        return status == OrderStatus.SHIPPED;
    }

    fn shipped() -> OrderStatus {
        return OrderStatus.SHIPPED;
    }
}
```

Normative Requirements:

- Both the type and the value are resolved by full name or by an unambiguous simple name, including
  enums nested in messages. A simple name matching more than one type is `PL0074`; a name that is
  not a value of the named enum is `PL0076`.
- The value name is the one the `.proto` file declares, exactly as written. ProtoLang does not
  re-spell it, even though both backends do.
- A name that is in scope as a value wins over an enum type spelled the same way, so
  `something.field` stays a field access. Adding an enum to a schema must not silently change what
  an existing expression means.
- Enum values are ordinary expressions, so they are equally available in a `test` fixture and in an
  expectation. A fixture sets an enum field from a named value rather than from a nested block,
  which is reserved for message fields.
- Enums compare only for equality. Ordered comparison is rejected, because the numbers behind the
  values are a wire detail rather than a ranking the schema author asked for.

Backend obligations, because the two targets name a value in unrelated ways and neither spelling is
derivable from the other:

| ProtoLang | C# | C++ |
|---|---|---|
| `TopLevelStatus.TOP_LEVEL_STATUS_OK` | `TopLevelStatus.Ok` | `TOP_LEVEL_STATUS_OK` |
| `Outer.Nested.NESTED_SOME` | `Outer.Types.Nested.Some` | `Outer_Nested_NESTED_SOME` |
| `Outer.Inner.Deep.DEEP_NONE` | `Outer.Types.Inner.Types.Deep.None` | `Outer_Inner_Deep_DEEP_NONE` |

- **C#** strips the enum's own name from the front of the value, ignoring case and underscores, and
  PascalCases what is left. A value that does not carry the prefix keeps its whole name, and one
  where stripping would leave a leading digit gains an underscore. Backends must reproduce this
  exactly rather than approximate it: a near-miss names an identifier that does not exist, which
  fails in the consumer's build rather than in this compiler.
- **C++** keeps the declared spelling but places values at namespace scope, prefixing a nested
  enum's values with the flattened enum type name and leaving a top-level enum's values bare.
  protoc also emits a `static constexpr` member on the containing class, but the namespace-scope
  constant is the one every enum has.
- protobuf C++ enums additionally carry `_INT_MIN_SENTINEL_DO_NOT_USE_` values that are not part of
  the schema and must never be emitted. They will matter again for `switch`.

Open Questions:

- Should enum exhaustiveness be checked? proto3 enums are open -- a field may legally hold a number
  with no declared value -- so no switch over one is exhaustive at runtime regardless of the schema.
- Should unknown enum values be representable?
- Should an enum be convertible to or from an integer? 10.3 rejects the conversion for now.
