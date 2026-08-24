---
id: platform-architecture-fencing-leases-authority
title: "Fencing, leases, and attempt authority"
status: active
category: concept
updatedAt: 2026-08-17
last-updated: 2026-08-17
reason: "Transfer the durable single-writer contract out of the hardening dossier so the architecture survives the dossier lifecycle"
taskKey: AGT-2671
tags: [fencing, lease, attempt-authority, single-writer, idempotency, distributed]
related-tasks: [AGT-2147, AGT-2222, AGT-2370, AGT-2371, AGT-2372, AGT-2373, AGT-2631, AGT-2633]
related-adrs: []
related-docs:
  - "docs/concepts/platform-architecture/README.md"
  - "docs/operations/haertung-verteilte-ausfuehrung/index.html"
  - "docs/system/domains/runner.md"
---

# Fencing, leases, and attempt authority

> **Source dossier:** [Hardening distributed execution](../../operations/haertung-verteilte-ausfuehrung/index.html)
> (`AGT-W7`, still `active` because AGT-2373 is open).
> Index: [Platform architecture](README.md).

## Purpose

Distributed execution splits one logical task across three processes that can
each die, sleep or lose the network independently. Without a write-ordering
contract, a woken standby runner can still call completion after another runner
took the work over, and two writers can both believe they own the same task.

This page records the durable contract that prevents that: who may write, for
how long, under which generation, and what happens to a writer that has been
superseded. It is the extract of section 9 of the source dossier, which is the
only genuinely durable part of that document; the status narrative, option
comparisons and incident chronology stay in the dossier.

The rule the whole model reduces to: **a lease says how long you may write, a
fence says whether you are still the newest writer, and only the Task Server
decides both.**

## The authority model

| Actor | Owns | Never does |
|---|---|---|
| Task Server | Truth. Issues attempts, leases, fences and the authority epoch, and evaluates expiry against its own clock. | Checkout, build, test or provider CLI work (`backend/Features/Runner/AttemptAuthorityService.cs`). |
| Agent Host runner (`runner/`) | Execution. Claims work, renews its lease, ships logs and artifacts, delivers one immutable result. | Owns no task state and cannot move a lane ([runner domain](../../system/domains/runner.md)). |
| Agent Studio | Console and read model. | Holds no execution truth of its own. |

Two structural consequences follow. First, the runner clock is never
authoritative: it can neither postpone expiry nor authorize a takeover. Second,
there is deliberately no leader election and no mutual supervision. Staleness
is displayed, not acted on, because the lease already carries the answer
([connection-health.md](../../operations/haertung-verteilte-ausfuehrung/target-architecture/connection-health.md)).

Identity of a holder is a tuple, not a single id. The enforced fields are
`ExecutorId`, `LeaseId`, `Fence`, `AuthorityEpoch` and `AttemptId`. `HostId`,
`ProcessId` and `BackendName` are carried for diagnosis and restart
re-adoption but are not checked on ordinary writes
(`backend/Shared/Lease/RunLeaseWireModels.cs`). A process id is explicitly not
a takeover criterion.

## Vocabulary

Five identifiers travel together and answer five different questions.
Confusing them is the most common source of incorrect reasoning about this
system.

| Token | Question it answers | Scope | Changes on | What it is not |
|---|---|---|---|---|
| Attempt ID | Which durable run or review attempt is this? | one attempt | a new attempt | Not a lease. An attempt outlives a lease change. |
| Lease | Who may write until which server timestamp? | one attempt | acquire, takeover | Not a proof of death. Expiry does not remove a process. |
| Fence | Is this the newest lease generation of this task? | one task | every grant or takeover | Not a write counter and not an attempt number. |
| Authority Epoch | Which global claim generation of the store issued this? | whole store | controlled rotation | Not per task. Rotation does not bump any task fence. |
| Idempotency Key | Has exactly this delivery already been applied? | one operation | every logically new delivery | Not a permission. A fresh key cannot revive a stale writer. |

