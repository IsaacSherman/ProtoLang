## 11. Strings

### 11.1 String Model

Strings are Unicode text corresponding to protobuf `string`. The current language supports string
literals, assignment to string-typed locals, return values, parameters, field reads, and equality
or inequality against another string value of the same type.

Normative Requirements:

- No locale-sensitive operations are available.
- Ordered string comparison is not supported.
- String indexing, length, case conversion, and normalization operations are not implemented.

Open Questions:

- Should string indexing be supported at all?
- Should normalization be specified?
- Should string equality be specified in terms of Unicode scalar values, UTF-16 code units, or a
  protobuf-runtime guarantee?
- Should string comparison beyond equality be supported?
