---
id: platform-architecture-remote-gate
title: "Remote gate and integration target architecture"
status: proposed
category: concept
updatedAt: 2026-09-18
last-updated: 2026-09-18
reason: "AGT-2881 extends the proposed remote gate boundary through integration and publication"
taskKey: AGT-2881
tags: [remote-gate, integration, claim, lease, fencing, delivery]
related-tasks: [AGT-2369, AGT-2262, AGT-2736, AGT-2737, AGT-2839, AGT-2854, AGT-2872, AGT-2875]
---

# Remote gate and integration target architecture

> Staged extracted-page update, inside the only authorised Dossier directory.
> Publication destination: `docs/concepts/platform-architecture/remote-gate.md`.
> The destination is unchanged by this task. Links here resolve from this staged
> location; adjust relative links when publishing. Keep the canonical URL and
> document ID stable. Source: [AGT-W18 Dossier](index.html), extended by AGT-2881.

## Decision status

The operator's direction on AGT-2839, 18 September 2026, is the only decision
adopted by this extension:

> Das Zielbild sollte sein, dass der Remote-Host auch merged. Der lokale Studio soll nur schauen.

The remote host also merges; the local Studio only watches. D1-D6 remain open.
D7-D14 in the Dossier propose authority, attempt shape, verification identity,
Windows coverage, recovery, observer behavior, migration and transitional work.
No human sight review or technical implementation approval is claimed.

The Dossier metadata records consolidation into AGT-W59 on 14 September. This
extension retains that history and provides a new integration proposal; it does
not silently reverse the consolidation or update the Gates theme Dossier.

## Current state as of 18 September 2026

Checked-out HEAD and local develop both equal
`704ed89b87d03e9a85e22019fc12b9b5da196e68`. This is repository evidence, not
an assertion that the Windows installation runs that exact binary.

- Integration is backend-owned in
  [MergeIntoDevelopRunner.cs](../../../backend/Features/Pipeline/MergeIntoDevelopRunner.cs),
  `RunAsync`, `MergeIntoIntegrationGatedAsync`, `PushIntegrationBranchAsync`.
  [GitService.cs](../../../backend/Features/Git/GitService.cs),
  `MergeBranchIntoIntegration`, `MergeRemoteDeliveryIntoIntegration`,
  `TryMechanicalMerge` and `TryMechanicalRebase`, supplies the Git operations.
- [RemoteDeliveryIntegration.cs](../../../backend/Features/Pipeline/RemoteDeliveryIntegration.cs),
  `RemoteDeliveryIntegrationCoordinator.EnqueueAsync`, serialises deliveries
  per project. [BuildTestGateRunner.cs](../../../backend/Features/Pipeline/BuildTestGateRunner.cs),
  `ProcessGate` and `ReviewWorkspaceRoot`, serialises local gate execution and
  roots disposable exact-subject workspaces at
  `Path.GetTempPath()/agentstudio-review-gates` (`%TEMP%` on Windows).
- [PreDevelopBuildGate.cs](../../../backend/Features/Pipeline/PreDevelopBuildGate.cs),
  `ResolveTestLevel`, `LevelFor`, `RunAsync`, uses AGT-2854 work-package
  verification for code diffs, build-only otherwise, and compile-only when
  AGT-2839 reuse is granted. The local gate is not universally a full suite.
- [IntegrationGateReusePolicy.cs](../../../backend/Features/Pipeline/IntegrationGateReusePolicy.cs),
  `Decide`, requires passed review/build-test evidence, unchanged integration
  tip, delivery inclusion, no replay/conflict resolution and matching tested
  tree. It still requires local compilation.
- [RemoteReviewWorkspace.cs](../../../runner/RemoteReviewWorkspace.cs),
  `PrepareAsync`, `MaterializeGitAsync`, `ExecutePlanAsync`, materialises
  `ExpectedResultSha` and executes the declared plan while recording integration
  tip and tree evidence. It does not universally build the final merge candidate.
