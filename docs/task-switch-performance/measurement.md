# Measurement protocol

Canonical entry: [Task switch performance](index.html). Source card: AGT-2910.

## Provenance

The operator supplied v0.8.0 observations for 25 September 2026: task detail
roughly 230-2700 ms, grouped roughly 300-1000 ms, one background index scope
with 68 `config` spawns totaling 4759 ms, and one `config` duration of
40345891 ms after host wake. These are reported observations, not an imported
raw dataset. A range cannot yield a percentile. The long spawn duration may
include host suspension; it is not evidence of 11 hours of CPU work.

The initial measurement run reached the operator's Windows Stable through forwarded ports 5031
and 4011 from a Linux runner. `/api/diagnostics/log-location` identifies the
Stable backend directory. `/api/system/version` reports **v0.9.1**, commit
`09e189092396700adf0cbafbb38e6ad5b89a818d`, built 2026-09-25 09:08 UTC.
The analysis checkout is `93d23ac43939d8670c5a67a25a6c8dea4e38af36`.
Source inspection and the live binary therefore have distinct provenance.

The API replay covers 13:57:28-14:00:57 UTC on 25 September 2026. It alternates
AGT-2797 (backlog), AGT-2736 (progress) and AGT-2878 (human review), ten reads
each. It makes ten unconditional and ten conditional grouped attempts.
The conditional experiment intentionally holds the initial ETag fixed; its
200 responses do not establish a defect in validator reuse. It does not test
a fresh validator after each response. The script serializes API probes with
200 ms gaps, but the real operator, runner, watchers and a browser startup
probe remain active. No cache is flushed and no backend is restarted.

Only durations, response field sizes, public task keys and diagnostic
aggregates are stored. No prompt, status body or task response is retained.
The browser script captures this source card for visual context. It exercises
real navigation without intercepting or fabricating API responses.

## Reproduction

From the repository root, with the existing Stable forward available:

```sh
node docs/task-switch-performance/measure-api.mjs
node docs/task-switch-performance/analyze.mjs
node docs/task-switch-performance/measure-browser.mjs
```

Set `STABLE_API_URL`, `STABLE_FRONTEND_URL`, `MEASURE_PROJECT`,
`MEASURE_KEYS`, `MEASURE_COUNT`, `MEASURE_OUT` or `MEASURE_ASSETS` as required.
Set both output directories to retain the committed samples and captures. The default project cohort
discovery is specific to Agent Studio; specify public keys for another
project. Scripts are read-only against the application. They write only
their evidence output and adjacent screenshots; no script belongs in the
product's `scripts/` directory in this concept run.

To reduce an exported, already date-filtered log as well:

```sh
node docs/task-switch-performance/analyze.mjs /path/to/2026-09-25.api.log.out
```

The optional log reducer aggregates complete `task-operation-timing` lines.
Check its matched-line count before using it. It does not invent absent
stages or join Git scopes to browser switches. Logs without switch/request
correlation cannot support per-switch spawn counts under concurrent work.

## Units and limits

- Percentiles use linear interpolation at `p * (n - 1)` on sorted samples,
  matching the live Git telemetry implementation. Each population has its own
  count. Percentiles from nested or parallel stages must never be added.
- `task-op` in `Server-Timing` brackets endpoint-filter execution. It ends
  before the returned result is serialized and written. It is not full HTTP
  latency. The difference between client time and `task-op` includes network,
  queueing, serialization and transfer; it cannot be labeled one of those
  stages alone.
- API byte counts are decoded UTF-8 response bytes. Field sizes are separate
  JSON encodings and exclude key/delimiter overhead. They are not compressed
  transfer size.
- API percentiles describe successful reads only. Three of twenty grouped
  attempts reached the 15000 ms client timeout. Their eventual latency is
  unknown. An all-attempt p95 is therefore not established by the successful
  grouped percentile. The performance gate must fail these attempts.
- The browser sample's readiness criterion is a changed visible overview
  task key and visible prompt-pane body followed by two animation frames.
  This is an observed detail proxy, not proof of the proposed complete core.
  Existing `job-select-to-rendered` measures end at signal assignment.
  Existing `beautiful-results-render` covers markdown conversion, not DOM
  layout, all markdown surfaces or paint. Missing measures remain missing.
- Browser resource timings cover completed requests in the observation
  window. Polls, neighboring-task prefetch and lazy requests must be classified
  from initiators and identity before attributing them to the selected task.
  Requests started before a switch can finish during it. No aggregate Git
  counter subtraction can remove that ambiguity.