These are modelled directly as
`AttemptWriteReference(AttemptId, Fence, AuthorityEpoch, IdempotencyKey)` in
`backend/Shared/Attempts/AttemptAuthorityModels.cs`.

## Lease lifecycle

A lease is the time-bounded exclusive write permission of exactly one executor
for one attempt.

**Acquire.** The runner presents task, executor, host and an idempotency key.
If a current attempt is still `Leased` with `ExpiresAt` in the future,
acquisition is refused; a live lease cannot be stolen at any price. Otherwise
the previous non-terminal attempt is marked `Superseded`, a new fence is
minted, and a new attempt plus lease is created atomically.

**Renew.** The same executor presents the full write reference. Renewal is a
full TTL reset, not an extension, and it sets `LastHeartbeat`. Renewal never
mints a new fence. A transport error is not a confirmation.

**Release.** The holder gives the lease back cooperatively. Release stays
fenced and idempotent so a retry cannot double-apply a completion. Terminal
attempts still accept the final cleanup delivery.

**Expire.** If renewal stops, authority ends by server time. A later takeover
receives a higher fence. The old process is not thereby dead, only no longer
permitted to write.

| Plane | Default TTL | Min | Max | Source |
|---|---|---|---|---|
| Run and review attempts (backend) | 2 min | 30 s | 10 min | `backend/Features/Runner/AttemptAuthorityService.cs` |
| Task Server leases (SQLite plane) | requested, clamped | 30 s | 900 s | `task-server/TaskServerOptions.cs` |
| Integration lease | 10 min | 30 s | 45 min | `backend/Features/Runner/IntegrationLeaseService.cs` |
| Runner request and heartbeat cadence | requests 900 s, beats every 30 s, interval floor 5 s | | | `runner/RunnerOptions.cs`, `runner/LeaseHeartbeat.cs` |

Routes are asymmetric by history and worth knowing exactly:

| Plane | Acquire | Renew | Release | Peek |
|---|---|---|---|---|
| Run lease | `POST /api/runner/lease/acquire` | `POST /api/runner/lease/renew` | `POST /api/runner/lease/release` | `GET /api/runner/lease/{taskKey}` |
| Integration lease | `.../integration-lease/acquire` | `.../integration-lease/heartbeat` | `.../integration-lease/release` | `.../{projectName}/{integrationBranch}` |
| Attempts | `POST /api/attempts/reviews/{id}/claim` | `.../renew` | settle via `.../settle` | `GET /api/attempts/tasks/{taskKey}` |

Registered in `backend/Features/Tasks/LeaseEndpoints.cs`,
`IntegrationLeaseEndpoints.cs` and `AttemptAuthorityEndpoints.cs`. There is no
steal, force-acquire or revoke route on any plane.

Expiry is detected lazily, inside the next read or write, and never by a
background sweeper. In the SQLite plane the equivalent sweep runs inside the
claim transaction and returns the run to `pending`
(`task-server/TaskServerOrchestrationStore.cs`).

## Fencing tokens

The fence is a per-task monotonically increasing `long`. It is minted only at
run acquire and review claim, never on renew and never on release:

```csharp
private long NextFenceLocked(string taskKey)
{
    var last = _state.LastFenceByTask.TryGetValue(key, out var value) ? value : 0;
    _state.LastFenceByTask[key] = last + 1;
    return last + 1;
}
```

The counter survives Task Server restarts because it is persisted with the
store. A load failure refuses to reset fences rather than starting over.

Comparison on write is **strict equality**, not "greater or equal":
`write.Fence != run.LastFence` yields `StaleFence`. This is what makes a
resumed standby writer harmless. After fence 10 exists, fence 9 is permanently
stale, and no new idempotency key can restore it.

The same shape is implemented independently in the SQLite plane as
`fence_counters` with `last_fence + 1` per task
(`task-server/TaskServerStore.cs`) and as `orchestration_fence_counters` per
orchestration run (`task-server/TaskServerOrchestrationStore.cs`).

## Authority epoch

The authority epoch is a single global claim generation of the store, seeded to
1. Only `RotateAuthorityEpoch(reason)` advances it.

