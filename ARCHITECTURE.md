# ProtoLang architecture

A map for a cold start: what exists, where it lives, and which invariants constrain a change. The
language itself is specified in [Protolang_Spec/](Protolang_Spec/README.md); how to write code here is in
[CLAUDE.md](CLAUDE.md); the per-issue process for the editor-support epic is in
[docs/epic-47-workflow.md](docs/epic-47-workflow.md).

ProtoLang compiles small methods written against protobuf messages into equivalent C# and C++.
Behavior is defined once and generated per target, and it has to mean the same thing in each.

## The solution

`ProtoLang.slnx`, six projects, `net10.0`. Settings are central in
[Directory.Build.props](Directory.Build.props): nullable enabled, implicit usings, **warnings as
errors**, and `CheckForOverflowUnderflow=false` on purpose — the compiler must never inherit the
arithmetic behavior it exists to define.

| Project | Role |
|---|---|
| [src/ProtoLang.Core](src/ProtoLang.Core) | Lexer, parser, binder, IR, diagnostics, config. No CLI coupling. |
| [src/ProtoLang.Backend.CSharp](src/ProtoLang.Backend.CSharp) | C# emission, plus generated test projects. |
| [src/ProtoLang.Backend.Cpp](src/ProtoLang.Backend.Cpp) | The same for C++. |
| [src/ProtoLang.Cli](src/ProtoLang.Cli) | `protolangc`: argument parsing, driving a compilation, writing files. |
| [src/ProtoLang.LanguageServer](src/ProtoLang.LanguageServer) | `protolang-server`: LSP over stdio, and the workspace configuration model under it. |
| [tests/ProtoLang.Tests](tests/ProtoLang.Tests) | One xunit project covering all of it. |

Dependencies run one way. Backends, the CLI and the language server reference Core; **Core
references nothing in the repo**. That is what lets a language server consume the compiler without
dragging the CLI along, and it is worth preserving.

## The pipeline

Driven by [`Compilation`](src/ProtoLang.Core/Compilation.cs). Three doors into it: the constructor
(hold it to recompile the same buffer), `Compile(SourceDocument, …)`, and `Compile(string path, …)`.

1. **Source.** [`SourceDocument`](src/ProtoLang.Core/SourceDocument.cs) is text plus a
   `SourceIdentity` — the name diagnostics print, the directory that settles policy and anchors
   imports, and the path, which is `null` for a buffer that was never saved. `ReadFrom` is the only
   place the compiler reads ProtoLang source from disk.
2. **Policy.** The nearest `protolang.config.xml` at or above the source directory
   ([`ProjectConfig.Discover`/`Load`](src/ProtoLang.Core/Config/ProjectConfig.cs)). A config that
   exists and cannot be read **stops** the compilation rather than falling back to defaults. A host
   serving an editor settles this per document instead, through
   [`WorkspaceConfiguration`](src/ProtoLang.LanguageServer/Workspace/WorkspaceConfiguration.cs) — see
   *Configuration* below.
3. **Lex.** [`Lexer.Tokenize`](src/ProtoLang.Core/Syntax/Lexer.cs) → `List<Token>`. No token spans
   more than one line.
4. **Parse.** [`Parser.ParseCompilationUnit`](src/ProtoLang.Core/Syntax/Parser.cs) → the AST in
   [Ast.cs](src/ProtoLang.Core/Syntax/Ast.cs). Recursive descent, error-recovering, depth-budgeted
   (`MaxNestingDepth`) because a `StackOverflowException` cannot be caught. A name it expected and
   did not find is a [`SyntaxName`](src/ProtoLang.Core/Syntax/SyntaxName.cs) that says so, carrying
   the empty range where the name would go.
5. **No gate.** Parse errors do not stop the pipeline. A buffer being typed into is broken most of
   the time an editor asks anything about it, and what it most often asks — what may follow this
   dot — only the binder can answer.
