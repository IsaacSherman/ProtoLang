# Performance budgets and how they are measured

Several issues in [#47](https://github.com/IsaacSherman/ProtoLang/issues/47) said "fast enough to
feel instant" or "fast enough to run on caret movement". That is not a specification and it cannot
fail a build. This file is the specification.

The numbers do not need to be exactly right. They need to **exist**, so that somebody choosing
between two designs has something to check against, and so that a regression is a failing test
rather than a feeling that things got worse.

## What the budgets are

| Operation | Budget | Why |
|---|---|---|
| diagnostics after edit | 400 ms | measured after the debounce, so this is the compile itself; slower and the squiggles stop feeling attached to the typing that caused them |
| completion | 50 ms | above this the list arrives after the author has typed past the word it was offering |
| hover | 50 ms | the same threshold, because a hover is asked on dwell and answers into a gesture the reader has already committed to |
| occurrence highlighting | 20 ms | fires on caret movement, so it is paid on every arrow key; anything slower makes cursor motion itself feel heavy, which is the one cost a reader blames on the editor |
| go-to-definition | 100 ms | a discrete action with a visible result, so a little latency reads as the editor working rather than as lag |

**A descriptor cold load is measured and reported, never budgeted.** It is `protoc` starting, reading
a schema closure and writing a descriptor set, and this project does not choose how long that takes.
The budget on a cold load is that it happens once.

Each budget is the **95th percentile over the stress corpus, warm**. A median hides the keystroke
that stutters, and the stutter is the whole experience being budgeted for — nobody notices the
nineteen fast hovers. Warm means the descriptors are loaded and the buffer has been compiled, which
is the state an editor is in for every keystroke after the first.

These figures live in exactly one place a machine reads:
[`PerformanceBudgets.cs`](../tests/ProtoLang.Tests/Performance/PerformanceBudgets.cs). The table
above is the copy for people, and
`PerformanceBudgetTests.TheDocumentedBudgetsAreTheEnforcedOnes` fails if the two disagree — because a
rule written twice disagrees eventually, and the copy that would quietly stop being true is the one
people read.

## What "a normal file" means

Stated rather than assumed, which is what #57 asks for.

| | File | Lines |
|---|---|---|
| normal | [`examples/simpleScript.protolang`](../examples/simpleScript.protolang) | 274 |
| stress | [`tests/perf/corpus/wide.protolang`](../tests/perf/corpus/wide.protolang) | 2,814 |

**The normal case is a real file on purpose.** `simpleScript.protolang` is maintained for its own
reasons and goes on being edited by people who are not thinking about measurement, which is exactly
what keeps it representative. A fixture written for a benchmark drifts towards whatever the benchmark
finds convenient.

**The stress case is generated and committed, which looks like wanting it both ways and is not.** A
file generated at measurement time can be raised later but is a different file every time the
generator is touched, so two runs a month apart would not be comparable — and comparability is the
only reason to write numbers down. Committing the output settles it. The generator is
[`StressCorpus`](../tests/ProtoLang.Tests/Performance/StressCorpus.cs), and
`PerformanceCorpusTests.TheCommittedStressFileIsWhatTheGeneratorProduces` runs on every build, so the
two cannot drift.

It is shaped rather than merely long, because the operations above are bounded by different things:

- **170 methods on one receiver** — the breadth of scope a completion has to gather.
- **One method called from every one of them** (516 references) — the length of the list occurrence
  highlighting walks.
- **One body of 60 chained locals** — a scope deep in declarations rather than wide in members.

To raise it, change `StressCorpus.Steps` and rewrite the committed file from the generator.

## How to measure

```bash
PROTOLANG_BENCH=1 dotnet test ProtoLang.slnx --filter "FullyQualifiedName~Performance"
```

In PowerShell the variable is set separately — `$env:PROTOLANG_BENCH = 1` — and stays set for the
rest of the session, so clear it with `$env:PROTOLANG_BENCH = $null`.

Every run writes `artifacts/perf/report.md`: every operation, both corpora, median, p95, min, max,
and whether it was within budget. The report is written whether the run passes or fails, because the
question after "too slow" is always "by how much, and was it always?".

The measurement drives the providers directly rather than going over the wire. Framing, the reader
loop and the worker handoff are real costs, but they are not what a design decision moves, and
reporting them as the cost of a hover would be misleading. End-to-end is
[#58](https://github.com/IsaacSherman/ProtoLang/issues/58)'s to show.

## How regressions are caught

#57 left this open deliberately and asked for a decision rather than a default. The decision is
**both, split by what each kind of check is good at**:

- **CI checks counted work, on every pull request.** How many compilations a caret move costs, how
  many times `protoc` is invoked, how deep the queue gets, how many answers are in flight. These are
  deterministic: they cannot flake, and they fail the moment somebody adds a compile to a path that
  did not have one. That is
  [`PerformanceCostTests`](../tests/ProtoLang.Tests/Performance/PerformanceCostTests.cs).
- **A person checks milliseconds, on a machine they chose.** Wall-clock deadlines on a shared runner
  are a coin toss with a build attached: they flake until somebody loosens them, and a threshold
  loose enough never to flake no longer describes the budget. This repository has already spent two
  commits on deadlines that were fine locally and were not fine on a runner.

The honest weakness of that split is the one #57 names: a benchmark nobody runs is worth little. What
makes this one worth running is that it **asserts** as well as reports — a run that is over budget
fails and says by how much — so it is a check rather than a printout.

## What the measurements found

Measured 2026-09-12 on a 16-processor desktop, .NET 10.0.11. Reproduce with the command above; the
numbers below are a snapshot and the report is the authority.

| Operation | Normal p95 | Stress p95 | Budget |
|---|---:|---:|---:|
| hover | 0.5 ms | 0.9 ms | 50 ms |
| occurrence highlighting | 0.7 ms | 1.2–1.7 ms | 20 ms |
| go-to-definition | 0.7 ms | 1.2–1.5 ms | 100 ms |
| diagnostics after edit | 1.0 ms | 29–33 ms | 400 ms |
| completion | 1.9 ms | 41–48 ms | 50 ms |

Ranges where repeated runs on the same machine disagreed by more than rounding. That spread is
itself a result: a single figure would imply a precision these measurements do not have, and the
operation whose spread matters is the one closest to its ceiling.

**Four of the five have one to two orders of magnitude of headroom, and that is the finding.** #57
exists partly to inform design — whether scope data is cached, whether occurrence highlighting can
consult the reference index directly, whether completion can resolve documentation eagerly. The
answers are: **no caching is warranted**, **yes it can**, and the reference index at 1.2 ms against a
20 ms budget on a file ten times normal size is not something to optimise. Anything built on top of
those paths to make them faster would be paying complexity for latency nobody can perceive.

**Completion on the stress corpus is the one operation near its budget** — 37 ms median and 48 ms at
p95 against 50 ms on the slowest run observed, and it did not clear 41 ms on the fastest. It is
within budget and it is the row that will not stay within budget by accident. It is the row a new
feature should be measured against before it is added, and `ScopeSearch` — the linear scan it leans
on hardest — is the first place to look if it ever goes over.

The budget was **not** tightened to match the other four, and that is deliberate: a budget is a
threshold a person can feel, not a ratchet against the last measurement. Tightening hover to 2 ms
because it measures 0.9 ms would fail on a slower machine without anything having got worse, and
would say nothing about whether a hover felt slow. Regressions are the cost assertions' job.

### Descriptor loads, and the bounds that come off them

**A cold load of the examples' schema closure is 21–33 ms**, essentially all of it `protoc` starting.
Warm, the same load is 0.13 ms — a factor of about 200, which is the entire argument for the cache
existing.

| | Wall clock | Thread pool |
|---|---:|---|
| 1 cold load | 25 ms | 5 → 7 |
| 4 cold loads (the concurrency limit) | 44 ms | 7 → 15 |
| 8 cold loads (twice the limit) | 76 ms | 15 → 21 |

**The bounds #54 guessed at are now measured, and all three stand:**

- **The `protoc` timeout of 30 s is a backstop, confirmed.** A cold load is three orders of magnitude
  under it. It exists for a hung process, not for a slow one, and nothing normal approaches it.
- **The concurrency limit of 4 stays 4.** #54 changed `DescriptorCache` to hold each load as a `Task`
  rather than a `Lazy`, so a superseded compile can abandon its wait — at the cost of a second
  blocked thread per load. The measurement shows exactly that: roughly two pool threads per
  concurrent load, 7 → 15 at four and 15 → 21 at eight. Wall clock still scales sub-linearly, so the
  pool is absorbing it **here**. The honest caveat is that this machine has 16 processors, and the
  default minimum worker count is one per processor: this is the best case, not the typical one. A
  four-core machine running four concurrent cold loads is the case that would bite, and raising the
  limit doubles the thread demand with it. **So the limit is not raised, and the reason is a
  measurement rather than caution.**
- **The cache capacity of 16 stays 16, and its footprint is now a number**: about 28 KiB per retained
  bundle for the examples' closure, so about 0.4 MiB for a full cache of that closure. That is small
  enough that capacity is not what bounds memory here — but the examples' closure is two messages,
  and a real workspace's is not, so the figure to carry forward is the per-bundle one and not the
  total. #48 asked for this and could not answer it without measurement.

**What would change the answers**: a schema closure an order of magnitude larger than the examples',
or a machine with four cores or fewer. Both are worth re-measuring on before the concurrency limit or
the cache capacity moves.

### What is still a guess

Every bound above was measured. These were not, and saying so is the point — a reader should be able
to tell "measured and confirmed" from "nobody has looked", and the code comments at each site now
distinguish the two rather than all pointing here.

| Bound | Where | Why it was not measured |
|---|---|---|
| the include-root walk budget, in entries examined | `SchemaCatalog` | it bounds a directory walk against a vendored tree or a network mount, and a measurement taken against this repository's own directories would say nothing about either |
| the answer concurrency limit of 4 | `DeferredAnswers` | it bounds simultaneous directory walks, not compiles, so the cold-load measurement above does not reach it |
| the scheduler's yield interval | `CompileScheduler` | it affects fairness under sustained editing, which the soak exercises and the budget table does not |
| whether configuration resolution needs revisiting | `WorkspaceConfiguration` | it is per document and per generation, and never appeared in any measurement here |

None of them is on a per-keystroke path, which is why none of them is urgent. All of them are still
numbers somebody chose.

## What this does not cover

Nothing here measures the wire: framing, dispatch, and the ordered worker are #58's to surface, and a
user reporting slowness should produce numbers from the server itself rather than from this file.

`DocumentSemantics` does not serialize two concurrent misses for one buffer, so a classification
request overlapping the debounced compile can compile the same text twice. It is a known cost, it is
counted by `DocumentSemantics.Compilations`, and at these latencies it is not worth the machinery to
prevent — which is a conclusion this measurement licenses rather than an omission.