Rotation is deliberately a **soft drain, not a kill switch**. A lease issued
before rotation keeps its own older epoch and may keep renewing, writing and
settling under exactly that identity until it is released, expires or is taken
over by a higher fence. The comparison rules encode this precisely: a known
attempt epoch is any positive value not greater than the current global epoch,
and a write must match the epoch **of its own attempt**, not the current global
epoch. An older generation therefore drains cleanly, while a future epoch is
never accepted. This avoids a requeue wave every time recovery runs. Rotation
does not supersede attempts and does not make their task appear unleased.

## Single-writer invariants

Every server-mediated write passes one ordered gate before any side effect. The
order is load-bearing, because it decides which rejection an operator sees.

| # | Check | Result on failure |
|---|---|---|
| 1 | AttemptId, positive Fence, positive Epoch, IdempotencyKey present | `Invalid` (400) |
| 2 | Attempt exists | `NotFound` (404) |
| 3 | Delivery key already recorded | `Duplicate` (200, accepted) |
| 4 | Write epoch matches the attempt's own epoch | `AuthorityEpochMismatch` (409) |
| 5 | Attempt is still the current one for the task | `Superseded` (409) |
| 6 | Fence equals `LastFence` | `StaleFence` (409) |
| 7 | State is `Leased` | `InvalidState` (409) |
| 8 | Lease not expired | `LeaseExpired` (409) |
| 9 | Executor and lease id own the lease | `StaleFence` (409) |
| 10 | Task key matches | `SubjectMismatch` (409) |

The rejection vocabulary is the `AttemptWriteStatus` enum
(`backend/Shared/Attempts/AttemptAuthorityModels.cs`) and the check order is
implemented in `ValidateRunWriteLocked`. HTTP mapping lives in
`backend/Features/Tasks/AttemptAuthorityEndpoints.cs`.

Note the legacy lease facade returns HTTP 200 with `Granted = false` and an
outcome string (`StaleToken`, `Expired`, `NotHeld`) rather than 409
(`backend/Features/Runner/RunLeaseService.cs`). Callers must inspect the body,
not only the status code.

Serialization has three layers:

- **In-process gate.** Every mutating and reading method of the authority
  service takes one process-wide lock; claim, standalone acquire and completion
  additionally share a single `ClaimGate` semaphore.
- **Cross-process gate.** The persisted fence, epoch and `CurrentRunByTask`
  pointer, written atomically. A failed disk write restores the last durable
  snapshot before the error escapes, so a live process can never retain
  authority a restarted server would not recognize.
- **Filesystem gate.** `LaneMutexRegistry` serializes the seven writers of the
  lane folder tree per project, keyed by watch path, with ordered acquisition
  of both lanes on cross-lane moves to avoid deadlock
  (`backend/Features/Tasks/LaneMutexRegistry.cs`,
  `backend/Features/Tasks/TaskStateMachine.cs`).

## Replay and idempotency

Idempotency keys are scoped per operation, not global: `DeliveryKey(scope, key)`
produces `acquire:`, `renew:`, `release:`, `settle:`, `write:` and `evidence:`
prefixes. The same raw key in two scopes is two different deliveries.

The host-side write-ahead journal is the runner's only durable delivery
channel. `runner/DurableRunOutbox.cs` stores per run an `authority.json`
binding the outbox to `(RunId, TaskKey, RunnerId, InstanceId, LeaseId, Fence)`,
an append-only fsynced `journal.jsonl`, and an atomically replaced `ack.json`.
Item keys are `{RunId}:{sequence}` with a strictly monotonic sequence; a torn
tail is repaired on open. Delivery is **at-least-once**: send precedes
acknowledge, so a crash between them re-sends.

Deduplication is enforced at the destination, not by the sender. The Task
Server declares `idempotency_key TEXT NOT NULL UNIQUE` on event, artifact,
handoff and completion tables and ingests with `ON CONFLICT DO NOTHING`
(`task-server/TaskServerStore.cs`). Outbox sequences below the recorded
high-water mark are rejected as `stale-outbox-sequence`. A replayed handoff
must match run id, sequence, envelope digest, runner, instance, lease and
fence, otherwise it is an `idempotency-conflict`.

