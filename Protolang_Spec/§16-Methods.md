## 16. Methods

### 16.1 Method Attachment

Methods are attached to protobuf message types using `extend`.

```protolang
extend DetectorReading {
    fn calculate_rate() -> double {
        return counts / live_time on_zero fail;
    }
}
```

Normative Requirements:

- All ProtoLang-defined methods are public.
- Method behavior must not depend on target-language inheritance.
- Method names share a namespace with protobuf fields on the receiver. A method whose name collides
  with a field is `PL0023`.
- Overloading is not supported. Two methods with the same name on the same receiver are `PL0022`,
  even if their parameter lists differ.
- A method whose declaration is syntactically incomplete may still be bound into a partial semantic
  model for editor use, but it is not callable when it lacks a usable declaration name.

Open Questions:

- Should methods ever be allowed to mutate the receiver?

### 16.2 Parameters and Returns

Normative Requirements:

- Parameters are named and typed: `name: type`.
- `void` is allowed only as an omitted or explicit method return type. It is not allowed for
  parameters or variables (`PL0024`).
- Methods have either one return value or no return value. Multiple return values are not supported.
- A non-void method must return a value on every path that can reach the end of the body.
- A void method may use `return;`.
- Parameters are not assignable. The only assignment target currently supported is a local variable.

Open Questions:

- Should recoverable errors be represented through user-defined protobuf messages, a future standard
  library `Result`, or some other convention?
