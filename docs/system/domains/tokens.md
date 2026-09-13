# Token Aggregation: Sources, Aggregation, and Migration Record

> **Concept + living knowledge page:**
> [`docs/concepts/token-aggregation.md`](../../concepts/token-aggregation.md)
> explains the aggregator -> bus-shim concept for operators and LLM instances and
> is the knowledge-collection point for this area. This document is the
> system-of-record plan and migration record.

> **Status (2026-09-13):** Project and task-card surfaces read a deduplicated
> union of the historical token bus and durable `task.json.tokenSummary`
> receipts. The bus remains the historical source, while task receipts are the
> current source for remote runner calls. Project summary, heatmap, and pipeline
> cost responses include the newest successfully read usage timestamp and
> report partial or unavailable sources instead of presenting an unexplained
> zero. The legacy services (`TokenSummaryService`,
> `WorkspaceTokensTimelineService`, `ProjectTokenUsageService`) retain the pure
> fold helpers used by the canonical readers and parity fixtures. Each surface ships
> with a Phase-5 parity test
> (`TokenSummaryBusParityTests`, `WorkspaceTokensTimelineBusParityTests`,
> `ProjectTokenUsageBusParityTests`, alongside the earlier
> `AdHocUsageBusParityTests`) that drives both readers over the same data
> set and asserts byte-identical numeric output. The drift rule
> `token-aggregation-canonical` is ready to graduate from `Info` to `Warn`.
> Remote completion now materializes that receipt from the fenced attempt's
> provider usage frames in `logs/cli-output.log`; a bounded startup sweep repairs
> recent remote cards that completed before this writer existed.
> The workspace timeline also emits separate project and UTC-week usage lines
> for token calls whose latest route-admission boundary carried a better Token
> Economy benchmark candidate.

## Why this document exists

Five backend services compute token-spend aggregations independently, each
with its own source file, output shape, and consumer. On every observability
surface (workspace timeline, project-detail token-usage panel, job-card
footer, ad-hoc-usage chart) the "tokens today" number is *slightly* different
because each aggregator rounds, filters, and categorises in its own way.

This is the same drift pattern the codebase already eliminated for CLI
invocations (`ICliOneShot`), JSONL appends (`IJsonlAppender`), and frontmatter
parsing (`FrontmatterParser`). The fix is the same shape: one canonical
aggregator (`ITokenAggregator`), explicit source precedence, and a drift rule
that flags new ad-hoc roll-ups.

## July 11 remote-runner telemetry gap

The Project Token Usage page stopped receiving current calls when remote task
execution became the primary run path around 2026-07-11. The local
`ProjectRunner` mirrors completed CLI usage to the Agent Message Bus through
`EmitTokenUsageRichAsync`. `RemoteTaskRunner` does not execute that code. Its
lease completion envelope carries the run outcome and evidence, but no token
usage payload. As a result, the historical bus stayed readable and lifetime
totals remained near 2 billion tokens, while rolling windows and heatmaps
quietly aged out.

The task layout migration from legacy lane folders to
`tasks/<three-digit-bucket>/<task-id>` was not itself a timestamp parsing bug.
It did make a direct lane-folder-only fallback insufficient. The receipt reader
therefore supports both layouts and enumerates every bucket, including later
buckets such as `tasks/002`.

The repair keeps the canonical read-side union and adds the missing durable
receipt writer at remote completion:

- `task.json.tokenSummary` is already the durable token receipt used by current
  task-card token chips. It retains per-call timestamps, participants, models,
  and token dimensions.
- `RemoteTokenReceiptService` parses only the matching fenced session window,
  preserving input, output, cache-read, and cache-creation categories. Attempt-
  scoped participant ids make completion replay idempotent and prevent a later
  continuation from counting an earlier attempt twice.
- Historical bus entries remain in the aggregate. Receipt calls are merged by
  task, timestamp, and token dimensions with multiset deduplication, so an
  overlap does not count twice and the pre-July lifetime is retained.
- Project pipeline cost uses receipt calls when a task has them, mapping
  `agent:*` to core, `support:*` to aspect, and `orchestrator:*` to orchestrator.
  Tasks without receipts retain their historical `pipeline-execution.json`
  records, including previous attempts.
- `freshness.status`, `freshness.asOf`, `freshness.warning`, and
  `freshness.sources` make source health part of the API contract. A read
  failure produces partial or unavailable data rather than a silent zero.

## The five duplicated aggregators

