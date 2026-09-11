## 7. Grammar and Syntax

This section describes the grammar implemented by the parser. The grammar is still summarized rather
than mechanically exhaustive.

### 7.1 Implemented Grammar

```ebnf
source_file       = { import_decl | extend_decl | test_decl };

import_decl       = "import" "proto" string_literal ";";

extend_decl       = "extend" qualified_name "{" { method_decl } "}";

method_decl       = [ "virtual" ] "fn" identifier
                    "(" [ parameter_list ] ")"
                    [ "->" type_ref ]
                    block;

parameter_list    = parameter { "," parameter };
parameter         = identifier ":" type_ref;

block             = "{" { statement } "}";

statement         = var_decl
                  | return_stmt
                  | if_stmt
                  | while_stmt
                  | for_in_stmt
                  | break_stmt
                  | continue_stmt
                  | block
                  | assignment_stmt
                  | expression_stmt;

var_decl          = "var" identifier [ ":" type_ref ] "=" expression ";";
return_stmt       = "return" [ expression ] ";";
if_stmt           = "if" expression block [ "else" ( if_stmt | block ) ];
while_stmt        = "while" expression block;
for_in_stmt       = "for" identifier "in" expression block;
break_stmt        = "break" ";";
continue_stmt     = "continue" ";";
assignment_stmt   = expression "=" expression ";";
expression_stmt   = expression ";";

test_decl         = "test" qualified_name string_literal
                    "{" { receiver_fixture | test_arg | test_expectation } "}";
receiver_fixture  = "receiver" "{" { fixture_field } "}";
test_arg          = "arg" identifier "=" expression ";";
test_expectation  = "expect" ( "return" expression | "fail" ) ";";
```

Normative Requirement:

- The final grammar must be unambiguous.
- Backend code generation must not depend on parser quirks or target-language parsing.
- Semicolons are mandatory after imports, variable declarations, `return`, `break`, `continue`,
  assignment statements, expression statements, scalar fixture fields, test arguments, and test
  expectations.
- Top-level helper functions are not implemented.
- Variable declarations may state an explicit type or infer from the initializer.
- A test declaration must contain a receiver fixture and an expectation. The parser accepts
  `receiver`, `arg`, and `expect` members in any order and reports missing required members after
  the block is parsed.
- Parser recovery synthesizes missing tokens and missing names so later compiler stages can continue
  reporting useful diagnostics and editor tooling can still anchor completion points.

Open Questions:

- Should top-level helper functions be allowed in a later version?
- Should this section be replaced with exact EBNF generated from or checked against the parser?