Two rules keep replay from becoming a second execution path:

- **Replay is read-only and can never mint authority.** An ambiguous key that
  matches more than one attempt is `Invalid`, not a guess.
- **Claim replay is lane-gated.** A durable claim may only be replayed when
  card and attempt converge on Progress: `Progress` is `AlreadyConverged`,
  `Ready` is `RepairToProgress`, everything else including a missing lane is
  `Refuse` (`backend/Features/Tasks/RemoteClaimReplayLanePolicy.cs`).

A subtle guard deserves naming: if a dead lease was claimed with exactly the
presented delivery key, the claiming process is still alive, because a restart
would change the instance id and therefore the key. The server keeps answering
`LeaseExpired` instead of minting a fresh fence, which would double-execute the
work and discard the first run as stale.

## Failure modes and recovery

| Failure | What survives | Detection | Recovery |
|---|---|---|---|
| Host standby, then wake | Nothing on the host side is trusted | Server clock expired the lease while the runner was frozen | Higher fence already exists, so every late call from the old process fails `StaleFence`. This is the case the model was built for. |
| Runner process restart | Durable slot state, outbox journal, worktree | Startup reconciliation | Lease authority is downgraded to `uncertain` and replay is blocked until one fenced renewal confirms it (`runner/DurableLeaseAuthority.cs`). Dead attempts are released, a foreign runner id fails closed. |
| Renewal fails, definitive 4xx | Local evidence | `LeaseHeartbeat` classifies 4xx except 408 and 429 as definitive | One re-adoption attempt on 404 or 409, otherwise the lease is marked lost and the CLI process group is terminated. Completion is then suppressed so the takeover holder owns the outcome (`runner/RemoteTaskRunner.cs`). |
| Network partition, no answer | Everything | Renewal window arithmetic | The run is cancelled once the clock reaches `StopBeforeUtc` (last known expiry minus one heartbeat interval), reason "renewal safety boundary reached". Silence is never read as permission. |
| Task Server restart | All durable truth on disk | Runner registration reports its exact fenced attempts | Recovery is **cooperative, not a takeover**: re-adoption requires attempt, task, lease, fence, epoch, executor, host and lease instance to match unchanged. A takeover or terminal transition changes at least one of those and cannot be reversed by re-registration. |
| Two runners claim one task | One winner | Claim selects only unleased Ready tasks inside one transaction | The incumbent wins while its lease is live. After expiry the newcomer gets a strictly higher fence and the loser becomes `Superseded`. Latest fence wins, there is no merge. |
| Local run goes silent | Worktree, logs | `RunLivenessMonitor`, sweep every 15 s, grace 30 s, invariant "no zombie survives 60 s" | Demote to Ready with the worktree intact, or retrigger post-processing if the core run had finished. Remote-leased tasks are deliberately excluded, their liveness is the lease TTL. |

The Task Server additionally runs an invariant reconciler every 30 to 60
seconds over four declared invariants
(`task-server/TaskServerInvariantReconciliation.cs`):

| Invariant | Detects | Action |
|---|---|---|
| `run-inventory` | Process without lease after grace | Emits an actionable `terminate-process` directive the runner acknowledges |
| `lease-without-process` | Lease without process after grace | Recorded only |
| `lane-process-consistency` | Progress lane without active run heartbeat | Recorded as `containment-required`, lane retained |
| `worktree-hygiene`, `load-gate` | Declared in the registry | Arrive as runner-submitted reports, no reconciliation branch |

The doctrine is stated in the code itself: runner-local orphan termination is
actionable here, while lease and lane mismatches are recorded without
requeueing, because the deployed backend remains the sole requeue authority.

## Boundaries of the guarantee

Fencing protects exactly the write paths the Task Server validates. It is not a
general sandbox.

