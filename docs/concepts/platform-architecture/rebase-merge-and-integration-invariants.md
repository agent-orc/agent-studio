---
id: platform-architecture-rebase-merge-invariants
title: "Rebase, merge, and bounce steering: integration invariants"
status: active
category: concept
updatedAt: 2026-08-17
last-updated: 2026-08-17
reason: "Transfer the delivered integration invariants out of the decision dossier so the architecture survives the dossier lifecycle"
taskKey: AGT-2671
tags: [git, integration, rebase, merge, bounce-steering, invariants]
related-tasks: [AGT-2662, AGT-2544, AGT-2557, AGT-2563, AGT-2632, AGT-2648, AGT-2654]
related-adrs: [ADR-0052]
related-docs:
  - "docs/concepts/platform-architecture/README.md"
  - "docs/concepts/task-integration-and-merge-workflow.md"
  - "docs/operations/develop-main-promotion.md"
  - "docs/operations/git/commit-push-doctrine.md"
---

# Rebase, merge, and bounce steering

> **Source dossier:** [Rebase vs merge and bounce steering](../../operations/rebase-merge-and-steering/index.html)
> (`AGT-W37`, source card AGT-2662, status `decision-pending`).
> Index: [Platform architecture](README.md).

## Purpose

This page documents how a reviewed delivery reaches the integration branch,
when the platform preserves delivery commit objects and when it rewrites them,
and how a delivery that failed to integrate is returned to an agent for one
bounded recovery round.

Unlike its source dossier, this page describes **implemented behaviour verified
against the backend**. The pending-decision material stays in the dossier; only
the parts already expressed in code and in the existing concept pages are
recorded here.

Naming trap: `backend/Features/Tasks/Merge/` is the task-card consolidation API
(`MergeService`, `MergeCandidateFinder`, `MergeAuditLog`, routes
`GET|POST /api/tasks/{primaryId}/merge*`). It merges cards, not Git history.
Git integration lives in `backend/Features/Git/GitService.cs` and
`backend/Features/Pipeline/MergeIntoDevelopRunner.cs`.

## The objects the policy protects

| Object | Definition | Owner |
|---|---|---|
| Delivery evidence | Active, non-superseded `commits[]` entries on the task card | `backend/Features/Tasks/TaskMutationService.cs` |
| Review subject | One fenced result ref plus the expected head SHA | `backend/Features/Pipeline/ReviewSubjectStore.cs` |
| Integration truth | Every active attributed commit is an ancestor of the configured integration branch | `backend/Features/Tasks/TaskIntegrationStatusService.cs` |
| Promotion truth | `main` advances only to the exact gated candidate SHA | `docs/operations/develop-main-promotion.md`, `backend/Features/Pipeline/MergeIntoDevelopRunner.cs` |
| Integration record | Append-only classification of one task's integration history | `backend/Shared/Models/TaskIntegrationRecord.cs` |

## Invariants

1. The run agent performs no Git. Branching, staging, committing, pushing,
   merging and tagging are platform pipeline steps (ADR-0052,
   [task-integration-and-merge-workflow.md](../task-integration-and-merge-workflow.md)).
   A worker CLI is additionally blocked by `AgentGitCommandGuard`.
2. Integration is never merged into a dirty tree.
   `GitService.MergeRefIntoIntegration` refuses with `Error` and names the dirty
   files before touching anything.
3. Integration is idempotent. If the source ref is already an ancestor of the
   integration branch, the result is `AlreadyMerged` and no history is created.
4. The integration branch is synchronized before any merge.
   `GitService.SynchronizeIntegrationBranch` fetches `origin/<branch>` and
   fast-forwards a stale local branch. A local-ahead branch is left alone;
   genuine divergence returns `Diverged` and fails closed rather than
   overwriting either tip.
5. Canonical integration preserves delivery SHAs first. A direct
   `git merge --no-ff --no-edit` is attempted before any history rewrite.
