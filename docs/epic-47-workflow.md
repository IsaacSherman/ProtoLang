# Implementing epic #47, one issue at a time

[Epic #47](https://github.com/IsaacSherman/ProtoLang/issues/47) — first-class editor support — is 26
sub-issues and counting. It is too large for one session, one branch, or one review. This file is the
process we run for each of them, so that the process itself does not have to be re-derived every
time context is cleared.

Read it together with [ARCHITECTURE.md](../ARCHITECTURE.md) and [CLAUDE.md](../CLAUDE.md).

## The shape of the work

The 12 issues originally sketched became the 26 sub-issues listed on #47 as each was specified in
detail; several of them name their own successor, so the plan lives partly in the issues rather than
only here.

**Fixed order first.** These modify existing compiler code and carry the regression risk. Nothing
else should start until they are in.

| | Issue | State |
|---|---|---|
| 1 | [#35](https://github.com/IsaacSherman/ProtoLang/issues/35) Compile from in-memory source text | done |
| 2 | [#37](https://github.com/IsaacSherman/ProtoLang/issues/37) Give `SourceSpan` a real end position | done |
| 3 | [#36](https://github.com/IsaacSherman/ProtoLang/issues/36) Bind through parse errors | done |
| 4 | [#39](https://github.com/IsaacSherman/ProtoLang/issues/39) Preserve declaration sites and symbol identity; scope publication remains #49 | done |

**Then mostly issue order**, following each issue's own instructions about what comes next. A few
ordering constraints from #47 are easy to miss and are not implied by the numbers:

- **#53 before #42**, or #42, #45 and #46 each invent part of a settings model and the parts disagree.
- **#48 before #41.** #48 must retain the `FileDescriptorSet` and mappable `protoc` stderr, not just
  built descriptors, or #41 forces a redesign of it.
- **#42 ships lexical semantic tokens only**; classifying an identifier as a local needs #38 and #39.
  Its token legend has to leave room for #50.
- **#55 gates #45.** Untrusted-workspace posture is a prerequisite for shipping the extension to
  anyone, not a refinement.
- **#57 informs design, not just verification.** Latency targets decide whether scope data is cached
  or recomputed; pinning them late means revisiting those decisions expensively.

## One session per issue

Each issue gets a fresh session with no inherited context. The issues are written to be read cold —
that is deliberate, and it is why the process below starts by reading the issue and the epic rather
than by picking up where anything left off.

## The loop

### 1. Pick the issue and plan

```bash
gh issue view 36
```

Read the sub-issue **and** [#47](https://github.com/IsaacSherman/ProtoLang/issues/47), then
[ARCHITECTURE.md](../ARCHITECTURE.md). Trace the actual call sites before planning — the issues are
specific about scope ("constructed in only eleven places") and those claims are worth verifying, not
assuming.

Plan carefully before writing. Where an issue deliberately leaves a decision open, decide it explicitly and ask
rather than picking silently; those choices are the ones that cost the most to reverse three issues
later.

### 2. Branch

Off `epics/language-server-2`, never off `main`. The first epic branch was merged and closed once
its wave landed; each wave gets a fresh one, so `main` sees one pull request rather than one per
issue.

```bash
git checkout epics/language-server-2 && git pull && git checkout -b issue-36-bind-through-parse-errors
```

Naming: `issue-<number>-<short-slug>`.

### 3. Implement

Follow [CLAUDE.md](../CLAUDE.md). Finish the whole issue, including its requirements list read as a
checklist — the issues state requirements sentence by sentence and each one is meant literally.

### 4. Commit

The implementation, as one commit, in the house style: imperative subject, prose body on the defect
and the fix, `Closes #N. Part of #47.`

### 5. Test

```bash
dotnet build ProtoLang.slnx
```

```bash
dotnet test ProtoLang.slnx
```

Both must be clean, warnings included. Then, whenever the change could have moved published output —
generated code, rendered diagnostics — **prove it did not** rather than asserting it did not. A
throwaway worktree at the base commit makes that a two-command check:

```bash
git worktree add /tmp/base epics/language-server-2
```

Run the CLI on the same input from both trees and `diff` the results. Remove the worktree afterwards.

### 6. Review

```bash
/code-review high
```

Run it on every issue. On the ones that modify existing compiler code (#36, #37, #39), Isaac also
reviews by hand — so the session must hand over an explicit short list of what deserves a human eye:
behavior that moved, assumptions taken, anything that was hard to test. Bury nothing in a summary.

### 7. Fix, then review again

Repeat 5–6 until a review comes back clean. Re-run the full suite after each round of fixes.

### 8. Update ARCHITECTURE.md if the shape changed

Most of this epic adds surface: a server project, a queryable semantic model, new public types on the
pipeline. [ARCHITECTURE.md](../ARCHITECTURE.md) is a living document — if the issue added a project,
a pipeline stage, or a type a cold reader would need to know about, update it now. A stale map is
worse than none, because it is trusted.

### 9. Commit the fixes

Separate commit or commits. No squashing before the PR; the review history is worth keeping.

### 10. Open the PR

```bash
gh pr create --base epics/language-server-2 --title "..." --body-file pr-body.md
```

Base is always the current epic branch, `epics/language-server-2`. The body carries `## Why`,
`## What`, `## Compatibility`, and `## Tests`, and states plainly what did not move and how that was
verified.

### 11. Clear context and repeat

## Non-negotiables carried from #47

These apply to every issue in the epic and do not need restating in each one.

1. **The CLI must not regress.** Generated output stays byte-for-byte identical, and every existing
   test keeps passing **unmodified**. Where an issue changes a public shape it says so explicitly and
   states the compatibility story — as #37 did, keeping `Line`, `Column`, `Length` and `File` as
   members so no consumer had to change.
2. **Prefer additive work.** New types and new projects over reshaping what exists. Only #35, #36,
   #37 and #39 genuinely have to touch the compiler. Rewriting the binder is the signal to stop and
   re-scope.
3. **One server, two editors.** VS Code and Visual Studio both consume LSP. Build the server once;
   editor work is packaging, not logic.
4. **Do not assume single-file forever.** #27 proposes multi-file compilation units. Prefer designs
   where "the compilation" is an object that could later hold more than one file.
