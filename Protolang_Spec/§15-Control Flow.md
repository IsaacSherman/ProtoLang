## 15. Control Flow

### 15.1 Conditional Statements

```protolang
if condition {
    ...
} else if other_condition {
    ...
} else {
    ...
}
```

Normative Requirements:

- The condition is an expression of type `bool`. There is no truthiness: a numeric, string, or
  message value in condition position is a diagnostic (PL0071), not a shorthand for a comparison.
- The condition is unparenthesized and every branch is braced. There is no single-statement form,
  so no statement can dangle off an `if`.
- An `else` binds to the nearest unmatched `if`. `else if` is a chain rather than a block
  containing a nested `if`, and generated code preserves that shape.
- A method that declares a return type must not be able to reach the end of its body. An `if`
  guarantees that only when it has an `else` and every branch guarantees it.

### 15.2 Loops

The current implementation has two loop forms:

```protolang
while condition {
    ...
}

for item in collection {
    ...
}
```

Normative Requirements:

- A `while` condition is an expression of type `bool`, under the same rule as 15.1.
- `for` iterates a protobuf repeated field in field order ([14](./§14-Repeated%20Fields%20and%20Collections.md#14-repeated-fields-and-collections)).
- `break` exits the innermost enclosing loop and `continue` advances it to its next iteration.
  Either one outside a loop is a diagnostic (PL0072, PL0073).
- The compiler performs no termination analysis. `while true` is legal, and a method whose only
  exit is a `return` inside `while true` satisfies the missing-return check, because control
  cannot reach the end of the body. A `break` that can leave that loop makes the end reachable
  again, and the method then needs a return after it.

Open Questions:

- ~~Should numeric `for` loops exist?~~ Decided: yes. `Yes. ~IS` Not yet implemented; `for`-`in`
  remains the only `for` form.
- ~~Should `break` and `continue` be supported?~~ Decided: yes, and implemented. `Yes. ~IS`
- ~~Should loops require static termination checks?~~ Decided: no. `No. ~IS`

### 15.3 Switch

Open Question:

- Should `switch` be included, or should version 1 use only `if` / `else if`? `Yes.  Support switch, including enums. ~IS`
