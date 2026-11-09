# Batch gate delivery state

## One-step speculative stacking (AGT-3546)

The implemented choice is AGT-2994 D1 option C, a speculative train with ordered publication,
adapted to the existing batch gate as one successor (`stacking: "1"`). The default
is `"off"`. A formed batch N+1 may use a spare gate slot while N is gating.
Its immutable manifest names N and uses N's assembled candidate SHA as its
base; replaying N+1's members produces candidate N+1 = candidate N plus those
members. Each batch keeps its own candidate ref, membership digest, exact SHA
gate receipt and coordinator fence. Only one stacked successor may run per
project, and it uses no slot when the host reports every gate slot busy. While
that successor runs, later ordinary batches stay formed behind it. Once it
publishes or retires, they start against the real integration tip.

The successor does not reserve the project during its gate. Before publication
it takes the project reservation and ref mutation lease, checks that N's
publication receipt verifies exactly the SHA used as its base, checks its own
green exact-SHA receipt and current members, then requires the remote tip to
equal that base. The push is a compare-and-swap of that tip to the tested
candidate. This gives strict N then N+1 publication; an unrelated external
push makes the base stale and sends the members back to planning on the real
tip. A confirmed red or abandoned predecessor cancels its successor. A
same-candidate infrastructure retry or targeted flaky rerun leaves the chain
in place; a terminal red or pause retires it. Supersession of a member retires
that successor and excludes the old delivery from its next formation.

After an API restart, an interrupted gate has no inherited verdict. The
coordinator lease is allowed to expire, the interrupted manifest is abandoned,
and its members are re-formed under the same fresh-tip rules as AGT-3466.
An orphaned formed successor is also abandoned if its predecessor did not
publish. The state route reports `stacking.started`, `published`, `cancelled`,
and `gateMinutesWasted`. Wasted minutes sum measured host execution durations
for cancelled stacked batches; if the cancelled gate has no completion fact,
the run start to cancellation interval is the estimate. These are spent host
minutes, not a projected delay saved.

To pilot, set the project's batch gate `stacking` field to `"1"` (Project
settings, Batch verification, Stacked batches: One successor, or the batch gate
settings API) and keep at least two gate slots. During the first
hours, watch those four counters, the batch predecessor links, slot occupancy,
stale-base cancellations, and published cards per hour. Set `stacking` back
to `"off"` to stop forming speculative successors; already formed or running
successors retain their fenced publication checks.

Decision D1 is option A: keep model review per card, run preparation, build,
lint, and affected tests per code card, then run the complete suite once on
the combined candidate before publication. This follows the operator direction
of 2026-10-11. The project setting defaults off and to documentation scope.

## Delivered

- A reviewed, batch-deferred delivery gets an opportunistic pre-check on its
  result SHA: project build commands, `.NET` guards, and touched frontend lint
  and format scripts. Regular batches retain the first gate slot; the pre-check
  uses only spare slots. A green verdict admits the card; a red verdict returns
  it to Ready with failed tests and route lines in the agent follow-up. An
  unchecked card may enter a batch at its deadline. `preCheckEnabled` is a
  per-project switch, on by default for Agent Studio. State and report expose
  pre-check run count, red count, and estimated gate minutes saved. The estimate
  uses recent batch gate durations and expected base plus bisection runs.

- Pending continuation intents and cards already queued in Progress exclude the
  old delivery from batch planning. A review is eligible only while its subject
  SHA matches the current run's immutable delivery. The same checks apply to
  waiting cards and upcoming reviews before delivery diff reads.
  Before a formed batch starts, an affected member retires the immutable
  manifest; its audit state records each removed task and reason. Remaining
  members return to planning, and a batch below the minimum size dissolves.
  A newly passed review restores eligibility. Running batches retain the existing
  supersession, StaleBase, and abandon behavior.
- Code scope and look-ahead settings are validated per project. Queued review
  attempts are replanned against their immutable result refs in the background
  when settings change. Claims may proceed while selection is computed.
- The real completion path gives the review planner the run envelope's SHA and
  immutable ref before the subject file exists. Full-suite selections stay on
  the per-card route.
