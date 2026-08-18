---
id: platform-architecture-batch-gate
title: "Batch Gate: one full suite for a delivery wave"
status: proposed
category: concept
updatedAt: 2026-08-17
last-updated: 2026-08-17
reason: "Transfer the durable batch-gate mechanics out of the decision dossier so the architecture survives the dossier lifecycle"
taskKey: AGT-2671
tags: [batch-gate, integration, gate, delivery, fencing, promotion]
related-tasks: [AGT-2648, AGT-2543, AGT-2528, AGT-2603, AGT-2594]
related-adrs: []
related-docs:
  - "docs/concepts/platform-architecture/README.md"
  - "docs/concepts/platform-architecture/rebase-merge-and-integration-invariants.md"
  - "docs/operations/batch-gate-concept/index.html"
  - "docs/operations/develop-main-promotion.md"
---

# Batch Gate

> **Source dossier:** [Batch Gate: one full suite for a delivery wave](../../operations/batch-gate-concept/index.html)
> (`AGT-W41`, source card AGT-2648, status `decision-pending`).
> Index: [Platform architecture](README.md).

This page records the durable mechanics only. The option comparison, cost
evidence and pilot thresholds stay in the dossier.

## Status of this document

The mechanism below is the agreed target shape, not shipped behaviour. Nothing
named "batch gate" exists in code today. The final section separates the
delivered substrate from what is still open.

## Purpose

A Batch Gate replaces N concurrent full test suites with one suite over one
combined candidate. Every eligible pending delivery for the same project,
repository, integration branch and gate profile is mechanically replayed onto
one temporary branch, the complete suite runs once on that exact tip, and only
a green tip is published to `develop`.

The driver is host capacity at wave scale, not test cost. Test CPU remains
negligible against token spend; the measured problem was several full suites
competing with active coding runs on one 12-core host. Batch Gate reduces the
number of simultaneous suites. It does not weaken any suite.

A Batch Gate is a transaction scoped to one tuple: project, repository,
integration branch, gate profile. There is no cross-project batch.

## Mechanism

### 1. Close the manifest

At close time the coordinator snapshots the set of eligible deliveries into a
durable manifest. Once closed, a manifest never absorbs a new member. Later
arrivals wait for the next batch.

A delivery is eligible only when all of the following hold:

| Condition | Meaning |
|---|---|
| Settled envelope | The Result Envelope names an immutable result ref, result SHA, run attempt, delivery epoch and fencing token. |
| Model review passed | Per-card review passed the exact subject, with the build/test aspect recorded as `deferred-to-batch`, never as not applicable. |
| Current generation | No newer attempt, no reissue, no operator supersede. |
| Profile match | Project, repository, integration branch, gate profile and platform version match every other member. |
| Unowned | Not already claimed by another batch or an active per-task gate. |

The manifest records every eligible key and every exclusion with a typed
reason. The coordinator may not silently pick an easier subset.

### 2. Order deterministically

Members are ordered by `review-subject.completedAtUtc`, then durable enqueue
sequence, then task key as a final stable tie-breaker. Order, base SHA, member
subjects and gate profile together form the **membership digest**, which
identifies the batch for the rest of its life.

Manifest shape:

```json
{
  "batchId": "bg-20260811-0042",
  "project": "agent-taskboard",
  "baseSha": "<full develop SHA>",
  "gateProfileDigest": "<digest>",
  "members": [{
    "taskKey": "AGT-0000",
    "runAttemptId": "<attempt>",
    "deliveryEpochId": "<epoch>",
    "fencingToken": 7,
    "resultRef": "<project>/results/run_<id>/fence-<n>/<result-sha>",
    "resultSha": "<full delivery SHA>",
    "modelReviewId": "<review>"
  }],
  "membershipDigest": "<digest>"
}
```

### 3. Replay mechanically

Each member is replayed in a disposable detached worktree onto the current
batch tip using the existing conflict-free mechanical rebase rules in
`backend/Features/Git/GitService.cs` (`MergeBranchIntoIntegration`,
`TryMechanicalRebase`, `MechanicalRebaseFailureKind`). Every replacement SHA
maps back to its original delivery SHA, as already modelled by
`RebasedCommitReplacement(OriginalSha, RebasedSha)`.

A successful replay advances only the temporary batch ref. It does not make the
card integrated.

If a member conflicts, the replay aborts and its worktree is removed without
touching the current batch tip. That member is ejected to the existing
rebase-and-retry steer path
(`backend/Features/Tasks/TaskIntegrationRecoveryEndpoints.cs`,
`POST /api/tasks/{jobId}/integration/rebase`) and construction continues with
the next member. The coordinator never authors content resolution.

