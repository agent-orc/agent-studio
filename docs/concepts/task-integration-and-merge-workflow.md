# Task Integration and the Worktree/Merge Workflow

Status: Current implemented behaviour (verified against code on the `main`/dev checkout). Some aspects are under review; see "Known sharp edges".

This page explains how a finished task's work reaches the effective integration branch. A project setting selects an existing local or remote branch; otherwise repository truth from `origin/HEAD` selects the branch. It documents what the code does today, not any proposed redesign.

## The one rule for agents

The run agent does NO git. You do not branch, stage, commit, push, or merge. The platform owns all git operations as pipeline steps (ADR-0052). Just edit files in your working directory; the runner captures, commits, and integrates your changes for you. If your run leaves the shared main checkout dirty in an unexpected way during a parallel run, that is reported as a containment violation, not committed.

Agent Studio dependency changes have one additional repository rule. When a
change affects `backend/OrchestratorApi.csproj`, update and commit
`backend/packages.lock.json` in the same delivery, then prove `dotnet restore
backend/OrchestratorApi.csproj --locked-mode`. Keep the lock generated against
the normal NuGet sources. A package copied through a local scratch feed can
leave the same version with a different content hash in the local cache and
later fail with `NU1403`; clear the contaminated cache entry and regenerate
from the registry instead of accepting the scratch-feed hash.

## Worktree + branch model

- Branch naming: every isolated task runs on `task/<id>` (`WorktreeTaskLifecycle.BranchFor`).
- Whether a `task/<id>` branch and worktree exist at all depends on `MaxParallelism`:
  - `MaxParallelism == 1` (default, sequential): NO worktree, NO `task/<id>` branch. The agent edits the shared main checkout directly, on whatever branch it has checked out.
  - `MaxParallelism >= 2` (parallel): each run gets an isolated git worktree on its own `task/<id>` branch, cut from `IntegrationBranch`. `WorktreeTaskLifecycle.Prepare` / `PrepareOrReuse` create/reuse it.
- File: `backend/Features/Runner/WorktreeTaskLifecycle.cs`.

## The git pipeline steps

Committing, integrating, and merging are catalogue steps, not agent actions. Relevant step ids (`backend/Features/Pipeline/PipelineCatalogue.cs`):

- `post-integrate-merge` ("Integrate merge") - the automatic parallel integration at run end. Not deferred.
- `post-git-commit-attribution` ("Git commit attribution") - pins a task's commit set to its own run windows; runs on the `3-progress -> 4-auto-review` transition.
- `post-merge-into-develop` ("Merge into Develop") - the common fenced-delivery merge step. It remains `Deferred = true` in the catalogue because the ordinary local post-bracket does not run it. A green Remote delivery runs it before Human Review; acceptance runs it as a retry for failed immediate or legacy deliveries.

## Commit and push timing

Driven by `TaskTransitionService.MoveAsync` (`backend/Features/Tasks/TaskTransitionService.cs`), read live from `ProjectSettings` on every transition.

- Auto-commit (sequential path): on `3-progress -> 4-auto-review`, if `AutoCommit == true` and the mode is not read-only, `TryAutoCommitAsync` commits the dirty tree, scoped to this task's run windows, and stamps the SHA on the job folder (lines ~101-137). Read-only modes (planning/research) skip every git side effect.
- Auto-commit (parallel path): the per-transition auto-commit does not apply; instead `ProjectRunner.IntegrateWorktreeRunAsync` commits the agent's edits onto `task/<id>` at run end via `GitService.WorktreeRunCommit` (line ~933).
- Push timing is governed by `AutoPushStrategy`:
  - `never` - commits stay local.
  - `on-completed` - pushed when the task reaches `6-completed` (queued to a background push worker; a periodic backstop covers shutdown drops).
  - `always-immediate` (default) - queued for push as soon as the platform-owned commit exists. Network work stays off the transition/run path.
- A repository with both `develop` and `main` has one integration writer. Legacy stamped-SHA pushes cannot advance `main` with a raw task or delivery commit; they may only no-op when the SHA is already present or advance to the exact tip already published on `develop`. The immediate integration runner merges into `develop`, queues that push first, and then fast-forwards and pushes `main` to the same commit.
- Integration-branch commits are also queued for push after merge. A final push failure is recorded as the typed `managed-repo-push-failed` operator-feed event; verified remote status remains ahead until a retry succeeds.
- Workspace artifact commits use the global `WorkspaceArtifacts:AutoPushEnabled` switch (default `true`) and `WorkspaceArtifacts:PushRetrySeconds` retry base (default `30`). Every successful artifact commit queues an immediate `origin/main` push.

## Unified integrate-before-review policy and acceptance backstop

Local worktree runs and fenced Remote deliveries now share the same policy: a green coding delivery is integrated before Human Review. Local worktree runs integrate during run finalization. A Remote delivery integrates after its immutable Result Envelope and Remote Review report have settled, before the `4-auto-review -> 5-human-review` move. The Remote gate remains fail-closed: the review outcome must be `Pass`, and the build/test aspect must either pass or belong to the explicit not-applicable class. A review plan with no applicable build/test command is also not applicable, not a failed gate.

