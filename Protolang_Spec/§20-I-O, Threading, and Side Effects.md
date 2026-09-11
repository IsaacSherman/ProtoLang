## 20. I/O, Threading, and Side Effects

Normative Requirements:

- ProtoLang has no I/O primitives.
- ProtoLang has no threading or concurrency primitives.
- ProtoLang has no clock, randomness, environment, filesystem, network, console, or process APIs.
- Backends must not silently introduce observable I/O or concurrency behavior into generated method bodies.
- Generated terminal-failure paths are the exception: `on_zero fail` writes a diagnostic to standard
  error and terminates the process as specified in 10.2.1.
- ProtoLang methods may call only ProtoLang methods resolved by the compiler.

Open Questions:

- Should deterministic pure helper functions be explicitly marked? `Seems unnecessary. ~IS`
