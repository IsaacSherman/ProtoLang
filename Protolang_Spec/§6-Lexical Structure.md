## 6. Lexical Structure

This section defines the token-level syntax.

### 6.1 Character Set

- Source files are UTF-8.
- Identifiers use .NET character classification: the first character is `char.IsLetter` or `_`, and
  following characters are `char.IsLetterOrDigit` or `_`.
- String literals support `\n`, `\t`, `\r`, `\\`, and `\"`.
- A string literal ends at the closing quote or at the end of the line. Multi-line string literals
  are not supported.
- Numeric literals use invariant-culture parsing. Integer literals fit in signed 64-bit storage
  before contextual typing; floating literals contain a fractional part and have no exponent syntax.

### 6.2 Comments

```protolang
// line comment

/*
   block comment
*/
```

Normative Requirements:

- `//` starts a line comment.
- `/* ... */` starts a block comment.
- Block comments are not nested.
- An unterminated block comment is `PL0004`.

Implementation Note:

- Comments are not tokens and the parser never sees one, but where each of them was is preserved:
  `Lexer.Comments` carries a range per comment, covering its delimiters, and a block comment's range
  crosses lines where the comment does. An unterminated one is recorded to the end of the text as
  well as reported.
- That exists so that 6.5's classification comes from the same scan that decides what a comment is.
  A host that recognized comments a second way -- a client-side grammar written in regular
  expressions is the obvious one -- would disagree with this lexer the first time somebody wrote
  `/* /* */`, and would then colour the rest of the file as a comment while the compiler went on
  reporting errors inside it.

### 6.3 Identifiers

The implemented rule is:

```text
identifier = (.NET letter | "_") { .NET letter-or-digit | "_" }
```

Identifiers are case-sensitive. ProtoLang does not impose a naming convention on source names.
Backends may map method names to target conventions when emitting public APIs ([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)).

Open Question:

- Should source names be restricted to ASCII before language stabilization to avoid backend-specific
  identifier edge cases?

### 6.4 Keywords

Reserved keywords:

```text
and
as
arg
bool
break
bytes
case
continue
double
else
enum
expect
extend
fail
false
float
fn
for
has
if
import
in
int32
int64
message
not
on_zero
or
proto
receiver
return
string
switch
test
true
uint32
uint64
var
virtual
void
while
```

Open Question:

- `case`, `enum`, `message`, and `switch` are reserved by the lexer but do not yet have source
  syntax.

### 6.5 Source Classification

**Decided: the compiler classifies source text, and publishes one fixed set of categories that a
later refinement adds to rather than changes.**

An editor colours ProtoLang from the compiler rather than from a pattern-matching grammar, so that
what is coloured as a keyword is what the lexer resolves as a keyword. The categories are LSP's
standard semantic token types; the set is fixed here because it is negotiated once per session and
indexed by position, so inserting a category later renumbers every category after it.

Classification is published in two layers over that one set. The first reads the token stream alone
and is always available, including for a file that does not lex cleanly. The second gives each
identifier the category of the symbol the binder resolved it to, and is available wherever there is a
compilation to read. The second is a refinement of the first and never takes anything back from it,
which is what makes it safe to apply only where it reaches.

Normative Requirements:

- The published category set is the standard LSP token type set, in its standard order, and the
  standard modifier set with it. Every category is declared whether or not anything currently
  produces it.
- A keyword ([6.4](#64-keywords)) is `keyword`, a string literal is `string`, an integer or floating-point literal is
  `number`, and a comment ([6.2](#62-comments)) is `comment`.
- `->`, `+`, `-`, `*`, `/`, `%`, `=`, `==`, `!=`, `!`, `<`, `<=`, `>`, `>=`, `&&` and `||` are
  `operator`.
- **From the token stream alone, every identifier is `variable`, whatever it names.** Distinguishing
  a local from a parameter from a field from a method is a semantic question, and this layer runs
  over the tokens so that a file which does not parse is still classified -- which is exactly when a
  reader needs it. A classification that is right sometimes is worse than one that is consistently
  coarse, because a wrong colour reads as a fact about the code.
- **An identifier the binder resolved takes the category of what it resolved to**: a parameter is
  `parameter`, a field of a message is `property`, a constant of an enum is `enumMember`, a ProtoLang
  method is `method`, a message type is `class`, an enum type is `enum`, and both a local and the
  name a `for` binds are `variable`. It is the binder's own resolution that is published, not a
  second lookup of the same name, so what is coloured is what the program means -- a value in scope
  winning over an enum type spelled the same way ([12](./§12-Enums.md#12-enums)) colours as the
  value.
- **A name that may not be assigned says so.** A parameter, the name a `for` binds, a message field
  and an enum constant carry `readonly`, because [18](./§18-Mutability.md#18-mutability) makes a
  local the only thing a method may assign. Where a name is introduced carries `declaration`, and the
  target of an assignment carries `modification` -- including an assignment the language refuses,
  since what is being described is what the author wrote.
- **Refinement adds and never subtracts.** An identifier that resolved to nothing, a file that did
  not parse, a schema that would not load, a category the client did not say it could paint: each
  keeps the answer the token stream gave. A file is never less classified for having been compiled.
- A name written as several tokens -- a message or enum named through its package -- classifies every
  identifier in it and still leaves the dots between them unclassified.
- Braces, parentheses, semicolons, commas, colons and the member dot are **not** classified. Nothing
  is conveyed by colouring them, and leaving them out lets a client's own grammar keep whatever it
  does with them.
- Classification never fails. A file that does not lex cleanly is classified as far as the lexer got.

Implementation Note:

- The categories still reserved and not yet produced -- `namespace`, `interface`, `struct`,
  `typeParameter`, `event`, `function`, `macro`, `modifier`, `regexp` and `decorator` -- are declared
  so that producing one later needs no renegotiation and repaints no open file. That is the same
  reason the seven unemitted modifiers are declared.
- Scalar type names are keywords ([6.4](#64-keywords)) and stay `keyword`. They are declared nowhere
  a reader could be sent, so there is nothing a refinement could say about one that the lexer has not
  already said, and recolouring them would put the two layers in disagreement about a token whose
  meaning never varies.
- A token may not cross a line in the published encoding, so a block comment is emitted as one token
  per line it touches.
- Columns count UTF-16 code units, matching `SourcePosition` and the protocol's default encoding.