**Conflict cascade guard.** If more than 25 percent of a batch, or three
members in succession, conflict during construction, close the candidate with
the members already admitted and stop admitting later members. The unattempted
tail moves intact to the next batch. The failure semantic is "smaller closed
batch", never "resolve serial conflicts centrally".

### 4. Run one suite

Persist a batch-run record before execution: batch id, membership digest, base
SHA, candidate SHA, gate profile and digest, host, lease fence, command list,
start time, evidence location.

Then run the complete suite once on a clean checkout of the candidate SHA. The
pass receipt carries the same candidate SHA and profile digest. One active full
suite per physical host, one active Batch Gate per repository. The gate slot is
reserved before the suite starts so coding admission cannot consume the
promised capacity, and a batch suite is not started while host load already
exceeds the admission threshold.

### 5. Publish or isolate

Green path, in order:

1. Revalidate every member generation.
2. Acquire the project publication lease.
3. Fetch `develop` and verify its tip still equals the recorded pre-tip.
4. Publish by fast-forward when the tested candidate descends directly from the
   pre-tip. A merge topology is allowed only when the merge commit itself was
   prebuilt and tested as the candidate.
5. Verify remote `develop` resolves to the tested candidate.
6. Apply the existing auto-main policy only while holding the same ref-mutation
   authority and only after the batch is green.
7. Write per-member gate and integration evidence, then release each member to
   Human Review.

Red path: classify first, then isolate. See below.

### 6. Evidence per member

Each member needs two linked records, not one batch-wide narrative.

| Record | Required identity | Failure semantic |
|---|---|---|
| Gate-passed record | Task key, run attempt, delivery epoch, original result SHA, replacement SHA set, batch id, membership digest, base SHA, tested candidate SHA, gate profile digest, batch-run id, verdict, evidence path. | `batch-gate-evidence-missing`. The card cannot be marked gate-passed. |
| Integration record | `develop`, exact remote tip, the member's integrated SHA set, and evidence pointing to the same batch run and candidate. | `integration-unverified`. The card stays out of Completed. |

The existing endpoint `POST /api/tasks/{jobId}/integration-records`
(`backend/Features/Tasks/Acceptance/TaskIntegrationRecordEndpoints.cs`) can
append an idempotent `integrated-verified` row once the card is in Human Review
or later. Its five-class schema in
`backend/Shared/Models/TaskIntegrationRecord.cs` (`integrated-verified`,
`integrated-historical`, `no-code-expected`, `content-on-fence`,
`genuinely-missing`) is **not** a native gate-pass authority: it rejects
in-flight lanes and never moves a card or changes Git. A native batch-gate
record is required before lane release.

## Invariants

1. One suite run authorises exactly one candidate SHA and exactly one member
   set. A green result is not transferable to any other tip or membership.
2. No canonical branch mutation happens before the green publication step
   acquires ref-mutation authority.
3. Never create a new, untested merge commit after the suite. Fast-forward, or
   a merge commit that was itself the tested candidate.
4. No force-push at any stage.
5. A lane position, a review narrative, a temporary branch or a historical
   record never implies gate-passed or integrated. Git ancestry is stronger
   than bookkeeping; a record cannot manufacture integration.
6. A temporary replay is not the "integrated before review" event. Only
   verified membership in `develop` releases a member to Human Review.
7. Human acceptance stays a separate step and is unchanged.
8. Infrastructure failure is never reinterpreted as product failure, and
   repeated failure is never converted into green by majority vote.
9. Machine-bound suites keep their separate scheduling contract and are not
   hidden inside the routine full suite.
10. Projects remain sequential at the canonical integration boundary even while
    coding is concurrent.

The card lifecycle becomes: settled delivery, per-card model review, batch
pending, batch gate and verified mirror, Human Review.

## Ref and branch naming contract

| Artifact | Form | Notes |
|---|---|---|
| Batch branch | `agent-studio/batch-gate/<project>/<batch-id>` | Temporary, disposable, created from a fetched immutable `develop` base. Never a promotion input. |
| Batch id | `bg-<yyyymmdd>-<seq>`, for example `bg-20260811-0042` | Appears in the batch-run record and in every per-member evidence row. |
| Integration branch | `develop` | The only publication target of a batch. |
| Release branch | `main` | Advanced only by the existing auto-main policy or the promotion train, inside the same authority window. |
| Delivery result ref | `<project>/results/run_<id>/fence-<n>/<result-sha>` | The implemented form, enforced by the regex in `backend/Features/Tasks/RemoteCommitAttributionGuard.cs`. The dossier's `refs/agent-studio/results/...` spelling is illustrative and does not match code. |

Authority is layered. The batch coordinator holds a durable lease keyed by
project, repository, integration branch and gate profile; every state write and
candidate ref carries its fencing token. Member attempt fences prevent stale
attribution. A separate project ref-mutation lease serialises canonical ref
writes across the batch publisher, direct integration, auto-main advance and
the promotion train. It is acquired only for final revalidation and
publication, so a long suite does not block read-only preparation.