6. **Descriptors.** Each import is resolved into an
   [`ImportResolution`](src/ProtoLang.Core/ImportResolution.cs) — resolved, not found, or never
   written — against the roots
   [`SchemaCatalog.RootsFor`](src/ProtoLang.Core/Binding/SchemaCatalog.cs) settles: the search paths,
   then the loader's own. The whole list is published on the result, and one that was not found is
   told which schema in the directory it named it came closest to. Then
   [`DescriptorLoader`](src/ProtoLang.Core/Binding/DescriptorLoader.cs) shells out to `protoc`
   (located by [`ProtocLocator`](src/ProtoLang.Core/Binding/ProtocLocator.cs)) and returns a
   [`DescriptorBundle`](src/ProtoLang.Core/Binding/DescriptorBundle.cs): the built `FileDescriptor`s,
   the `FileDescriptorSet` they came from with its `--include_source_info` source info, and the file
   each schema in the transitive closure was read from. `Load` still returns the descriptor list
   alone, so no existing caller moved. A [`DescriptorCache`](src/ProtoLang.Core/Binding/DescriptorCache.cs)
   on the loader's options keeps bundles, keyed by a
   [`DescriptorRequest`](src/ProtoLang.Core/Binding/DescriptorRequest.cs) — which `protoc`, which
   roots in which order, which files — and re-checked against a content hash of every file in the
   closure, because the request cannot name a schema that is only reached through an import. The
   loader is uncached unless a caller supplies one, `protoc` runs under a timeout — reported as
   `PL0083` and as a kind on the failure, so an expiry is not mistaken for a schema error — and a
   failure keeps its report line by line as
   [`ProtocDiagnostic`](src/ProtoLang.Core/Binding/ProtocDiagnostic.cs) rather than only as prose.
   A cached entry holds the load as a `Task` rather than a `Lazy`, which is what lets a caller
   abandon its wait: `LoadBundle` and `Compile` take a `CancellationToken` that stops the *waiting*,
   and stops `protoc` itself only for a load no cache holds, because a cached load belongs to the
   cache and its successor usually wants exactly it. The source info the set carries is answered rather than merely kept:
   `DescriptorBundle.DeclarationOf` turns a message, enum, field or enum value descriptor into a
   [`SchemaDeclaration`](src/ProtoLang.Core/Symbols/SchemaDeclaration.cs) — the `.proto` it was
   written in, the range of the declaration and of its name, and the comments around it — through a
   per-file [`SchemaSourceIndex`](src/ProtoLang.Core/Binding/SchemaSourceIndex.cs) built on first ask
   and kept on the bundle. The same question is answerable from a `SymbolId` rather than a descriptor,
   which is the handle a caret produces: a name in type position resolves to a type and leaves no IR
   node behind, so an identity is all there is to ask with. Both doors are fed by one walk over what a
   schema declares ([`SchemaSymbols`](src/ProtoLang.Core/Binding/SchemaSymbols.cs)) — the bundle uses
   it to index which file declares each identity, the source index to address each declaration's
   `SourceCodeInfo` path — because two descents disagree first about an enum nested in a message.
   That is what lets go-to-definition and hover cross the file boundary, which is where most of what a
   ProtoLang file talks about lives.
7. **Bind.** [`Binder.Bind`](src/ProtoLang.Core/Binding/Binder.cs) resolves names against the
   descriptors and produces typed IR. It does **not** throw on bad input: an unresolved name becomes
   `ErrorType` (`PL0037`) and binding continues, a name the parser never saw resolves to `ErrorType`
   in silence, and an extend block whose receiver cannot be resolved is skipped because there is no
   message to bind against. Declarations inside a resolvable receiver are kept as far as possible:
   every local, parameter, loop binding and method carries a
   [`DeclarationSite`](src/ProtoLang.Core/Symbols/DeclarationSite.cs), so a reference can reach the
   declaration it means and say which symbol that is. It also records the other direction as it
   goes: every name it resolves becomes a [`SymbolReference`](src/ProtoLang.Core/Symbols/SymbolReference.cs)
   in `IrModule.References`, spanning the name alone. That has to happen here rather than in a later
   pass, because a type reference resolves to a type and leaves no IR node behind, and because the
   spans the IR does carry are extents — `IrMethodCall` covers its arguments — rather than names. Its
   `Scope` chain is published the same way, as a flat list of
   [`ScopeEntry`](src/ProtoLang.Core/Symbols/ScopeEntry.cs) in `IrModule.Scope`: one entry per name
   that *entered* a scope, carrying the range it can be written over and the offset it starts
   resolving from. Recorded where each name is declared, because whether a name won is decided there
   and nowhere else — a parameter with no name, a second parameter of one name, and a `var` that
   collides with an enclosing one are all still in the IR and all resolve nothing, and the tree does
   not say so.
