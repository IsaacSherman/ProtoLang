## 28. Security and Determinism

Normative Requirements:

- ProtoLang behavior must be deterministic for a fixed input message and method arguments.
- No source-level access is provided to time, randomness, environment variables, filesystem, network, process state, or threads.
- Generated code must not depend on locale unless explicitly specified.
- Parser nesting is bounded to avoid compiler stack exhaustion on malformed or generated input.
- Runtime loops and recursive method calls are not currently resource-limited.

Open Questions:

- Should resource limits be specified for generated methods?
- Should recursion be allowed?
- Should the compiler reject potentially unbounded recursion?
