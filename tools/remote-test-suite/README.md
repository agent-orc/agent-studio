# Remote test suite core

This repository-owned harness verifies Remote Run infrastructure. It does not
compare models or CLIs, execute benchmarks, or rank results. A run may retain
token counts as telemetry, but tokens never affect acceptance.

Run a scenario with a stable seed and a unique run id:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario divergent-salvage-lineage \
  --seed historical-agt-2177 \
  --run-id "$(date -u +%Y%m%dT%H%M%SZ)"
```

Every command starts an isolated Task Server and provisions resources through
`/api/v1`. Runner actions use the real fenced claim, lease, event, immutable
Result-SHA handoff, completion, and review contracts. The restart replay also
launches the real `agent-host` daemon around a controlled detached worker. The
harness never writes under the managed task workspace.

| Scenario | Historical boundary | Expected terminal | Recovery budget |
|---|---|---|---|
| `reference-change` | Deterministic protocol reference | `6-completed` | 0 reference recoveries |
| `divergent-salvage-lineage` | Divergent canonical and host lineage, contaminated and stale review scopes | `5-human-review` | 3 review attempts |
| `lease-adoption-restart` | Live, dead, and PID-mismatched generations across daemon restart | `4-auto-review` | 1 live-generation adoption |
| `external-completion-cycle` | Idempotent external completion, requeue replay, and failed stranded salvage | `2-ready` | 1 post-completion requeue |

Historical replay manifests declare `contract`, which selects the replay
engine. Reference and fault-injection manifests declare `acceptance` and stay
on the standard engine. Both metadata objects carry the hardening-chronicle
links, expected terminal, bounded recovery budget, and machine assertion IDs;
the schema keeps both optional so a standard fault manifest is not
misclassified or invalidated. The executor must satisfy every declaration
before it can report `accepted: true`. The complete evidence record is written to
`.tmp/remote-test-suite/<scenario>/<run-id>/result.json`.

Add `--cleanup` to remove the isolated run root after success. Setup rollback
and cleanup are idempotent. To inspect the full resource plan without creating
anything, add `--dry-run`; its JSON lists every resource the run would create
and destroy.

Each manifest exposes `claim`, `run`, `gate`, `review`, and `integration`
hooks. A hook is an argv array and runs both before and after its phase with
`REMOTE_TEST_PHASE`, `REMOTE_TEST_HOOK_POINT`, `REMOTE_TEST_RUN_ID`, and
`REMOTE_TEST_SEED`. Hooks observe or inject infrastructure conditions; they do
not add test branches to the production scheduler.

Fast contract tests:

```bash
npm --prefix tools/remote-test-suite test
```

The real protocol integration tests are opt-in because they build and host the
Task Server, and the restart replay also builds and launches the Runner:

```bash
REMOTE_TEST_SUITE_INTEGRATION=1 npm --prefix tools/remote-test-suite test
```

## Parallel delivery and post-processing load

Run the isolated 12-task baseline plus the controlled worker-loss repeat:

```bash
node tools/remote-test-suite/parallel-harness.mjs \
  --run-id "$(date -u +%Y%m%dT%H%M%SZ)" \
  --seed parallel-reference-v1 \
  --export-root "$JOB_RESULTS_DIR/parallel-delivery"
```

Each scenario creates one disposable project and twelve disjoint reference
tasks. Three coding workers claim four slots each before execution starts, so
the Task Server records twelve simultaneous Progress cards and every admission
decision. Separate gate and review process pools materialize fresh workspaces,
verify the declared Result SHA, and report their process, queue, duration, CPU,
memory, and namespace evidence. The Studio host is not part of admission or
post-processing.

Integration workspaces prepare concurrently. Push admission then follows task
ordinal, and every stale-base observation records the deterministic
`refresh-and-merge-exact-result` collision decision before merging the reviewed
SHA. Result handoff, completion, and review report are replayed once to prove
idempotency.

The worker-loss repeat terminates one four-slot gate worker while its processes
are live. Those cancellations are classified
`environmental-worker-loss`, retried once on healthy workers, and recorded as a
capacity reduction from twelve to eight gate slots. Healthy gate processes
continue during the loss.

Evidence lives under
`.tmp/remote-test-suite/parallel-delivery/<run-id>/evidence/`, or is copied to
`--export-root`. The durable files are:

- `acceptance.json` and `concurrency-report.md`.
- Per-scenario `timeline.jsonl`, `telemetry.jsonl`, and
  `runtime-events.jsonl`.
- Task histories, review attempts, audit rows, invariant snapshots, and final
  task state.

The real regression is opt-in:

```bash
REMOTE_TEST_PARALLEL_INTEGRATION=1 \
  node --test tools/remote-test-suite/test/parallel-delivery.integration.test.mjs