8. **Result.** `CompilationResult` carries the IR *even when the file did not parse*, the syntax
   tree, the descriptors, the whole `Schema` bundle they came from, the import outcomes, the
   diagnostics, the settled config, and the search paths that were used. When the schemas could not
   be loaded it carries `SchemaFailure` instead — `protoc`'s own report, line by line with positions,
   beside the `PL0003` that renders it as prose. `Module` is null only when the compilation stopped before the binder: an
   unreadable config, an unusable include path, or a schema that could not be found or loaded.
   **`Module` is the partial one. Emit from `EmittableModule`**, which is null unless the
   compilation produced a whole program.
9. **Emit.** Backends consume the IR only.

Both trees are **addressable**: [`SemanticModel.For(result)`](src/ProtoLang.Core/Semantics/SemanticModel.cs)
answers "what is at this offset" for the syntax tree and for the IR, hands back the chain of nodes
above the answer, and crosses between the two by span. The rule the awkward positions follow — a
caret at the end of an identifier, between two nodes, on an empty range — is written once in
`PositionSearch` and documented on the query methods.

The same model answers the reference questions: `ReferenceAt` turns a caret into a symbol,
`ReferencesTo` gives every place that symbol is written with its declaration among them and marked,
and `DeclarationOf` gives the two ranges an editor navigates with — null for a field, an enum
constant or a type, whose declaration is in a `.proto` this compiler does not own. Nothing is cached:
a keystroke produces a new compilation and a new model over it, and the index that merges the
binder's references with the declarations is built on the first question that needs it.

What the model deliberately cannot answer is where a schema element is declared, and it says so:
that is a `.proto` this compiler does not own. The other side is `DescriptorBundle.DeclarationOf`,
which takes a descriptor or a `SymbolId`, and `SchemaTypes.Find`, which turns an identity back into
the message or enum it names. A host joins the two — see *Serving an editor* — and exactly one of them
answers for any symbol that resolved.

`ScopeAt` is the third question: what a bare identifier written at this offset could mean, as the
names in scope there with their types and declarations, plus the receiver they are looked up against.
It is narrower than "everything nameable" on purpose — a method resolves only in call position and a
type only in type position, so neither is in the list, and where a local and a field of the receiver
share a spelling only the one that binds is. That contract, *everything offered binds and nothing
that binds is missing*, is what makes it safe for completion to accept an entry blind.

## Key types