| # | Service | Source file | Reads | Produces | Consumed by |
|---|---------|-------------|-------|----------|-------------|
| 1 | `AdHocUsageService` (read path) over `AdHocUsageRecorder` | `backend/Features/AdHoc/AdHocUsageService.cs`, `AdHocUsageRecorder.cs` | `adhoc-usage.jsonl` (workspace-wide) | Per-source / per-day / per-model rollup of one-shot Haiku calls | `GET /api/adhoc/usage` — ad-hoc usage chart in the status-bar modal |
| 2 | `ProjectTokenUsageService` | `backend/Features/Runner/ProjectTokenUsageService.cs` | Historical token bus + durable task token receipts | Lifetime/24h summary with Job/Supporting/Orchestrator split; per-day × per-job heatmap; expensive-jobs top-N; per-job drill-down with deltas | `GET /api/projects/{project}/token-usage/*`: Project-Detail Token-Usage panel |
| 3 | `WorkspaceTokensTimelineService` | `backend/Features/Runner/WorkspaceTokensTimelineService.cs` | `orchestrator.jsonl` for *every* watched project | (project × time-bucket) cells with priced dollars | `GET /api/workspace/tokens` — `#/workspace/tokens` stacked timeline |
| 4 | `TokenSummaryService` + `TokenSummary` | `backend/Features/Runner/TokenSummary.cs` | Historical token bus + durable task token receipts for canonical project/card reads | Per-project lifetime totals + per-model split + estimated dollars; aggregate across all projects | Project-card last-usage, status-bar usage modal, `TaskEndpointHelpers.WithRuntime` per-job rollups |
| 5 | `BusAggregationCache` (the canonical one) | `backend/Features/Bus/BusAggregationCache.cs` | `logs/bus/*.jsonl` via `AgentMessageBusStore` | `byModel` / `byParticipant` / `byDay` totals plus context-window and latency awareness | `GET /api/bus/{project}/token-aggregate` |

