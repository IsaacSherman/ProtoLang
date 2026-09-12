## 8. Type System

### 8.1 Type Sources

Types come from:

- Protobuf scalar primitive types.
- Protobuf enum types.
- Protobuf message types.

Normative Requirements:

- ProtoLang does not define an independent application type system.
- ProtoLang value types are protobuf scalar primitives, protobuf enums, and protobuf messages.
- ProtoLang does not add non-protobuf numeric types such as `decimal`.
- `void` is a method return marker only; it is not a protobuf value type and cannot be used for fields, variables, or parameters.
- Repeated fields have a compiler type, `repeated <element>`, so they can be iterated, but there is
  no source syntax for declaring a repeated local or parameter.

Open Questions:

- Should type aliases be allowed if they resolve only to protobuf scalar, enum, or message types?
- Should helper/result types ever be allowed, or must error handling also be represented using protobuf-defined messages?

### 8.2 Protobuf Scalar Mapping

ProtoLang accepts all protobuf scalar spellings that name a value domain. Wire-encoding variants
collapse to the decoded value type:

| Protobuf spelling | ProtoLang type |
|---|---|
| `double` | `double` |
| `float` | `float` |
| `int32`, `sint32`, `sfixed32` | `int32` |
| `int64`, `sint64`, `sfixed64` | `int64` |
| `uint32`, `fixed32` | `uint32` |
| `uint64`, `fixed64` | `uint64` |
| `bool` | `bool` |
| `string` | `string` |
| `bytes` | `bytes` |

Normative Requirements:

- The spelling may be used in a ProtoLang type reference when protobuf defines it.
- Once decoded, `sint32`, `sfixed32`, and `int32` have the same ProtoLang type; likewise for the
  other encoding families above. Encoding is a schema concern, not a behavior-language concern.
- `bytes` is a valid type and field value type. The language currently has no bytes literal and no
  bytes-specific operators.
- Floating-point behavior is covered by the numeric rules in 10.

Open Question:

- Should version 1 add bytes literals or bytes-specific operations?

### 8.3 Non-Protobuf Types

Normative Requirements:

- ProtoLang does not support additional value types outside the protobuf type universe.
- `decimal` is not supported because Protocol Buffers do not define a decimal scalar type.
- Backends must not silently map a ProtoLang type to target-specific types such as C# `decimal`, Python `Decimal`, or arbitrary-precision numeric classes unless that value is represented by an explicit protobuf message type.

Implementation Note:

- Projects that need decimal-like behavior should define an explicit protobuf message, such as a fixed-scale money or decimal representation, and then define ProtoLang behavior over that message.

Open Questions:

- Should the standard library eventually provide recommended protobuf message shapes for common non-scalar concepts such as money, fixed-scale decimal, dates, durations, or UUIDs?

### 8.4 Nullability and Presence

**Decided: `has <field>` is syntax, and which fields answer it is protobuf's own question.**

Protobuf presence semantics differ between proto2, proto3, optional fields, messages, wrappers, and
repeated fields. ProtoLang does not re-derive those rules; it asks the descriptor.

```protolang
if has customer.email {
    return customer.email;
}
```

Normative Requirements:

- `has <field>` is a prefix expression of type `bool`. It binds at the same precedence as `not`, so
  `has a.b` tests `b` and `has a and has a.b` groups as written.
- Its operand must name a protobuf field. A local, a parameter, a literal, and a method result
  always hold a value, so there is no question to ask about them (`PL0080`).
- Asking about `a.b` reads `a`, so `a` is subject to 13.1 like any other read.
- A field admits the question exactly when protobuf says it has presence. That is one rule covering
  every case in the table below, rather than four rules the compiler could get out of step with a
  schema.
- `has` on a field with no presence is `PL0079`. It is not `false`: the field has no answer, and
  returning one would be a different question silently substituted for the one asked.

| Field kind | Presence | Unset reads as |
|---|---|---|
| Singular message | Yes, always | Nothing -- the read requires a guard ([13.1](./§13-Messages.md#131-field-access)) |
| proto3 singular scalar or enum | **No** | The type's zero, indistinguishable from a set zero |
| proto3 `optional` scalar or enum | Yes | The type's zero, distinguishable by `has` |
| proto2 singular field | Yes | The field's declared default |
| Repeated field | No | An empty collection |
| Map field | No | Not supported at all ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)) |

- An `optional` scalar set to its zero value, or an `optional` string set to empty, is **set**.
  `has` reports presence, not difference from the default.
- Reading a scalar never needs a guard, under any syntax version. Both targets have always agreed
  about an unset scalar; only message fields diverged.

Open Question:

- `oneof` has presence per case as well as per field, and nothing here addresses the case
  discriminator. No syntax, no IR node, no diagnostic.