- `MergeIntoIntegrationGatedAsync` resets a fresh local merge to its recorded
  pre-tip on red and releases no push. AlreadyMerged recovery leaves existing
  history intact. Reset failure requires repair. A remote published ref cannot
  safely inherit this local-reset rollback mechanism.
- [IntegrationLeaseService.cs](../../../backend/Features/Runner/IntegrationLeaseService.cs),
  `TryAcquire` and `_slots`, uses in-memory authority. The proposed durable
  project publication lease must not be inferred from that implementation.
- [IntegrationAgentRoundService.cs](../../../backend/Features/Pipeline/IntegrationAgentRoundService.cs),
  `RemoteIntegrationContinuationPolicy.Decide` and `MaxAutomaticAgentRounds`,
  provides one automatic attribution-ambiguity recovery round per operator
  epoch. It does not establish an automatic loop for every merge conflict.
- [promote-develop-to-main.sh](../../../scripts/release/promote-develop-to-main.sh),
  script entrypoint, pins and gates its candidate, then atomically publishes
  main and a release marker without force. Linux placement is operator evidence.

The old SSH description is historical. Searching backend, runner,
orchestrator-engine and task-server at this revision finds no `RemoteSshHost`,
`ResolveRemoteGateSshHost` or `RemoteGateActivityStore`. The Dossier's earlier
AGT-2262 deletion inventory must be reconciled with what remains, not repeated
as a claim of current code. The task context says AGT-2262 is parked; no live
queue inspection was performed.

The operator's 17-18 September log reports approximately 40 minutes per local
Windows gate, eight queued reviewed cards, spare Linux capacity, budget-only
rollbacks of AGT-2826/2839/2869 by 0.01-1.2%, Windows-only test failures,
restart interruptions, a 17-second task-list response, and frequent moved-base
conflicts after 45-90 minute reviews. These are supplied installation
observations, not independently reproduced measurements. The Dossier retains
the qualifications, including cancellation shielding in the accepted worker
and the difference between hard process loss and graceful drain.

## Target object model

Retain the D1 recommendation for deterministic verification:

- **GateSubject:** immutable repository, expected SHA, declared result ref or
  digest-pinned bundle, plan/policy hash, pipeline version and selection audit.
- **GateAttempt:** attempt number, state, executor, outcome, typed failure and
  timestamps, with at most one live fenced attempt for a subject/policy.
- **GateLease:** attempt, owner/host/instance, lease ID, fence, attempt epoch
  and resource namespace.
- **GatePlan:** catalogued commands, capabilities, deadlines and bounded output.
- **GateReport:** expected/tested SHA and tree, dirty proof, complete command
  evidence, environment identity and outcome. Conflicting duplicates fail.

Add the D8 recommendation:

- **IntegrationSubject:** project/repository, canonical ref, pre-tip, result
  ref/SHA, delivery/run generation, review identity, candidate SHA/tree and
  ordered parents, policy/environment digest, optional batch membership digest.
- **IntegrationAttempt:** durable candidate construction and verification
  references, publication intent, receipt, failure/recovery state and cleanup.
- **Project ref-mutation lease:** durable publication authority shared by
  direct integration, Batch Gate, operator merges, release commits and promotion.

This is a bounded claim kind alongside RunAttempt, ReviewAttempt and proposed
GateAttempt, not a general workflow engine. Widening GateAttempt to merge and
push remains the alternative in D8, not the recommendation.

## Authority and Batch Gate

Task Server owns claims, generations, fences, attempt epochs and idempotency.
The remote integration executor is the sole canonical publisher per project
while holding the project ref-mutation lease. Studio and coding/review/gate
workers have no canonical publication credentials.

[Batch Gate](../../concepts/platform-architecture/batch-gate.md) keeps its
coordinator lease for manifest and candidate construction. Acquire coordinator
or integration-attempt authority before the project publication lease; release
in reverse order. Acquire publication authority only for final validation and
publication/reconciliation. Never hold it while waiting for long verification.
A moved base invalidates publication eligibility and triggers remote rebuilding.
Batch Gate can follow initial cutover, but it cannot introduce a second writer.