- Immediate Remote trigger: `V1ReviewPlaneEndpoints` hands the immutable Fence ref to `RemoteDeliveryIntegrationCoordinator`. The endpoint awaits the merge result before moving the card to Human Review. The integration status projection includes Auto Review, so `integration.status == integrated` is observable before the lane move rather than inferred from later acceptance. A report whose authority record is missing, or whose non-replay lease has expired, returns HTTP 409 `LeaseExpired` before this integration path.
- Ordering: the coordinator admits one merge at a time per project. If several eligible Fence refs are waiting, it selects them by `review-subject.json.completedAtUtc`, with enqueue sequence as the stable tie-breaker. Replays of the same immutable delivery share its in-flight or recently completed integration task, so a runner HTTP timeout cannot append the same merge and build gate repeatedly. The short completed-result window is process-local; a restart can still re-drive durable accepted work. `MergeIntoDevelopRunner` supplies the existing mutation boundary and serialization.
- Acceptance retry trigger: accepting a task that is not already integrated starts a transaction inside `5-human-review`. `TaskTransitionService.MoveAsync` resolves the target through `GitService.ResolveIntegrationBranch`, resets the merge step to a fresh pending attempt, sets phase `integrating`, stamps the internal `integrationpending` recovery marker, writes `integration_started` to `timeline.jsonl`, and refreshes `origin/<integration-branch>` before its already-integrated check. The resolver keeps an existing configured local or remote branch, but replaces a missing legacy `develop` candidate with the repository default from `origin/HEAD`. The check consumes the refreshed remote-tracking ancestry, so an immediate or out-of-band integration completes without enqueueing another merge or build gate. Otherwise the request enqueues `AcceptedIntegrationQueue`; it does not wait for merge or build, and the card does not enter Completed while integration is pending.
- Delivery ref: `DeliveryRefResolver` reads card truth in this order: immutable result ref from `review-subject.json`, an attributed `commits[].branch`, `runner/<executor>/<task-key>` from the fenced review subject, then the legacy `task/<slug>` fallback. The merge path does not reconstruct a remote delivery from the folder slug. Remote refs are fetched and fenced to the reviewed result SHA before merge.
- Execution: both the immediate coordinator and `AcceptedIntegrationWorker` call `MergeIntoDevelopRunner.Run` (`backend/Features/Pipeline/MergeIntoDevelopRunner.cs`). Every git mutation of that run happens in the Studio-owned integration worktree, derived from the repository path and reset per integration, never in the registered developer checkout ([operations/git/integration-worktree.md](../operations/git/integration-worktree.md), AGT-2832). Immediately before any merge or release gate, the runner fetches the configured integration branch and fast-forwards a stale local branch. It leaves a local-ahead branch intact and reports a divergent branch without overwriting either tip. Cleanup removes both the temporary directory and its Git worktree registration before the target repository is mutated.

The integration stages run in this fixed order:

| Stage | Strategy | Failure evidence |
|---|---|---|
| Direct merge | `git merge --no-ff` preserves the reviewed delivery objects. | Number and names of conflicted files. |
| Mechanical merge | A three-way `ort`/`rerere` merge retries known mechanical resolutions without rewriting delivery history. | Number and names of files still conflicted. |
| Rebase fallback | An isolated `rebase --rebase-merges` runs with `rerere` and autostash disabled. It is accepted only with an unambiguous one-to-one old/new commit mapping. | Stopped commit and position, such as 2 of 3, plus conflicted files. |

If all three stages fail, the merge step persists one structured conflict report with every stage outcome, the capped file list and total count, the integration tip, and the delivery SHA. The card renders that record in three lines beginning `Merge into develop conflicted (direct merge, mechanical merge and rebase fallback all failed)`. Raw Git hint text is never card copy.

- Commit: `Merged`, `MergedAfterRebase`, `AlreadyMerged`, or a pre-existing Git-derived `integrated` status completes the acceptance transaction. `MergedAfterRebase` is a distinct pipeline verdict, but contributes to the existing `merged` counter. The worker clears phase and pending marker, writes `integration_succeeded`, and moves the card to `6-completed`. `NoTaskBranch`, `Conflict`, `GateFailed`, and `Error` clear the phase, retain the pending recovery marker, write `integration_failed`, and leave the card in Human Review. If a legacy accept path already placed the card in Completed, both the worker and restart backstop move it back to Human Review. The red badge names the typed failure, such as `No task branch`, and the application-owned `## Acceptance integration` section in `status.md` preserves the raw outcome, target lane, integration branch, reason, and timestamp. Immediate acceptance failures use the consistent `IntegrationFailed` transition outcome, which the single-card API maps to HTTP `409`; batch results use `integration-failed`. A configured pull-request handoff also remains in Human Review until target-branch membership is real.
- Explicit completion without integration: operator move requests may set `operatorOverride: true` only with target `6-completed`. The flag is never inferred or defaulted. It bypasses integration for that move and records `operator-override` in pipeline history plus `OperatorOverride` and the supplied reason in `status.md`. Cards that explicitly expect no branch are exempt without an override: report-only or concept modes, epics, `taskType=concept|decision` records, and cards with `noBranchExpected: true`.
- Restart recovery: `AcceptedIntegrationBackstopHostedService` is a safety net, not the normal Remote path. It resumes cards in `5-human-review` with phase `integrating` from their phase, marker, and pipeline facts. This includes a human acceptance that retried a failed immediate merge but lost its volatile queue item. The backstop consumes the same `TaskIntegrationStatusService` recovery decision as the board status projection, so a stale Passed step cannot overrule missing target-branch membership. It processes accepted deliveries by project and original delivery time, moves cards to Completed only after successful integration, and returns decided failures to ordinary Human Review instead of replaying them in a loop. Legacy Completed and archived recovery remains supported when the card carries live recovery facts. Historical verification runs first, and its bookkeeping records never authorize a merge or lane move. The 15-minute sweep logs `attempted`, `merged`, `alreadyMerged`, and `failed` independently; `Merged` and `MergedAfterRebase` contribute to `merged` and `integrated`. The acute 30-minute alert evaluates terminal Completed and Archive cards that carry a native acceptance receipt, have no historical verification, and remain unintegrated beyond the threshold. Each refresh reads the live task folders so a direct record append converges within one interval. Its project-filtered banner shows at most ten task keys and links to the complete `integration:stalled` Board filter.
- Read model, attribution, and conflict honesty: `TaskIntegrationStatusService` recomputes `integration.status` from the attributed `commits[]` membership in the configured target branch and projects `integration.deliveryRef` through the same `DeliveryRefResolver` used by both triggers. A mechanical replay retains every historical commit entry, marks it with `supersededBySha`, and appends the replacement SHA with the original producer attribution. Entries superseded by SHA or by a later attempt do not participate in integration completeness, so rewritten objects do not create false `partial` status. Remote `runner/<host>/<KEY>` refs and evidenced local `task/<slug>` refs therefore use one card field; `no-branch` is valid only when neither a delivery ref nor an attributed commit exists. A terminal failed attempt sets typed `integration.failure` state so the chip names the concrete class, including `no-task-branch`, and states whether rebase recovery applies. The service uses a target-HEAD-fingerprinted ancestor set, accepts valid abbreviated SHAs, and invalidates immediately when the target HEAD moves. Lane, provenance merge records, pipeline success, and curated merge subjects cannot force `integrated`. A failed immediate merge still proceeds to Human Review and remains visibly `conflict-skipped` or failed on that card, with the actual conflicted files in the durable verdict. An out-of-band merge self-heals the card on the next read. Card integration badges, delivery-ref wording, the develop segment, Git-state wording, and acceptance wording consume this computed field; `integrationpending` is not rendered as a second status chip. At startup, V2 of `HistoricalIntegrationVerificationSweep` reads task folders directly and extends the V1 population to terminal acceptance-start cards inspected by the alert. It preserves existing V1 rows, checks Git ancestry, no-code artifacts, and surviving result or salvage refs, and classifies evidence-free cards without a historical `commits[]` field as `no-attribution-legacy`. The following `AcceptedIntegrationInventorySweep` emits rows only for `content-on-fence`, `genuinely-missing`, and current recorded `Error` or `NoTaskBranch` outcomes.
- Merge-queue terminal history: the Project Hub keeps historical archive outcomes visible without counting them as actionable conflicts. An archived `conflict-skipped` record with a pre-authority review subject that has no `RunAttemptId` is `legacy-unverifiable`; another archived conflict is `superseded` as an in-place merge subject and must be recovered through a new card if its behavior is still required. Both states retain the original integration outcome in their reason. The queue counters are filters for all outcomes, while `conflict` is reserved for work that can still be resolved on its current card.
- Conflict recovery: a reviewed delivery that exhausts the three-stage sequence
  renders **Rebase & retry** next to its red integration badge. The action calls
  `POST /api/tasks/{id}/integration/rebase`, appends a focused steer prompt,
  moves the card to the top of Ready, and lets the assigned remote runner
  resume its existing delivery ref and return a new fenced result for acceptance.
  The prompt lists the report's conflicted files and prefers merging the latest
  integration branch into the delivery branch with conflict resolution over
  rewriting history. If rewriting is unavoidable, the one-to-one mapping rule
  remains mandatory.