6. A history rewrite is admissible only with an exact one-to-one replacement
   map. The fallback rebase refuses on a non-linear delivery
   (`rev-list --count --merges` greater than zero) or on changed commit
   cardinality, and reports `AgentRoundRequired` instead of guessing.
7. A rewrite that cannot be persisted is rolled back. If
   `TaskMutationService.RecordMechanicalRebaseOnFolder` fails after a
   `MergedAfterRebase`, `MergeIntoDevelopRunner` hard-resets the integration
   branch to `PreviousIntegrationSha` and nothing is pushed.
8. Rewritten commits are retained, not deleted. The original entry stays in
   `commits[]` marked `supersededBySha`, and the replacement SHA is appended
   with the original producer attribution. Superseded entries do not count
   toward integration completeness, so a rewrite cannot produce a false
   `partial`.
9. Acceptance does not integrate.
   `TaskTransitionService.ValidateIntegratedAcceptance` validates the current
   review attempt, then requires a Git-derived `integrated` status. A card that
   is not integrated stays in Human Review with
   `MoveJobStatus.IntegrationFailed` (HTTP 409).
10. The gated subject is the merge result, not the delivery.
    `MergeIntoIntegrationGatedAsync` captures the pre-merge tip, merges, builds
    the merge commit in an isolated worktree, and on a red gate resets the
    branch to the captured tip and returns `GateFailed`.
11. `main` advances only from `develop`, and only to an exact commit.
    `ImmediateIntegrationLineagePolicy` blocks any other topology. The operator
    promotion train publishes the pinned candidate SHA by non-force atomic push
    and never creates a merge, squash or rebase after the gate.
12. Automation may steer, never resolve. `IntegrationAgentRoundService` and
    `TaskIntegrationRecoveryEndpoints` persist a steer intent, supersede the
    failed delivery and queue Ready. Neither authors Git history, waives a gate
    or moves a protected branch.
13. Integration records are bookkeeping only. Appending one never changes a
    lane, branch, commit chain or Git history, and never authorizes a merge.

## Rebase versus merge: the stage matrix

The apparent inconsistency is a stage boundary, not one Git policy applied
twice. Each stage has a different relationship to the evidence that already
names the delivery.

| Stage | Operation | SHA identity | Why | Code |
|---|---|---|---|---|
| Parallel run-end integration (`MaxParallelism >= 2`) | `rebase` onto the integration tip, then `merge --ff-only` | Rewritten, before any review exists | Linear history and a deterministic conflict; no evidence has named the SHAs yet | `WorktreeTaskLifecycle.Integrate`, `GitService.RebaseOnto`, `GitService.MergeFastForward` |
| Sequential acceptance merge (`MaxParallelism == 1`) | `merge --no-ff` | Preserved | One revertable delivery commit on the integration branch | `GitService.MergeBranchIntoIntegration` |
| Canonical integration, step 1 | `merge --no-ff --no-edit`, `rerere` disabled | Preserved | The reviewed SHAs are already named by `commits[]` and the review subject | `GitService.MergeRefIntoIntegration` |
| Canonical integration, step 2 | `merge --strategy=ort --no-ff`, `rerere` enabled with autoupdate | Preserved | Mechanical resolution without rewriting the source ref | `GitService.TryMechanicalMerge` |
| Canonical integration, step 3 | `rebase --rebase-merges --onto <tip> <merge-base>` in a disposable detached worktree, then `merge --no-ff` | Rewritten, only with an exact map | Last preservation-compatible option before asking a human or an agent | `GitService.TryMechanicalRebase` |
| Agent recovery round | Agent rebases its own delivery onto current `origin/<branch>` and republishes | Rewritten under a fresh attempt fence | A new attempt records replacement attribution before review, so rewriting is legitimate | `TaskIntegrationRecoveryEndpoints`, `IntegrationAgentRoundService` |
| Release-line merge from a task branch | Fast-forward only, after the pre-main full suite | Preserved | `MergeIntoDevelopRunner` refuses with `Release source '<branch>' must be rebased onto '<release>' before the full-suite gate` when the source is not a descendant | `MergeIntoDevelopRunner.MergeIntoMainAsync` |
| `develop` to `main` promotion | Non-force atomic push of the pinned candidate SHA | Preserved exactly | Any new object after the gate would release something untested | `scripts/release/promote-develop-to-main.sh` |