- **A usable Git credential can push outside the contract.** The compensating
  controls are attempt-scoped immutable refs, protected branches, expected-SHA
  verification and credential isolation. Remote completion fetches
  `agent-studio/results/<attempt>/fence-<n>/<result-sha>` and verifies the tip
  equals the fenced `ResultSha` ([runner domain](../../system/domains/runner.md)).
- **Worker agents never author history.** They edit files in an assigned
  worktree, the platform commits, stamps the SHA and pushes. A HEAD delta
  detected across a run becomes `agent-git-violation` and quarantines the
  result
  ([agent-fencing.html](../../operations/haertung-verteilte-ausfuehrung/agent-fencing.html)).
- **Log ingestion deliberately bypasses fencing** when the runner omits the
  write reference, because a diagnostic log line needs no authority
  (`backend/Features/Diagnostics/LogIngestionEndpoints.cs`). Artifact ingestion
  does not bypass it.
- **The runner token is not a security boundary.** It is a precursor, the
  split-brain guard is the fence, not the credential
  (`backend/Features/Runner/RunnerIdentity.cs`).

## Delivered versus open

Delivered and load-bearing today:

| Capability | Evidence |
|---|---|
| Persisted attempt authority with per-task monotonic fences and a global epoch | `backend/Features/Runner/AttemptAuthorityService.cs`, store at `.metadata/attempt-authority.json`, schema v4 |
| Soft-drain epoch rotation that does not requeue live work | `AttemptAuthorityService.RotateAuthorityEpoch` |
| Ordered single-writer validation with a stable rejection vocabulary | `backend/Shared/Attempts/AttemptAuthorityModels.cs`, `ValidateRunWriteLocked` |
| Fenced, fsynced, at-least-once host outbox with digest-bound final handoff | `runner/DurableRunOutbox.cs` |
| Cooperative restart re-adoption that cannot reverse a takeover | `AttemptAuthorityService` re-adoption path |
| Runner-side split-brain guard with a renewal safety boundary | `runner/LeaseHeartbeat.cs`, `runner/DurableLeaseAuthority.cs` |
| Lane-gated idempotent claim replay | `backend/Features/Tasks/RemoteClaimReplayLanePolicy.cs` |

Open, and honest about it:

- **Two authority implementations coexist.** The backend JSON store and the
  `task-server/` SQLite store implement the same fence and lease contract
  separately. Convergence is not done, and the contract is currently maintained
  by discipline rather than by shared code. See
  [task-server topology](task-server-topology.md).
- **The integration lease is in-memory only.** Its fences reset to zero on
  process restart (`backend/Features/Runner/IntegrationLeaseService.cs`), so it
  does not survive the failure class the run lease was hardened against.
- **`LaneMutexRegistry` fails open.** After a 30 second timeout it logs a
  warning and proceeds without exclusion. The comment correctly calls a timeout
  a bug signal, but the fallback is availability over exclusion.
- **Fencing checks are skipped for unauthenticated callers.** Both
  `RunnerMatches` and `RunnerLeaseAuthorization.IsCurrent` return true when no
  runner principal is present
  (`backend/Features/Security/RunnerLeaseAuthorization.cs`).
- **No dead-letter or backoff for outbox delivery.** Retry is an unbounded
  5 second flush loop; only daemon startup has bounded linear backoff capped at
  60 seconds.
- **Two declared invariants have no reconciliation branch.**
  `worktree-hygiene` and `load-gate` are registry entries fed by runner
  reports, not server-side checks.
- **The acquire-to-lane crash window remains open**, as recorded in section 4
  of the source dossier. AGT-2373 (lane `2-ready`) is the open cleanup card
  that keeps `AGT-W7` out of History.

## Living knowledge log

- **2026-08-17 (AGT-2671):** Page created by the Dossier curation sweep. During
  extraction two dossier statements were corrected against the code: the run
  lease has no `/heartbeat` route (only the integration lease does), and there
  is no steal endpoint or background expiry sweeper anywhere. The dossier's
  section 9.4 rejection vocabulary turned out to be a literal transcription of
  the `AttemptWriteStatus` enum.