- The look-ahead planner sizes a batch within configured bounds from risk and
  expected suite runs, waits for compatible near-term arrivals, and honors
  deadline, urgent tags, operator close-now, and overlap policy. A separate
  overlap policy excludes cards touching a chosen member's files. Decisions
  are persisted for the state API. Pending reviews appear in the look-ahead.
- AGT-3407: a lone card takes its own gate before its deadline only on an idle
  gate host. The host counts as idle when the load gate is clear, no batch
  and no per-task gate runs or waits for a slot, and a gate slot is free
  (`BatchGateLookAheadPlanner.GateHostIdle`). Behind busy slots the card
  waits for a batch partner until its deadline, and the plan reads
  `waiting for target size or deadline`. After the deadline it takes its own
  gate, unless a gate backlog holds it for a due partner. The age-out after
  two deadlines still applies. Cards past their deadline that are waiting
  together close as one batch when the overlap policy allows it. Each plan
  records the waiting cards it `considered`. In Engine mode the tick refuses
  a per-task intent when two or more cards are eligible and one of them is
  missing from that list. The receipt reads `rejected` with `per-task
  decision did not consider every eligible card`, and the tick plans again
  on current facts. Before AGT-3407 only a running batch kept the host busy.
  On 1 November 2026 six cards each took a per-task gate of about 50 minutes
  behind three busy slots.
- AGT-3466: on 3 November 2026 a restart dissolved formed batch 944a1c38 and
  13 gates ran on a host with five slots. Three rules now prevent this:
  - A formed batch whose base moved before replay closes again on the new
    base. The abandoned state reads `base moved before replay; re-formed as
    <id>`. Before AGT-3466 the members went back to planning with their old
    review times, already past every deadline, and the cards the planner did
    not batch at once took per-task gates. AGT-3498 adds per-member checks so
    one ineligible member does not dissolve the compatible group.
  - A card waiting for its pre-check counts as a batch partner. Pre-checks
    run only on spare slots. While per-task gates filled the slots, each
    unchecked card became eligible alone at its deadline and took its own
    gate, and the plan read `idle` with 14 cards pending. Now a lone card past
    its deadline waits while enough queued cards wait for a pre-check to
    reach the minimum batch size. Both planners apply this rule, and the plan
    reads `queued cards wait for their batch pre-check`. The hold ends no
    later than the partners' own deadlines. An operator close-now, an urgent
    tag, or a high-risk card still takes its own gate.
  - At most one slot fewer than the gate host has (at least one) runs a
    per-task fallback gate at once. Other gates wait for a slot before
    choosing their merged candidate. The batch keeps its slot, the rule the
    pre-check already follows (`BatchGateLookAheadPlanner.PerTaskGateLimit`).
  The state route names why each `fallback` member left the batch route:
  `fallbackRule` (`planner-per-task`, `batch-paused`, `batch-ejected`,
  `unattributable-replay`, `operator-eject`, `batch-disabled`,
  `documentation-scope`, `already-integrated`, `emergency-admission`,
  `interrupted-gate`) and `fallbackEvent`, also mirrored in `reason` and
  `reasonDetail`. The first cause of a review generation is kept until the
  review returns to the batch queue. A member of an abandoned batch that is
  queued again shows `pending` with reason `requeued`, not `fallback`.
- AGT-3498: when a formed batch's base moves before replay, the worker checks
  each member's current review and immutable delivery against the new base.
  Compatible members close together in one successor linked by
  `reformedFromBatchId`; they run a new exact-candidate gate with no inherited
  run or verdict. A conflicting member starts its merge-conflict round, and a
  superseded review resolves from the queue. The retired batch state records
  typed `baseMoveMemberReasons` and the successor ID. The state response
  reports `baseMoves` by new base SHA and `baseMoveTotals`, each counting formed
  batches kept and abandoned. A replay probe infrastructure failure pauses
  the batch rather than classifying a member as conflicting.