| Concern | Type | File |
|---|---|---|
| Location | `SourceSpan`, `SourcePosition` | [Diagnostics/SourceSpan.cs](src/ProtoLang.Core/Diagnostics/SourceSpan.cs) |
| Whether two paths are one path | `PathIdentity` | [PathIdentity.cs](src/ProtoLang.Core/PathIdentity.cs) |
| A document, to an editor and to the compiler | `DocumentUri` | [Workspace/DocumentUri.cs](src/ProtoLang.LanguageServer/Workspace/DocumentUri.cs) |
| What an editor may configure, and where it wins | `WorkspaceConfiguration`, `ProtoLangSettings` | [Workspace/WorkspaceConfiguration.cs](src/ProtoLang.LanguageServer/Workspace/WorkspaceConfiguration.cs) |
| What one document compiles under | `DocumentConfiguration`, `ConfigurationSource` | [Workspace/DocumentConfiguration.cs](src/ProtoLang.LanguageServer/Workspace/DocumentConfiguration.cs) |
| One JSON-RPC conversation | `JsonRpcConnection`, `MessageReader` | [Protocol/JsonRpcConnection.cs](src/ProtoLang.LanguageServer/Protocol/JsonRpcConnection.cs) |
| The server itself | `LanguageServerHost` | [Hosting/LanguageServerHost.cs](src/ProtoLang.LanguageServer/Hosting/LanguageServerHost.cs) |
| Who is told what is wrong with which file | `DiagnosticRouter`, `DiagnosticContribution` | [Hosting/DiagnosticRouter.cs](src/ProtoLang.LanguageServer/Hosting/DiagnosticRouter.cs) |
| What the compiler tells an editor to colour | `SemanticTokenLegend`, `SemanticTokenEncoder` | [Hosting/SemanticTokenLegend.cs](src/ProtoLang.LanguageServer/Hosting/SemanticTokenLegend.cs) |
| Where a comment was | `Comment` | [Syntax/Comment.cs](src/ProtoLang.Core/Syntax/Comment.cs) |
| Written or not-yet-written names | `SyntaxName` | [Syntax/SyntaxName.cs](src/ProtoLang.Core/Syntax/SyntaxName.cs) |
| What became of an import | `ImportResolution` | [ImportResolution.cs](src/ProtoLang.Core/ImportResolution.cs) |
| Which file a schema path names | `SchemaLookup` | [Binding/SchemaLookup.cs](src/ProtoLang.Core/Binding/SchemaLookup.cs) |
| Which roots are searched, and what they hold | `SchemaCatalog`, `SchemaCandidate` | [Binding/SchemaCatalog.cs](src/ProtoLang.Core/Binding/SchemaCatalog.cs) |
| What could be typed at a position | `CompletionProvider`, `ImportPathContext` | [Hosting/CompletionProvider.cs](src/ProtoLang.LanguageServer/Hosting/CompletionProvider.cs) |
| What a request is about, and what it owes for leaving the process | `DocumentRequest`, `DeferredAnswers` | [Hosting/DocumentRequest.cs](src/ProtoLang.LanguageServer/Hosting/DocumentRequest.cs), [Hosting/DeferredAnswers.cs](src/ProtoLang.LanguageServer/Hosting/DeferredAnswers.cs) |
| What the caret names, and where that was declared | `DeclaredSymbol` | [Hosting/DeclaredSymbol.cs](src/ProtoLang.LanguageServer/Hosting/DeclaredSymbol.cs) |
| What the pointer resting somewhere says | `HoverProvider`, `HoverCard` | [Hosting/HoverProvider.cs](src/ProtoLang.LanguageServer/Hosting/HoverProvider.cs), [Hosting/HoverCard.cs](src/ProtoLang.LanguageServer/Hosting/HoverCard.cs) |
| Where a name leads | `DefinitionProvider` | [Hosting/DefinitionProvider.cs](src/ProtoLang.LanguageServer/Hosting/DefinitionProvider.cs) |
| The shape of a file | `DocumentOutline` | [Hosting/DocumentOutline.cs](src/ProtoLang.LanguageServer/Hosting/DocumentOutline.cs) |
| Compiler coordinates ↔ editor coordinates | `EditorPositions` | [Protocol/Lsp/EditorPositions.cs](src/ProtoLang.LanguageServer/Protocol/Lsp/EditorPositions.cs) |
| What a descriptor load produced | `DescriptorBundle`, `SchemaFile` | [Binding/DescriptorBundle.cs](src/ProtoLang.Core/Binding/DescriptorBundle.cs) |
| What decides a load, and keys it | `DescriptorRequest` | [Binding/DescriptorRequest.cs](src/ProtoLang.Core/Binding/DescriptorRequest.cs) |
| Whether a load can be reused | `DescriptorCache`, `SchemaClosure` | [Binding/DescriptorCache.cs](src/ProtoLang.Core/Binding/DescriptorCache.cs) |
| Which way a load failed | `DescriptorLoadFailureKind`, `SchemaLoadFailure` | [Binding/DescriptorLoadFailureKind.cs](src/ProtoLang.Core/Binding/DescriptorLoadFailureKind.cs) |
| When a document is compiled, and whether the answer still counts | `CompileScheduler` | [Hosting/CompileScheduler.cs](src/ProtoLang.LanguageServer/Hosting/CompileScheduler.cs) |
| What `protoc` said, and about where | `ProtocDiagnostic`, `SchemaLoadFailure` | [Binding/ProtocDiagnostic.cs](src/ProtoLang.Core/Binding/ProtocDiagnostic.cs), [SchemaLoadFailure.cs](src/ProtoLang.Core/SchemaLoadFailure.cs) |
| Offset ↔ line/column | `LineMap` | [Diagnostics/LineMap.cs](src/ProtoLang.Core/Diagnostics/LineMap.cs) |
| Messages | `Diagnostic`, `DiagnosticBag` | [Diagnostics/Diagnostic.cs](src/ProtoLang.Core/Diagnostics/Diagnostic.cs) |
| Type system | `PlType` and friends | [Types/PlType.cs](src/ProtoLang.Core/Types/PlType.cs) |
| Typed IR | `IrNode`, `IrModule` … `IrLiteral` | [Ir/Ir.cs](src/ProtoLang.Core/Ir/Ir.cs) |
| Position and reference queries | `SemanticModel` | [Semantics/SemanticModel.cs](src/ProtoLang.Core/Semantics/SemanticModel.cs) |
| What is here, and what holds it | `SyntaxLocation`, `IrLocation` | [Semantics/NodePath.cs](src/ProtoLang.Core/Semantics/NodePath.cs) |
| Down through a tree | `SyntaxWalk`, `IrWalk` | [Semantics/SyntaxWalk.cs](src/ProtoLang.Core/Semantics/SyntaxWalk.cs) |
| Where a declaration is | `DeclarationSite` | [Symbols/DeclarationSite.cs](src/ProtoLang.Core/Symbols/DeclarationSite.cs) |
| Where a `.proto` declared it, and what it said | `SchemaDeclaration`, `SchemaSite`, `SchemaComments` | [Symbols/SchemaDeclaration.cs](src/ProtoLang.Core/Symbols/SchemaDeclaration.cs) |
| Everything a schema declares, once | `SchemaSymbols` | [Binding/SchemaSymbols.cs](src/ProtoLang.Core/Binding/SchemaSymbols.cs) |
| Which symbol a reference means | `SymbolId` | [Symbols/SymbolId.cs](src/ProtoLang.Core/Symbols/SymbolId.cs) |
| Where a symbol is used | `SymbolReference`, `ReferenceKind` | [Symbols/SymbolReference.cs](src/ProtoLang.Core/Symbols/SymbolReference.cs) |
| What a name is in scope over | `ScopeEntry` | [Symbols/ScopeEntry.cs](src/ProtoLang.Core/Symbols/ScopeEntry.cs) |
| What a bare name may mean here | `ScopeAtPosition`, `VisibleName` | [Semantics/ScopeAtPosition.cs](src/ProtoLang.Core/Semantics/ScopeAtPosition.cs) |
| What kind of symbol it is | `SymbolKind` | [Symbols/SymbolKind.cs](src/ProtoLang.Core/Symbols/SymbolKind.cs) |
| Emission behavior | `ArithmeticBehavior`, `ConversionBehavior` | [Ir/ArithmeticBehavior.cs](src/ProtoLang.Core/Ir/ArithmeticBehavior.cs) |
| Policy → behavior | `NumericPolicy` | [Ir/NumericPolicy.cs](src/ProtoLang.Core/Ir/NumericPolicy.cs) |
| Backend contract | `IBackend`, `ITestBackend`, `ITestProjectScaffold` | [Backend/IBackend.cs](src/ProtoLang.Core/Backend/IBackend.cs) |
| Identifier mapping | `NameConventions` | [Backend/NameConventions.cs](src/ProtoLang.Core/Backend/NameConventions.cs) |

