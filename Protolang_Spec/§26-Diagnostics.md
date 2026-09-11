## 26. Diagnostics

The compiler should provide deterministic diagnostics for:

- Syntax errors.
- Unknown protobuf types.
- Unknown fields.
- Type mismatches.
- Unsupported backend features.
- Ambiguous method names.
- Non-portable operations.
- Invalid mutation.
- Missing return statements.
- Possible arithmetic errors, where statically detectable.
- Parser recovery without duplicate follow-on diagnostics for the same missing token or name.
- Partial binding diagnostics when a source has parse errors but valid descriptors.

Diagnostic template:

```text
PL####: short diagnostic title
file.protolang:line:column
message
optional help text
```

Code ranges:

| Range | Owner |
|---|---|
| `PL0001`–`PL0999` | The compiler front end: lexer, parser, binder |
| `PL1001`–`PL1099` | The C# backend |
| `PL1101`–`PL1199` | The C++ backend |
| `PL2001`–`PL2099` | The driver and the configuration file ([10.4](./§10-Numeric Semantics.md#104-compile-time-policy)) |
| `PL2100`–`PL2199` | Host configuration: settings, scopes, and precedence ([10.4.1](./§10-Numeric Semantics.md#1041-host-configuration)) |

A configuration diagnostic names `protolang.config.xml` and the line and column inside it, rather
than a position in a `.protolang` source. A host-configuration diagnostic has no file and no
position at all: it names the scope the setting was written at — `<user settings>`,
`<workspace settings>`, `<folder settings>`, `<environment>` — because a client sends settings as
values rather than as the text of the file it read them from.

Open Questions:

- Should diagnostic codes be part of the compatibility contract?
- Warnings exist today, so which warnings are compatibility-stable and which remain advisory?

### 26.1 Diagnostics in an Editor

**Decided: a host publishes every part of a diagnostic, reports one with no location at the start of
the document it belongs to, and puts a `protoc` failure both in the schema it names and on the import
that reached it.**

The template above is the command line's rendering. A host has the same information and a different
surface, and the decisions it has to make -- what a severity maps to, where help text goes, what to
do with a diagnostic that is nowhere -- are decisions about published output just as much.

Normative Requirements:

- **Severity is mapped, not invented.** The compiler has `Warning` and `Error`; a host publishes
  exactly those two. Nothing is promoted to an informational or hint level, because that would be a
  host asserting a distinction the language does not draw. Adding a third severity is a change to the
  compiler.
- **The code, the title, the message and the help text all survive.** Help is not dropped and not run
  into the message where the client can show it separately: several diagnostics put the only
  actionable instruction there. It is also carried structurally, so a later quick-fix feature reads
  the string the compiler wrote rather than recovering it from prose.
- **A diagnostic with no location is published at the very start of its document.** An unusable
  include path, an ignored setting, a refused configuration file: none is anywhere in the source, and
  all of them have to be seen. It is never converted from the 1-based scheme, which for a
  `SourceSpan.None` would name line zero minus one.
- **A diagnostic that does have a position is published against the file that position is in**, which
  is not always the file being compiled. A `protolang.config.xml` reports a line and a column inside
  itself, and a `protoc` failure reports a line and a column inside a `.proto`; published against the
  source buffer instead, an error on line 4 of the configuration file becomes a squiggle on line 4 of
  a source that says something else entirely, or past the end of a source shorter than it. Where the
  named file cannot be resolved to a document, the diagnostic goes to the document being compiled at
  its start rather than at that position: a range that is honestly wrong is worse than one that admits
  it knows nothing, and the message names the file either way.
- A configuration diagnostic with no position ([10.4.1](./§10-Numeric Semantics.md#1041-host-configuration)) is published against **every** open document,
  because that is the extent of what it affects. The ones with positions belong to the configuration
  file, and are published once however many documents that file governs.
- **A `protoc` failure is published in the `.proto` it names, at the position it gave, and summarized
  on the `import proto` declaration that reached that schema.** The import line is not optional: a
  reader looking at a ProtoLang buffer whose schema is broken must not be shown an empty problem
  list. Where the schema named is not the schema imported, the summary says so, because a squiggle on
  one file name reporting an error in another is otherwise simply confusing.
- A `protoc` message carries `protoc` as its source and **no** `PL` code. It has none in this
  compiler's numbering and inventing one would be ProtoLang asserting a taxonomy for another tool's
  output.
- Where `protoc`'s output was parsed into positions, it **replaces** the `PL0003` that carries the
  same text as prose rather than being published beside it. A `protoc` that could not be found at all
  produces no such output, and `PL0003` -- which then names everywhere the compiler looked -- is
  published unchanged.
- **A file's diagnostics survive while any open document still reports them.** Two documents
  importing one broken schema both report it, identical reports are published once, and closing one
  of them does not withdraw the other's.
- Diagnostics are cleared when a document closes, and when they stop applying.
- **An answer describes the version of the buffer it was computed against, and is never published
  for a version the buffer has moved past.** This is the rule a host is strictest about, because
  breaking it is the most visible failure it can have: the user corrects an error, watches the
  squiggle vanish, and then watches an older compilation finish and put it back. It holds for every
  kind of answer and not only for diagnostics -- a hover or a completion computed against text the
  user has already replaced is describing something nobody is looking at -- so a request that can
  answer only about a superseded version is refused as such rather than answered. A stale
  computation is discarded silently; a refusal is only for a request that is owed a reply.
- **Which buffer an answer is about is settled when the request arrives, not when it is answered**,
  and **the version number alone does not settle it**. A host that reads messages in order and then
  defers the lookup lets the edit queued behind a request be applied first, and measures the position
  against text the client had not sent when it asked — after which every staleness check agrees,
  because they are all asking about the wrong document. And a version is unique only within one open
  session: closing a document and reopening it starts the client's numbering again, so a version read
  before the close compares equal to a buffer that may hold something else entirely. The buffer a
  request was read against is therefore identified as a thing rather than as a number, which answers
  editing, closing and reopening at once. The configuration a request was resolved under is settled
  and checked the same way.
- **A compilation kept and answered from again is checked against everything it was computed from,
  and the buffer is only one of those things.** A host that answers questions between keystrokes
  keeps the compilation it built, because lexing, parsing and binding one file per keystroke is what
  it is avoiding. Three things decide that compilation and the editor owns one: the buffer, the
  configuration the document resolves to, and the schemas that configuration reaches. The other two
  live in files, and a file changes with no keystroke to notice it -- an imported `.proto` edited in
  another window, a branch switched underneath the session, a `protolang.config.xml` repaired after
  it was refused. So a kept compilation answers only while the configuration still resolves the same
  way and the schemas still stand as they were read; the second is the check a descriptor load
  already makes on its own entries ([21.1](./§21-Interoperability With Protobuf.md#211-descriptor-input)), asked one level up, because a host that skips it answers
  from a compilation the loader would itself have refused. Without this the cache is observable in
  exactly the way 21.1 forbids, and it is observable as the worst kind of wrong answer: a completion
  offering a field the schema no longer has, which goes on being offered until the user happens to
  type in this buffer.
- **A compilation whose schemas failed to load is not reused at all**, which is 21.1's rule applied
  at a second layer rather than a new one. There is no closure to compare against, because `protoc`
  never reported one, and treating that as "nothing to check" makes the refusal permanent: the author
  creates the missing schema or corrects the malformed one, and every answer still comes out of the
  failure until they edit the ProtoLang buffer -- which is the one thing they have no reason to do
  while waiting to be told the import is fixed. Neither may the dependencies be reconstructed to
  stand in for the closure. `protoc` blames a use rather than a declaration, so the file the author
  edits to fix it is routinely one nothing named; recovering the rest means reading `import`
  declarations out of schema text, which is this compiler holding a second opinion about another
  language's grammar, and a wrong one, since `protoc` accepts spellings a scan will miss and any
  bound on such a walk turns a missed dependency back into a permanent refusal. 21.1 already settles
  it one layer down -- a load that failed is not cached at all -- and a host that kept one would be
  reintroducing at its own layer precisely what the layer below refuses. What that costs is a load
  per question while the workspace is broken. What it costs otherwise is very little: no descriptors
  means no module, no types and no scope, so the answer being declined had almost nothing in it.
- **Closing a document withdraws what it published and abandons what is outstanding for it.** Work
  already under way may finish, since some of it is shared and cannot be recalled, but nothing it
  produces is published, and **work not yet started is not started**. The second half is not a
  refinement of the first: a host that answers requests concurrently has a bounded number of them
  running, and one of those held on behalf of a closed buffer is one a live buffer is waiting for.
  Discovering at the end that the answer is unwanted is correct and too late — it has already been
  paid for. The same reasoning applies to a buffer that merely moved: what is waiting for its turn is
  checked for freshness when it gets one, not only when it is finished.