- AGT-3562: one flight per project starts, so the start order decides who
  waits (`BatchGateStartPolicy`). A stacked successor starts first. Within
  its maximum wait (`maximumWaitMinutes`, default 45, the planning deadline)
  the larger formed batch starts first, then the older one. A batch past its
  maximum wait starts before younger ones, oldest first, regardless of size.
  On 5 November 2026 batches 24991efe (12:59 UTC) and baddb7f9 (15:11 UTC)
  stayed formed for hours while later, larger batches ran.
- AGT-3562: a flight can wait on the project reservation before it acquires
  its coordinator lease, so lease expiry cannot free its project slot. A
  flight that has not claimed its lease within `flightLeaseDeadlineMinutes`
  (default 40, above the 27-minute integration gate lease cap) is cancelled
  and loses the slot even if it ignores the cancellation. The batch stays
  formed with the phase reason `flight cancelled: no coordinator lease within
  <n> minutes`, each member's timeline records
  `batch-flight-lease-deadline`, and the next formed batch starts first. The
  cancelled batch is retried after it. On 2 November 2026 only an API
  restart freed such a slot. Until the detached flight exits, the batch gate
  state lists it under `detachedFlights` (batch, start, detach time, deadline,
  reason), and a flight wait does not wait for it.
- AGT-3562: a flight that completes or fails (its batch ran a gate, or left
  the formed phase other than by retirement before replay) starts the next
  eligible formed batch of its project at once, instead of on the next hosted
  tick. A publication moves the base of that next batch, so when a flight
  started this way re-forms its batch on the moved base, the successor also
  starts at once. A flight that ends with its batch still formed, for example
  without a lease, or that a tick started and that retired before replay,
  leaves the start to the next tick, so a refused start cannot loop. A
  re-formed batch keeps the wait of the batch it replaced.
- Each card's paths and changed lines come from its result SHA, measured
  from the merge base with `refs/remotes/origin/<branch>`, the ref the
  planning tick fetches. A card whose diff cannot be computed scores at the
  balanced limit and uses the per-task gate under `balanced` and
  `conservative`.
- The planner's risk score (`BatchGateRiskModel`) combines changed-path
  count, merge-base changed lines, the number of infrastructure paths, and
  the prior-red, flaky, and red-area history flags. It is a relative scale
  tuned to the measured deliveries of 2026-10-12, not yet a calibrated red
  probability: D10's historical per-card green rate after review is 57.9%,
  and the batch report's observed green rate replaces it once enough batches
  have run.
- The bounded advisor can approve, adjust, or veto membership. It cannot change
  tests, and errors or timeouts use the deterministic plan.
- Batch runs request the full test level and use the configured timeout, default
  150 minutes. The gate runner performs its one targeted failed-test rerun.
  A red candidate is compared on the server with a durable full-suite base
  verdict keyed by base SHA and gate profile. A matching red base is reported
  as pre-existing and pauses as `integration-branch-red`. It cannot publish or
  validate the build profile because the suite stopped at the first red step.
  Members stay pending until a new base SHA triggers reconstruction. The remote
  worker does not spend the batch budget on a
  clean-repeat diagnosis.
  The report counts batches currently held for a red integration branch.
  A collided base red or one measured with at least 25% average external CPU
  stays provisional. The service runs the base again before holding a batch;
  a still provisional verdict is retried on the next red. The state route shows
  the base verdict, its provisional flag, and each batch's `phaseReason`.
  Different parsed failing tests on base and candidate send the candidate to
  member isolation. A confirmed deterministic red still isolates when the
  same run quarantined a separate flaky test.
- A cancelled per-task fallback can return to the pending queue with its passed
  review. The pilot also re-admits a current passed review moved back into Auto
  Review when its pending record was resolved. Fallback requests use at most
  one fewer concurrent slot than the gate host has configured, preserving a
  slot for a formed batch.