### Diagnostics

`Diagnostic` is `(Code, Severity, Title, Message, Span, Help?)` — very nearly the LSP diagnostic
shape already, `Help` included. Codes are `PL####`. Rendering is
`CODE: title` / `file:line:column` / message / optional `help:` line, per spec 26. **That rendering
is published output**; a change to it moves what users see.

Spans are half-open, carry an absolute offset and line/column at both ends, and are 1-based on
line/column, 0-based on offset. `SourceSpan.None` is line 0 — out of band — and `IsNone` is the
question to ask before mapping a span to an editor range.

### Configuration

`protolang.config.xml` (spec 10.4) states the language-dependent policies: overflow, conversion,
divide-by-zero, unset-message reads. Discovery walks up from the source directory, the way
`.editorconfig` does. A command-line flag that contradicts an explicit setting is **refused** unless
`--override-config` is passed — generated code has to mean the same thing however it was built.

An editor adds an axis the command line never had: one process, many documents, one or more
workspace folders, each able to state settings of its own. Spec 10.4.1 settles that in the server
and `WorkspaceConfiguration.Resolve` is the only place it is applied. Configuration is resolved
**per document**, in the order folder → workspace → user setting → `PROTOLANG_PROTOC` → discovery.
Language policy stays out of settings entirely — a host may name a different `protolang.config.xml`
and may not restate what is in one — and **every setting that is not being used is reported**
(`PL2101`–`PL2105`), because a user who cannot tell a typo from a refusal has nothing to go on. A
`protolang.config.xml` that is found and cannot be read stops the document and is named as *refused*
(`PL2106`), rather than being reported as having supplied the defaults it did not supply.
`DocumentUri` and `PathIdentity` are between them the only places a URI becomes a path and two paths
are compared, which is what makes one file one document and one cache entry however it is spelled.