The promotion train never discovers or promotes a temporary batch ref. It
promotes only its own recorded candidate SHA. Train and batch may prepare
concurrently; their publish phases may not overlap.

## Failure and recovery behaviour

### Red classification

| Class | Definition | Response |
|---|---|---|
| Infrastructure red | Host loss, timeout without verdict, registry outage, disk pressure, lease loss. | Attribute no member. Retry the same candidate once on a healthy gate host. A second infrastructure red pauses the batch for operator visibility. |
| Deterministic suite red | Reproduced failing build or test on the same candidate. | Start ordered halving from the same base and gate profile. |
| Flaky red | Existing bounded quarantine policy applies. | Preserve both runs. No majority-vote green. |

### Bounded halving

1. Split the ordered member set in half. Reconstruct the first half on the
   original base and run the same complete gate.
2. If that half is red, continue with it. If it is green, continue with the
   complement only under the single-cause assumption. Preserve the green subset
   as evidence.
3. At one member, eject that delivery to a per-task gate. A conflicting replay
   goes to rebase-steer; a red replay gets the failing gate evidence attached
   to the card.
4. Rebuild all survivors in original order and run one complete suite. If still
   red, start another cycle or eject the unresolved cohort when the diagnostic
   budget is exhausted.

Each isolation cycle is capped at `ceil(log2(n))` diagnostic runs. The bound is
honest only for a single reproducible monotone offender.

| Failure shape | Bound | Fallback |
|---|---|---|
| One independent offender | `ceil(log2(n))` diagnostic runs, `ceil(log2(n)) + 2` suite runs including the initial red and the survivor run. | Eject one, rerun survivors once. |
| `k` independent offenders | Up to `O(k log n)`; each red survivor set starts a new bounded cycle. | Stop at the configured total budget, per-task gates for the unresolved cohort. |
| Cross-member interaction | No logarithmic guarantee. A half can be green while the union is red. | Preserve the smallest known failing cohort, send to isolated gates or an operator-owned interaction card. |
| Non-deterministic failure | No offender conclusion. | Flaky or infrastructure policy. Never steer a member from an unproven association. |

An unresolved cohort is a visible fallback, not a batch success.

### Condition table

| Condition | Batch outcome | Member outcome | Recovery |
|---|---|---|---|
| Construction conflict | Continue without member, apply cascade guard. | `conflict-ejected`, not gate-run. | Rebase-and-retry steer creates a new fenced generation. |
| Member superseded before suite start | Reconstruct without it. | `member-superseded`. | New generation joins a later batch. |
| Member superseded during suite | Stop safely, verdict unusable, reconstruct, rerun. | `member-superseded`. | Green result for the old manifest cannot publish. |
| Member superseded after green, before publish | Final membership check fails closed. | No mirror. | Rebuild. |
| Member superseded after verified publish | Transaction complete. | Integration history preserved. | Never transfer the gate result to a newer attempt. |
| Coordinator lease lost | `abandoned-fence`, never publish. | Still batch-pending. | Successor resumes only from a manifest whose digest and fence still match, otherwise reconstructs. |
| Infrastructure gate failure | `infrastructure-red`. | No attribution. | One same-SHA retry, then visible pause or alternate gate host. |
| Deterministic gate failure | `product-red`. | Unknown until isolated. | Bounded halving, cohort fallback, survivor rerun. |
| `develop` pre-tip changed | `stale-base`. | No pass released. | Reconstruct on the new base and rerun. Do not merge a green batch into a moved tip. |
| Promotion holds publication lease | `publish-waiting`. | Batch-pending. | Wait, then revalidate. Rebuild if the pre-tip changed. |
| Push rejected or remote verify differs | `publish-failed`. | Not integrated, not released. | Retry the exact non-force publication only if the pre-tip still matches, otherwise rebuild. |
| Per-member gate record write fails | Green but unfinalised. | `batch-gate-evidence-missing`. | Idempotently rewrite from the durable run record, block lane release. |
| Integration-record append fails after Human Review move | Publication stays valid. | `integration-record-pending`, completion blocked. | Retry the stable record id. Never roll back Git for a bookkeeping failure. |
| Coordinator restart after green | Recover from run receipt and remote refs. | No duplicate pass record. | Compare batch id, digest, candidate, remote `develop` and per-member ids before resuming. |

### Rollback

Batch Gate is disabled per project by stopping manifest formation. Any
unmirrored batch is marked `abandoned-by-rollback` with its manifest, logs and
candidate ref retained for a bounded period. Unmirrored members return to the
per-task gate using the same immutable delivery subject; coding is not reissued
merely because batching was disabled. An already published candidate is normal
Git history: do not reset or rewrite `develop`, address regressions through a
normal revert delivery. Missing per-member bookkeeping is still retried from
the durable batch record after routing is disabled.