- Full-suite execution runs outside the 30-second planning tick. One batch runs
  per project. A documentation-only base movement queues a successor manifest
  for replay and another full gate. Publication pushes the tested SHA to origin
  and verifies the remote SHA. Member release follows that verified remote
  publication; local checkout sync is best effort. Ejected members start their
  per-card gates after the batch releases its project reservation. A rebased
  member records its exact source-to-published-SHA mapping before lane release.
  A deferred `IntegrationBranchDefect` review releases on verified batch evidence.
  Recovery failures are logged per manifest, allowing later batches to proceed.
  A queued code push keeps its project batch reservation pending until origin
  verifies the merged SHA. The pending marker is a durable recovery job, so a
  startup sweep re-drives it even if the card remains in Auto Review and the
  project's automatic artifact-push strategy is `never`.
  Before waiting and on each code-enabled planning tick, the batch gate clears markers
  whose approved SHA is already on origin, whose merge was superseded,
  rolled back, or left the integration path, or whose push ended with a
  permanent branch divergence, or a completed local-only push. A failed push is
  recorded on the card and releases its reservation. State exposes each push
  wait and the oldest wait as the plan reason. The age alert starts after three
  minutes by default;
  `BatchGate:PendingPushAlertMinutes` configures that limit.
  Persisted markers are loaded at startup. Reconciliation shares one integration-tip
  fetch per repository and branch in each pass, and a disabled or documentation-only
  batch gate does not reconcile code push markers.
  Disabling the integration push step does not create a reservation that no
  worker can clear.
- Red-batch isolation keeps durable findings for each delivery and tested base.
  A green child can publish without waiting for sibling probes. If a sibling's
  delivery changes, only that member leaves isolation; its remaining siblings
  continue. A red singleton, or the sole member of a red child not already
  proven green, is sent back to its agent with the failing gate evidence and
  stays out of planning until its delivery changes. Green members are admitted
  first on the same base. Restart reconstruction uses these findings and the
  closed child verdicts. The state route and report expose the findings as
  `proven-red`, `proven-green`, and `superseded`.
- Isolation in the UI (AGT-3577). Each finding names the child batch run that
  decided it (`provingBatchId`, `provingRunId`). On the state route a
  proven-red finding also carries that run's `failures` (test name and error
  excerpt, from the cached failed-test projection) and `evidenceAvailable`.
  `GET /api/projects/{project}/batch-gate/isolation-evidence?parentBatchId=&taskKey=`
  (project read) returns the evidence file of the member's newest finding, and
  only a file inside the batch store. The batch panel lists each member of a
  red batch as proven red, proven green, superseded or not yet isolated; the
  proven-red member shows its failing tests, excerpts and evidence link. The
  culprit card's Batch verification section says "Proven red in `<batch>`".
  The lane indicator shows `Batch <id> isolating` while a child of a red batch
  is active, then `Batch <id> red` with the proven-red members until a newer
  batch publishes. "Create fix card" is offered for every ranked failed test;
  a non-suspect test opens "Fix failing test" instead of "Fix flaky test".
- While a red batch isolates (latest phase `Red`), continue, steer and requeue
  of a member that the isolation still holds are refused with
  `409 batch-isolation-active` naming the flight (AGT-3483). Eject stays the
  member action. The guard reads the existing ownership, pending record,
  batch state and isolation findings and does not change isolation itself; see
  [tasks](../../system/domains/tasks.md).
- `GET /api/projects/{project}/batch-gate/state` exposes plan, pending, batches,
  and member state. The existing report includes observed batch green rate.