Squash is not an admissible canonical integration operation anywhere in the
code path. There is no squash branch in `MergeRefIntoIntegration`.

## The canonical integration ladder

`GitService.MergeRefIntoIntegration` executes in this fixed order, and each
step only runs when the previous one reported an actual textual conflict:

1. Guard: integration branch exists, tree is clean, integration branch is
   checked out.
2. Ancestry short circuit: source already contained, return `AlreadyMerged`.
3. Capture `integrationTip` as the rollback anchor.
4. Direct `merge --no-ff --no-edit`. Success returns `Merged`.
5. On conflict, abort the merge and record the unmerged file list. A failure
   with no conflicted files is a plain `Error`, not a conflict.
6. Mechanical three-way merge with `rerere`. Success returns `Merged`. A rerere
   resolution is only eligible when it stages every conflicted path.
7. Mechanical rebase probe in a temporary detached worktree, with `rerere` and
   `rebase.autoStash` disabled. Refuses on merge commits in the delivery range,
   on cardinality change, or on any textual conflict.
8. Re-check that the integration tip did not move during the probe; if it did,
   return `Error` and ask for a retry against the current tip.
9. `merge --no-ff` of the rebased tip, returning `MergedAfterRebase` with
   `PreviousIntegrationSha` and the replacement list.
10. Cleanup must remove both the temporary directory and the Git worktree
    registration; a failed cleanup fails the whole attempt rather than mutating
    the target repository.

## Outcome vocabulary

`MergeIntoIntegrationOutcome` (`backend/Features/Git/GitService.cs`):

| Outcome | Meaning | Counts as integrated |
|---|---|---|
| `Merged` | A merge commit was created, delivery SHAs preserved | Yes |
| `MergedAfterRebase` | Cardinality-preserving replay, then merge; replacement map attached | Yes |
| `AlreadyMerged` | Source already contained, no-op | Yes |
| `NoTaskBranch` | No delivery branch existed or it could not be fetched | No |
| `PushedForReview` | `IntegrationStrategy == pull-request` deliberately left the ref for external review | No |
| `Conflict` | Merge conflicted, aborted, conflicted files reported | No |
| `GateFailed` | Merge succeeded, pre-develop build gate went red, branch rolled back | No |
| `AgentRoundRequired` | Preservation impossible and mapping ambiguous, caller must start a bounded steer round | No |
| `Error` | Precondition or Git failure | No |

`MergeIntoIntegrationOutcomePolicy.IsSuccessfulIntegration` is the single
predicate that decides whether a push is enqueued and whether acceptance may
complete.

## Failure classification decision matrix

