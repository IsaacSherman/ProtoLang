## 2. Goals

ProtoLang should:

- Define portable methods over protobuf message types.
- Use protobuf schemas as the source of data types and message structure.
- Provide explicit cross-language semantics.
- Avoid relying on target-language-specific features.
- Compile through a typed intermediate representation before target-language emission.
- Support per-language translation backends.
- Be small enough that generated behavior can be audited and tested across languages.
- Produce predictable generated APIs for C#, C++, and future backends.
- Allow conformance testing from shared source and expected behavior vectors.
