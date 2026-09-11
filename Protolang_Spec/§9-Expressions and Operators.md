## 9. Expressions and Operators

### 9.1 Expression Categories

The language currently includes:

- Integer, floating-point, string, and boolean literals.
- Local, loop-binding, parameter, and implicit receiver field references.
- Field access through `.`.
- Method calls on message receivers.
- Arithmetic, boolean, and comparison expressions.
- Prefix field-presence checks with `has`.
- Explicit numeric conversions with `as`.
- Parenthesized expressions.

Not implemented:

- Top-level function calls.
- Indexing.
- Message literals in ordinary method bodies.
- Bytes literals.

### 9.2 Operators

Implemented operator set:

```text
+  -  *  /  %
== != < <= > >=
and or not
&& || !
has
=
```

`has` is a prefix operator on a field, producing `bool` (8.4). It sits at the same precedence as
`not`, and unlike every other operator its operand is a field rather than a value -- reading the
value is exactly what it must not do.

Normative Requirements:

- Both word and symbolic boolean operators are accepted: `and`/`&&`, `or`/`||`, and `not`/`!`.
- Assignment is a statement only.
- `%` is included and follows the same `on_zero` rule as integer `/`.

### 9.3 Evaluation Order

Normative Requirement:

- The evaluation order of expressions must be explicitly defined.
- Backends must preserve the specified evaluation order.

Current defined subset:

- Method call arguments evaluate left to right.
- Boolean `and` and `or` short-circuit left to right.
- Assignment evaluates the right-hand side before storing the result.

Open Question:

- Should all non-short-circuit binary operators evaluate the left operand before the right operand?
  This only becomes observable when an operand can terminate through `on_zero fail`.