```

The harness records no model or CLI comparison dimension. Token counts, if a
future executor provides them, remain per-run telemetry only.

## Three-unit Docker Compose harness

The remote-host harness provisions a separate Task Server, deterministic Agent
Runner protocol process, and production Studio UI. A small TCP proxy supplies
named Runner and Studio links so partitions are deterministic and do not
disconnect or enumerate unrelated Docker networks. An identity-scoped MinIO
fixture supplies the S3-compatible cold archive target.

Inspect every resource and lifecycle operation without changing Docker:

```bash
node tools/remote-test-suite/compose-harness.mjs inspect \
  --run-id "$(date -u +%Y%m%d-%H%M%S | tr '[:upper:]' '[:lower:]')"
```

Run the complete acceptance workflow on a Docker host such as
`agent-runner-01`:

```bash
node tools/remote-test-suite/compose-harness.mjs run \
  --run-id "$(date -u +%Y%m%d-%H%M%S)"
```

That one command uses the explicit `remote-integration` Compose profile,
builds and health-gates the isolated stack, archives and verifies one task in
MinIO, restores its byte-identical payload, and then runs `reference-change`. The
card-safe default partitions the Task Server for 25 real seconds. Override that
duration with `REMOTE_TEST_AUTONOMY_SECONDS` or
`--autonomy-duration-seconds`. A duration of 600 seconds or more is rejected
unless `--machine-bound` is also explicit. Both already-claimed slots must
journal useful work through the configured outage while a waiting third task
remains unclaimed. Recovery must reconcile each exact fence before idempotently
replaying events, artifacts, Result SHAs, terminal reports, and completions.
The workflow separately replaces Runner and Task Server after safely claiming
the same third task that remained Ready through the outage. The Task Server
update must quarantine the old attempt, reject its stale completion, and accept
a higher-fenced recovery only after the old Runner container is positively
gone. It exports periodic and asserted autonomy timelines, versions, logs,
container inspection, API history, audits, invariant state, and assertions below
`.tmp/remote-test-suite/compose/<run-id>/evidence/`. Finally it drains the
server, proves there is no unresolved authority, and removes only containers,
volumes, networks, and images under the run's harness identity. Add `--keep`
only for interactive diagnosis.

Container inspection evidence is sanitized before it reaches disk. Values of
environment variables whose names match `TOKEN`, `SECRET`, `KEY`, or `PAT` are
replaced with `[REDACTED]`. Report publication applies the same filter again
before copying Compose evidence into the checked-in raw report tree.

The unit controls are available after `up`:

```bash
node tools/remote-test-suite/compose-harness.mjs up --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control partition runner --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control heal runner --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control replace studio --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs down --run-id rehearsal-01
```

`stop`, `restart`, `replace`, `partition`, and `heal` accept `studio`,
`task-server`, or `runner`. Task Server stop, restart, and replacement first
enter `Draining` and call `prepare-shutdown`. They refuse unresolved authority
unless `--force` is explicit. Successful restart and replacement wait for
readiness before returning Task Server to `Normal`; stop deliberately leaves
the persisted mode unchanged. The full acceptance workflow uses a forced
restart only to verify the documented fail-closed recovery contract.

The Docker scenario stays opt-in:

```bash
npm --prefix tools/remote-test-suite run test:compose
```

The release-suite proof of ten real wall-clock minutes is deliberately separate
and marked `MachineBound`. It is never part of a card run:

```bash
npm --prefix tools/remote-test-suite run test:machine-bound:autonomy
```

Routine `npm test` runs only fake and plan-level checks. The harness never
selects a model or provider CLI, runs a benchmark, or compares models. The
Runner image contains the production `agent-host` binary for version capture,
but its deterministic harness process drives only the public lease protocol.

## First dated acceptance package

After the named AGT-2399 scenario, parallel, and machine-bound Compose runs
finish, publish their retained evidence with:

```bash
npm --prefix tools/remote-test-suite run publish:acceptance-canary
```

The publisher validates every executed phase boundary, the report schema,
incident links, authority and delivery guards, the AGT-2200 acceptance matrix,
and exact Docker identity teardown. It copies reviewable raw evidence to
`docs/quality/remote-run-testsuite-report/2026-07-29/`, records SHA-256 hashes
and component versions, then removes the exact temporary fixture roots. It
refuses to run unless the board-facing identity is `agent-runner-01` and all
expected source roots exist.

## Safe fault injection catalog

`fault-catalog.json` defines the harness-only fault vocabulary and deterministic
operation/occurrence schedules. Production Runner and Task Server binaries do
not import it. A fault manifest is inert unless all of these interlocks hold:

1. The manifest selects a checked-in catalog id.
2. The command includes `--enable-faults`.
3. `--fault-ack` matches the SHA-256 acknowledgement bound to the exact
   scenario, run id, and resolved run root.
4. The harness owns the isolated Task Server. Fault injection refuses
   `--server-url`.
5. The per-run safety marker remains unchanged before every scheduled fault.

Use the dry run to inspect every resource, incident anchor, schedule, and the
run-bound acknowledgement:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario fault-network-and-terminal \
  --seed shipping-v1 \
  --run-id network-terminal-1 \
  --dry-run
```

