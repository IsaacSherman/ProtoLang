## 19. Error Handling Without Exceptions

ProtoLang has no exceptions. The implemented failure model is:

- Compile-time rejection where portability cannot be guaranteed.
- `on_zero <fallback>` for recoverable integer division and modulo by zero.
- `on_zero fail` for deterministic terminal failure with exit code 70.

No built-in `Result` type, generated status type, or catchable runtime error model exists today.

Open Questions:

- Does ProtoLang define a built-in `Result` type? `I think we should- it doesn't have to be used, but it should be there for ease of use. ~IS`
- Are arithmetic errors expressible in the type system? `Yes, it should be a fairly expansive enum. ~IS`
  Note: overflow is no longer one of them. Since 10.1 defines overflow as wrapping, it is a defined
  result rather than a failure, and nothing about it needs to reach the type system.
- Can methods be declared as total, meaning they cannot fail at runtime? `I lean toward no, but this might be a nice optimization at some point. ~IS`
- How do target backends map error results idiomatically while preserving semantics?  `Error handling is heavily on the user to define; we'll provide the Result type with primitive error enums for primitive "exceptions" such as dividing by zero. More advanced stuff?  Users can (and should!) tailor that to their specific needs. ~IS`