- `GET /api/operator/batch-triage` (OP-4, AGT-3462) reads the same state for
  every project the caller may read. It lists open flights, open members with
  red and isolating markers, pending pushes with age and the
  `BatchGate:PendingPushAlertMinutes` threshold (`overdue`), queue stalls, and
  the next safe member operation (`eject`, `wait` or `close-now`). The rules
  are in the [operator API reference](../operator-api.md#batch-triage).
- AGT-3055 (D16 option A): the batch suite is claimable gate work by default.
  The backend pushes the candidate to its fenced candidate ref and creates a
  batch `GateSubject` that binds the membership digest. A runner host with the
  gate role claims the attempt through `/api/v1/gates` under lease and fence.
  A stale lease or fence cannot advance or report the attempt. A lapsed lease
  requeues the attempt on another host after containment. The subject lives
  where runner gate claims land: the standalone Task Server when
  `TaskServer:BaseUrl` proxies the versioned plane, otherwise the monolith gate
  store. In the networked profile, a runner credential reaches the monolith gate
  routes with `runner.claim` (claim, subject read), `runner.lease` (renew),
  `runner.events` (phase), and `runner.completion` (report, containment).
  `BatchGate:Execution` selects the path: `bridge` (default when unset), the
  SSH bridge build/test gate runner, or `claimed`, the opt-in claimable gate
  work. Any other value stops the backend at startup.
- AGT-3055 (D14 option A, D15 option A): the batch decision runs one shared
  library (`BatchPlanDecisionPolicy`) in the monolith and in the Orchestrator
  Engine. `MonolithTransitionSwitch` selects the owner for the `BatchPlan`
  transition; Monolith stays the default and Engine or Shadow ownership is
  opt-in. With `TaskServer:BaseUrl` set, `/api/v1/orchestration` is proxied to
  the Task Server, where the engine claims occurrences, so the backend refuses
  Engine and Shadow ownership of `BatchPlan`: it logs
  `batch-plan-engine-ownership-refused` once and keeps the decision in the
  monolith, and the rollout switch answers `409`
  `orchestration-profile-batch-plan-task-server-proxy`. The batch tick runs in
  every execution mode. In Engine mode it
  publishes a `BatchPlan` occurrence and applies the returned intent only after
  the authority floors. In Shadow mode it decides itself and records the engine
  comparison. The occurrence carries the advisor CLI type and the model that
  the authority resolved, including the Codex economy pick. The engine calls
  exactly that model with its own provider credential:
  `ADVISOR_ANTHROPIC_CREDENTIAL` for `claude` and `ADVISOR_OPENAI_CREDENTIAL`
  for `codex`, each with a `_FILE` variant. Without that credential, the engine
  records an advisor fallback with the requested model and keeps the
  deterministic plan. It never substitutes another model.
- The networked profile never starts the advisor CLI on the server
  (AGT-3411). With Engine ownership of `BatchPlan` the engine carries the
  advisor as above. Otherwise the configured advisor does not run: the batch
  tick decides deterministically, logs `batch-plan-advisor-off` once, and
  `GET /api/projects/{name}/batch-gate` reports `advisorEffective` with
  `effectiveMode: off`, `carrier: none`, and the reason, which Batch settings
  shows beside the advisor fields.

## Open

- The claimed suite needs at least one registered gate host with the plan's
  capabilities. Without one, the attempt expires at its dispatch deadline as
  gate infrastructure. Keep `BatchGate:Execution` unset (`bridge`) until a
  host is enrolled.
- The D10 historical proxy is not a real combined-tree pass rate. Compare the
  state and report with actual batches before increasing the size bounds.
- The first red candidate on a new base SHA can require one separate full
  base run. Its cached verdict is reused for later comparisons with the same
  gate profile.
- In monolith mode, the advisor uses the registered one-shot CLI on the backend
  host. Its quality
  and quota behavior need live pilot evidence before using `enforce` mode.
- The command-inventory change guard lists the built-in Agent Studio project,
  .NET, and Angular inventory files. Custom build profiles need declared
  inventory paths before the same guard can cover their commands.

## Rollout and rollback

Enable one project with `scope: code`, `planner: look-ahead`,
`minimumSize: 2`, `maximumSize: 6`, `deadlineMinutes: 20`,
`lookAheadMinutes: 45`, `riskPolicy: balanced`, `overlap: adjacent`,
`reviewTestLevel: work-package`, `gateTimeoutMinutes: 150`, and
`advisor.mode: advise`. `maximumWaitMinutes: 45` and
`flightLeaseDeadlineMinutes: 40` are the defaults. Roll back with `scope: documentation` or
`enabled: false`; pending members use the per-task gate, which tests their
immutable result SHA merged onto the current integration tip (AGT-3434). The setting does not authorize a project-wide rollout.
