---
id: token-aggregation-concept
title: "Token aggregators use canonical hybrid projections (concept + living knowledge)"
status: active
category: concept
updatedAt: 2026-08-11
last-updated: 2026-08-11
reason: "Align pipeline RUNS totals with canonical task-ledger attribution and expose missing run telemetry"
taskKey: AGT-2626
tags: [token-aggregation, hybrid-projection, task-receipt, drift-rule, observability, cost]
related-tasks: [ASS-881, ASS-1717, AGT-2542, AGT-2624, AGT-2626]
related-adrs: []
related-docs:
  - "docs/system/domains/tokens.md"
  - "docs/system/domains/token-pricing.md"
  - "docs/system/contracts/code-patterns.md"
  - "docs/system/architecture/bus/agent-message-bus.md"
---

# Token aggregators -> canonical hybrid projections

> **What this page is.** A living concept and knowledge-collection page for the
> token-aggregation area of the backend. It explains *why* every token roll-up
> now reads through one canonical projection, *what* the four legacy
> aggregators were and why they became thin shims, and *how* the architecture
> guard test stops the old pattern from creeping back. New findings about this
> area belong in the [Living knowledge log](#living-knowledge-log) at the
> bottom. The engineering plan and phase-by-phase migration record live in the
> token domain doc: [`docs/system/domains/tokens.md`](../system/domains/tokens.md).

## TL;DR

- There used to be **five** independent token-spend aggregators. Three of them
  read the *same* file (`orchestrator.jsonl`) and each rounded, filtered, and
  categorised differently, so the "tokens today" number disagreed across the UI.
- The fix is one canonical interface, **`ITokenAggregator`**, backed by an
  explicit hybrid projection: historical Agent Message Bus records plus
  durable current task token receipts.
- **ASS-881 (Refactor Phase 4)** converted the four legacy aggregators into
  *bus-backed shims*: their pure math is reused, but the input now comes from
  the bus instead of each surface re-reading log files its own way.
- **AGT-2542** repaired the July 11 gap caused by remote runs bypassing the bus
  usage writer. Project, aggregate, and task-card reads now merge the bus with
  `task.json.tokenSummary` and deduplicate overlapping calls.
- A drift rule (`token-aggregation-canonical`) plus an **architecture guard
  test** (`TokenAggregationCanonicalDependencyTest`) prevent any new code from
  injecting the old concrete aggregators again.

## Why bus-backed: the drift problem

Every observability surface needs a token number: the workspace timeline, the
Project-Detail Token-Usage panel, the status-bar usage modal, the kanban
card footer, the ad-hoc-usage chart. Before consolidation each surface owned its
own aggregator. Because each one rounded, filtered, and categorised in its own
way, the same underlying spend produced slightly different totals depending on
which screen you looked at. That is classic *aggregation drift*.

This is the same drift pattern the codebase already eliminated elsewhere:
one-shot CLI invocations (`ICliOneShot`), JSONL appends (`IJsonlAppender`), and
frontmatter parsing (`FrontmatterParser`). The cure is always the same shape:

1. **One canonical surface** that is the union of every consumer's needs:
   `ITokenAggregator`.
2. **Explicit source precedence**: the Agent Message Bus remains the immutable
   history source. Durable `task.json.tokenSummary` receipts supply current
   remote-runner calls. Atomic chat fallback receipts retain usage when a
   completed chat transcript cannot append to the bus. The canonical reader
   merges these sources with multiset
   deduplication instead of assuming either source is complete by itself.
3. **A drift rule** that flags any new ad-hoc roll-up so the problem cannot
   silently return.

## The canonical chain

```
consumers (endpoints, job-card footer, status bar)
        |
        v
ITokenAggregator                         <- the only thing new code depends on
        |  (interface: backend/Features/Tokens/ITokenAggregator.cs)
        v
TokenAggregationService                  <- the single implementation
        |  (backend/Features/Tokens/TokenAggregationService.cs)
        |
        +--> BusAggregationCache          (bus-native rollup: byModel/byParticipant/byDay)
        +--> BusBackedProjectTokenUsageReader (hybrid project, lifetime, aggregate, task-card reads)
        |       +--> AgentMessageBusStore     (historical logs/bus/*.jsonl)
        |       +--> ProjectTokenReceiptReader (current task.json.tokenSummary)
        +--> BusBackedWorkspaceTimelineReader (project x time-bucket cells)
        +--> BusBackedAdHocUsageReader        (TitleGen / SummaryGen / ... one-shot rollup)
```

`TokenAggregationService` does almost no math itself. Each method delegates to
the matching canonical reader. The readers, in turn, do not re-derive the math.
They convert historical bus messages and current receipts into transient log
entries and then call the **same pure-function folds** that the legacy services
always used. Model-key normalisation, USD estimation through `TokenPricing`,
day-bucket formatting, and the Job/Supporting/Orchestrator split still run
through one code path. The hybrid reader only composes the inputs.

## What the legacy aggregators were, and why they became shims

Four services predated `ITokenAggregator`. Each one read log files directly and
produced its own shape for one surface:

| Legacy service | Surface it fed | Read from | What it computed |
|---|---|---|---|
| `AdHocUsageService` | ad-hoc usage chart (`GET /api/adhoc/usage`) | `adhoc-usage.jsonl` | per-source / per-day / per-model rollup of one-shot Haiku calls (TitleGen, SummaryGen, PromptEnhance, CommitMessage, ...) |
| `WorkspaceTokensTimelineService` | workspace timeline (`GET /api/workspace/tokens`, `#/workspace/tokens`) | `orchestrator.jsonl` for every watched project | `(project, time-bucket)` cells with priced dollars |
| `ProjectTokenUsageService` | Project-Detail Token-Usage panel (`GET /api/projects/{project}/token-usage/*`) | `orchestrator.jsonl` + job-folder scan | lifetime/24h summary with Job/Supporting/Orchestrator split, per-day x per-job heatmap, expensive-jobs top-N, per-job drill-down with deltas |
| `TokenSummaryService` (+ `TokenSummary`) | project-card last-usage, status-bar usage modal, per-job card footer | `orchestrator.jsonl` | per-project lifetime totals + per-model split + estimated USD; workspace aggregate |

Three of these (`WorkspaceTokensTimelineService`, `ProjectTokenUsageService`,
`TokenSummaryService`) read the **same** `orchestrator.jsonl` and produced three
different shapes. That overlap was the drift source.

**What "became a shim" means here.** The service files were *not* deleted. ASS-881
kept their pure static fold functions (the actual math) and parity fixtures, but
moved the runtime read path to a bus-backed reader that reuses those folds:

- `TokenSummaryService.Summarize(...)` / `AggregateSummaries(...)` are reused by
  the canonical project reader for lifetime and workspace aggregates.
- `WorkspaceTokensTimelineService.BuildFromEntries(...)` is reused by
  `BusBackedWorkspaceTimelineReader`.
- `ProjectTokenUsageService.Build*` folds are reused by
  `BusBackedProjectTokenUsageReader`.
- `AdHocUsageService.Aggregate(...)` is reused by `BusBackedAdHocUsageReader`.

So the legacy classes survive as **pure-function libraries plus parity
fixtures**, not as injectable runtime aggregators. The canonical readers adapt
the explicit source union onto the old, trusted math.

### Why keep them at all instead of deleting?

1. **Parity tests.** Each surface ships a Phase-5 parity test
   (`TokenSummaryBusParityTests`, `WorkspaceTokensTimelineBusParityTests`,
   `ProjectTokenUsageBusParityTests`, `AdHocUsageBusParityTests`) that drives
   both the legacy reader and the bus reader over one fixed data set and asserts
   byte-identical numeric output. Those tests stay in the repo as regression
   guards, and they need the legacy fold to compare against.
2. **Historical-data fallback.** Very old records that predate the Phase-2 bus
   emit, and legacy `orchestrator.jsonl` entries with no `participantId`, still
   read correctly through the legacy path (which falls back to
   `SupportingJobTitlePrefixes` matching when no bus participant id is present).

One documented exception to byte-exact parity: the per-call drill-down's
user-facing `Summary` string differs (the bus mints its own
`tokens: in=... out=...` headline at emit time, while `orchestrator.jsonl`
carries the runner's own headline). All numeric fields and `Topic` are still
asserted verbatim; only that one presentation-only string is excluded.

## The canonical-dependency rule and its guard test

The rule, in one sentence: **runtime code depends on `ITokenAggregator`, never
on a legacy concrete aggregator.**

This is enforced two ways.

### 1. Architecture guard test (the hard gate)

`backend.Tests/Architecture/TokenAggregationCanonicalDependencyTest.cs` scans
every `*.cs` file under `backend/` and fails the build if it finds a line that
*injects* one of the legacy concrete types
(`TokenSummaryService`, `WorkspaceTokensTimelineService`,
`ProjectTokenUsageService`, `AdHocUsageService`) as a field or constructor
parameter.

Two things make the test precise rather than blunt:

- It matches an **injected concrete type** (e.g. `private readonly
  TokenSummaryService _tokens;` or `AdHocUsageService usage,`) but deliberately
  ignores **static calls** (`TokenSummaryService.Summarize(...)`) and DI
  registration (`builder.Services.AddSingleton<TokenSummaryService>();`). That is
  why reusing the pure folds is allowed while re-injecting the aggregator is not.
- It allow-lists the legacy files themselves plus everything under
  `backend/Features/Tokens/`, so the canonical readers can keep calling the
  legacy folds without tripping the guard.

If a future change injects a legacy aggregator into a runtime service, this test
goes red with a remediation message: *inject `ITokenAggregator` and use its
bus-backed methods.*

### 2. Drift rule (the soft, repo-wide signal)

`token-aggregation-canonical` in [`docs/system/contracts/code-patterns.md`](../system/contracts/code-patterns.md)
is a drift-analysis rule. It scans for the telltale markers of a hand-rolled
roll-up (`entry.TokenUsage` access, `AgentMessageTokens`, a string-keyed token
total dictionary) outside the `Tokens/` and `Bus/` namespaces, and the "good
variant" is membership in those namespaces or use of `ITokenAggregator`. Now
that Phase 4 is complete, its severity is **Warn**: any new aggregator outside
`backend/Features/Tokens/` or `backend/Features/Bus/` gets flagged.

The guard test is the build-breaking gate; the drift rule is the broader,
advisory radar that catches patterns the narrow injection regex would miss.

## How this connects to the token domain and the UI

- **Token domain doc (system of record):**
  [`docs/system/domains/tokens.md`](../system/domains/tokens.md) holds the full audit,
  the five-aggregator table, and the phase-by-phase migration record. Read it
  when you need the *plan*; read this page when you need the *concept*.
- **Pricing:** [`docs/system/domains/token-pricing.md`](../system/domains/token-pricing.md) owns the
  per-model price table. Pricing is deliberately *separate* from aggregation;
  the aggregator delegates to `TokenPricing` only for the `Dollars` field.
- **Bus:** [`docs/system/architecture/bus/agent-message-bus.md`](../system/architecture/bus/agent-message-bus.md) describes the
  historical channel. It is no longer assumed to contain remote-runner usage.
- **Schemas:** `docs/app/schemas/token-aggregate.schema.json`,
  `token-aggregate-by-client.schema.json`, `token-timeline-bucket.schema.json`
  pin the wire shapes.
- **Frontend surfaces that consume the endpoints:**
  - `frontend/src/app/features/tokens/` (workspace token timeline + status-bar
    summary block)
  - `frontend/src/app/features/project-token-usage/` (Project-Detail Token-Usage
    panel)
  - `frontend/src/app/features/board/components/task-card/token-popover.directive.ts`
    (kanban card footer)
  - `frontend/src/app/features/task-detail/components/prompt-pane/pipeline-token-usage/`
    (per-task pipeline cost)

## Practical guide: adding or changing a token surface

For an operator or an LLM instance working in this area, the rules of the road:

1. **Need a token number somewhere new?** Inject `ITokenAggregator` and call the
   method that matches your shape. Do not read `orchestrator.jsonl`,
   `adhoc-usage.jsonl`, or `logs/bus/*.jsonl` yourself, and do not build a
   string-keyed token dictionary. The guard test will reject an injected legacy
   aggregator, and the drift rule will warn on a hand-rolled roll-up.
2. **The shape you need does not exist on `ITokenAggregator`?** Add a method to
   the interface and implement it in `TokenAggregationService`, backed by a
   canonical reader. Reuse an existing pure fold if one fits.
3. **Changing the math?** Change the pure fold on the legacy service (that is
   what it is for now) so both the bus reader and the parity fixture move
   together, then update the parity test fixture under
   `backend.Tests/Fixtures/TokenAggregationParity/`.
4. **Emitting new spend?** Preserve a durable per-call receipt with timestamp,
   participant, model, and all four token dimensions. Local paths should also
   keep their existing `kind=token-usage` bus emit. Do not add another private
   aggregate outside the canonical reader.

## Living knowledge log

Append new findings about the token-aggregation area here, newest on top. Keep
each entry short: date, what was learned, and a pointer to the code/commit/task.

- **2026-10-07 (AGT-2986).** A chat reply can be persisted before its bus usage
  append fails. That path now writes a turn-keyed fallback receipt atomically;
  the canonical reader includes it after restart and deduplicates it if the bus
  row is also present. Failure of both writes is reported to the caller.
- **2026-09-28 (AGT-2986).** The workspace timeline (and the status-bar
  usage panels that read it) still read the bus alone, so a week of remote
  runner work showed zero cells while cards carried receipts. The timeline now
  reads the same merged ledger as the project surfaces. Remote review attempts
  write receipt rows and orchestrator chat turns write project bus rows, so
  every execution path lands in one ledger with host, CLI, level, and cached
  input. Model ids are stored as ids; `claude-opus-5-5` is a routing alias of
  `claude-opus-5`, not the same model, and no longer borrows its label.
- **2026-08-12 (AGT-2624).** Receipt discovery was working, but remote
  completion never wrote `task.json.tokenSummary`; recent TE/CAC cards therefore
  had no current source to discover. `RemoteTokenReceiptService` now folds the
  matching fenced session's provider usage frames into an attempt-scoped,
  replay-safe receipt. `RemoteCompletionAttributionSweep` repairs recent remote
  cards from their durable logs alongside the exact BaseSha commit repair.
- **2026-08-11 (AGT-2626).** The task-detail pipeline counted visible RUNS
  from `session-events.jsonl` but built token totals from pipeline attempts.
  Those are different lifecycles, so six CLI runs could appear beside one
  empty attempt and a silent zero total. `PipelineCostCalculator` now divides
  the canonical `ITokenAggregator.WorkspacePerJob` calls into the same session
  windows used by RUNS, sums input, output, cache-read, and cache-write tokens,
  and reports `missingTokenRuns` separately from `unpricedRuns`. In-repository
  `.orchestrator/jobs` receipt discovery and historical repair remain owned by
  AGT-2624; the pipeline consumes that canonical seam rather than adding a
  second storage or backfill path.
- **2026-08-09 (AGT-2542).** Remote task execution became primary around July
  11 but did not traverse `ProjectRunner.EmitTokenUsageRichAsync`; its
  completion envelope contains outcome and evidence rather than token usage.
  The bus history therefore froze for project rolling windows even though
  durable task token receipts stayed current. `ProjectTokenReceiptReader` now
  enumerates both task layouts, merges receipts with bus history without double
  counting, supplies pipeline step-kind cost, and reports source freshness.
- **2026-06-09 (ASS-1717).** Page created. State of the world at creation:
  ASS-881 (Phase 4) has landed (commits `aba24661`, `d5597897`, `9bf95984`);
  all four bus-backed shims are live, the four parity tests are green, the
  architecture guard test is in place, and the drift rule severity is `Warn`.
  Open follow-ups noted in the domain doc: optional one-shot backfill of
  historical `orchestrator.jsonl` into the bus (only the lifetime-totals surface
  reads older history, and the shim can read it straight from the log until
  backfill lands).