- The reducer aggregates traced stage durations per switch before calculating
  stage p50/p95. Each stage reports its own sample count; an absent stage is
  not silently counted as zero. Markdown conversion and DOM work retain
  separate sample counts and percentiles.
- The read-only capture records every attempted switch, including a detail DOM
  timeout, and fails the Playwright test after writing the complete report when
  any attempt failed. The offline reducer reports null Git counts when no
  correlated request trace exists; null means unmeasured, while zero requires
  at least one traced request. A percentile from one successful proxy switch
  is not a workstation cohort baseline.
- The supplied old-log incident, live rolling telemetry, API replay and
  browser replay are separate populations. Clock offsets across hosts do not
  affect local monotonic durations, but prohibit timestamp-only causal joins.

## Stage capture contract

### Opt-in detail trace (AGT-2952)

Send `X-Task-Switch-Trace: 1` on a detail GET. The caller may send UUIDs in
`X-Task-Request-Id` and `X-Task-Switch-Id`; the server returns canonical UUIDs
in the same response headers and generates either ID when the input is invalid.
Each browser switch reuses one switch ID across its API requests and gives each
request a distinct request ID. The detail request logs one `task-switch-trace`
JSON record after its response write. It contains outcome, status, wall time,
written bytes, exclusive named stage milliseconds, invocation counts and
scanner-owned file-open counts, Git spawn count,
summed Git time and Git timeouts. No task body, prompt, filesystem path or secret
is serialized into this record. The `task-op` Server-Timing metric remains the
endpoint-filter duration, before serialization. Missing stage entries mean no
recorded operation; a measured zero is represented by a present entry. The
existing `git-index-run` scope is background work and must not be joined to a
detail request without a matching correlation ID. The trace is diagnostic,
not a performance saving; its enabled p95 overhead target is at most 1 ms.

The read-only Playwright capture lives at
`frontend/e2e/perf/task-switch-capture.spec.ts`. Run it with
`PW_TARGET=stable TASK_SWITCH_CAPTURE=1` and set `TASK_SWITCH_OUTPUT` to a
retained result path. `TASK_SWITCH_COUNT` defaults to 30 each for board click,
pager, back/forward and deep-link navigation cohorts, plus separate small,
long-history, active and archived task-class cohorts. The task-class cohorts
use real deep links, and all eight populations retain separate counts.
It uses actual click and history events, navigation timing, a DOM-ready mark,
two animation frames, resource timings and existing markdown-conversion
measures. DOM work is reported only when the older signal-assignment mark is
present; absent values stay null. `TASK_SWITCH_TRACE=1` injects correlation
headers using Playwright request continuation and should be declared as a
distinct instrumentation run. Reduce a capture offline with
`node docs/task-switch-performance/reduce-switch-capture.mjs CAPTURE [TRACE_LOG] [SUMMARY]`.
The reducer retains every attempt in the sample count, reports errors and
percentiles over successful paint samples, and keeps Git counts separate from
browser durations. A forwarded Linux browser is a remote diagnostic run, not
the required designated-workstation baseline. The later acceptance gate still
requires 100 measured switches per cohort after warmups.

The future harness must carry `switchId`, `requestId`, task/project identity,
core and resource generation, revision, timestamps, outcome and sample class.
Record these stages with explicit zero versus missing semantics:

| Stage | Boundary | Requirement |
| --- | --- | --- |
| Input and route | Actual input event to selected identity | Include click, keyboard and Back/Forward. |
| Index | Lookup, refresh ownership and wait separately | Identify cold, dirty, TTL and mutation targets. |
| task.json | Targeted read and deserialization | Count files; zero reads is a valid warm hit. |
| Sidecars | Prompt/status/history/log/evidence separately | Count files and bytes; never copy body text. |
| Git | Detail boundary plus nested scopes | Spawn count, subcommand time and timeout; background separate. |
| Integration | BuildLookup including membership/cache | Record hit/miss and input generation. |
| Review | Canonical review projection | Separate evidence file I/O from projection. |
| Tokens | Project token projection and task lookup | Separate cache hit from aggregation. |
| Response | Serialize and write completion | Decoded and transferred bytes, disconnects. |
| Markdown | Conversion and DOM insertion/layout separately | Identify document, truncated head and full text. |
| Core paint | All core fields or explicit empty states, then paint opportunity | A skeleton cannot satisfy the gate. |

Record parent wall spans and exclusive child spans. The accounting tree must
show overlap. Git wall-time sums can exceed request wall time under parallel
execution. Count nested process events once using the outer request scope.
Never infer per-switch Git counts from `git-index-run` sample averages.