Three of these (#2, #3, #4) read the *same* file (`orchestrator.jsonl`) and
each produce a different shape. Two (#1, #5) read different files. None of
the four legacy aggregators are aware of the `contextWindow` / `latency`
fields that the bus messages now carry.

### What each service categorises differently

- **`ProjectTokenUsageService`** splits spend into `Job` / `Supporting` /
  `Orchestrator` by matching the job's title prefix against
  `SupportingJobTitlePrefixes`. The bus uses `participantId` (`agent:*` vs
  `support:*` vs `orchestrator:*`) — semantically equivalent but driven by a
  different field.
- **`AdHocUsageService`** splits by `Source` (TitleGen, SummaryGen,
  PromptEnhance, CommitMessage, ...) — these never reach the bus today.
- **`WorkspaceTokensTimelineService`** buckets by `(project, time-bucket)`
  with a configurable window/bucket size (1h/6h/24h/168h × 5/15/60min).
- **`TokenSummaryService`** estimates dollars through `TokenPricing` for
  every reader.
- **`BusAggregationCache`** rolls `byParticipant` and `byDay` (UTC) but does
  **not** yet expose per-job rows, expensive-jobs top-N, or the
  Job/Supporting/Orchestrator category split that
  `ProjectTokenUsageService` needs.

## Target state — `ITokenAggregator`

The canonical aggregator is the union of all five consumer needs. The
interface is defined in
[`backend/Features/Tokens/ITokenAggregator.cs`](../../../backend/Features/Tokens/ITokenAggregator.cs).
The shape:

```csharp
public interface ITokenAggregator
{
    // Workspace-wide rollups
    TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null);
    IReadOnlyList<TokenAggregateBucket> ForWorkspaceTimeline(int windowHours, int bucketMinutes, DateTime? nowUtc = null);

    // Per-project breakdowns the Project-Detail panel needs
    ProjectTokenUsageSummary ProjectSummary(string project, string watchPath, DateTime? nowUtc = null);
    ProjectTokenHeatmap ProjectHeatmap(string project, string watchPath, int days, DateTime? nowUtc = null);
    IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string project, string watchPath, int limit);
    ProjectJobTokenDetail? ProjectJobDetail(string project, string watchPath, string jobId);

    // Lifetime / dollars surface (status-bar usage modal)
    TokenSummary LifetimeSummary(string project, string watchPath);
    TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects);

    // Ad-hoc-call rollup (TitleGen, SummaryGen, ...)
    AdHocUsageAggregate AdHocAggregate(DateTime? since = null);
}
```

`TokenAggregationService` lives next to the interface. Project summary,
heatmap, expensive-task, drill-down, lifetime, workspace aggregate, and task
card reads share `BusBackedProjectTokenUsageReader`, which now performs the
hybrid merge. New code must depend on `ITokenAggregator` rather than legacy
services directly.

## Migration order

Phases 1 (this audit), 2 (bus emission for ad-hoc calls), 3 (interface +
delegating service), and 6 (drift rule) ship in the same commit that produced
this document. The remaining phases are:

### Phase 4 — Convert legacy services to bus-backed shims

All four shims have landed in this order:

1. **Landed.** `AdHocUsageService` read path. `BusBackedAdHocUsageReader`
   queries `support:adhoc` / `kind=token-usage` messages from the
   `_workspace` bus projection and folds them through the same pure
   aggregator function as the JSONL reader. The `AdHocUsageRecorder`
   emits to the workspace scope (`project=null`) and stamps the bus
   message's `CreatedAt` with the record's `Ts` so multi-day rollups
   match. The parity test
   [`AdHocUsageBusParityTests`](../../../backend.Tests/AdHocUsageBusParityTests.cs)
   drives nine realistic records (every named source, mixed days, mixed
   models including one unpriced) and asserts byte-identical output.
   The `AdHocUsageService.Aggregate` source/model ordering picked up
   stable tie-breakers in the same change so insertion-order differences
   between JSONL and bus paths can no longer leak into the output.
2. **Landed.** `TokenSummaryService.Summarize` read path.
   `BusBackedTokenSummaryReader` now queries every project-scoped
   `kind=token-usage` message, including `agent:*` coding-run turns,
   `support:*` supporting calls, and `orchestrator:*` decisions. It
   converts each message into a transient `OrchestratorLogEntry` with the
   participant id preserved for runtime categorisation, then folds through
   the same pure `TokenSummaryService` helpers. The legacy parity entry
   point still queries `orchestrator:<project>` only so old
   `orchestrator.jsonl` fixtures remain byte-comparable. The task-card
   per-job summary prefers the latest `agent:*` model for the aggregate
   model row and displays per-call model labels through
   `ModelMetadataRegistry`. `TokenSummaryService.Aggregate` was
   refactored so the workspace fold (`AggregateSummaries`) is a static
   helper both readers reuse. Regression tests:
   [`TokenSummaryBusParityTests`](../../../backend.Tests/TokenSummaryBusParityTests.cs)
   and [`TokenSummaryTests`](../../../backend.Tests/TokenSummaryTests.cs).
3. **Landed.** `WorkspaceTokensTimelineService.Build` read path.
   `BusBackedWorkspaceTimelineReader` walks every supplied project, pulls
   the orchestrator-attributed token-usage messages, and feeds them
   through `WorkspaceTokensTimelineService.BuildFromEntries`. Window
   snapping, bucket span, dollar accounting, and the per-project peak /
   last-activity trackers are unchanged because the bucketer is unchanged.
   Parity test:
   [`WorkspaceTokensTimelineBusParityTests`](../../../backend.Tests/WorkspaceTokensTimelineBusParityTests.cs).
   `BetterCandidateUsageReportService` augments this response by joining the
   merged bus and receipt calls to durable quota-admission decisions by task
   and time. It counts only calls whose actual model matches the selected route
   in the latest candidate note, then groups the result by project and UTC
   week. The separate line is informational and never changes routing.
4. **Landed, repaired 2026-08-09.** `ProjectTokenUsageService.BuildSummary` /
   `BuildHeatmap` / `BuildExpensiveJobs` / `BuildJobDetail` read paths.
   `BusBackedProjectTokenUsageReader` merges bus history with durable task
   receipts, then uses the participant
   split for runtime reads: `agent:*` counts as Job, `support:*` counts as
   Supporting, and `orchestrator:*` counts as Orchestrator. Legacy
   `orchestrator.jsonl` entries with no participant id still use
   `SupportingJobTitlePrefixes` against `JobScannerService.ScanAllJobs`,
   which keeps old fixtures and historical logs readable. Per-job
   expensive rows and drill-down rows display model labels through
   `ModelMetadataRegistry`. Parity tests keep the old orchestrator-only
   static entry points byte-comparable, while instance-reader tests lock
   the bus-native participant path:
   [`ProjectTokenUsageBusParityTests`](../../../backend.Tests/ProjectTokenUsageBusParityTests.cs).

Each parity test compares every numeric field plus the ordered breakdown
lists across the two readers driven against the same data set. The
per-call drill-down's user-facing `Summary` is the one documented
exception: the bus mints its own `tokens: in=... out=...` headline at
emit time while `orchestrator.jsonl` carries the runner's own headline,
so the parity assertion excludes that one presentation-only field
(numeric fields and `Topic` are still checked verbatim).

### Phase 5 — Parity tests

For each surface listed above, a fixture file fed through both the legacy
service and the new aggregator must produce byte-identical output. The
fixtures live under `backend.Tests/Fixtures/TokenAggregationParity/` and are
checked in. The tests stay in the repository after migration as regression
guards.

### Phase 6 — Drift rule

The drift rule `token-aggregation-canonical` is in
[`docs/system/contracts/code-patterns.md`](../contracts/code-patterns.md). Phase 4 is now complete, so
the severity is ready to move from `Info` to `Warn`; the rule will then
flag any new aggregator outside `backend/Features/Tokens/` or
`backend/Features/Bus/`.

The candidate marker scans for the two telltale patterns:

- `entry.TokenUsage` access outside `Services/Tokens/` / `Services/Bus/`
- A `Dictionary` of token totals keyed by string that doesn't go through
  `ITokenAggregator`

The good variant is membership in the `Tokens` or `Bus` namespace.

## Frontend scope levels

Workspace Settings presents Token usage as a small page tree instead of one
mixed dashboard:

- `#/workspace/tokens` is the workspace and project level. It keeps the
  existing time-window and project filters, and every aggregate reconciles to
  the visible projects.
- `#/workspace/tokens/claude` and `#/workspace/tokens/codex` are CLI account
  pages. Each page labels provider quota windows separately from locally
  captured workspace tokens and shows resets, average burn projection, the
  1h/24h/7d trend selector, effort-attribution availability, and a plausibility
  explanation.
- The current workspace timeline contract has no CLI field per bucket and the
  aggregate contract has no reasoning-effort field. The UI therefore labels
  the trend as a workspace plausibility baseline and effort as unattributed. It
  does not invent either attribution.
- Per-task cap forecast (TE-4) is a labelled future integration point only.

Workspace and project usage calculations are unchanged by this navigation
split. CLI pages are extendable by adding another page key and model mapping.

## What's deliberately out of scope

- **Token pricing tables.** `TokenPricing` stays where it is; cost lookup is
  separate from aggregation. The aggregator delegates to it for the
  `Dollars` field on the bus response.
- **CLI quota** (`/api/cli/quota`). Different source (subscription window),
  different cadence, different consumer.
- **Rewriting historical bus files or task receipts.** The hybrid reader keeps
  both immutable sources in place and merges them at read time. No destructive
  backfill is needed.

## Reference — file paths

| File | Role after consolidation |
|------|--------------------------|
| `backend/Features/Tokens/ITokenAggregator.cs` | Canonical interface |
| `backend/Features/Tokens/TokenAggregationService.cs` | Canonical consumer implementation |
| `backend/Features/Tokens/ProjectTokenReceiptReader.cs` | Reads both task layouts, converts receipts, and deduplicates overlap with history |
| `backend/Features/Bus/BusAggregationCache.cs` | In-memory rollup over historical `logs/bus/*.jsonl` |
| `backend/Features/Bus/AgentMessageBusBridge.cs` | Legacy/local producer side: `EmitTokenUsageAsync` / `EmitTokenUsageRichAsync` |
| `backend/Features/AdHoc/AdHocClaudeInvoker.cs` | Ad-hoc-call recorder; **also fires `EmitTokenUsageAsync` after Phase 2** |
| `backend/Features/AdHoc/AdHocUsageRecorder.cs` | Legacy write path (kept for disk-format readers) |
| `backend/Features/AdHoc/AdHocUsageService.cs` | Legacy aggregator; only the parity fixture still calls it directly |
| `backend/Features/Runner/ProjectTokenUsageService.cs` | Pure-function fold reused by `BusBackedProjectTokenUsageReader` |
| `backend/Features/Runner/WorkspaceTokensTimelineService.cs` | Pure-function bucketer reused by `BusBackedWorkspaceTimelineReader` |
| `backend/Features/Runner/TokenSummary.cs` | Pure-function summarizer reused by canonical readers |
| `backend/Features/Tokens/BusTokenEntryConverter.cs` | Adapter from bus `kind=token-usage` messages to the shared fold shape |
| `backend/Features/Tokens/BusBackedProjectTokenUsageReader.cs` | Hybrid read path for project, lifetime, aggregate, and task-card surfaces |
| `backend/Features/Tasks/TaskEndpointHelpers.cs` | Task-card token footer lookup through `ITokenAggregator.WorkspacePerJob` |