- Deterministic acceptance rail: `AcceptanceRailHostedService` repeats the
  post-integration acceptance and conflict-recovery decisions without depending
  on a live orchestrator session. On its bounded timer it accepts only
  Git-derived `integrated` coding cards, honors explicit hold tags and the
  `orchestrator-hold` list, and sends recoverable `conflict-skipped` cards
  from Human Review or Escalated through the same recovery service used by
  the operator endpoint even when a generic park marker says a decision card is
  required. The shared two-round budget is counted from durable automatic
  recovery events for the fenced delivery chain, identified by its immutable
  result ref and delivered SHA. Re-reviewing that delivery shares the budget;
  publishing a new delivery starts a new one. Identifier-less legacy events
  count conservatively. Once the budget is spent, the park reason names both
  the usage and delivery, for example
  `automatic recovery budget used: 2/2 for delivery 5cd6b1d4bc07`. Concept and report-only cards
  remain in Human Review. The rail emits one action event per mutation plus a
  structured sweep summary and a last-run/lane-depth read endpoint.
- Gate environment retry: a merge gate that dies before test discovery is a
  broken gate host, not a verdict on the delivery.
  `GateEnvironmentRetryService` replays the integration alone on a bounded
  ladder; see [Gate environment retry (AGT-2824)](#gate-environment-retry-agt-2824).
- Review-verdict reuse: when the merge result is still the subject the
  Remote Review verified with the same integration tip and tested tree,
  `IntegrationGateReusePolicy` reduces the gate to its compile step instead
  of re-running the suite the review just ran; see
  [Integration gate reuse of the Remote Review verdict (AGT-2839)](#integration-gate-reuse-of-the-remote-review-verdict-agt-2839).
- Push durability: `IntegrationPushQueue` remains in memory to keep network work off the request path. `IntegrationPushBackstopHostedService` re-drives any passed merge with a non-terminal push step after restart, so queue loss cannot leave the integration branch local-only.
- Shutdown drain: once the accepted worker enters merge + build gate + possible rollback, it ignores host cancellation until that consistency boundary reaches a terminal result. `/healthz/drain` returns `gate-busy` during that window. The external stable restart watcher waits up to `ATP_GATE_DRAIN_TIMEOUT_SECONDS` before it invokes the hard update/restart path.

### Gate environment retry (AGT-2824)

On 15.09.2026 three cards passed Remote Review and then lost their merge gate to
`Tool 'node' version v24.18.0 does not match .nvmrc`. The card said the
integration "will be retried" and nothing retried it for over an hour: the
accepted-integration backstop only re-drives `6-completed` / `7-archive`, and
the acceptance rail only reacts to `conflict-skipped`, which CAC-18 deliberately
keeps this failure out of. The only operator path left was `/move 4-auto-review`,
a complete new remote review of a delivery whose review had already passed.

AGT-2872 includes gate-run budget overruns without red tests in this same ladder.
The one contention allowance and slow-test evidence are documented in
[build/test gate recovery](../operations/testing/build-test-gate-flaky-rerun.md#budget-overruns-and-contention-agt-2872).

`GateEnvironmentRetryService`
(`backend/Features/Pipeline/GateEnvironmentRetry/`) closes that gap.

- **Eligibility.** `GateEnvironmentRetryPolicy` is a pure matrix. A card
  qualifies when it sits in `4-auto-review`, `5-human-review`, or `5e-escalated`, expects a code
  delivery, has no acceptance integration already in flight (`phase` is not
  `integrating`), carries integration failure code `gate-environment-failure`,
  and the **latest review** for exactly the delivery SHA in
  `review-subject.json` ended in `Pass`. Latest, not any: a delivery can be
  re-reviewed without changing, and an older `Pass` that a later
  `ProductFailure` overturned is not a green light. Any other failure code
  belongs to its existing owner: `merge-conflict` to rebase recovery,
  `build-gate-failed` to the operator, `integration-push-blocked` to the push
  backstop.
- **Ladder.** Rungs of 5, 15, and 45 minutes. The first rung measures from the
  recorded gate failure, every later rung from the replay that produced the
  current completed failure (or its retry receipt if later). An unfinished review
  prevents replay. Configure with `GateEnvironmentRetry:BackoffMinutes`,
  `GateEnvironmentRetry:SweepIntervalSeconds`, and `GateEnvironmentRetry:Enabled`.
- **No new review.** A rung replays `MergeIntoDevelopRunner` against the
  unchanged delivery SHA and nothing else. It never creates a review attempt, so
  a broken gate host costs zero review slots.
- **Receipts.** `pipeline-execution.json` keeps one row per step id, so it
  cannot carry a count. The budget is counted from append-only
  `integration_gate_environment_retried` timeline events scoped to the delivery
  SHA; a new delivery is a new review and therefore a new ladder. The receipt is
  written before the merge starts, so a crash costs one rung instead of leaving a
  budget that can never be exhausted.
- **Parking.** After the last rung the service writes the parked reason onto the
  durable merge step, so the integration chip, its tooltip, and the Evidence tab
  all name the environment failure the ladder gave up on, together with the
  original gate reason. An `integration_gate_environment_parked` receipt makes
  the write idempotent across sweeps.
- **Operator action.** `POST /api/tasks/{id}/integration/retry` and the
  **Retry integration** button next to the card's integration badge run the same
  replay with the same semantics: same delivery SHA, reused passed review, no new
  review round. It ignores the remaining backoff, because an operator asking for
  it is the signal that the gate host was repaired, and it restarts the bounded
  ladder, so a parked card recovers its automatic budget instead of parking again
  on its next fault. An operator replay is not a rung and reports `rung: 0`.
  It overrides the ladder's *timing* only: eligibility is decided by the same
  `GateEnvironmentRetryPolicy` evaluation the sweep runs, so a card the policy
  ignores is refused with `409` carrying the policy's reason slug in `code`
  (`not-a-gate-environment-failure`, `no-passed-review-for-delivery-sha`,
  `acceptance-integration-in-flight`, `outside-retry-lanes`, `no-code-delivery`,
  `gate-environment-retry-disabled`). Replaying past an in-flight acceptance
  integration in particular would race a second merge into the integration
  branch against the acceptance transaction that already owns it. Every `409`
  refusal includes `comparedDeliverySha`, the full SHA read from the card's
  review subject (or `null` if no comparison ran), and `latestAttempt`: the latest
  review found for that exact SHA, with `id`, `outcome`, and `terminalAt`, or
  `null` if none was found. Review history is checked only after the card has
  a gate environment failure eligible for a retry; other refusals carry a
  `null` attempt because review history was not consulted. The review lookup
  reads archived attempts as well as live attempts, matching
  `GET /api/attempts/tasks/{id}`. A failed history
  read appears in `error` as `Review lookup failed: <exception>` and is logged
  at Warning level, so a lookup defect can be distinguished from a missing
  review. The lookup uses the card's stable `key` (for example `AGT-2880`),
  which is also the attempt authority key; the scanner's path-qualified
  `taskKey` is only an internal card identity.
- **One replay per card.** A sweep rung and the operator action take the same
  in-flight guard before reading anything. During a replay the merge is already
  in the integration branch and its gate has not run yet, so deciding from the
  card's integration verdict mid-transaction would answer "nothing to retry" for
  a card that is being retried right now.

### Integration gate reuse of the Remote Review verdict (AGT-2839)

Every card that passed the Remote Review used to run the local gate again on the
Windows Studio: `dotnet build`, the backend suite, the frontend tests and lint,
about seven minutes per card, one card at a time. With a queue of passed cards
behind the review executor, that gate was the second bottleneck, and it re-ran
exactly the suite the review had just run on the same result SHA.

The local gate reuses that verdict only when the merge result is the state the
review tested:

- **Review proof.** The report endpoint writes `logs/review-verification.json`
  (`ReviewVerificationStore`) before integration. It records the attempt, Pass
  outcome, green build/test verdict, immutable result SHA, integration ref,
  merge base, exact integration tip, and tested Git tree. The executor captures
  the integration tip before running verification commands. A resumed attempt
  that may reuse older command results emits no tip proof and keeps the full gate.
- **Reuse decision.** `IntegrationGateReusePolicy` requires the current pre-merge
  integration tip to equal `IntegrationTipSha` exactly, and the local merge tree
  to equal `TestedTreeSha`. It also requires the same integration line, unchanged
  delivery ancestry, and no replay or conflict resolution. Merge-base equality
  is not evidence that the integration tip stayed unchanged: an unrelated commit
  can advance the branch without changing its merge base.
- **Compile still runs.** The `compile-only` level keeps build commands and
  omits test and lint commands. `TestSelectionPlanner` records both omitted
  inventories in its audit.
- **Full gate.** Any advance of the integration branch runs the full gate, even
  when the merge base or tree stays the same. A different merged tree also runs
  the full gate: current Remote Review tests the immutable delivery, so a clean
  merge that adds integration-only changes is not eligible. Legacy proof without
  an integration tip, tree, merge base, or ref; a replayed or conflict-resolved
  merge; an `AlreadyMerged` recovery without a trustworthy anchor; and an
  opted-out project all keep the full gate.
- **Evidence.** `post-steps/pre-develop-build-gate-N.log` records `reviewReuse=`,
  the review attempt, and the reason for both reuse and fallback. Reuse evidence
  names the unchanged tip and tested tree. This follows the existing three-line
  durable-recovery header, preserving `IntegrationGateReceipts.ReadExact`.
- **Setting.** `ProjectSettings.IntegrationGateReviewReuse`, exposed through
  `PUT /api/projects/{project}/integration-gate-review-reuse` and the
  "Integration gate" control in Project settings, defaults on for remotely
  executing projects and off for locally executing projects. Explicit on never
  bypasses the proof checks.

### Incidents: 2026-07-24 and 2026-07-28 bulk acceptance

The operator accepted 35 remote-runner cards. The acceptance hook did fire:
each sampled card recorded `post-merge-into-develop` immediately after the lane
move. Every sampled step returned `no-branch`, however, because the merge runner
constructed `task/<slug>` while the remote runner had published the reviewed
delivery as `runner/agent-runner-01/<task-key>`. Since no local merge succeeded,
`IntegrationPushQueue` correctly received no item. Its separate defect was that
an item already enqueued after a successful merge existed only in the process
channel and had no restart backstop. The warning and `integrationpending` tag
made the failure visible, but cleanup compared it with the invalid
pre-normalization spelling `integration:pending`. `SetJobTags` had already
stripped the colon to satisfy the tag-id grammar, so manually integrated cards
continued to display the stale marker. The restart sweeps also originally used
the live-board-only scanner despite claiming archive support, which excluded a
card after it moved to `7-archive`.

A push that reached the worker was not swallowed: its pipeline push step was
recorded as failed and the managed-repository bus failure was emitted. The
silent durability gap was an item lost with the in-memory channel before the
worker could process it.

The first correction made `review-subject.json` available as a remote acceptance
source and added restart backstops. It did not close the contract: later delivery
refs could live under immutable result refs, accepting still moved the card to
Completed before checking the outcome, and the displayed status could remember a
merge attempt instead of observing the target branch.

The 29 July correction resolves the delivery ref from card truth, makes acceptance
transactional in Human Review, and computes every accepted-card integration status
from target-branch commit membership. This closes the `NoTaskBranch` series from
the 28 July 23:09 acceptance wave and lets out-of-band salvage merges repair the
display without fabricating a new merge attempt.

A read-only `git merge-tree` replay used all eleven reported delivery refs as
incident fixtures. Against the incident head `dfa806689`, all eleven reproduced
conflicts. Against the later `origin/develop` head, AGT-2227, AGT-2234,
AGT-2238, AGT-2240, AGT-2253, AGT-2259, AGT-2261, AGT-2263, AGT-2265, and
AGT-2273 still reproduced conflicts, while AGT-2294 was already an ancestor.
The replay did not mutate `develop`; conflict resolution proceeds through the
steer recovery action above.

The subsequent operator reconciliation landed AGT-2238, AGT-2240, AGT-2253,
AGT-2259, AGT-2261, AGT-2263, AGT-2265, and AGT-2294 in `origin/develop`.
AGT-2227, AGT-2234, and AGT-2273 remain the live pending cases. After this fix
is deployed, the accepted-integration backstop replays their fenced delivery
refs, records the concrete conflict files, and enables the card action for the
focused rebase round. This sequencing is intentional: a pending legacy
`no-branch` record is not rewritten or moved through a task-store filesystem
shortcut.

### Incident: 2026-08-10 behind-base conflict wave

The operator report called this a ten-card sample but named eleven cards:
AGT-2515, AGT-2532, AGT-2536, AGT-2537, AGT-2543, AGT-2545, AGT-2547,
AGT-2551, AGT-2552, AGT-2555, and AGT-2558. Each immutable delivery tip was
replayed in an isolated detached worktree onto the integration target's
first-parent head immediately before that card's recorded conflict. `rerere`
was disabled, and every worktree was removed after the experiment.

Result: **0 of 11** deliveries applied mechanically without a textual conflict.
All eleven retained at least one unmerged file, so this historical sample would
still have reached `conflict-skipped` with its file list. The new recovery path
does not change those historical content conclusions. It closes the general
Fence-to-integration base window for future non-overlapping deliveries and
removes unnecessary agent rounds when the replay is textually clean.

## The automatic integration at run end (parallel path)

For `MaxParallelism >= 2`, integration happens automatically at run finalization, before the task ever reaches review.

- Entry: `ProjectRunner.IntegrateWorktreeRunAsync` (`backend/Features/Runner/ProjectRunner.cs`, line ~907). Guarded by `if (!run.IsWorktreeRun) return null;` - a no-op for the sequential path.
- Sequence: commit agent edits onto `task/<id>` (`WorktreeRunCommit`) -> push `task/<id>` to origin for portability -> acquire the per-project merge serialization (local `_integrateLock` semaphore + a cross-runner integration lease) -> `WorktreeTaskLifecycle.Integrate`.
- `Integrate` (direct-merge): rebase the worktree onto the `IntegrationBranch` tip, then fast-forward the integration branch itself (`GitService.FastForwardIntegrationBranch`). The branch does not have to be checked out anywhere, and uncommitted work in the shared checkout neither blocks the integration nor is touched by it (AGT-2832): a checkout that holds the branch is offered the fast-forward first, which git refuses rather than overwriting local modifications, and otherwise the ref is advanced with a compare-and-swap. Result history is linear with rewritten SHAs.
- Conflicts: a rebase conflict returns `IntegrationOutcome.Conflict`; the conflicted state can be preserved and escalated to a managed conflict-resolution agent (`CompleteIntegrationAfterResolution`). Unresolved work is left in place.
- `IntegrationStrategy == pull-request`: `Integrate` returns `IntegrationOutcome.PushedForReview` without merging. Operator acceptance also honors this strategy and records the delivery as awaiting a pull request instead of reporting a successful merge.

Fenced Remote delivery is orthogonal to `MaxParallelism`: after its Remote Review gates pass, it uses the common `post-merge-into-develop` runner before Human Review regardless of the local worktree setting. Human acceptance is only its retry path.

## Worktree cleanup policy

Teardown is deferred and conditional on the work being merged (`WorktreeTaskLifecycle.TeardownIfIntegrated`, lines ~341-361).

- The runner does NOT tear down per run, so a resume/reissue can reuse the worktree (`PrepareOrReuse`).
- At terminal exit (accept into review / escalate), teardown runs only if `task/<id>` is already an ancestor of `IntegrationBranch`. If it is merged, the worktree and branch (local + `origin/task/<id>`) are removed. If it is NOT merged (e.g. a conflict left for resolution), teardown is skipped and the branch/worktree are preserved.

## Side-by-side: maxParallelism == 1 vs >= 2

| Aspect | `MaxParallelism == 1` (sequential, default) | `MaxParallelism >= 2` (parallel) |
| --- | --- | --- |
| Worktree / branch | None; shared main checkout | Isolated worktree on `task/<id>` |
| Where agent edits land | Current branch of shared checkout (usually `develop`) | `task/<id>` branch in the worktree |
| Commit timing | On `3-progress -> 4-auto-review` (if `AutoCommit`), scoped to run windows | At run end via `WorktreeRunCommit` (whole worktree) |
| Merge timing | Deferred; inside the transactional Human Review accept | Automatic at run end, before review |
| Merge trigger | Operator acceptance | Run finalization (no human gate) |
| Merge command / history | `git merge --no-ff` in the Studio-owned integration worktree (one revertable merge commit, original SHAs) | rebase + fast-forward of the integration ref (linear, rewritten SHAs) |
| Serialization | Implicit (single active slot) | `_integrateLock` semaphore + cross-runner integration lease |
| Conflict handling | Abort, record Failed, operator resolves manually | Preserve and escalate to a managed resolver; block teardown if unresolved |
| Push of `task/<id>` to origin | n/a (no task branch) | Pushed at run end for portability |
| Pipeline step | "Merge into Develop" (`post-merge-into-develop`, deferred) | "Integrate merge" (`post-integrate-merge`, automatic) |
| Worktree cleanup | n/a | Deferred; only if branch is an ancestor of `IntegrationBranch` |

This table describes local execution. Remote fenced deliveries integrate before Human Review through `RemoteDeliveryIntegrationCoordinator`, not through the sequential acceptance timing shown above.

## Per-project settings that affect integration

Defined in `backend/Shared/Models/ProjectSettings.cs`. Read live on each transition.

| Setting | Type / default | Effect |
| --- | --- | --- |
| `MaxParallelism` | `int`, `1` | Concurrency slots; clamped to `>= 1`. `1` = sequential (no worktree). `> 1` = worktree-isolated parallel. Today this also selects the entire integration path (see sharp edges). |
| `IntegrationBranch` | `string`; effective default `origin/HEAD` | Target branch used by parallel run-end integration, immediate Remote integration, and the acceptance retry. An existing configured local or remote branch wins. If the stored/default candidate does not exist, the resolver uses the repository default branch, so a main-only project never attempts to fetch `develop`. |
| `IntegrationStrategy` | `string`, `direct-merge` | `direct-merge` or `pull-request`. Run-end integration, immediate Remote integration, and operator acceptance consult it. A pull-request handoff remains in Human Review instead of claiming that it merged. |
| `AutoCommit` | `bool`, `true` | When true, auto-commit dirty changes on `3-progress -> 4-auto-review` (sequential). Read-only modes skip it. |
| `AutoPushStrategy` | `string`, `always-immediate` | `never` / `on-completed` / `always-immediate` - when committed work is queued for push to origin. |
| `IntegrationGateReviewReuse` | `bool?`, `null` | Whether the local build/test gate may reuse a passed Remote Review verdict with the exact reviewed integration tip and tested tree and run its compile step only. `null` = the safe default: on for a project whose execution is placed on a remote runner, off for a locally executing project that has no Remote Review to reuse. |

## Known sharp edges (under review)

These behaviours are real today and being reviewed. See the configuration analysis: [./task-integration-merge-config-analysis.html](./task-integration-merge-config-analysis.html).

- Parallelism coupling: `MaxParallelism` is perceived as a throughput knob, but flipping it `1 <-> >=2` also silently changes the commit target, merge timing/trigger, merge command and history shape, conflict handling, and what "Accept" means. `IntegrationBranch` and `IntegrationStrategy` are not exposed in the frontend.
- Auto-commit on transition: in sequential mode the auto-commit can land directly on the configured target branch with no `task/<id>` branch. The computed membership check recognizes this as already integrated and completes acceptance without running a merge. The completed-push target can still diverge from the integration target; that configuration detail remains under review.

## The completion contract (AGT-2817)

A card in the delivered lane makes a claim. Until AGT-2817 nothing checked it:
the move endpoint accepted `operatorOverride: true` with no reason, and nothing
asked whether an integration record existed, whether the attributed commits were
contained in the integration branch, or whether the card declared a deliverable
without code. AGT-2795 therefore sat in `6-completed` while its single
attributed delivery had failed review and was never integrated.

### Containment decides; the record is a cache

Every place the product judges whether a delivery is integrated asks Git whether
the delivery commit is contained in the integration branch: the archive guard,
the completion contract, the board badge, and the per-card deployment answer.
A stored integration record, a pipeline verdict, and a lane position are read
beside that answer, never instead of it.

**A missing record is never reported as "not integrated".** It is *unknown, not
yet checked*, and the containment question is asked before anything is said.
AGT-2706's delivery `79c2dcf8c` was contained in `develop` and in `main` and had
shipped with v0.3.0, yet the archive dialog reported it as a pending
integration purely because its `integration` field was `undefined`.

### The three grounds

`CompletionContractPolicy` (`backend/Features/Tasks/Acceptance/CompletionContractPolicy.cs`)
is pure and has a direct matrix test. A move into `6-completed` is accepted on
exactly one of three grounds, and the card records which one in
`task.json.completionClaim`:

| Basis | Accepted when |
|---|---|
| `integrated-delivery` | The effective delivery is contained in the integration branch. Outranks every other ground, including an override the operator did not need. |
| `deliverable-without-code` | The card expects no code and names its deliverable - for a concept card the Dossier path and key, otherwise a workbench reference, a written `results/deliverables.md`, or a deliberate no-Dossier declaration. |
| `operator-override` | The operator completed the card against the contract **with a written reason**, stored on the card and shown wherever the card claims completion. |

Refusals are typed and answerable: `unintegrated-delivery`,
`superseded-only-delivery`, `undeclared-deliverable`, and
`override-without-reason`. A card whose every attributed delivery is marked
superseded with no successor recorded claims a delivery that does not exist, so
it is refused unless the operator overrides it in writing.

The check runs at the lane change, for every transition into `6-completed`, not
only from Human Review. Operator-initiated moves are refused (HTTP 409);
automated paths record the claim they can prove and are never blocked by it.
`POST /api/tasks/{id}/move` and `PUT /api/tasks/{id}/state` reject
`operatorOverride` without a reason at the boundary (HTTP 400).

### `next-attempt` is a placeholder, not a verdict

`TaskCommitSupersession.PendingAttempt` (the literal `next-attempt`) is written
between an explicit requeue and the publication of the replacement attempt, then
resolved to the real fenced attempt id. When no replacement ever publishes - the
card is integrated by an operator merge, or the requeue is abandoned - the
placeholder was never cleared and every consumer that read supersession as a
verdict saw a shipped delivery as replaced. That is AGT-2706.

- `replacement-pending` means "requeued, replacement not published yet". The
  commit is still the delivery the card has, so it stays in the current commit
  list with its own quiet marker.
- `replaced` means "replaced by this": a replacement SHA, or a resolved attempt
  id. Only this moves a commit into the replaced history.

A delivery contained in the integration branch is not superseded: the completion
contract and the reconciliation pass both clear the placeholder from contained
commits. A named successor is never touched.

### The deployment answer per card

`GET /api/tasks/{id}/delivery-claim` answers, for one card: the delivery ref,
whether it is contained in the integration branch, the curated
`merge(KEY): ...` commit that carried it, whether it is contained in the release
line, the supersession state of each attributed commit, and the recorded
completion claim. The task detail renders it where the operator already looks,
beside the lane and the evidence.

### Operator merges write their record

A card-scoped merge performed outside the pipeline
(`merge(AGT-nnnn): integrate reviewed delivery (operator card-scoped merge)`,
and the salvage recipe) must write its integration record back to the card.
[`scripts/record-operator-merge.sh`](../../scripts/record-operator-merge.sh) is
the recorded step of that recipe: it derives the delivery commits from the merge,
posts the record through `POST /api/tasks/{key}/integration-records`, and then
reconciles the project so the placeholder the merge left standing is cleared.

```bash
scripts/record-operator-merge.sh --task AGT-2706 --merge 9cfc0e074 --project PROJ-002
```

The numbered recipe an operator follows - merge, record, check the claim - is a
step of the git doctrine runbook:
[Out-of-pipeline card-scoped merge](../operations/git/commit-push-doctrine.md#out-of-pipeline-card-scoped-merge-operator-recipe).

### The sweep and the reconciliation pass

- `GET /api/projects/{id}/delivery-claims` reports every delivered and archived
  card's class and its contradictions. Run it after a promotion so the
  divergence between "delivered" and "deployed" is visible without asking.
- `POST /api/projects/{id}/delivery-claims/reconcile` runs the same sweep and
  repairs the caches that contradict containment: it appends the integration
  record a contained delivery is missing, and clears the stale `next-attempt`
  placeholder. Both repairs move only in the direction where containment is
  positive, which is the one direction that cannot lose information. Everything
  else is reported and left alone; the pass never rewrites history it cannot
  prove.

Reported classes and findings (`DeliveryClaimSweepPolicy`, pure, matrix-tested):

| Finding | Shape |
|---|---|
| `unintegrated-delivery` | The effective delivery is not in the integration branch (AGT-2795). |
| `superseded-only-delivery` | Every attributed delivery is replaced and no successor is recorded (AGT-2743). |
| `unmarked-replaced-delivery` | An uncontained delivery is kept as a live attribution beside a contained one (AGT-2744). |
| `missing-integration-record` | Contained, but the card carries no integration record (AGT-2706). Repairable. |
| `stale-pending-supersession` | A contained commit still carries the `next-attempt` placeholder (AGT-2706). Repairable. |
| `nothing-claimed` | Neither a delivery nor a named deliverable. |

## Branch cleanup (AGT-2009, AGT-2793)

Over time a project repository accumulates dead refs across six namespaces: `task/*`,
`runner/*`, `delivery/*`, `agent-studio/results/*`, `agent-studio/salvage/*`, and
`agent-studio/quarantine/*` (see `runner/GitWorkspace.cs` for where each namespace is
created). Branch reclamation automatically prunes these refs as the task lifecycle
completes, so the repository does not accumulate unbounded branches.

### Automatic reclamation lifecycle

`BranchReclaimTriggerService` (`backend/Features/Git/BranchReclaimTriggerService.cs`)
is the event-driven counterpart to the periodic `GitBranchRetentionHostedService` sweep;
both apply the same `BranchRetentionPolicy` and share `GitRetention:Enabled` as their
settings gate. It is wired into three lifecycle points, always after the transition
lands and never blocking it on failure:

1. **After integration.** `AcceptedIntegrationWorker.FinalizeAcceptedTaskAsync` calls
   `ReclaimAfterIntegration` once a merge into the project's integration branch
   succeeds (`MergeIntoIntegrationResult.Outcome.IsSuccessfulIntegration()`). It never
   runs on a failed or skipped merge, and never for the branchless-delivery path
   (`FinalizeBranchlessTaskAsync`, which has no ref to reclaim). It evaluates the
   task's `task/*`, `runner/*`, and `delivery/*` refs.
2. **When a card is archived.** `TaskTransitionService.MoveAsync` calls
   `ReclaimAfterArchive` once the move to `7-archive` lands successfully. It evaluates
   the same task/runner/delivery namespace plus this task's salvage and quarantine
   refs. This is distinct from `ArchivedResultRefPruner`, a periodic, all-archived-cards
   sweep that only deletes local remote-tracking copies of already-known results/
   quarantine refs and never contacts origin; the archive trigger is the one that
   actually deletes the remote refs for one task, once, at the transition.
3. **After promotion to main.** Promotion runs as
   `scripts/release/promote-develop-to-main.sh` on the runner host, outside the
   backend process, so it has no in-process transition to hook a trigger off of.
   Instead, once its atomic `main` + tag push is verified, the script calls
   `POST /api/git/branch-reclaim/promotion?project=<name>` (best-effort, logged, never
   fails the promotion), which runs `ReclaimAfterPromotionToMain` -> the full
   `GitBranchRetentionService.RunOnce` sweep across every project, not just the one
   that promoted. See [develop-main-promotion.md](../operations/develop-main-promotion.md#branch-reclaim-after-promotion).

### Ref eligibility by namespace

| Namespace | Delete when | Retention | Evidence |
|-----------|-------------|-----------|----------|
| `task/*` | Merged into both develop and main, >= 7 days old | 7 days after merge | Ref name, SHA, reachability proof |
| `runner/*` | Merged into both develop and main, >= 7 days old | 7 days after merge | Ref name, SHA, reachability proof |
| `delivery/*` | Merged into both develop and main, >= 7 days old | 7 days after merge | Ref name, SHA, reachability proof |
| `agent-studio/results/*` | Proof commit in main (immutable delivery proof) | 0 days after proof merge | Ref name, SHA, proof branch, task key |
| `agent-studio/salvage/*` | Terminal task + (in main OR >= 14 days old) | 14 days or on completion | Ref name, SHA, task key, terminal status |
| `agent-studio/quarantine/*` | >= 30 days old (unless referenced by open escalation) | 30 days | Ref name, SHA, age |

Safety guardrails (AGT-1945 invariant):
- Refs are evaluated by `BranchRetentionPolicy.ClassifyNamespace` and evaluated against `BranchRetentionPolicy.Evaluate`.
- A second recheck immediately before deletion verifies the SHA has not changed.
- Protected refs (`main`, `develop`, `release/*`, `v*`) are never deleted.
- Refs checked out in a live worktree are never deleted.
- Proof commits stay reachable in main even after their immutable ref is deleted (ancestor check binds deletion eligibility).
- Before any ref namespace is scanned, origin is fetched to ensure ancestry checks use current develop and main tips.

### Evidence and reporting

`BranchRetentionEvidenceWriter` appends one JSONL row per deleted ref to
`{TaskRepository}/projects/{project}/reports/git-branch-reclaim.jsonl`:
```json
{"ref":"task/AGT-1234","sha":"<sha>","class":"Task","reason":"Deleted after age and develop/main ancestry recheck.","taskKey":"AGT-1234","timestampUtc":"2026-09-14T12:00:00Z"}
```
This runs for both the periodic sweep (`GitBranchRetentionService.RunRepository`) and
the per-task reclaim (`ReclaimForTask`), so it is the durable, ref-name-independent
audit trail the deletion itself can no longer serve as evidence for once the ref is gone.

The two per-task triggers (`ReclaimAfterIntegration`, `ReclaimAfterArchive`) also echo a
`branches_reclaimed` (`TimelineEventKinds.BranchesReclaimed`) entry onto that task's own
`logs/timeline.jsonl` when any ref was actually deleted, carrying the deleted ref names
and SHAs in `Details`. The project-wide promotion sweep only has a repository path, not
a task folder, so it relies on the JSONL report alone.

### Manual cleanup with dry-run

`GitBranchRetentionService.RunOnce(dryRun: true)` generates a report of what would be deleted without
mutating any refs. This is useful for auditing policy changes or previewing the reclamation schedule.
Dry-run actions are never appended to the evidence report (nothing was actually deleted).

### See also (Project Hub Git-Management, AGT-2009)

Operator-driven cleanup remains available as a secondary tool for edge cases:
- Analysis via `GitCleanupService.BuildPlan` shows which `task/*`, `runner/*`, and stale worktree entries are merged.
- Execution via `GitCleanupService.Execute` applies operator-confirmed deletions with additional safety gates.
- This tool serves as a backstop for refs that automatic reclamation does not handle (e.g., refs from incomplete
  or failed cleanup runs).

## Stale-branch sweep (AGT-2794)

The event-driven reclamation above stops new growth. It cannot reach the refs that
already exist: cards archived without integration, refs from crashed runs, refs whose
card no longer exists, and every ref created before the policy did. The stale-branch
sweep is the periodic, all-namespace pass that closes that gap. It uses the **same**
`BranchRetentionPolicy` - `BranchSweepPolicy` only derives the class and the task key
from the ref name and hands the facts over; there is no second policy.

### What one run does

`BranchSweepService.Run` (`backend/Features/Git/BranchSweep/`) runs per project
repository, in this order:

1. `git fetch --prune`, so ancestry is answered against current tips.
2. Classify **every** ref under `refs/remotes/origin`: class, task key when the name
   carries one, tip age, containment in `main` and `develop`, the lane state of the
   owning card, and whether an open card still references the ref. Containment comes
   from two `git for-each-ref --merged` calls, not one ancestry spawn per ref.
3. Decide keep/delete per ref through `BranchRetentionPolicy.Evaluate`.
4. Write the report, then - only in `reclaim` mode - delete what the policy allowed.

`GitBranchRetentionHostedService` runs the sweep after the existing retention pass and
the archived-result prune, gated on `GitRetention:Sweep:Enabled` (default on).

### Two modes per project

| Mode | Meaning |
|---|---|
| `report-only` | Default, including for every project that existed before this card. The run classifies and reports; no delete primitive is reached. |
| `reclaim` | The scheduled run deletes what the policy allows. Operator-confirmed deletion from the UI works in either mode. |

A project may also override the per-class retention windows. Defaults come from the
policy above: task/runner/delivery 7 days, salvage 14, quarantine 30, results 0 (bound
to main containment, never to age). Both live in `project-settings.json` under
`branchSweep` and are read and written through `/api/git/branch-sweep/settings`.

### The one rule that drops unmerged work

Every rule in the table above only ever deletes refs that are contained in the
integration line. That leaves the backlog untouched, so the sweep adds exactly one
rule, `AbandonedRefAged`, for `task/*`, `runner/*`, and `delivery/*`:

> An unmerged ref may be reclaimed when its owning card is archived **or** no longer
> exists, no open card references it, and the tip is older than the abandoned window
> (default 90 days, per-project overridable).

Refs of a live card are never abandoned, however old. A result ref carries no task key
at all, so "orphan" cannot apply to it: it stays bound to main containment and is never
dropped on age alone. Protected refs (`main`, `develop`, `release/*`, `v*`) and refs
outside every managed namespace are reported for completeness but are never candidates,
and neither is a ref checked out in a live worktree.

### Reports

Each run writes `reports/branch-sweep/<project>/<timestamp>.json` plus a short
`<timestamp>.md` summary under the workspace root (`TaskRepository`). The JSON carries
the windows used, ref counts before and after, totals by class x decision, the full
candidate list with its reason, and the deletions performed; the markdown carries the
totals, the tip-age histogram, the decision distribution, and the deletions. One
Activity-feed line per run (`topic: branch-sweep`) carries the same totals. The report
is the artifact the operator reads before switching a project to `reclaim`.

### Operator UI

Project Hub -> Git shows the latest sweep under the graph: totals per class, the
tip-age histogram, and every candidate with its decision and reason. Three actions:

- **Run report-only sweep** / **Re-classify** - the second never writes a report.
- **Delete selected** - the ticked subset. `BranchSweepService.Execute` re-derives
  eligibility from a fresh classification and rejects a ref whose tip moved, the same
  contract `GitCleanupService.Execute` uses.
- **Reclaim all allowed** - the backlog pass. Refs go out in batches of at most 100 per
  push (`GitService.MaxRefsPerDeletePush`), with per-batch progress and a stop button
  that takes effect between batches.

Only refs the policy marked eligible can be ticked, and nothing is pre-selected.

### Backlog pass

Per repository the operator runs report-only once, reads the report, then switches the
project to `reclaim` (or uses "Reclaim all allowed" for a one-off). The first pass
clears refs already contained in `main`; the second clears by age and card state once
the abandoned window applies. The before/after ref counts of each pass are in its own
report (`refsBefore` / `refsAfter`).

### Out of scope

Local refs. The sweep is about remote branch state; local heads and stale worktree
registrations stay with `GitBranchRetentionService` and the Git-Management cleanup.

## See also

- `docs/concepts/parallel-task-execution.md` - parallel execution model, integration strategies, merge-queue.
- `docs/concepts/release-semantics.md` - the decided integration and release model (supersedes the retired `git-branching-integration-zielbild.md` draft). The target three-tier branching model (`task/<id>` -> `develop-local` -> `develop`) described in that draft was not carried forward; the `develop-local` tier remains a target, not yet implemented.
- ADR-0052 in `docs/system/architecture/decisions/adr-archive.md` - the parallel-execution decision and the "run agent does no git" contract.