## Workstation gate

### Workstation verdict pending

After Stable contains cards 1-6, run the existing gate on the designated
workstation against the ready Stable server. Provide production-shaped task
folders for the .NET helper and real Stable keys for active, review, archived
and long-document tasks. From the repository root:

```sh
export TASK_SWITCH_PROFILE=<designated-workstation-id>
export TASK_SWITCH_PROFILE_KIND=designated-workstation
export TASK_SWITCH_SOURCE=<promoted-revision>
export TASK_CORE_BENCH_FIXTURE=<production-shaped-task-folders>
export TASK_SWITCH_ACTIVE_KEY=<active-key>
export TASK_SWITCH_REVIEW_KEY=<review-key>
export TASK_SWITCH_ARCHIVED_KEY=<archived-key>
export TASK_SWITCH_LONG_KEY=<long-document-key>
export JOB_RESULTS_DIR=<retained-results-directory>
npm --prefix frontend run e2e:task-switch-budget
```

The command runs `frontend/e2e/perf/task-switch-budget.spec.ts` against Stable,
the labeled isolated cohorts against a fixture-managed dev backend, and the
evaluator. It writes the verdict to
`$JOB_RESULTS_DIR/task-switch-budget-summary.json` alongside raw captures.
The performance gate fails closed until that workstation verdict exists.

### Card 6 gate wiring (7 October 2026)

The existing machine-bound `task-switch-workstation-budget` suite in
`.agent-studio/project.yml` runs `npm --prefix frontend run e2e:task-switch-budget`
with one Playwright worker. Set `TASK_SWITCH_PROFILE` to the designated
workstation identifier, `TASK_SWITCH_SOURCE` to the tested revision,
`TASK_SWITCH_PROFILE_KIND=designated-workstation`, and
`TASK_CORE_BENCH_FIXTURE` to production-shaped task folders, plus the four
`TASK_SWITCH_{ACTIVE,REVIEW,ARCHIVED,LONG}_KEY` values. The default is
100 measured switches after five warmups in each declared cohort. Output goes
to `JOB_RESULTS_DIR` when present. The normal Stable capture requires its
backend to be ready and changes no Stable data. The isolated spec starts any
task-worktree backend through `frontend/e2e/fixtures/dev-backend.ts`.

`frontend/e2e/perf/task-switch-budget.spec.ts` records the raw attempts, page
errors, API waterfall, core decoded bytes, Server-Timing stages, request IDs,
Git spawn and workspace-scan counters, and screenshot paths. The offline
`evaluate-budget.mjs` uses the same interpolated percentile convention as this
protocol and fails on any missing required cohort, timeout, warming response,
missing stage, missing counter, excess concurrency or failed core DOM check.
It requires both themes and the normal, cached, cold-client, dirty-index,
mutation and Git-failure cohort names. The isolated evaluator tests exercise
150 ms delay, one request Git spawn and one selection board read; these are
synthetic fault proofs, not workstation measurements. The normal browser spec
collects seven cohort types. `task-switch-budget-isolated.spec.ts` uses the
existing `dev-backend` fixture to create disposable tasks, mutate an unrelated
task or the target task, and simulate Git resource 503 and held responses with
page-local routes. These are labeled fixture faults; they do not prove a real
Git worker was disconnected or hung. The runner combines both raw captures
only if their declared workstation and backend revision match. Neither suite
was run for 100-switch populations in this Linux task run.
Each isolated fault cohort now rotates verified ready-lane active, human-review
and archived fixture tasks. Active and review switches use the pager and observe
the `job` route key; archived switches use the Archive row click because the
grouped board intentionally omits archived pager peers. Fixture setup returns
to the existing Board tab between samples; cold-client loads remain a separate
normal cohort.
One isolated Linux diagnostic of the unrelated mutation cohort completed one
switch in each theme against the fixture-managed worktree backend. Both
samples had zero grouped reads, zero core Git spawns and zero core workspace
scans, with decoded core bodies below 16 KiB. Paint times were 777.1 ms in
light and 1321.3 ms in dark. These are one-sample remote diagnostics under
dev-server load, not cohort percentiles or workstation acceptance evidence.
The four correlated core responses from those two switches report these
diagnostic stage values. Each row is an independent percentile of four
requests; parent `task-core` and `task-op` include child work and cannot be
added to it.

