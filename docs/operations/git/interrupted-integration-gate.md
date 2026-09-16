# Interrupted Integration Gate

## Headline

**A build gate that never reached a verdict is an interrupted run, and the next
process repairs the integration branch before anything else is merged on top.**
The pre-develop gate publishes before it verifies, so a process that dies inside
that window leaves a merge nobody judged. Startup recovery either rolls the
integration branch back to the exact pre-merge tip or resumes the merge whose
verdict is already durable, and the affected cards are integrated again without
a new remote review.

## Why

On 16.09.2026 three Task Server restarts (13:07, 13:17, 13:47) each hit a running
pre-develop build gate. The gate process died with the backend, no rollback ran,
and the merge commit stayed on the integration branch. The next integrations were
merged on top of the un-gated ones, so the Studio-owned integration worktree sat
at `origin/develop` plus three merges of which two had never been gated. A later
gate would have tested, and on success published, all of them together; a failure
would have rolled back only to the previous un-gated tip.

Two separate mistakes made that invisible:

1. **Nothing recorded that a gate was in flight.** Ancestry on the integration
   branch proves that a delivery is present. It cannot tell a verified merge from
   an un-gated one, and after the process is gone there is nothing else to ask.
2. **The card claimed integration for a merge that only one machine could see.**
   Two of the three cards showed `integration.status=integrated` although neither
   commit was on `origin/develop`.

## The in-flight record

[backend/Features/Pipeline/IntegrationGateJournal.cs](../../../backend/Features/Pipeline/IntegrationGateJournal.cs)
writes one small file per card,
`<job folder>/post-steps/integration-gate.inflight.json`, next to the numbered
gate-evidence logs. It answers the mirror question the logs answer: the logs say
which subject reached a verdict, this file says which subject was **promised**
one.

- It is opened **before** the merge, carrying the rollback anchor observed at
  that moment: the local branch tip, or the `origin/<branch>` tip when the merge
  has to recreate the branch.
- It is completed with the merge result as soon as the merge returns.
- It is cleared by every path that reaches a verdict, including the rollback
  path, and by every path that never created a merge.

An entry that is still present when a process starts is therefore, by
construction, a gate that was interrupted.

## The repair

[InterruptedIntegrationGateRecoveryHostedService](../../../backend/Features/Pipeline/InterruptedIntegrationGateRecoveryHostedService.cs)
runs one pass per process start, before the merge gate opens for new work - the
only moment at which an un-gated merge can still be rolled back safely. The
decision itself is pure
([InterruptedIntegrationGatePolicy](../../../backend/Features/Pipeline/InterruptedIntegrationGatePolicy.cs))
and is taken **per integration branch**, not per card: rolling one card's merge
back while a later merge sits on top of it is not a rollback, it is a rewrite of
somebody else's delivery.

| Fact | Action |
|---|---|
| A durable receipt whose expected **and** tested SHA are exactly the merge result, green | Resume: the merge stands, the card is integrated again so the push is released |
| No receipt, or a red one, and the merge is still in the branch graph | Roll the branch back to the **oldest** unverified pre-merge tip, then re-queue every affected card |
| The merge is not in the branch graph any more | The branch was already repaired; re-queue the card only |
| The anchor is not reachable from the branch tip | Escalate: the exact rollback anchor is gone |
| The anchor does not contain `origin/<branch>` | Escalate: resetting would un-publish commits that are already on origin |

The reset runs in the Studio-owned integration worktree
([integration worktree](integration-worktree.md)), never in the developer
checkout, and uses the same `GitService.ResetIntegrationBranch` primitive as the
live gate rollback, so a checkout that was fast-forwarded along is returned with
the branch.

## Re-queueing without a new review

A repaired card gets a durable merge step with verdict and failure code
`gate-interrupted`. That code joins `gate-environment-failure` in the host-fault
family: the reviewed delivery is untouched, so the card stays `pending` (never
`conflict-skipped` or `partial`) with the reason prefixed `gate interrupted: `,
and the bounded ladder in `GateEnvironmentRetryService` replays **only the
integration** for the unchanged delivery SHA after checking that its latest
settled review is still a `Pass`. Accepted cards in `6-completed` / `7-archive`
are re-driven by the accepted-integration backstop instead; `gate-interrupted` is
deliberately not a decided attempt, so that sweep retries rather than returning
the card to an operator.

An escalated branch gets a failed merge step naming the reason and needs manual
repair. Every case appends an `integration_gate_interrupted` timeline event
carrying the action, the policy reason, the gated SHA, and the rollback anchor,
so the card says which of the three things happened to it.

## The publication boundary

`integration.status=integrated` now means **reachable from the pushed remote
integration branch**. A delivery that only the local branch or a worktree ref can
see is `merged-locally`, and the detail names the commits `origin/<branch>` cannot
reach. A repository without an origin mirror of the branch is its own
publication, so its local graph stays authoritative and its verdict does not
degrade.

`merged-locally` is *merged* work, not *integrated* work, and the difference is
load bearing in both directions:

- Acceptance, the acceptance rail, and integration recovery ask
  `IntegrationStatuses.IsMerged`, because the origin push is a separate step
  owned by the integration push backstop and re-running the merge cannot advance
  it. A card is not sent back to Human Review for a push that is still queued.
- `IntegrationStatuses.IsNotIntegrated` is true for it, so the
  `integrationpending` audit marker survives until the push lands, the archive
  warning fires, and the board badge shows the amber
  `merged locally, not pushed` pill instead of the green `merged @sha`.

## Related

- [Integration worktree](integration-worktree.md)
- [Commit / Push doctrine](commit-push-doctrine.md)
- [Pipeline domain map](../../system/domains/pipeline.md)
- [Tasks domain map](../../system/domains/tasks.md)
- [Task integration and merge workflow](../../concepts/task-integration-and-merge-workflow.md)