## Delivered versus open

A repository-wide search for `batch-gate`, `BatchGate` and `batchGate` matches
only `docs/operations/README.md` and `docs/start/README.md`. The dossier
carries `status: decision-pending` and an empty `implementationTasks` list.

The substrate the mechanism composes on is delivered:

| Building block | Where it lives | State |
|---|---|---|
| Mechanical conflict-free replay, SHA replacement map, typed conflict list | `backend/Features/Git/GitService.cs` (`MergeBranchIntoIntegration`, `MergeRemoteDeliveryIntoIntegration`, `TryMechanicalRebase`, `RebasedCommitReplacement`, `MergeIntoIntegrationOutcome`) | Delivered |
| Integrate-before-review coordinator, per-project delivery serialisation, typed pre-review gate failures | `backend/Features/Pipeline/RemoteDeliveryIntegration.cs`, call site in `backend/Features/Runner/V1ReviewPlaneEndpoints.cs` | Delivered (AGT-2528) |
| Merge into `develop`, develop-first lineage, auto-main advance behind a full pre-main gate | `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`, `ImmediateIntegrationLineagePolicy.cs`, `PreMainTestGate.cs`, `PreDevelopBuildGate.cs` | Delivered |
| Full-suite runner, exact-subject enforcement, machine-wide single-flight lock, exact-SHA workspace lease | `backend/Features/Pipeline/BuildTestGateRunner.cs` | Delivered |
| Gate profile | `BuildProfile` in `backend/Shared/Models/ProjectSettings.cs`. There is no `GateProfile` type and no gate profile digest today | Partially delivered |
| Result Envelope, digest, attempt authority, supersede | `contracts/TaskServer.Contracts/ResourceContracts.cs`, `ResultEnvelopeDigest.cs`, `backend/Features/Runner/AttemptAuthorityService.cs` | Delivered |
| Delivery subject and result ref resolution | `backend/Features/Pipeline/ReviewSubjectStore.cs`, `DeliveryRefResolver.cs`, `backend/Features/Tasks/RemoteCommitAttributionGuard.cs` | Delivered |
| Rebase-and-retry steer path | `backend/Features/Tasks/TaskIntegrationRecoveryEndpoints.cs`, `backend/Features/Pipeline/IntegrationAgentRoundService.cs` | Delivered |
| Integration records, five-class schema, historical verification sweep | `backend/Features/Tasks/Acceptance/TaskIntegrationRecordEndpoints.cs`, `backend/Shared/Models/TaskIntegrationRecord.cs`, `HistoricalIntegrationVerificationSweep.cs` | Delivered as bookkeeping only, not as gate authority |
| Per-project, per-branch lease with FIFO queue, TTL and monotonic fencing token | `backend/Features/Runner/IntegrationLeaseService.cs`, `backend/Features/Tasks/IntegrationLeaseEndpoints.cs` | Delivered, but scoped to the integration branch, not to canonical ref mutation |
| Candidate-SHA promotion train, atomic `main` plus tag push, remote verification | `scripts/release/promote-develop-to-main.sh`, `scripts/release/promotion-full-gate.sh`, `docs/operations/develop-main-promotion.md` | Delivered as operator-run shell (AGT-2594) |

Open, with no implementation:

- The batch coordinator, closed manifest, membership digest, batch-run record
  and batch branch namespace.
- The **shared project ref-mutation lease**. Today the promotion train (shell,
  guarded only by re-fetch plus atomic non-force push) and the in-product
  auto-main advance do not share a lease. This is the largest gap between the
  concept and the code.
- A **native per-member batch-gate record**. The existing five-class
  integration record cannot honestly express batch gate authority and rejects
  in-flight lanes.
- A gate profile digest and the `deferred-to-batch` review aspect value.
  Neither string appears in the codebase.
- Bounded halving, the conflict cascade guard, and all typed batch failure
  codes (`conflict-ejected`, `member-superseded`, `abandoned-fence`,
  `infrastructure-red`, `product-red`, `stale-base`, `publish-waiting`,
  `publish-failed`, `batch-gate-evidence-missing`,
  `integration-record-pending`, `abandoned-by-rollback`).
- Formation triggers and scheduling parameters. The dossier's values (close at
  4, maximum 8, 15-minute age, pressure close at 2, docs-only first) are pilot
  parameters, not product defaults.

## Living knowledge log

- **2026-08-17 (AGT-2671):** Page created by the Dossier curation sweep. During
  extraction the dossier's `refs/agent-studio/results/...` ref spelling was
  found to disagree with the form enforced in
  `backend/Features/Tasks/RemoteCommitAttributionGuard.cs`; this page records
  the implemented form. The dossier stays `decision-pending`.
