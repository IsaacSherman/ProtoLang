## 4. Terminology

Message:
: A protobuf message type defined in a `.proto` schema.

Field:
: A protobuf field belonging to a message.

ProtoLang method:
: A method defined by ProtoLang and associated with a protobuf message.

Receiver:
: The message instance a method operates on, equivalent to `this` or `self` in target languages.

Front end:
: The compiler components that produce the IR: lexer, parser, descriptor binding, name resolution,
  and type checking. The compiler-literature sense of the word, not the web one -- it has nothing to
  do with a user interface.

Backend:
: A compiler component that emits code for a specific target language. Everything after the IR.

IR:
: The typed intermediate representation produced after parsing, name resolution, and type checking.

Normative:
: Required behavior for conforming implementations.

Implementation-defined:
: Behavior that must be documented by each implementation or backend.

Unspecified:
: Behavior that programs must not rely on.