### Serving an editor

[`LanguageServerHost`](src/ProtoLang.LanguageServer/Hosting/LanguageServerHost.cs) is the whole
server: `protolang-server`, LSP over stdin and stdout, driven by VS Code and Visual Studio alike.
There is **no LSP framework**. Everything below
[`JsonRpcConnection`](src/ProtoLang.LanguageServer/Protocol/JsonRpcConnection.cs) is transport —
`Content-Length` framing, correlation, a writer gate — and nothing above it knows how a message is
framed, so the decision is one file wide.

Two rules in the connection are load-bearing and easy to undo. **Reading and handling are separate**:
the read loop parses, completes responses and honours `$/cancelRequest`, and everything else goes to
a queue one worker drains in order. That separation is what lets a handler ask the client a question
— `workspace/configuration` is a request the *server* sends — without waiting for itself. And when
the connection ends, outstanding work is cancelled **before** the dispatcher is awaited; the other
order waits forever for a handler whose answer is never coming.

Draining in order is the default and is right for a handler that is arithmetic over a buffer the
server already holds. A handler that **leaves the process** — one that opens a directory, or waits on
a tool — opts out with `OnRequest(..., concurrent: true)`, because answered in order it holds the
reading worker for as long as the outside world takes, and behind it sit every `didChange`, every
`didClose`, and the `$/cancelRequest` that would have shortened it. Completion is the only such
handler today, and what it owes in return is four things, all of them easy to get wrong:

- **Settle which buffer the request is about before yielding.** `CompletionProvider.Read` runs on the
  ordered worker; only the walk is deferred. Deferring the lookup lets the `didChange` behind the
  request be applied first, and the position is then measured against text the client had not sent —
  after which every staleness check agrees, because they are all asking about the wrong document.
- **Identify the buffer and the configuration as objects, not as a version and a generation.** Both
  are immutable, so holding them holds the question. A version number is unique only within one open
  session: close a document and reopen it and the client starts again at one.
- **Bound its own outstanding work.** A newer completion supersedes the outstanding one for its
  document, exactly as a keystroke supersedes a scheduled compile, and a semaphore bounds how many
  run across documents. A per-walk budget bounds one walk and says nothing about how many there are.
- **Give up the ones nobody is waiting for, before they take a slot.** `didClose` calls
  `CompletionProvider.Forget` beside `CompileScheduler.ForgetAsync`, and a request that reaches the
  front of the queue re-checks freshness before it walks rather than only after — an edit is the one
  reason for abandonment that carries no cancellation to notice. Waiting for a slot is part of the
  request: it sits inside the same cleanup as the walk, so a request that ends while waiting is still
  retired, and gives back only a slot it actually took.

Hover, go-to-definition and the outline arrive on the same terms and split three ways. The outline
lexes and parses and stops, so it answers on the ordered worker beside classification, never waits on
protoc, and survives a file that does not parse — which is the point of it, since an outline that
vanishes while you type is worse than a stale one. Hover and go-to-definition can only be answered by
the binder, so both compile through `DocumentSemantics` and both are concurrent, and everything the
architecture above demands of a concurrent handler is stated once in
[`DeferredAnswers`](src/ProtoLang.LanguageServer/Hosting/DeferredAnswers.cs) rather than three times:
supersession per document, a bounded gate, abandoning work nobody waits for, and the staleness
refusal. One instance per request kind, because a passing mouse must not cancel a deliberate click.

What they answer is joined in one place. `DeclaredSymbol` turns a caret into a symbol and then asks
whichever compiler owns the declaration — `SemanticModel.DeclarationOf` for a local, a parameter, a
loop binding or a method, `DescriptorBundle.DeclarationOf` for a field, an enum constant, a message or
an enum — so neither hover nor definition knows which side a symbol falls on. Hover adds the type,
the `.proto` comment, and the one thing the source text cannot show: the arithmetic policy in force,
read off the behavior the binder stamped on the node rather than off the configuration, and stated
only where a node carries one (spec 10.4). Capabilities are honoured throughout: links carry a
declaration's two ranges only to a client that declared `linkSupport`, and an outline nests only for
one that declared it can show a tree.

The buffer the client sent is the source of truth and the file on disk is never read for an open
document. Edits are applied incrementally, in order, each against the text the one before it
produced. A compile is debounced and coalesced, carries the document version and the configuration
generation it began under, and its result is **discarded rather than published** if either has moved
— the most visible failure a server can have is an old compile putting a fixed error back on screen.
The rule is not only about diagnostics: every answer describes the version it read, and a request
that could only answer about a superseded one refuses instead.