The house rule in [`docs/quality/dotnet-backend.md`](../../quality/dotnet-backend.md)
("express branching lifecycle decisions as pure policy with direct matrix
tests") is satisfied by `AcceptedIntegrationFailurePolicy` in
`backend/Features/Pipeline/AcceptedIntegrationFailurePolicy.cs`. Both the
pipeline writer and the card projection call it, so persisted codes, recovery
eligibility and operator copy cannot drift. Its matrix is pinned directly by
`FailureMatrix` in `backend.Tests/AcceptedIntegrationFailurePolicyTests.cs`.

| Step verdict or reason signal | Code | Operator label | Rebase recovery offered |
|---|---|---|---|
| `conflict` | `merge-conflict` | Merge conflict | Yes |
| `gate-failed` | `build-gate-failed` | Build gate failed | No |
| `delivery-gate-failed` | `delivery-gate-failed` | Delivery gate failed | No |
| `agent-round-required` | `delivery-attribution-ambiguous` | Delivery attribution needs a new round | No |
| reason contains `must be rebased onto` | `source-needs-rebase` | Rebase required | Yes |
| reason contains `no stable key for review-subject validation` | `review-subject-task-key-unavailable` | Task key unavailable | No |
| reason mentions `review subject` or `review-subject` | `review-subject-invalid` | Review subject invalid | No |
| `no-branch` | `no-task-branch` | No task branch | No |
| anything else on a failed step | `integration-error` | Integration failed | No |

A persisted code always wins over inference. A step that is neither `Failed`
nor `no-branch` classifies to null, so a passed step can never render a failure
chip.

Three further pure policies complete the lifecycle decision surface:

| Policy | Inputs | Decision |
|---|---|---|
| `AcceptanceIntegrationPolicy.IsIntegrationRequired` | `NoBranchExpected`, read-only mode, epic kind, `taskType` in `concept` or `decision` | Whether the card must be integrated at all |
| `AcceptanceIntegrationPolicy.Decide` | merge outcome, `operatorOverride`, `integrationRequired` | `Complete` or `ReturnToHumanReview` |
| `ImmediateIntegrationLineagePolicy.Decide` | target branch, `develop` availability, `main` is ancestor of `develop` | `DirectToConfiguredTarget`, `DevelopThenMain`, or `Blocked` |
| `ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance` | target branch, `develop` availability, candidate is the published `develop` tip | `Allowed` or `Blocked` |

## Bounce steering

A bounce is the loop that returns a reviewed but unintegrated delivery to an
agent for exactly one mechanical recovery round.

State machine:

1. Integration returns a non-successful outcome. `MergeIntoDevelopRunner.Record`
   persists the typed failure code on the `post-merge-into-develop` step.
2. `TaskIntegrationStatusService.ClassifyNotIntegrated` projects the card as
   `conflict-skipped` with `integration.failure` populated. Without a recorded
   failure the card is `pending` or `no-branch`.
3. Automatic pre-Human-Review round: only for `AgentRoundRequired`.
   `RemoteIntegrationContinuationPolicy.Decide(outcome, automaticAgentRoundsUsed)`
   is the budget gate.
4. Operator-triggered round: `POST /api/tasks/{jobId}/integration/rebase`,
   surfaced in the UI as **Rebase and retry** next to the red badge.
5. Either path persists a Steer intent, appends a continuation note, supersedes
   the current delivery with `TaskCommitSupersession.PendingAttempt`, promotes
   the card to the top of Ready, and appends an `integration_recovery_queued`
   timeline event.
6. The next claim opens a new attempt and fence. The new delivery and review
   re-record current SHAs, so the rebase is never substituted underneath old
   evidence.

Budget matrix (`RemoteIntegrationContinuationPolicy`, pinned by `[Theory]`
cases in `backend.Tests/RemoteDeliveryIntegrationTests.cs`):

| Outcome | Automatic rounds used | Action |
|---|---|---|
| `Merged` | any | `None` |
| `Conflict` | any | `None` |
| `AgentRoundRequired` | 0 | `StartAgentRound` |
| `AgentRoundRequired` | 1 or more | `LeaveForHumanReview` |

`MaxAutomaticAgentRounds` is 1 and is counted per operator review epoch. The
epoch is read from `OperatorReviewRequeueService.ReadEpoch` and incremented
whenever a human deliberately moves a reviewed card back for another attempt,
so an operator requeue re-opens the budget while a machine loop cannot.

## Conflict handling and recovery

- A conflict never leaves a dirty tree. Every conflicting path aborts its merge
  or rebase, and the conflicted file list is reported rather than swallowed.
- The conflicted file list is durable verdict evidence on the card, so the
  operator sees which files actually collided.
- Preconditions for the operator recovery action, all enforced server-side: the
  task is in Human Review, Completed or Archive; the computed status is
  `conflict-skipped`; the projected failure has `RebaseRecoveryAvailable`; the
  last `post-merge-into-develop` step re-classifies to the same conclusion; and
  a fenced result ref exists in `review-subject.json`. Any missing precondition
  returns HTTP 409, not a partial mutation.
- The steer prompt is fixed and narrow: resume the existing delivery branch at
  the fenced result SHA, fetch the current integration branch, rebase onto it,
  resolve without dropping intended changes, run the relevant tests, publish
  only the delivery branch. It explicitly forbids the agent from merging or
  pushing the integration branch.
- The automatic round adds one further constraint: preserve a one-to-one
  delivery commit history, do not squash, split, drop or combine delivery
  commits.
- Restart recovery is a backstop, not the normal path.
  `AcceptedIntegrationBackstopHostedService` resumes cards in Human Review with
  phase `integrating` from phase, the `integrationpending` marker and pipeline
  facts, consuming the same `TaskIntegrationStatusService` decision as the board
  so a stale Passed step cannot overrule missing branch membership.
  `AcceptedIntegrationBackstopPolicy` supplies the pure candidate, alert and
  sweep-summary decisions.
- The acute alert fires at 30 minutes for accepted, non-archived cards that are
  still not integrated and carry a record.
- Push durability is separate from integration. `IntegrationPushQueue` is in
  memory; `IntegrationPushBackstopHostedService` re-drives any passed merge
  whose push step is non-terminal after a restart.

## Integration record contract

`TaskIntegrationRecord` (`backend/Shared/Models/TaskIntegrationRecord.cs`) is
append-only, application-owned bookkeeping. It exists so historical cards whose
acceptance predates the recording contract can carry a durable classification.
It is not the live acceptance signal; live acceptance still reads pipeline,
timeline and Git ancestry facts.

Fields: `id`, `version` (1), `classification`, `recordedAtUtc`,
`acceptedAtUtc`, `integrationBranch`, `commitShas[]`, `fenceRefs[]`,
`evidence`.

| Classification | Meaning | Operator-visible |
|---|---|---|
| `integrated-verified` | Ancestry proves the work is on the integration branch | No |
| `integrated-historical` | Landed under an older recording regime | No |
| `no-code-expected` | The card was never expected to produce code | No |
| `content-on-fence` | Content survives only on a result or salvage ref | Yes |
| `genuinely-missing` | No evidence the work landed anywhere | Yes |

Boundary rules enforced by `TaskIntegrationRecordAppendPolicy` behind
`POST /api/tasks/{jobId}/integration-records`:

- Allowed lanes only: `5-human-review`, escalated, `6-completed`, `7-archive`.
  An in-flight task returns HTTP 409.
- `id` is 3 to 96 characters of lowercase letters, digits and hyphens.
- `classification` must be one of the five values above.
- `evidence` is 8 to 4000 characters and is mandatory.
- At most 100 `commitShas`, each 7 to 40 hexadecimal characters; at most 100
  `fenceRefs`, each at most 512 characters.
- `integrationBranch` defaults to the project setting and must be a valid
  branch name.
- Appending is idempotent by `id`, and a historical verification record
  disables the recovery sweep for that card so bookkeeping can never trigger a
  merge or a lane move.

## Ordering guarantees

- One integration writer per project. The immediate coordinator admits one
  merge at a time, ordered by `review-subject.json.completedAtUtc` with enqueue
  sequence as the stable tie-breaker.
- Replays of the same immutable delivery share the in-flight or recently
  completed integration task, so a runner HTTP timeout cannot append the same
  merge and gate repeatedly.
- Merge, gate and rollback are one atomic step against the shared checkout,
  held inside the runner's merge gate. `MergeIntoDevelopThenMainAsync` holds
  the same gate across both the `develop` merge and the `main` advance, so no
  other integration can interleave between them.
- Once the accepted worker enters merge plus build gate plus possible rollback
  it ignores host cancellation until a terminal result; `/healthz/drain`
  reports `gate-busy` for that window.
- The push SHA is pinned at release time, not in the worker, so a queued push
  cannot publish a tip that no gate approved.
- Parallel run-end integration serializes on a per-project semaphore plus a
  cross-runner integration lease.
- A `develop` advance during the promotion gate is informational. The train
  still publishes its pinned candidate and the newer commit waits for the next
  train.

## Delivered versus open

Delivered and verifiable in code:

- Merge-first canonical integration with mechanical three-way merge and a
  guarded, cardinality-preserving rebase fallback.
- Attribution rollback when a replacement map cannot be persisted.
- Git-derived integration status with the five states `integrated`, `partial`,
  `pending`, `conflict-skipped`, `no-branch`, and typed failure classes.
- One automatic bounded steer round for attribution ambiguity, counted per
  operator review epoch.
- Operator-triggered rebase recovery through
  `POST /api/tasks/{jobId}/integration/rebase`, wired to the badge action in
  the frontend.
- Append-only integration records with a five-class schema and lane-gated
  boundary validation.
- Develop-then-main lineage enforcement and exact-SHA promotion with an atomic
  non-force push.
- Restart backstops for both accepted integration and integration push.

Open, and deliberately not documented as behaviour:

- The deterministic backend rule that would own the first routine
  `conflict-skipped` bounce for every reviewed card. Today that broad loop is
  session-owned and stops when the orchestrator session stops.
- Thinking-level and route selection for a recovery round, and the
  strong-guardian escalation tier for repeated or semantic conflicts.
- [Batch Gate](batch-gate.md) assembly, which would change the publication unit
  and move stale-base reconstruction into a coordinator.

## Source files

- `backend/Features/Git/GitService.cs` (`MergeBranchIntoIntegration`,
  `MergeRemoteDeliveryIntoIntegration`, `MergeRefIntoIntegration`,
  `TryMechanicalMerge`, `TryMechanicalRebase`, `SynchronizeIntegrationBranch`,
  `RebaseOnto`)
- `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`,
  `AcceptedIntegrationFailurePolicy.cs`,
  `ImmediateIntegrationLineagePolicy.cs`,
  `AcceptedIntegrationBackstopPolicy.cs`, `IntegrationAgentRoundService.cs`
- `backend/Features/Tasks/TaskIntegrationStatusService.cs`,
  `TaskIntegrationRecoveryEndpoints.cs`, `TaskTransitionService.cs`,
  `TaskMutationService.cs`
- `backend/Features/Tasks/Acceptance/AcceptanceIntegrationPolicy.cs`,
  `Acceptance/TaskIntegrationRecordEndpoints.cs`
- `backend/Features/Runner/WorktreeTaskLifecycle.cs`,
  `Runner/OperatorReviewRequeueService.cs`
- `backend/Shared/Models/TaskIntegrationRecord.cs`,
  `Shared/Models/TaskProvenance.cs`, `Shared/Models/TimelineEvent.cs`
- `backend/Host/EndpointMapping.cs` (route group `/api/tasks`)
- Tests: `backend.Tests/AcceptedIntegrationFailurePolicyTests.cs`,
  `RemoteDeliveryIntegrationTests.cs`,
  `ImmediateIntegrationLineagePolicyTests.cs`,
  `AcceptanceIntegrationPolicyTests.cs`,
  `AcceptedIntegrationBackstopPolicyTests.cs`,
  `MergeIntoDevelopRunnerTests.cs`, `TaskIntegrationStatusServiceTests.cs`,
  `TaskIntegrationRecordEndpointTests.cs`

## Living knowledge log

- **2026-08-17 (AGT-2671):** Page created by the Dossier curation sweep. The
  invariants and matrices here were re-verified against the backend during
  extraction, so this page describes shipped behaviour even though its source
  dossier `AGT-W37` is still `decision-pending` on the steering questions.