Reuse the [fence/lease/epoch vocabulary](../../concepts/platform-architecture/fencing-leases-and-authority.md).
A write matches its own attempt epoch; global rotation soft-drains rather than
invalidating live older epochs. A takeover changes the durable fence. Every
operation binds attempt, owner, lease, fence, epoch and idempotency key.

Git must enforce this authority at publication, not merely when accepting a
report. Use protected refs and a fenced receive boundary or exclusive
publication gateway. Expected-old-to-exact-new non-force ref updates and
current-grant validation must meet at that boundary. An HTTP fence check
followed by unrestricted host push leaves a stale-writer race and is not enough.

## State and publication recovery

Proposed state flow:

```text
queued -> claimed -> materialising -> candidate-ready
       -> verifying / receipt reuse -> ready-to-publish
       -> publishing -> reconciling -> integrated
```

Before publication, terminal outcomes include conflict, product-failed,
infra-failed, timed-out, cancelled and superseded. Infrastructure retry has a
bounded budget, a higher fence and positive containment. Unknown publication
remains reconciliation-required and blocks another writer. Cleanup is a
separate durable obligation and cannot undo a proven published result.

Persist expected old ref, exact candidate and target-ref set, receipt digests,
subject identity and operation key before push. Journal delivery durably. On a
lost acknowledgement, inspect authoritative remote refs against this intent:
exact candidate proves publication; a later approved descendant needs ancestry
and publication-chain proof. Atomic main/tag intents require both refs and the
tag object. Expected old still present allows an authorised retry of the same
intent. Divergence or partial publication stops for an authority incident.
Identical reports replay the stored result; conflicting digests are rejected.
Do not blindly merge twice or force-reset the remote ref.

A red gate before publication only abandons a disposable candidate. A defect
found after publication needs a new reviewed revert/fix, not local-reset
semantics. Stop during a push waits for reconciliation rather than promising
that publication did not occur.

## Review and gate materialisation

Recommend constructing the final immutable merge object once on the host and
letting Review verify that object. Publish that exact candidate, without a new
untested merge commit after verification. Existing delivery-only review requires
a contract extension; co-location alone does not provide candidate identity.

Reuse requires repository/ref and pre-tip equality, current generation and
review identity, result inclusion and attribution, exact candidate and ordered
parents, tested tree, clean workspace proof, complete mandatory commands and
matching policy/toolchain/environment. Linux proof never satisfies a required
Windows gate. Partial/waived or baseline-adjusted evidence is not a complete pass.
If only tree equivalence is available, re-verify remotely, including
commit-sensitive build inputs. If develop moved, build a new candidate and
re-verify on the host. Semantic conflict resolution or changed delivery requires
new review; mechanical reconstruction follows the approved review policy.

## Windows and recovery

D10 recommends a dedicated Windows gate host with `os:windows` and
`executor:gate`, claiming required platform steps on the same candidate. It
costs a maintained host/VM, licensing where applicable, reserved slots, caches
and latency. Unknown impact selects conservative coverage. This keeps
path/case, drive/temp, locking, shell/process and installer defects off develop
when the tests exercise them.

Scheduled Windows verification instead raises a branch defect after the next
schedule plus execution delay; promotion-only Windows verification delays
finding the same classes until release and may block a wave. Both are explicit
risk choices, not equivalent per-card coverage. The workstation is never the
fallback Windows host in the target.

Mechanical merge and guarded rebase run in the integration executor. Semantic
conflict recovery is a Task Server/Engine-scheduled RunAttempt, with platform
Git ownership and a new review for changed content. Recommend one automatic
round per operator epoch; repeated failure escalates. AGT-2875 reporting should
include conflicting paths, base/result/candidate, operation, host, attempt/fence,
typed reason and recovery count. Remote proximity shortens transfer/queue time,
not the underlying review window or the need to revalidate develop.

## Observer and cutover

Studio reads attempts, evidence and canonical-ref projections. Accept, continue,
stop and release are authenticated idempotent requests to Task Server; they
never bypass a gate. No checkout, lease heartbeat, publication credential,
retry or recovery depends on Studio remaining on.

