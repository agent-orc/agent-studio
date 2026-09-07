# AGT-2428 Transition and Integration Code Quality Review

Date: 2026-07-29

## Scope and decision boundary

Reviewed:

- `TaskTransitionService`
- `MergeIntoDevelopRunner`, `AcceptedIntegrationWorker`, and the drain/backstop path
- `AcceptedIntegrationBackstopHostedService`
- the integration-facing settlement contract in `AttemptAuthorityService`
- `HumanReviewEscalation` and verdict/integration backfills

The decision-ready status workbench on
`origin/runner/agent-runner-01/AGT-2424` is the behavior boundary. Its R4 Git
truth is implemented here. Its R1, R2, R3, R5, and R6 proposals and E1 through
E5 choices remain explicitly open, so this change does not introduce new
review-admission, accept, archive, or lane-routing guards.

## Findings and disposition

| ID | Area | Classification | Severity | Finding | Disposition |
|---|---|---|---|---|---|
| CQ-01 | Status and backstop | Correctness, coupling | High | Board status and restart recovery independently interpreted pipeline steps. A stale `Passed` step could overrule missing Git ancestry, while an out-of-band merge could remain displayed as failed. | Fixed. `TaskIntegrationStatusService` now owns both current status and accepted-delivery recovery. Exact reviewed `ResultSha` ancestry is primary; merge steps are diagnostic. |
| CQ-02 | Remote release integration | Correctness | High | A remote review subject targeting `main` went through the local-branch release path. It could not fetch an origin-only `ResultRef`, and the queued push used the caller fallback branch instead of the recorded target. | Fixed with a shared fenced remote preparation path and a remote-main regression test. The full suite runs on the immutable fetched SHA and the push targets the recorded branch. |
| CQ-03 | Accepted delivery resolution | Correctness, naming | High | An existing but malformed `review-subject.json` was indistinguishable from no remote subject and could fall back to guessed `task/<slug>`. | Fixed. Invalid accepted-delivery metadata produces a recorded `Error` and never guesses a local branch. |
| CQ-04 | Merge runner | Coupling, testability | Medium | `RunSerializedAsync` combined repository resolution, delivery selection, strategy routing, gate choice, pipeline recording, and push publication. `taskBranch` also named both local branches and remote result refs. | Fixed structurally. Delivery resolution, execution, and successful publication are separate methods backed by an explicit `AcceptedDelivery`. |
| CQ-05 | Transition service | Coupling, testability | High | `MoveAsync` interleaved the state mutation with requeue, commit stamping, post-processing, ordering, provenance, completed push, accepted integration, and notification. | Fixed structurally. Named post-move phases now share a named `TransitionContext`; behavior and ordering are pinned by transition, push, review, integration, and sister-card tests. |
| CQ-06 | Attempt authority | Testability, coupling | Medium | `SettleRun` exposed ten positional parameters, and long positional DTO projections made signature drift easy. | Fixed. A required-property `SettleRunAttemptRequest` carries settlement identity and result envelope; DTO projections use named arguments. |
| CQ-07 | Startup backfill | Correctness, coupling | Medium | `RemoteDeliveryBackfillService` was a startup-time, hard-coded, five-task repair still registered as a general service. It duplicated the durable accepted-integration recovery path. | Removed with an architecture guard. Generic recovery remains in `AcceptedIntegrationBackstopHostedService`. |
| CQ-08 | Integration error handling | Correctness | High | Returned `Error` outcomes were logged as normal worker completion, while terminal recording, push recording, and settings-read failures used silent catches. | Fixed. Integration errors and recording failures are logged at error level. Settings are read once per gated integration. The backstop continues per item but emits an error for each failed delivery. |
| CQ-09 | Review admission and accept | Correctness | High | Normal `* -> HumanReview` and `HumanReview -> Completed` paths do not require already-integrated delivery. Accept can still be the first merge trigger. Only `Escalated -> Completed` currently has an integration guard. | Open for status workbench R1/R2 and E1/E2. No behavior change made before Robert's decision. |
| CQ-10 | Archive | Correctness | High | `Completed -> Archive` has no integration or explicit-override guard. | Open for status workbench R3/E4. |
| CQ-11 | Transition ownership | Coupling, correctness | High | `TaskTransitionService` is the intended application funnel, but multiple production callers still invoke `TaskStateMachine.MoveJob` directly. The synchronous `HumanReviewEscalation.Escalate` path therefore bypasses `OnJobMoved`. | Open for status workbench R5. Migrate after the actor/delivery-class policy is decided and affected synchronous call chains can be made async. |
| CQ-12 | Pipeline and attempt projection | Correctness, naming | Medium | `PipelineExecutionLog.EnsureRun(Standard)` can materialize unrelated pending catalogue rows when only an integration fact is being recorded. Pipeline, result, and attempt recency can still present competing pending states. | Open for status workbench R6. |
| CQ-13 | Authority and settings seams | Testability, coupling | Medium | `AttemptAuthorityService` still owns locking, leases, fencing, subjects, JSON persistence, archive compaction, and legacy migration in one 1,600-line class. Merge and transition tests also require concrete filesystem and Git services. | Follow-up refactor. Split persistence/archive and authority state transitions behind narrow internal seams without changing the on-disk contract. |
| CQ-14 | Integration vocabulary | Naming | Medium | `MergeIntoDevelopRunner` can target `main` or another configured branch. `NoTaskBranch` is also used for a missing remote delivery, and `ConflictSkipped` collapses several decided failure states in the board projection. | Open. Rename the runner and expand result vocabulary together with R6 so API and UI labels do not drift again. |
| CQ-15 | Verdict backfill | Correctness, testability | Medium | Verdict backfill treats a failed per-project decision-journal read as an empty record set. That can turn an observability failure into a retroactive `unknown-legacy` escalation. | Follow-up with an injected journal reader and a failing-read regression test. This is outside R4 and was not changed without that test seam. |
| CQ-16 | Pull-request integration | Correctness, naming | High | `PushedForReview` is a skipped merge outcome, but the lane model does not yet define whether PR approval precedes Human Review or Human Review acts as the merge gate. | Open for status workbench R2/E3. |
| CQ-17 | Durable Runner reattach | Correctness, coupling, testability | High | Startup discovery and the attached-process poll separately read the terminal result and PID liveness. A worker could write its atomic result and exit between those reads, causing the replacement daemon to classify a completed attempt as a missing process. | Fixed. `DurableAgentProcess.InspectForReattach` owns the verdict and re-reads the result after a negative liveness check. A deterministic injected-interleaving test pins the race. |

