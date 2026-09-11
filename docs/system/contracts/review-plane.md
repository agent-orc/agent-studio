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

## Requirement-to-evidence map

| Hand-off requirement | Evidence |
|---|---|
| Acknowledge before evidence (settlement returns before projection runs) | `backend.Tests/RemoteRunnerEndToEndTests.cs`: `Review_host_runs_tool_and_agent_aspect_end_to_end_with_honest_step_location`, `Review_daemon_restart_reports_non_adoptable_process_with_loss_extent_and_retry_reason`, and `Monolith_v1_review_report_after_operator_acceptance_keeps_terminal_lane_and_records_evidence` each assert the settlement response first, then `WaitUntilAsync` on the background-written grade file / timeline entry - the projection is proven to still be pending or racing, not already finished, when the response returns. |
| Replay answers fast, without touching git or the task folder | `backend.Tests/RemoteRunnerEndToEndTests.cs`: `Monolith_v1_review_report_after_operator_acceptance_keeps_terminal_lane_and_records_evidence` asserts the replay response equals the original settlement with `EvidenceProjection` reported as `Duplicate` (the original stays `Queued`), i.e. the replay is a read of already-settled state, not a second write. |
| No duplicate evidence on replay | Same test: only one grade file / evidence write is asserted across both the original report and its replay; the replay path in `V1ReviewPlaneEndpoints` returns before calling `EnqueueEvidenceProjection`. |
| Pending report never counts as an active attempt on re-registration | `runner.Tests/RunnerActiveAttemptReporterTests.cs`: `Durable_terminal_result_is_not_reported_active_even_though_the_process_is_live` (paired with `Live_process_with_no_durable_result_is_reported_active` and `Dead_process_with_no_durable_result_is_not_reported_active` for the other two states). |
| A stale leased attempt at the queue head is requeued, not surfaced as `LeaseExpired` | `backend.Tests/AttemptAuthorityServiceTests.cs`: `ClaimNextReview_requeues_a_stale_lease_at_the_queue_head_and_hands_out_the_next_claimable_attempt` and `ClaimNextReview_hands_out_its_own_requeued_successor_when_nothing_else_is_queued`. |
| A `LeaseExpired` claim response no longer triggers runner re-registration | `runner.Tests/RemoteRunnerDaemonFaultTests.cs`: `Other_claim_conflicts_do_not_trigger_registration_recovery` (parameterized to include `LeaseExpired`); `Unregistered_review_claim_requires_full_registration` pins the one code that still does. |
| Transport-failure retries back off, other retryable failures keep the poll-scaled delay | `runner.Tests/ReviewReportSubmissionPolicyTests.cs`: `RetryDelay_backs_off_exponentially_on_transport_failures`, `RetryDelay_uses_the_poll_scaled_delay_for_non_transport_failures`. |
| Projection throughput/median telemetry is computed correctly | `backend.Tests/RemoteReviewEvidenceProjectionTelemetryTests.cs`: `Summarize_EmptyWindow_ReturnsZeroRateAndNullMedian`, `Summarize_ExcludesSamplesOutsideTheTrailingWindow`, `Summarize_FailedProjectionsCountTowardDurationAndDrainRate`, `Summarize_MedianOfEvenCountAveragesTheTwoMiddleValues`, `RecordCompletion_EvictsOldestSampleBeyondCapacity`. |

## Verification

Foreground `dotnet test` runs for `backend.Tests` and `runner.Tests` filtered
to the tests above are recorded with this delivery (AGT-2762); see the task
result for the pasted summary lines.
