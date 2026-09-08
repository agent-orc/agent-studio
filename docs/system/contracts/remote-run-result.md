# Remote Run Result Contract

Version: `remote-run-result/v1`

Status: infrastructure verification contract for the Remote Run Infrastructure
Test Suite. This format must not be used for model, CLI, cost, leaderboard, or
benchmark comparisons. Token counts are optional per-run telemetry only.

## Authority boundary

`RemoteRunResultCollector` combines two finalized inputs:

- Task Server evidence owns scenario and run identity, task state, attempts,
  lease fences, authority epochs, expected and actual outcome, assertions,
  canonical chronicle links, and Task Server artifact references.
- Runner evidence owns observed host and runner identity, deployed component
  versions, injected fault schedule, monotonic measurements, token telemetry,
  and Runner artifact references.

The collector has no task mutation, lease mutation, retry, recovery, or outcome
classification capability. A scenario harness must settle those facts through
the existing authoritative APIs before collection.

The machine schema is
[`remote-run-result.schema.json`](../../app/schemas/remote-run-result.schema.json).
The C# disk and collector records live in
`contracts/TaskServer.Contracts/RemoteRunResultContracts.cs`.

## Timing

Every result contains exactly one Claim, Run, Gate, and Review timing record.
Integration is optional. Skipped and failed phases still have a record. Queue
duration and execution duration are separate.

Monotonic Runner measurements take precedence when present. UTC timestamps
remain mandatory for correlation. The collector rejects a monotonic duration
that differs from the corresponding UTC interval by more than five percent or
one second, whichever is larger. All durations are non-negative.

## Token telemetry

Input, output, cached, and total tokens each use a tagged value:

- `Available` carries a non-negative observed value, including a real zero.
- `Unavailable` carries no value and requires a non-empty reason.

Phase attribution is recorded only where the source supports it. Collection
never converts missing telemetry to zero. Migration from v0 writes the explicit
reason `legacy-v0-did-not-record-token-telemetry`.

## Identity and evidence

The result carries the task key, base SHA, Result SHA, reviewed SHA, final lane,
run and review attempt ids, lease fences, authority epochs, raw artifact
digests, injected incident id, fault schedule, assertion evidence, and an
anchored link into the
[hardening chronicle](../../operations/haertung-verteilte-ausfuehrung/historie.html).

`contentSha256` is calculated over the UTF-8 canonical JSON with that property
omitted. Canonical JSON uses the web serializer defaults, two-space indentation,
LF (`\n`) line endings on every platform, and no trailing newline. Transport or
checkout line-ending changes therefore do not change the digest. Property or
value changes still invalidate it, making fixture and stored-result tampering
detectable.

## Immutable storage and replay

Results are stored as `<root>/<scenarioId>/<runId>.json` with an atomic,
create-once move.

- The same content digest is an idempotent replay.
- A lower authority epoch, or a lower fence in the same epoch, is a stale
  writer and is rejected.
- A different report at the same or higher authority is also rejected after
  finalization. A scenario run has one immutable result, not revisions.

The scenario and run ids must be safe single path segments.

## Migration and fixtures

Readers accept v1 directly. The sole supported migration is v0 to v1:
`durationMs` becomes `wallClockDurationMs`, missing tokens become explicit
unavailable values, and the v1 content digest is generated. Unknown future or
older versions fail closed.

Golden fixtures live under
`contracts/fixtures/remote-run-result/`. Focused tests validate:

- a complete v1 reference run;
- a structurally invalid run;
- v0 migration;
- monotonic and UTC consistency;
- mandatory phase records;
- explicit missing-token reasons;
- create-once replay and stale-writer rejection.