[The sleep/tunnel incident](../runner-link/index.html) and
[remote Task Server deployment](../remote-task-server-local-studio.md) motivate
AGT-2737. Hosting the executor remotely while authority still sleeps on the
workstation does not satisfy this target. Phase B's 6 September Docker direction
is dependency context. AGT-2736's
[connector deployment choice](../setup/docker-compose-connector-gap.md) remains
separate; protocol/shadow work can proceed, but observer route readiness must be
proved before full workstation-off acceptance.

Resolve D1-D14, implement durable authority and executor, then shadow one project
for a proposed N=20 consecutive comparable cards. Local integration remains the
only canonical writer; the host can publish only attempt-specific shadow refs.
Compare pinned pre-tip/result/policy, tree, ordered parents, attribution, commands
and outcomes. Record commit-metadata SHA differences; incomparable moved-base
pairs do not count. Stop on every unexplained discrepancy.

Exercise pass/red/Windows red, no host, base movement, conflicts/recovery, lease
expiry and stale writers, restarts, lost push report, duplicate reports, cleanup
failure and cancellation. Measure queue/runtime and end-to-end latency; propose
no p95 regression against matched local runs. N=20 is not tail-reliability proof.
Drain local writers/pushes, reconcile intent state, revoke their grants and
transfer authority before enabling remote canonical publication. Prove a
12-hour Studio-off window and stale-writer rejection. N and duration remain open.

Fallback after cutover is another remote host or pause. Temporary local rollback
requires explicit drained authority/credential transfer and suspends the target;
never dual writers or remote history reset. After acceptance, remove the local
integration BuildTestGateRunner path, `%TEMP%` gate lifecycle, workstation budgets
and local merge/push ownership. Keep portable planners/receipts. Audit AGT-2262
against absent SSH symbols before residual teardown.

AGT-2839, AGT-2872, AGT-2824 and AGT-2854 are transitional placements. Keep or
finish bounded correctness/availability work needed before cutover, retain
portable identity/classification/selection rules, and stop local-only expansion.
AGT-2824's precise scope is not supplied and must be read before applying that
disposition. No cards are created, cancelled or released by this concept.

## Living knowledge log

- **2026-08-18 (AGT-2671):** GateSubject/GateAttempt/GateLease model extracted
  from the gate-only Dossier. D1-D6 open, not approved or implemented.
- **2026-09-14 (AGT-2801):** Source descriptor records consolidation into AGT-W59;
  retained as historical metadata, not overwritten with a human approval.
- **2026-09-18 (AGT-2881):** Operator direction extends ownership through merge
  and push. D7-D14 proposed. Develop inspection corrects historical SSH claims,
  delivery-only versus merged-candidate materialisation, compile-only local
  reuse and restart absolutes. Full integration authority, durable publication
  reconciliation, Windows coverage, conflict recovery and rollout are specified
  in the extended Dossier. This page is staged within that Dossier because the
  task permits repository edits in only one directory; canonical publication
  and AGT-W59/navigation reconciliation remain a separate scoped change.

## Proposed implementation cards

No cards created. Approval-dependent prompts are in
[workbench.json](workbench.json).

1. **Persist integration authority and fenced publication:** durable attempts,
   project lease and enforced canonical-ref publication.
2. **Execute remote integration candidates and reconcile publication:** host
   materialisation, verification, push intent and crash recovery.
3. **Share exact candidate verification between Review and Integration:**
   identity-bound receipts and conservative remote re-verification.
4. **Provide a Windows gate executor and coverage policy:** independent Windows
   capacity with candidate-specific platform evidence.
5. **Move integration recovery and operator requests to the remote authority:**
   bounded recovery and checkout-free Studio projections/requests.
6. **Unify promotion and Batch Gate publication authority:** one ref-mutation
   contract for every enabled publisher.
7. **Run remote integration shadow and cutover acceptance:** comparable-card and
   failure evidence, authority transfer and fallback rehearsal.
8. **Retire local integration execution and reconcile architecture pages:**
   remove obsolete paths and publish the staged knowledge update with stable links.
