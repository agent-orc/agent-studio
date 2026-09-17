# Review Plane Contract

The Review Plane is the Task Server protocol v1 surface a Review Executor uses
to claim, execute, and report a Remote Review attempt. `contracts/TaskServer.Contracts/ReviewContracts.cs`
pins the wire shapes; the monolith's `backend/Features/Runner/V1ReviewPlaneEndpoints.cs`
(the "Tranche-0 compatibility mount", see [pipeline domain](../domains/pipeline.md))
is today's production implementation, and `task-server/TaskServerEndpoints.cs` +
`TaskServerReviewStore.cs` is the standalone reference implementation for the
target Server/Runner split. Both serve the same contract described here.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/runners/{runnerId}/review-claims` | Claim the next claimable `ReviewAttempt` for this executor identity, or requeue a stale leased one. |
| `POST /api/v1/reviews/attempts/{attemptId}/lease/renew` | Extend the lease on an in-progress attempt. |
| `POST /api/v1/reviews/attempts/{attemptId}/report` | Settle the attempt with a terminal outcome. Two-phase (below). |
| `POST /api/v1/reviews/attempts/{attemptId}/cleanup` | Report workspace teardown after a terminal report. |
| `GET /api/v1/reviews/attempts/{attemptId}` | Read the current attempt state. |

Every write carries `AttemptId`, `Fence`, `AuthorityEpoch`, and `IdempotencyKey`
(`AttemptWriteReference`). The attempt authority (`AttemptAuthorityService` in
the monolith; `review_attempts` in the standalone store) is the single writer;
a request whose fence or epoch does not match the current record fails closed
(`StaleFence`, `AuthorityEpochMismatch`, `Superseded`) rather than silently
applying.

## Two-phase report hand-off (AGT-2762)

`POST .../report` used to settle the attempt authority and project the
evidence (task-folder grade markdown, aspect files, command logs, workspace
evidence commit, timeline entries) into the task folder in the same request.
Under host load the projection half could run past the runner's HTTP timeout;
the Task Server had already settled the attempt (terminal state, real
outcome), but the runner saw a transport failure, kept retrying into the same
slow path, and held its review slot busy for up to tens of minutes per report
(observed: AGT-2712, 16 submission attempts over 1289s; AGT-2724; QS-84).

The endpoint now splits the request into two phases:

1. **Settle (synchronous, durable).** `AttemptAuthorityService.SettleReview`
   records the fenced outcome and the raw report payload, and the lane
   transition (Auto Review to Human Review, or Escalated on a drained
   infrastructure-retry budget) runs inline. The response returns as soon as
   this completes, regardless of host load.
2. **Project evidence (asynchronous, best-effort ordering).** The endpoint
   enqueues a `RemoteReviewEvidenceProjectionRequest` onto
   `IRemoteReviewEvidenceProjectionQueue` (`backend/Features/Runner/RemoteReviewEvidenceProjectionQueue.cs`).
   `RemoteReviewEvidenceProjectionWorker`, a `BackgroundService`, drains the
   queue: it writes the grade markdown and artifact files
   (`RemoteReviewReportEvidence.WriteAsync`) and runs
   `RemotePipelineReviewEvidenceProjector`. A projection that fails with an
   `IOException` or `UnauthorizedAccessException` re-enqueues itself with
   exponential backoff (`RemoteReviewEvidenceProjectionWorker.RetryDelay`: 5s
   base, doubling, capped at 2 minutes) up to 5 attempts, then gives up and
   logs `remote-review-evidence-projection-exhausted`. A projection over 30s
   logs `remote-review-evidence-projection-slow`.

The enqueue happens after the lane-transition write on every response path,
including its error returns, so the background worker's own fresh
task-folder lookup never races the same request's synchronous lane move.

The response's `ReviewReportDto.EvidenceProjection` field tells the runner
which phase it is looking at: `"queued"` (evidence projection was hand off to
the background worker) or `"duplicate"` (see below). A `200` response with
`EvidenceProjection: queued` means the settlement is durable even though the
task folder has not been written yet; the runner must not resubmit that
report.

## Idempotent replay

A report is keyed by `IdempotencyKey`. If a retry (same executor, same
delivery) replays a key the authority already has recorded as settled, the
authority returns `AttemptWriteStatus.Duplicate` before doing anything else.
The endpoint answers `200` with the stored settlement summary and
`EvidenceProjection: "duplicate"` without touching Git or the task folder -
the original delivery's projection already ran or is already queued, and
redoing it would repeat the same I/O for no new information. The runner
treats `Duplicate` exactly like a fresh acceptance: the review slot is freed.

## Claim poll semantics: stale-lease requeue

`ReviewAttemptTaskLifecycleService.ClaimNextReview` used to return
`LeaseExpired` when the queue head was a leased-but-expired attempt. A
polling executor whose own earlier claim was the dead lease then read
`LeaseExpired` as "this identity lost claim authority" and re-registered from
scratch; combined with report settlement already having moved the attempt to
a terminal state, the re-registration's active-attempt list was rejected as
stale, and the daemon looped between poll and full re-registration without
ever claiming (AGT-2760).

`ClaimNextReview` now resolves a stale-leased attempt internally instead of
surfacing it: it terminalizes the expired attempt
(`Failed` / `InfrastructureFailure` / `LeaseExpired`) and mints an immediate
successor attempt for the same immutable subject with no frozen plan (the
existing null-plan fallback in `ToSubject` rebuilds one at claim time), then
continues the loop to hand out the next claimable attempt in the same
request. The loop is bounded by the review-attempt count at entry - each
iteration either claims and returns, or requeues exactly one stale-leased
attempt, so the claimable set strictly shrinks. Settlement is fenced, so an
eventual late report from the executor that held the dead lease resolves
`Superseded`; double execution costs wasted work, not correctness.

A claim response can no longer carry `LeaseExpired`. On the runner,
`ReviewClaimRegistrationRecovery.IsRequired` now triggers re-registration only
on `review-executor-not-registered` (this daemon identity itself lost
registration), not on a 409 that no longer occurs.

## Runner-side changes

- `TaskServerClient.ReportReviewAsync` uses a fixed 10-second timeout
  (`ReviewReportAckTimeout`) for the report call specifically, distinct from
  the configured `ServerRequestTimeoutSeconds` used for every other request.
  Ten seconds comfortably covers the acknowledged (settlement-only) path even
  under host load, since evidence projection no longer blocks the response.
- `ReviewReportSubmissionPolicy.RetryDelay` backs off exponentially on a
  transport failure (timeout, connection reset - the fault class a tunnel
  outage or an overloaded host produces): 1s base, doubling, capped at 60s.
  A non-transport retryable failure (5xx, 429, 408) keeps the existing
  poll-interval-scaled delay, since those already carry an authoritative
  response.
- `RunnerActiveAttemptReporter` no longer reports a review slot as active in
  the re-registration payload (`ActiveAttempts`) once verification has
  produced a durable terminal result and only report delivery remains -
  delivering that report is a fenced, idempotent replay against the attempt
  authority and needs no re-adoption. Reporting it as active is what made the
  server reject the next re-registration as "claim authority lost" once the
  attempt had already settled server-side. A coding slot's reporting rule is
  unchanged.

## Telemetry

- The report endpoint logs `review-report-accepted` /
  `review-report-duplicate` with the round-trip time
  (`roundTripMs`, measured up to the settlement response, not including
  projection) and `evidenceProjection=queued|duplicate`.
- The projection worker logs `remote-review-evidence-projection-finished`
  with `queueWaitMs` (enqueue to pickup) and `projectionMs` (pickup to
  write), and a `remote-review-evidence-projection-slow` warning above 30s.
- `RemoteReviewEvidenceProjectionTelemetry` keeps a bounded rolling window
  (500 samples) of completed projections; `Summarize` (pure, matrix-tested)
  derives drain rate per minute and median duration over a trailing window,
  the same shape as `AutoReviewQueueTelemetry`, surfaced through Execution
  Hosts host telemetry.

## Auto Review postprocessing wait for a canonical review executor (AGT-2842)

A card whose task is already tracked by the attempt authority (any RunAttempt
or ReviewAttempt on record - `AttemptAuthorityProjection.LegacyTask == false`)
belongs to this canonical Review Plane, not to the legacy in-process review
loop. `ReviewDecisionOrchestrator.EnumeratePending` skips such a card with one
of the four stable canonical-wait reasons on `PostProcessingCardResult` (see
AGT-2860 below); the
card stays in `4-auto-review` and is re-driven with backoff by
`AutoReviewPostProcessingWorker` until the fenced ReviewAttempt executor claims
and settles it through `ClaimNextReview` / `.../report` above. That skip
decision is unconditional and correct regardless of executor health - only the
Review Plane, never the legacy loop, may act on a canonical task.

Before AGT-2842 the re-drive backoff was blind to executor health: every
deferral doubled from 30s up to the generic 10-minute cap
(`AutoReviewPostProcessingWorker.DeferralRetryMaxDelay`) even while a
registered review executor was active and idle, so a healthy wait looked
identical to "nobody can ever claim this card" and the board's queue position
(`AutoReviewPostProcessingQueue.PositionOf`) read as empty for the entire gap
between passes (`agent-runner-01-review` registered, `review-slot-hygiene
total=0`, 21 cards sitting in `4-auto-review` with no visible queue reason).

`V1ReviewExecutorRegistry.EvaluateReviewExecutorAvailability` answers "is there
a review-role identity this backend can expect to claim a canonical
ReviewAttempt soon" as a coarse, immediate check: registered with the
`review-executor` capability, a heartbeat inside a generous 2-minute budget
(`ReviewExecutorHeartbeatStaleAfter` - the same convention
`RemoteQueueStarvationPolicy` uses for a live coding runner), and not currently
paused by a whole-host capability drain. It deliberately does not require a
matching claim/instance fence, a specific advertised capability key (the
review role advertises a different capability set than coding, so reusing
`EvaluateCodingAdmission`'s per-key check would always read "unavailable"), or
a freshness window tighter than the review daemon's own heartbeat cadence -
each of those would fail-closed a perfectly healthy executor.
`AutoReviewPostProcessingWorker.ScheduleDeferralRetry` consults it only for a
canonical-review wait (`PostProcessingCardResult.IsCanonicalReviewWait`) and,
when a review executor is registered,
caps the backoff at `CanonicalReviewExecutorRegisteredMaxDelay` (60s) instead
of letting it grow to the generic 10-minute cap, and records the availability
detail as an `AutoReviewQueueWaitState`. `TaskLiveStatusProjection` reads that
wait state whenever `PositionOf` has no real slot position and reports the
task's `liveStatus.queue.reason`, e.g. `waiting for review executor:
agent-runner-01-review is registered and active`, instead of an unexplained
empty queue.

## Restart-safe resume of the post-review delivery sequence (AGT-2860)

The report endpoint settles the ReviewAttempt, decides the delivery gate from
the report's aspect verdicts, integrates, and only then moves the card out of
`4-auto-review`. All of that lives inside one HTTP request. The Stable backend
restarted at 08:48-08:55 on 17.09.2026 and left two shapes behind:

- **AGT-2855** - review Pass at 08:39 (`review_e002948c`), integration queued
  behind AGT-2854's running gate, gate killed by the restart, post-processing
  deferrals exhausted at 09:00 (`attempts=5`). The card sat in `4-auto-review`
  with `integration: pending` and a terminal Pass nothing would ever act on; an
  operator had to create a second 45-minute review of an already passed subject
  just to start `remote-delivery-integration`.
- **AGT-2854** - the un-gated merge was published by the next green gate
  (AGT-2814, 10:29), the integration record read `integrated / anchor-ancestor`,
  and the card still never left the lane because its completion transition
  belonged to the killed gate's own post-processing.

Three things close that gap.

**A durable resume point.** `RemoteDeliverySettlementStore` writes
`logs/remote-delivery-settlement.json` beside the card before the first side
effect of the sequence, carrying the ReviewAttempt id, the delivery gate verdict
(`ShouldIntegrate` plus its build/test gate class and reason), the resolved
integration branch/strategy/pipeline type, and a monotonic
`Stage` of `IntegrationPending -> IntegrationSettled -> LaneSettled`. The gate
verdict has to be written down because the aspect verdicts that decided it exist
only in the report payload - integrating on the strength of `Pass` alone would
admit exactly the delivery the gate exists to refuse. The record is fenced by
`ReviewAttemptId`, so a requeued and re-reviewed card never replays an older
generation's decision.

**A pure resume policy.** `AutoReviewResumePolicy.Decide` answers one card from
primitive facts: lane, fixture flag, current ReviewAttempt state and outcome,
whether the delivery is merged (`IntegrationStatuses.IsMerged`), and the
settlement stage. Ancestry outranks the sidecar - a delivery the integration
branch already contains yields `CompleteTransition` and is never merged again,
whatever a record written before the merge says. A passed but unmerged delivery
with no usable record yields `None`, not a guess.

The policy also keeps out of AGT-2849's way: while an integration-gate journal
(`post-steps/integration-gate.inflight.json`) is still open for the card, a live
gate owns it or boot recovery has not judged its merge yet, so ancestry proves
nothing and the resume reports `integration-gate-recovery-pending` instead of
acting.

**One bounded coordinator.** `AutoReviewDeliveryResumeService` runs the decision
at boot (from `AutoReviewPostProcessingRecoveryService`, before the re-enqueue
scan) and on every deferral pass of `AutoReviewPostProcessingWorker`. It never
creates a ReviewAttempt. `StartIntegration` re-enters
`RemoteDeliveryIntegrationCoordinator` (which coalesces a replay of the same
delivery key, and whose merge runner answers `AlreadyMerged` for a delivery the
branch already holds); both actions then finish the normal
`4-auto-review -> 5-human-review` transition with the same park verdict the
report endpoint writes, after which the acceptance rail carries the integrated
card into the completed lane on its ordinary schedule.

The deferral budget no longer decides a card's fate.
`AutoReviewPostProcessingWorker.ScheduleDeferralRetry` exhausts only while the
blocking condition is genuinely unresolved; a registered review executor or a
terminal-Pass wait resets the counter instead, so
`auto-review-postprocessing-deferral-exhausted` can never be logged for a card
that already passed review. AGT-2842's growing 30s-to-10-minute backoff is
unchanged for the genuinely idle-executor case.

The single reason token was split so the log names what is actually missing:

| Reason | What is missing |
|---|---|
| `awaiting-review-executor-registration` | No review executor is registered at all. |
| `awaiting-canonical-review-verdict` | An executor is registered; the attempt has not settled. |
| `awaiting-delivery-integration` | The review passed; `remote-delivery-integration` has not started. |
| `awaiting-integration-completion` | The delivery is on the branch; the lane transition is pending. |

`PostProcessingCardResult.IsCanonicalReviewWait` keeps all four inside AGT-2842's
tightened backoff and inside `TaskLiveStatusProjection`'s named wait;
`IsDeliveryResumeWait` marks the two a terminal Pass has already earned. The
classifier in `ReviewDecisionOrchestrator.ClassifyCanonicalReviewWait` chooses
between the delivery reasons from the attempt and the sidecar; the worker's
`RefineCanonicalWaitReason` separates "nobody registered" from "registered and
busy" using `EvaluateReviewExecutorAvailability`, because only the registry
knows that.

## Requirement-to-evidence map

| Hand-off requirement | Evidence |
|---|---|
| A registered, non-drained review executor is recognized immediately, without requiring a capability key or freshness window the review role never advertises | `backend.Tests/V1ReviewExecutorAvailabilityTests.cs`: `Freshly_registered_idle_executor_is_immediately_available`, `A_capability_key_the_review_role_never_advertises_does_not_block_availability`, `Stale_heartbeat_past_the_staleness_budget_is_registered_but_not_available`, `Whole_host_capability_drain_is_registered_but_not_available`. |
| The canonical-review-executor deferral backoff is capped at 60s while an executor is registered, and keeps the generic 10-minute-capped schedule otherwise | `backend.Tests/AutoReviewPostProcessingWorkerTests.cs`: `ResolveReviewExecutorAvailability_RegisteredExecutor_CapsTheEffectiveDelayAtSixtySeconds`, `ResolveReviewExecutorAvailability_NoRegistry_ReturnsNullAndKeepsTheGenericCap`, `ResolveReviewExecutorAvailability_UnrelatedReason_NeverConsultsTheRegistry`. |
| The wait reason is exposed on the card instead of an empty queue | `backend.Tests/AutoReviewPostProcessingWorkerTests.cs`: `ApplyOutcome_CanonicalReviewExecutorDeferral_ExposesTheWaitReasonOnTheQueue`, `ApplyOutcome_CanonicalReviewExecutorDeferral_WithoutARegisteredExecutor_KeepsTheGenericBackoff`; `frontend/src/app/components/task-live-status/task-live-status.component.spec.ts`: `names the wait reason instead of an empty queue when no slot position is known`. |
| Acknowledge before evidence (settlement returns before projection runs) | `backend.Tests/RemoteRunnerEndToEndTests.cs`: `Review_host_runs_tool_and_agent_aspect_end_to_end_with_honest_step_location`, `Review_daemon_restart_reports_non_adoptable_process_with_loss_extent_and_retry_reason`, and `Monolith_v1_review_report_after_operator_acceptance_keeps_terminal_lane_and_records_evidence` each assert the settlement response first, then `WaitUntilAsync` on the background-written grade file / timeline entry - the projection is proven to still be pending or racing, not already finished, when the response returns. |
| Replay answers fast, without touching git or the task folder | `backend.Tests/RemoteRunnerEndToEndTests.cs`: `Monolith_v1_review_report_after_operator_acceptance_keeps_terminal_lane_and_records_evidence` asserts the replay response equals the original settlement with `EvidenceProjection` reported as `Duplicate` (the original stays `Queued`), i.e. the replay is a read of already-settled state, not a second write. |
| No duplicate evidence on replay | Same test: only one grade file / evidence write is asserted across both the original report and its replay; the replay path in `V1ReviewPlaneEndpoints` returns before calling `EnqueueEvidenceProjection`. |
| A review Pass recorded before a restart reaches integration afterwards, without a new review | `backend.Tests/AutoReviewRestartDrillTests.cs`: `Restart_before_integration_starts_integrates_afterwards_without_a_new_review` (asserts the delivery reaches `develop`, the card reaches Human Review, and the task still has exactly one ReviewAttempt). |
| A gate killed after a later gate published its merge completes on the next pass without re-merging | `backend.Tests/AutoReviewRestartDrillTests.cs`: `A_delivery_published_by_a_later_gate_completes_on_the_next_pass_without_remerging` (develop's tip is unchanged across the resume). |
| `deferral-exhausted` is never logged for a card with a terminal Pass attempt | `backend.Tests/AutoReviewPostProcessingWorkerTests.cs`: `ApplyOutcome_DeliveryResumeWait_NeverExhaustsTheDeferralBudget`, `ApplyOutcome_IntegrationCompletionWait_NeverExhaustsTheDeferralBudget`, `ApplyOutcome_RegisteredExecutor_ResetsTheBudgetInsteadOfExhaustingIt`, with `ApplyOutcome_IdleExecutorWait_StillExhaustsTheDeferralBudget` as the counter-example that keeps AGT-2842's backoff. |
| The resume never integrates a delivery it cannot prove the gate admitted, and never merges twice | `backend.Tests/AutoReviewRestartDrillTests.cs`: `A_passed_delivery_without_a_settlement_record_is_not_integrated_on_trust`, `Running_the_sweep_twice_changes_nothing_the_second_time`, `A_card_whose_review_has_not_settled_is_left_to_its_executor`; `backend.Tests/AutoReviewResumePolicyTests.cs` covers the full decision matrix. |
| The deferral reason names what is actually missing | `backend.Tests/AutoReviewPostProcessingWorkerTests.cs`: `RefineCanonicalWaitReason_NamesTheMissingRegistrationInsteadOfTheBusyExecutor`. |
| The resume point is written on the happy path too, so any restart inside the sequence has something to resume from | `backend.Tests/RemoteRunnerEndToEndTests.cs`: `Monolith_v1_green_remote_delivery_integrates_before_human_review` asserts the sidecar reaches `LaneSettled`; `backend.Tests/RemoteDeliverySettlementStoreTests.cs` covers the store's monotonic stage and attempt fencing. |
| Pending report never counts as an active attempt on re-registration | `runner.Tests/RunnerActiveAttemptReporterTests.cs`: `Durable_terminal_result_is_not_reported_active_even_though_the_process_is_live` (paired with `Live_process_with_no_durable_result_is_reported_active` and `Dead_process_with_no_durable_result_is_not_reported_active` for the other two states). |
| A stale leased attempt at the queue head is requeued, not surfaced as `LeaseExpired` | `backend.Tests/AttemptAuthorityServiceTests.cs`: `ClaimNextReview_requeues_a_stale_lease_at_the_queue_head_and_hands_out_the_next_claimable_attempt` and `ClaimNextReview_hands_out_its_own_requeued_successor_when_nothing_else_is_queued`. |
| A `LeaseExpired` claim response no longer triggers runner re-registration | `runner.Tests/RemoteRunnerDaemonFaultTests.cs`: `Other_claim_conflicts_do_not_trigger_registration_recovery` (parameterized to include `LeaseExpired`); `Unregistered_review_claim_requires_full_registration` pins the one code that still does. |
| Transport-failure retries back off, other retryable failures keep the poll-scaled delay | `runner.Tests/ReviewReportSubmissionPolicyTests.cs`: `RetryDelay_backs_off_exponentially_on_transport_failures`, `RetryDelay_uses_the_poll_scaled_delay_for_non_transport_failures`. |
| Projection throughput/median telemetry is computed correctly | `backend.Tests/RemoteReviewEvidenceProjectionTelemetryTests.cs`: `Summarize_EmptyWindow_ReturnsZeroRateAndNullMedian`, `Summarize_ExcludesSamplesOutsideTheTrailingWindow`, `Summarize_FailedProjectionsCountTowardDurationAndDrainRate`, `Summarize_MedianOfEvenCountAveragesTheTwoMiddleValues`, `RecordCompletion_EvictsOldestSampleBeyondCapacity`. |

## Verification

Foreground `dotnet test` runs for `backend.Tests` and `runner.Tests` filtered
to the tests above are recorded with this delivery (AGT-2762); see the task
result for the pasted summary lines.
