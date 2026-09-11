## 21. Interoperability With Protobuf

### 21.1 Descriptor Input

The compiler consumes protobuf descriptors produced from the `.proto` files named by `import proto`
declarations. The default descriptor loader invokes `protoc` with include paths and asks for
transitive imports.

Implementation Note:

- This aligns ProtoLang with `protoc`, Buf, and plugin-based code generation workflows.
- `CompilationResult.Imports` records every import declaration and whether it resolved, was not
  found, or was syntactically unwritten. Descriptor-load failures preserve this resolved-import list
  rather than replacing it with an empty one.
- A descriptor load produces the whole of what `protoc` emitted, not only the descriptors built from
  it: the `FileDescriptorSet` with the source info `--include_source_info` requests, and the file
  each schema in the transitive closure was read from. `CompilationResult.Schema` carries it. Source
  info is where a schema's declaration sites and doc comments live, so discarding it meant paying
  `protoc` to produce the one thing the compiler then threw away.
- That source info is answered, not merely kept. Given a message, enum, field, or enum value
  descriptor reachable from a compilation -- a field an `extend` block declares included, since it
  is a field like any other -- the compiler reports the schema that declares it, the
  range of the whole declaration, the range of the declared name inside it, and the leading,
  trailing, and detached comments written about it with the comment markers already removed.
  Missing information is ordinary rather than an error: a schema with no comments, a descriptor set
  built without source info, a file that cannot be read, a file whose bytes have changed since the
  descriptors were built, a recorded location naming a place the file does not have, and a schema
  `protoc` resolved from descriptors compiled into itself all give an answer with nothing in it -- which is a different answer from none at all, none meaning the
  compilation does not hold that file. A range is reported only against the exact bytes `protoc`
  compiled, because a range is measured against text and a range measured against the wrong text
  points confidently at the wrong characters; the answer follows the file rather than being settled
  once, so a schema edited after it has been located stops being located and one restored to those
  bytes is pointed at again, neither of them waiting on a recompilation. Documentation
  does not depend on the file at all, so a schema may be documented and nowhere on disk, or
  documented and since edited. The positions are the
  compiler's own -- 1-based lines and columns counting UTF-16 code units -- rather than the byte
  counts `protoc` reports, whose columns advance by the width of a character in bytes, jump a tab to
  the next multiple of eight, and count a byte-order mark as three characters of the first line.
  The conversion is made against the file's bytes rather than against its decoded text, so a schema
  `protoc` accepted but no decoder can fully read is located correctly all the same.
- Well-known schemas are answered by that same rule and no other. `google/protobuf/timestamp.proto`
  is a schema like any other; what differs between installations is whether a file backs it at all,
  since `protoc` resolves those schemas from descriptors compiled into the binary from version 33
  onwards and from files shipped beside the binary before that.
- **The names a schema makes reachable are published as one index rather than left to be
  re-derived.** A compilation reports every message and enum the imported schemas declare, nested
  declarations included, under both the full name and the simple one, as `CompilationResult.Types`.
  Type references are resolved against exactly that index, so a host predicting what a type position
  will accept asks it rather than walking the descriptors a second time. A second walk is not merely
  redundant. Enums and messages nested inside a message are reachable only by descending, so the
  first thing an independent walk omits is the nested enum; and what a caller must know about an
  ambiguous simple name is three different questions, not one. A receiver after `extend` is ambiguous
  only against other messages (`PL0020`). A type position takes messages and enums as a single name
  space, so a name matching one of each is as ambiguous as one matching two enums (`PL0074`). An enum
  in front of a dot is ambiguous only against other enums. An index that answered a single "is this
  ambiguous" would be wrong at two of those three sites, and wrong silently -- offering a name the
  compiler then refuses, or withholding one it would have accepted.
- A descriptor-load failure preserves `protoc`'s own report line by line, with the file and position
  each line names kept separate from its message, rather than only as prose inside a `PL0003`
  message. Publishing a schema error against the schema is only possible if that structure survives.
  It survives as far as the compilation: `CompilationResult.SchemaFailure` accompanies the `PL0003`
  whenever a schema load failed, and is present even when `protoc` was never reached, because
  reporting nothing and having nothing to report are different answers.
- Loading may be cached. Correctness is defined against the located `protoc`, the ordered include
  paths, and the content of every file in the transitive closure -- not against the files the
  compilation named, which do not determine the result. Caching is never observable: a cached load
  produces what a cold load would have produced, and a load that failed is not cached at all.
- **Every load is bounded in time, and there is no way to ask for an unbounded one.** A compiler an
  editor calls on every keystroke may not have a state in which one invocation stops it answering.
  A `protoc` that outstays its budget is stopped, along with the plugins it started, and the load
  fails; the descriptor set it was writing is deleted, and a delete that cannot be done now is done
  by the next load rather than abandoned.
- **A load stopped by its budget is reported apart from a schema `protoc` rejected**, as `PL0083`
  rather than `PL0003`, and the distinction survives to a host as the failure's kind. They are
  different things to act on: one names a line the author can go and correct, and the other says
  nothing whatever about the schema, which may be perfectly good. Reported under one code, the
  second reads as the first.
- **A load outlives the caller that asked for it.** A caller may abandon its wait -- an editor
  supersedes work constantly -- and abandoning it stops `protoc` only where that caller was the only
  thing the load existed for. A load reached through a cache belongs to the cache: the request that
  superseded this one usually wants the same schemas, so stopping it would discard exactly the work
  its successor needs. What bounds such a load is its budget, which is the other reason there is no
  way to switch that off.

Open Questions:

- Should the compiler run as a `protoc` plugin?  `Eventually. I don't think it's necessary right away, but should happen before v1. ~IS`
- Should it also support Buf plugin workflows?
- Should descriptor-set input be accepted directly, and how would it report per-import diagnostics?

### 21.2 Generated Code Integration

The current implementation defines:

- C# behavior is emitted as extension methods in generated static classes.
- C++ behavior is emitted as header-only free functions in the protobuf namespace.
- Target method names are mapped by the backend's protobuf naming convention helpers.
- Namespace/package mapping follows the generated protobuf target's conventions.

Open Question:

- Python and any future backend must define its generated API shape before it can be conforming.

### 21.3 Protobuf Editions and Syntax Versions

**Decided: proto2, proto3, and editions are all supported, and the compiler does not branch on the
version.**

The version mattered because presence rules differ by it. Once presence is a first-class question
([8.4](./§8-Type System.md#84-nullability-and-presence)), the compiler asks the descriptor rather than the syntax version, and
`FieldDescriptor.HasPresence` answers correctly for every one of them -- including editions, where
presence is a resolved feature rather than a property of the syntax line. A version check would be
a second, worse copy of a rule the protobuf runtime already implements.

Both non-proto3 cases were checked rather than assumed: protoc 31.1 generates C# for a proto2 file
and for an `edition = "2023"` file. The C# generator historically refused proto2, which is the only
reason this was ever in doubt.

Implementation Note:

- `FileDescriptor.Syntax` is deprecated in current protobuf runtimes, and its deprecation note says
  to use feature resolution instead. That is exactly what `HasPresence` consults, so taking the
  version out of the compiler follows the runtime's own advice rather than merely avoiding a
  warning.

Open Question:

- Whether a future edition could change a rule this specification states, and how the compiler would
  notice. Nothing here reads edition-specific features other than through `HasPresence`.