Then copy the printed acknowledgement into the explicit fault run:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario fault-network-and-terminal \
  --seed shipping-v1 \
  --run-id network-terminal-1 \
  --enable-faults \
  --fault-ack rts-fi-<the-dry-run-value>
```

Fault runs clean their isolated run root automatically after emitting the final
JSON assertion report. Add `--keep` when an operator deliberately needs the
fault journal, outbox, or fixture repositories for inspection. `--cleanup`
retains the original explicit cleanup behavior and is mutually exclusive with
`--keep`. Setup failures, watchdog processes, and Task Server processes are
always cleaned up.

### Manifests

| Manifest | Fault class | Declared terminal |
|---|---|---|
| `fault-task-server-network-blips` | Claim and heartbeat request loss; event, artifact, and completion response loss after commit | Completed after exactly one bounded replay per operation |
| `fault-gate-timeout` | Deterministic command exceeds the watchdog | Process tree reaped, infrastructure timeout recorded, one bounded retry, then Completed |
| `fault-worktree-collision` | Occupied target path for five preparation attempts | Human Review with `worktree-blocked` and the busy path |
| `fault-lost-completion-sentinel` | Sentinel lost while provider completion and immutable result proof remain | Completed from durable proof |
| `fault-interrupted-terminal-marker` | Sentinel and provider terminal proof interrupted | Human Review with `ProtocolInconclusive`; no integration |
| `fault-network-and-terminal` | Network blips plus lost sentinel | Completed after replay and durable-proof recovery |
| `fault-network-and-gate-timeout` | Network blips plus gate timeout | Completed after both bounded recoveries |

Claim loss is injected before send because the claim contract has no
idempotency key. Event, artifact, and completion loss is injected after the
Task Server commits the first delivery. Their retry therefore proves canonical
idempotent replay instead of merely exercising a request that never arrived.

Every final report asserts lane, lease/fence and duplicate-claim count, process
reaping, worktree isolation and foreign-path preservation, outbox monotonicity
and backlog, exact server-side evidence copies, Result-SHA continuity, and the
declared incident outcome. A failed assertion fails the harness even when the
fixture's product tests passed. Token counts may be attached as run telemetry,
but they never affect these acceptance rules.

## Static acceptance report

Validated run-result JSON can be rendered as one self-contained, offline HTML
file:

```bash
npm --prefix tools/remote-test-suite run report -- \
  --input /path/to/validated-runs.json \
  --output /path/to/remote-run-report.html
```

The versioned contract is
[`run-result.schema.json`](run-result.schema.json). In addition to structural
validation, the renderer enforces phase order, full SHAs, accepted-state
consistency, wall-time bounds, token arithmetic, unique run and assertion ids,
and safe evidence links. Invalid input still writes a visible rejection report
and exits with status 2, so incompatible records cannot disappear from a
nominally green report.

The report is infrastructure-only. Tokens are optional per-run telemetry.
Model or CLI grouping, comparisons, rankings, and benchmark execution are not
part of the contract.

Regenerate the repository-viewable deterministic example with:

```bash
npm --prefix tools/remote-test-suite run report:fixture
```