Cancellation reaches the one step that can outlast a keystroke. A superseded or closed document's
compile stops waiting on `protoc` and gives its worker back at once; everything after the load is
milliseconds and simply finishes into the discard. The queue holds one entry per document and a new
request supersedes the last, so it cannot outgrow the number of open documents, and `Pending`,
`InFlight` and `PeakInFlight` publish the backlog, what is running and the high-water mark. The
interval and the concurrency limit are still #57's to pin.

Diagnostics are published *per file* and produced *per compilation*, and the two stop lining up as
soon as a `.proto` can be blamed, so
[`DiagnosticRouter`](src/ProtoLang.LanguageServer/Hosting/DiagnosticRouter.cs) publishes the union of
what every open document says about a file. Two buffers importing one broken schema both report it,
identical reports collapse, and closing one does not withdraw the other's. Spec 26.1 has the rest:
severities mapped rather than invented, help text kept as its own thing, a locationless diagnostic
published at the start of its document, and a `protoc` failure landing both in the schema it names
and on the import that reached it.

Classification (spec 6.5) lexes and nothing more, so it answers for a file that does not parse. The
legend is the whole standard token set, declared now because it is negotiated once and indexed by
position; identifiers are uniformly `variable` until a semantic model can do better.

Completion is the same bargain and one step further out. `CompletionProvider` decides which context
the caret is in before it asks what belongs there, and today recognizes one — inside an `import
proto` string, found by `ImportPathContext` in the token stream, because the tree does not carry the
path's own span and the state this is invoked in is one the parser has already recovered from. What
is offered comes from
[`SchemaCatalog`](src/ProtoLang.Core/Binding/SchemaCatalog.cs), which is also where "the roots an
import is resolved against" now lives for everyone who asks: the include paths, then the source's own
directory, then whatever the loader adds. One directory listing per root, on demand, no index and no
cache — so progressive completion falls out of the shape rather than being built, and a schema that
appeared on disk a second ago is offered. Only the include roots are resolved for it, never the
language policy: settling that means searching upward for a `protolang.config.xml` and parsing it,
which decides nothing about where a path resolves and would be paid per keystroke. The listing is
lazy, reads each entry's kind from the same directory scan that found it, and carries a **budget in
entries examined**, because one level bounds depth and not breadth, and a root pointed at a vendored
tree or a network mount is one somebody will point at one. A walk that stops on its budget says so:
completion offers what it saw, since the list is already declared incomplete, and the near match
offers nothing, since the nearest of a partial reading is not the nearest. `#57` pins the figure for
both. The same catalog names that near match on `PL0002`, so the terminal and the editor say the
same thing about a path that resolved to nothing. Nothing here compiles, and this is the first
request that can go stale between reading the buffer and answering, so it re-checks the version and
refuses with `ContentModified` rather than inserting text at an offset that has stopped meaning what
it meant.

### Backends

Per spec 23 a backend consumes only the typed IR, never the AST, and rejects what it cannot support
rather than emitting something that quietly differs. A backend **cannot branch on policy**: how an
operation is emitted comes from the behavior annotation the binder stamped on the IR node. Policy
reaches a backend only as prose for the generated file's header.

## Tests

One project, [tests/ProtoLang.Tests](tests/ProtoLang.Tests), roughly organized by layer:
`LexerTests`, `ParserTests`, `ParserResilienceTests` and `BinderResilienceTests` (fuzz),
`SourceSpanTests`, `CompilationTests`, `InMemoryCompilationTests`, `PartialBindingTests`,
`SymbolIdentityTests`, `PositionQueryTests`, `ReferenceIndexTests`, `ScopeQueryTests`,
`DescriptorCacheTests`, `SchemaDeclarationTests`, `ProcessSupervisionTests`, `CompileSupervisionTests`,
`WorkspaceConfigurationTests`, `LanguageServerTests`, `SemanticTokenTests`, `SchemaCatalogTests`,
`ImportCompletionTests`, `SchemaCompletionTests`, `HoverTests`, `DefinitionTests`,
`DocumentSymbolTests`,
`TreeWalkTests`, `ImportResolutionTests`, `ProjectConfigTests`, `BackendTests`, `NameMappingTests`,
and the scaffolding and smoke suites.

