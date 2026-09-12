## 5. Source Organization

### 5.1 Files

ProtoLang source files use the extension:

```text
.protolang
```

Decision:

- The extension is `.protolang`. The CLI and tests use this extension, and path-based compilation
  treats the file path as a source identity rather than deriving semantics from any alternate suffix.

### 5.2 Relationship to `.proto`

A ProtoLang file imports one or more protobuf schema files directly.

Example:

```protolang
import proto "inventory.proto";

extend InventoryItem {
    fn total_value() -> int64 {
        return quantity * unit_price;
    }
}
```

Normative Requirements:

- Imports use `import proto "path/to/schema.proto";`.
- The path is resolved against compiler include paths, then against the source file's own directory.
- **One directory is one search path, however it is spelled.** Include paths are searched in the
  order given, and a directory already in that order is not added again — whether the second
  spelling differs by case where the file system ignores case, by a trailing separator, by the
  alternate separator, or by being the source directory that would have been appended anyway. A
  directory searched twice is a redundant `--proto_path`, a diagnostic that names it twice, and, for
  a cached load ([21.1](./§21-Interoperability%20With%20Protobuf.md#211-descriptor-input)), a second key for one configuration.
- Well-known protobuf imports may be resolved by the descriptor loader's implicit include paths.
- **The directories an import is resolved against are one ordered list**, and one place answers for
  it: the include paths, then each source's own directory, then the loader's implicit ones. Everything
  that has to predict resolution — a diagnostic naming where the compiler looked, an editor offering
  the paths that would resolve — asks that list rather than assembling one of its own. Two assemblies
  are two answers to which root wins, and the disagreement surfaces as a path that is offered and then
  not found.
- **An import that resolves to nothing names the schema it came closest to naming**, where the
  directory it named holds one that differs from it by little enough to be a plausible slip. `PL0002`
  still says where the compiler looked; the near match comes first, because a list of directories only
  helps a reader who already knew what they were aiming at. Nothing is suggested from a directory the
  author did not name, and **nothing is suggested from a directory too broad to be read within a
  bounded amount of work** — a diagnostic that takes seconds to print is a worse failure than one that
  says less, and the nearest of a partial reading is not the nearest.
- A file with no `import proto` declaration does not reach binding (`PL0001`).
- ProtoLang does not define an independent package declaration. Message, enum, and field names come
  from protobuf descriptors.
- One file may import schemas whose descriptors contain multiple protobuf packages; each `extend`
  resolves its target message through the descriptor pool.

Open Questions:

- Should descriptor-set input exist in addition to direct `.proto` imports?
- Should ProtoLang ever be embedded directly in `.proto` files?

### 5.3 Compilation Unit

A compilation unit currently consists of:

- One ProtoLang source file.
- The protobuf descriptors referenced by those files.
- Compiler options.
- Backend target configuration.

Implementation Note:

- The `Compilation` object is internally shaped around a source set, but the public implementation
  still binds only one source document. Multi-file binding is intentionally not exposed yet.