| Stage | p50 / p95 (ms) | n |
| --- | ---: | ---: |
| Core index lookup | 0.011 / 0.012 | 4 |
| Runtime facts | 0.005 / 0.013 | 4 |
| Core serialization | 0.075 / 0.084 | 4 |
| Core handler | 0.177 / 0.219 | 4 |
| Endpoint filter (`task-op`) | 0.381 / 0.439 | 4 |

Decoded core bodies were 2374 and 2432 bytes. All four traced requests
reported zero Git spawns and zero workspace scans. These values describe the
new core route in an isolated Linux fixture, not the old detail route in the
Dossier's historical stage table.

The additive core response now emits `core-index`, `core-runtime`,
`core-serialize` and `task-core` Server-Timing metrics. An opt-in
`X-Task-Switch-Trace: 1` core read also returns
`X-Task-Core-Git-Spawns` and `X-Task-Core-Workspace-Scans`; missing counters
are distinct from measured zero. The legacy detail trace remains separate.
The production-shaped .NET helper is marked `MachineBound` and uses the same
interpolated p95 convention for handler, HTTP and traced HTTP timings. With
`TASK_CORE_BENCH_FIXTURE` set on the designated host, the suite runs it before
Playwright and enforces the
opt-in trace overhead target of at most 1 ms at p95. It was not run without
that fixture in this task worktree.

No workstation capture or before/after comparison has been completed in this
Linux task run, so the Dossier's historical unavailable cells remain unknown.
The read-only Linux proxy diagnostic on 7 October 2026 reached Stable v0.9.4
(`c076d2da98799a8b09c5e5d2562176ba1f2ddea8`) but the shared crash
recovery overlay still covered the board. Preflight stopped before any switch;
the saved raw capture and screenshot are a remote diagnostic, not a baseline.
The overlay was not changed.

The read-only API replay was repeated against the same Stable v0.9.4 through
the Linux-to-Windows forward on 7 October 2026. It used 30 detail reads, ten
unconditional grouped reads and ten fixed-validator conditional grouped
attempts. All 30 detail reads returned 200. Detail `task-op` p50/p95 was
532.961/2284.465 ms; full client p50/p95 was 570.877/2337.369 ms. These use
linear interpolation on successful samples. The largest detail body was
121291 decoded bytes. These are current remote API values, not the old v0.9.1
population, not a browser core paint cohort and not a before/after saving.
The repeat still exposes only `task-op` for the legacy detail route; it cannot
fill the Dossier's previously unavailable internal-stage cells.

Use at least 100 measured switches per cohort after five declared warmups.
Keep board click, pager, back/forward, deep link, archive and long-history
populations separate. Cover a ready backend with a cold client cache, a warm
cache, unrelated invalidation, an own-task mutation, disconnected Git and a
hung Git refresh. Real operator fault injection is not allowed; use isolated
fixtures for faults and label their results. Any dev backend lifecycle must
come from a Playwright spec using `frontend/e2e/fixtures/dev-backend.ts`.

Fail the gate if any cohort has core p95 above 100 ms, resident-core p95 above
50 ms, handler p95 above 30 ms, a core over 16 KiB, any request-path Git spawn
or workspace scan, a selection-induced grouped reload, missing core facts,
page errors, missing required telemetry or timeouts. Preserve raw samples,
waterfall, request spans, screenshot provenance and the environment profile.
Cold process startup is a separate readiness metric, never silently excluded
from a supposedly ready-server cohort. Prove the gate detects an injected
150 ms delay and one forbidden Git spawn, then remove the injections.

The workstation p95 and the full internal-stage table remain required before
claiming the 100 ms goal is achieved. No product behavior changed in AGT-2910.

## Continuation and recovery finding

The continuation on 25 September recovered this Dossier and its original raw
measurements from task salvage commit `d06d426fd`. It did not repeat the live
replay or fabricate replacement samples. The reducer was rerun offline and
its output compared with the saved summary. `evidence/recovery-finding.json`
records the operator's restart time separately from observed capture times.

The reported restart was 12:23 Europe/Berlin (10:23 UTC). Both screenshots
show the overlay at 16:17:53 local, approximately 3 h 54 min 53 s later. The
visible pending recovery item is dated 22 September. No boot/record linkage
was captured, so this cannot be attributed specifically to the 12:23 restart.
Presence well after restart is observed; continuous display is not measured.
The final saved script stopped on overlay visibility before attempting pager
navigation. It provides no interception trace and no completed switch.

Per the operator's 16:58 delivery direction, the concept is decision-ready.
Unknown stage and paint values belong to implementation task 1; persistent
recovery obstruction belongs to task 8. Neither requires stopping delivery for
approval. Human sight review is the next pipeline lane and remains pending.
