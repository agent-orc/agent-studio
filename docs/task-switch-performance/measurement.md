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
- The supplied old-log incident, live rolling telemetry, API replay and
  browser replay are separate populations. Clock offsets across hosts do not
  affect local monotonic durations, but prohibit timestamp-only causal joins.

## Stage capture contract

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