- **Conformance corpus** — [tests/conformance/vectors](tests/conformance/vectors) holds `.protolang`
  files whose `test` blocks *are* the vectors, compiled and executed in both backends. This is the
  semantic gate: spec 25.2 left the vector format open and this repository answers it with the
  language's own `test` declaration, so a vector with a wrong-typed expectation is a compile error.
- **Harness** — [tests/ProtoLang.Tests/Harness](tests/ProtoLang.Tests/Harness) builds and runs real
  generated projects. Needs `protoc`, the .NET SDK, and a C++ toolchain.
- **Paths** — [TestPaths.cs](tests/ProtoLang.Tests/TestPaths.cs) finds the repository root and the
  fixture protos; use it rather than hand-rolling paths.
- **The server is driven over the wire** — [LanguageServerClient.cs](tests/ProtoLang.Tests/LanguageServerClient.cs)
  speaks framed JSON-RPC at a real host over a pair of in-memory streams, so the framing, the
  lifecycle gate and the dispatch order are under test rather than bypassed.

There is **no CI**. `dotnet test` locally is the gate.

## Invariants that constrain a change

1. **Generated output is byte-for-byte stable.** Any change that could move it gets diffed against
   the base commit, not asserted about.
2. **Rendered diagnostics are published output.** Format, codes, and positions are user-visible.
3. **Core stays free of CLI and editor coupling**, and dependencies keep running one way.
4. **The compiler does not throw on user input.** Bad source, bad config, and bad include paths are
   diagnostics. A long-lived host must survive all of them, through binding as well as parsing.
   Neither stage may throw, hang, or recurse without bound on any input at all.
5. **Backends see the IR only**, and cannot branch on policy.
6. **Do not assume single-file forever.** #27 proposes multi-file compilation units; `Compilation`
   already holds a *set* of sources for that reason.

## Where the editor-support epic lands

Epic [#47](https://github.com/IsaacSherman/ProtoLang/issues/47) makes the semantic model
*addressable* ("what is at line 12, column 7?") and *durable* (the binder discarded everything it
knew about a declaration as it went), then builds a language server on top. Both properties are in:
#35, #36, #37 and #39 were the four sub-issues expected to touch existing compiler code, and three
since have had to as well. #38 gave the IR an `IrNode` base so a path through it is expressible, and
stopped `BindInvocation` discarding the arguments of a call it could not resolve. #40 added a
recording line at each of the fifteen points the binder resolves a name, and turned
`IrAssignment.Target` into an `IrLocalReference` — the first change in this epic to reach a backend
file, and the reason `EmitStatement` now asks the expression emitter for the target it used to spell
itself. #49 gave `Scope` an extent and a recording line on each of the three branches where a
declaration is accepted, so that what the binder knew about visibility outlives the descent that
knew it. #48 made the descriptor load cacheable and stopped it discarding the descriptor set, which
reached `Compilation` twice: it now holds the loader it resolved rather than locating `protoc` again
per keystroke, and it publishes the bundle on the result. #53 opened the server project and settled
the configuration model in it before #42, #45 and #46 could each invent part of one; it reached Core
only to give "are these two paths the same path?" a single home, which is what collapses the
duplicate cache entries #48 left behind. #42 built the server itself — lifecycle, document sync,
diagnostics and lexical semantic tokens over a base protocol this repository owns — and reached Core
only to have the lexer keep the comment spans it was already walking past. #41 closed the second
wave by making the retained source info answerable, so a schema element's declaration and its doc
comment are reachable from a descriptor. #54 made abandoned work stop costing anything: a
cancellable wait on `protoc`, an expiry that says it is one, a stated queue bound, and counters that
turn "no leak over a working day" into a soak test. #56 opened the completion surface on the first
line anybody writes, and reached Core to give the include roots one home rather than the two
expressions that had been agreeing by coincidence. #43 answered what a dot can reach and what a bare
name may mean, from a compilation kept between keystrokes. #44 built the first navigation milestone —
hover, the document outline, go-to-definition — and reached Core twice, both times to open a door that
was missing rather than to reshape one: a schema declaration is now reachable by identity and not only
by descriptor, which is what a caret on a type name produces, and the walk that finds it is the walk
the source index already performed. Everything from here should be additive: new
types, new projects. Rewriting the binder is the signal to stop and re-scope.