## Commits

Each improvement is isolated:

- `56d996476` `fix(integration): resolve recovery from Git truth`
- `86c2808d8` `refactor(tasks): remove completed delivery backfill`
- `46dcc1173` `refactor(authority): replace positional run settlement`
- `c525fbd30` `refactor(integration): model accepted deliveries explicitly`
- `d81c88832` `fix(integration): fence remote release delivery`
- `ac8299963` `fix(integration): surface terminal processing errors`
- `96672f01d` `refactor(tasks): split transition side effects`
- `528cf3c20` `fix(runner): preserve terminal result across reattach race`

## Sister-card compatibility

AGT-2425 changes `HumanReviewEscalation` to write its Result scaffold before
the lane mutation. Its backend production diff and new regression test were
applied on top of this branch in a disposable worktree. All 16
`HumanReviewEscalationTests` and `HumanReviewVerdictBackfillTests` passed. The
temporary worktree was removed after verification.

## Verification

- Consolidated transition, integration, authority, escalation, backfill, and
  architecture suite: 200 passed, 0 failed.
- AGT-2425 production diff plus its new escalation regression test on this
  branch: 16 passed, 0 failed in a disposable worktree.
- `git diff --check`: passed.
- The broad `RemoteRunnerEndToEndTests` class also exposed two environment
  failures unrelated to this change:
  `Remote_assigned_ready_epic_completes_planning_with_children_and_no_runner_branch`
  and
  `Remote_runner_counts_unstartable_cli_as_pre_agent_environment_failure`.
  Both fail identically on untouched `origin/main` in a separate worktree, so
  they are confirmed baseline failures rather than regressions in this branch.
- Restore/build retains the existing AngleSharp `NU1902` advisory and existing
  compiler/analyzer warnings. No new compile warning was introduced by the
  changed files.

## Review-fix addendum, 2026-07-30

The Remote Review report classified
`DurableAgentProcessTests.Replacement_daemon_reattaches_live_fake_job_and_reads_its_terminal_result`
as a new product failure against baseline `77fd3f1f1`. The task diff from that
exact baseline contained no changes under `runner/` or `runner.Tests/`, and the
reported assertion was not retained in the review projection. Local repetition
showed the named test passing, but code inspection found the real adjacent race
recorded as CQ-17. The correction is deliberately limited to that recovery
contract.

Verification after `528cf3c20`:

- Durable process and remote restart subset: 11 passed, 0 failed.
- Complete `AgentRunner.Tests` project: 243 passed, 0 failed.
- Transition, integration, authority, escalation, backfill, and architecture
  subset: 87 passed, 0 failed, with the named Windows timing case excluded.
- The reported Windows timing failure in
  `AcceptanceIntegrationRoundTripTests.AcceptHttp_ReturnsWithinTwoSeconds_WhileColdGateFinishesAsGateFailed`
  was not investigated or changed, as directed.
- `git diff --check`: passed.
