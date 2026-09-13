## 18. Mutability

The current language is read-only with respect to protobuf data. It can mutate local variables only.

Normative Requirements:

- Methods may read receiver fields.
- Methods may read fields through parameters, locals, loop bindings, and other message-valued
  expressions subject to the presence rule in 13.1.
- Methods may assign local variables declared with `var`.
- Methods may not assign receiver fields, nested message fields, repeated fields, parameters, or
  loop bindings. An assignment target that is not a local is `PL0034`.
- Methods cannot allocate new protobuf messages in ordinary method bodies.

Open Questions:

- Should receiver mutation be added later, and if so should read-only or mutable behavior be the
  default? `I lean toward no.  Methods can mutate receiver members by default. They should be declared const if they're to be read only. ~IS`
- Should mutation require an explicit marker? `No, but const methods should. ~IS`
- Should immutable and mutable methods generate different APIs?
