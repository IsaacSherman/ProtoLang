## 14. Repeated Fields and Collections

### 14.1 Supported Operations

Implemented operation set:

- Iterate in order with `for <name> in <repeated expression> { ... }`.
- Read the repeated field as a collection value for iteration.

Not implemented:

- Length.
- Indexing.
- Append.
- Clear.
- Element assignment.

Example syntax:

```protolang
var total: int64 = 0;

for item in invoice.items {
    total = total + item.amount;
}

return total;
```

Normative Requirement:

- `for` may iterate only a protobuf repeated field value. Iterating anything else is `PL0033`.
- Iteration order is the protobuf repeated-field order.

Open Questions:

- Should filtering, mapping, sorting, or aggregation helpers exist? `No. Basics only. ~IS`
- Should repeated field mutation ever be allowed? Current implementation says no by absence: only
  locals can be assigned.
- Should collection indexing be bounds-checked with explicit error results if indexing is added?
  `Ugh... probably. I really want to say no, but... I have a feeling that not doing this could lead to security concerns in some language or another. ~IS`

### 14.2 Maps

Protobuf maps are repeated key-value structures with language-specific APIs.

Current Status:

- Maps are not supported. A map field is rejected rather than treated as an ordinary repeated
  key-value message.

Open Questions:

- Is map iteration order specified or explicitly unspecified?
- What map operations are portable?
