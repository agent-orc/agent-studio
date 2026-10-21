namespace AgentStudio.Shared;

/// <summary>
/// The closed set of timeline event kinds. Persisted as the <c>kind</c>
/// string in <c>timeline.jsonl</c>. When a new kind lands, add it here,
/// add a writer that emits it, and add a test that asserts the wire shape.
///
/// The list is closed by convention: <see cref="TimelineEvent.Kind"/> is a
/// string on disk so the file remains greppable and forward-compatible
/// with a reader that runs after a kind has been removed, but every
/// producer in this repo writes one of these constants.
/// </summary>
public static class TimelineEventKinds
{
    public const string ReviewPriorityChanged = "review_priority_changed";
    /// <summary>Task prompt was created on disk.</summary>
    public const string PromptCreated = "prompt_created";
    /// <summary>One CLI invocation started (start / continue / recovery).</summary>
    public const string AgentRunStarted = "agent_run_started";
    /// <summary>A run switched to its configured fallback because primary quota was exhausted.</summary>
    public const string QuotaFallbackActivated = "quota_fallback_activated";
    /// <summary>
    /// AGT-2716: the orchestrator rewrote a non-explicit task's stored model to
    /// a newer, catalog-safe replacement at run admission (e.g. a superseded
    /// Opus/Sonnet generation to the family's current member). Explicit pins
    /// are never rewritten this way. <see cref="TimelineEvent.Details"/> carries
    /// <c>fromModel</c>, <c>toModel</c>, <c>family</c>, and the migration
    /// catalog version that authorized it.
    /// </summary>
    public const string ModelMigrated = "model_migrated";
    /// <summary>
    /// AGT-2055: the algorithmic pre-launch quota check made a load-steering
    /// decision for a card before any launch was attempted - switch model,
    /// throttle, or wait for the next reset. <see cref="TimelineEvent.Details"/>
    /// carries the burn-rate/projection numbers so the load-distribution view
    /// has a stable data source.
    /// </summary>
    public const string QuotaAdmissionDecision = "quota_admission_decision";
    /// <summary>Sustained host CPU saturation deferred a new runner slot.</summary>
    public const string LoadThrottleDecision = "load_throttle_decision";
    /// <summary>
    /// ADR-0052: the parallel pick-gate admitted this task into a runner slot.
    /// <see cref="TimelineEvent.Summary"/> carries the occupancy
    /// ("slot N/M") and the <c>ParallelSlotPolicy</c> rationale, so the
    /// Timeline shows the pick decision + slot belegung the moment a task is
    /// picked. At MaxParallelism == 1 this is the single sequential slot.
    /// </summary>
    public const string RunnerSlotAdmission = "runner_slot_admission";
    public const string RunContinuedAfterRestart = "run_continued_after_restart";
    public const string RunLostAcrossRestart = "run_lost_across_restart";
    /// <summary>
    /// A local coding pickup could not prepare its isolated worktree. Details
    /// carry the stable failure code, exact Git message, canonical path, and
    /// bounded retry count.
    /// </summary>
    public const string WorktreePreparationFailed = "worktree_preparation_failed";
    /// <summary>
    /// ADR-0052 multi-system follow-up: the Task Server granted, rejected, or
    /// released the fenced integration lease that serializes direct merges into
    /// a project's integration branch across runner machines.
    /// </summary>
    public const string IntegrationLease = "integration_lease";
    /// <summary>The CLI invocation ended; <see cref="TimelineEvent.Summary"/> carries the outcome.</summary>
    public const string AgentRunFinished = "agent_run_finished";
    /// <summary>An operator move fenced a remote attempt while retaining its work as reference.</summary>
    public const string RunAttemptRevoked = "run_attempt_revoked";
    /// <summary>A remote result awaits an operator decision because its brief is older.</summary>
    public const string OlderBriefDeliveryOffered = "older_brief_delivery_offered";
    /// <summary>A result producer replaced status.md after preserving the prior version.</summary>
    public const string ResultReplaced = "result_replaced";
    /// <summary>
    /// AGT-2850: a delivered remote result was acknowledged before its Result
    /// summary existed, because summary generation was refused or outran the
    /// bounded acknowledgement budget (host load throttle, CLI unavailable,
    /// provider quota). The run is NOT lost - the artefacts are persisted and
    /// the runner tore its worktree down normally; only the summary is owed.
    /// <see cref="TimelineEvent.Summary"/> reads "Summary pending (&lt;reason&gt;)"
    /// and <see cref="TimelineEvent.Details"/> carry the wire status and the
    /// time the queued retry comes due.
    /// </summary>
    public const string ResultSummaryPending = "result_summary_pending";
    /// <summary>
    /// Code delivery completed, but one or more bounded result artifacts were
    /// skipped or refused. Details carry the typed outcome, path, size, and
    /// advertised limit; this event never reopens the coding run.
    /// </summary>
    public const string ResultArtifactsPartial = "result_artifacts_partial";
    /// <summary>A Progress requeue was replaced by forward recovery of a completed immutable result.</summary>
    public const string SettledRunRecovered = "settled_run_recovered";
    /// <summary>A pipeline pre-step started (ADR-0045).</summary>
    public const string PreStepStarted = "pre_step_started";
    /// <summary>A pipeline pre-step finished.</summary>
    public const string PreStepFinished = "pre_step_finished";
    /// <summary>A pipeline post-step started (the four aspect runs in the standard pipeline).</summary>
    public const string PostStepStarted = "post_step_started";
    /// <summary>A pipeline post-step finished.</summary>
    public const string PostStepFinished = "post_step_finished";
    /// <summary>A gate reused an exact-SHA verdict; details name the original evidence and time.</summary>
    public const string GateVerdictCacheHit = "gate_verdict_cache_hit";
    /// <summary>
    /// The orchestrator could not decide unattended and asked a human to
    /// take the wheel. The original card is escalated to
    /// <c>5e-escalated</c> via the human-review escalation funnel (the
    /// retired <c>1b-needs-human-review</c> lane previously held these); no
    /// wrapper card is spawned (ADR-0049).
    /// </summary>
    public const string OrchestratorEscalated = "orchestrator_escalated";
    /// <summary>The orchestrator emitted a STEER block (see OrchestratorReplyParser).</summary>
    public const string OrchestratorSteered = "orchestrator_steered";
    /// <summary>
    /// Run-Liveness Slice B (concept Rule 2): an unanswered steer / NeedsInput
    /// question hit its bounded timeout and the runner resolved it without a
    /// human - either auto-answered from the task context (the branch-state
    /// check) or routed to a blocked escalation. <see cref="TimelineEvent.Summary"/>
    /// carries the decision and the answer given; <see cref="TimelineEvent.Details"/>
    /// carries the reason code, how long it waited, and the timeout. Emitted so a
    /// card's history shows why the wait ended instead of an invisible 5-hour hang
    /// (belegt 2062/2067/2068, 2026-07-10).
    /// </summary>
    public const string SteerTimeoutResolved = "steer_timeout_resolved";
    /// <summary>
    /// The orchestrator's auto-review pass judged the run genuinely done
    /// and promoted it forward (to human review). The positive terminal of
    /// the completion loop (ADR-0049 / ASS-566): the counterpart to
    /// <see cref="QualityLoopReopened"/> ("go again") and
    /// <see cref="OrchestratorEscalated"/> ("hand to a human"). Emitting it
    /// lets the Overview/Timeline surfaces show the verdict that closed the
    /// loop rather than re-deriving it from the decision journal.
    /// </summary>
    public const string OrchestratorVerdictAccepted = "orchestrator_verdict_accepted";
    /// <summary>
    /// A downstream quality check (auto-review, completed-lane audit)
    /// disagreed with a previous <see cref="AgentRunFinished"/>
    /// "claimed=done" verdict and reissued the run.
    /// </summary>
    public const string QualityLoopReopened = "quality_loop_reopened";
    /// <summary>A per-card budget disposition, including its open aspects and round counts.</summary>
    public const string CardRoundBudgetDisposition = "card_round_budget_disposition";
    /// <summary>A human review verdict was recorded.</summary>
    public const string HumanReviewDecided = "human_review_decided";
    /// <summary>
    /// Human acceptance started the transactional delivery integration while
    /// the task remained in Human Review.
    /// </summary>
    public const string IntegrationStarted = "integration_started";
    /// <summary>
    /// Delivery integration reached Merged/AlreadyMerged, either immediately
    /// before Human Review or through the acceptance backstop.
    /// </summary>
    public const string IntegrationSucceeded = "integration_succeeded";
    /// <summary>
    /// Delivery integration failed. The pipeline record carries the concrete
    /// outcome and evidence; Human Review acceptance may retry it.
    /// </summary>
    public const string IntegrationFailed = "integration_failed";
    /// <summary>
    /// An operator explicitly completed acceptance without integration. The
    /// event details and status.md retain the one-shot override reason.
    /// </summary>
    public const string IntegrationOverridden = "integration_overridden";
    /// <summary>Written exception to the current-brief content acceptance gate.</summary>
    public const string CompletionContentOverridden = "completion_content_overridden";
    /// <summary>
    /// AGT-3076: an operator override accepted a card whose RunAttempt the
    /// attempt authority store no longer holds. Details carry the authority
    /// epoch and the delivery evidence (integration status, ref, SHA, commits)
    /// the acceptance rests on.
    /// </summary>
    public const string AcceptedWithoutRunAttempt = "accepted_without_run_attempt";
    /// <summary>
    /// AGT-2793: <c>BranchReclaimTriggerService</c> deleted one or more of this
    /// task's remote git refs (task/runner/delivery) after a lifecycle
    /// transition (integration or archive). Details carry the deleted ref
    /// names, SHAs, and reasons; the full evidence row also lands in the
    /// project's <c>reports/git-branch-reclaim.jsonl</c>.
    /// </summary>
    public const string BranchesReclaimed = "branches_reclaimed";
    /// <summary>
    /// A human deliberately moved a task out of human review or escalation,
    /// opening a fresh review-attempt epoch. Details carry the operator reason,
    /// new epoch, and rotated artefact count.
    /// </summary>
    public const string OperatorRequeued = "operator_requeued";
    /// <summary>An operator added, removed, or re-pointed a waits-on edge.</summary>
    public const string DependencyChanged = "dependency_changed";
    /// <summary>
    /// A fenced remote review report arrived after an operator had already
    /// accepted or archived the task. The report remains evidence, but cannot
    /// reopen the terminal lane.
    /// </summary>
    public const string PostAcceptanceReviewReportRecorded = "post_acceptance_review_report_recorded";
    /// <summary>
    /// An open Remote ReviewAttempt lost authority because its owning task left
    /// Auto Review. Details identify the terminal lane and whether the cleanup
    /// came from the live transition, claim guard, or boot sweep.
    /// </summary>
    public const string ReviewAttemptSuperseded = "review_attempt_superseded";
    /// <summary>
    /// AGT-2709: the explicit content-release flag consumed by release-gated
    /// <c>dependsOn</c> edges was set or withdrawn. Terminal completion never
    /// emits this - only the operator affordance or a dedicated release step
    /// does, which is why the decision needs its own auditable row.
    /// <see cref="TimelineEvent.Actor"/> carries who decided;
    /// <see cref="TimelineEvent.Details"/> carries the new flag value and the
    /// release-gated dependents the decision unblocks.
    /// </summary>
    public const string TaskReleased = "task_released";
    /// <summary>The task's lane changed (any move).</summary>
    public const string LaneChanged = "lane_changed";
    /// <summary>
    /// Another card was folded into this one via the consolidation API
    /// (sibling task). The merge endpoint mirrors the secondary's
    /// timeline events into the primary's ledger and emits one
    /// <c>merged_in</c> summary entry alongside.
    /// </summary>
    public const string MergedIn = "merged_in";
    /// <summary>
    /// A read-only task (planning / research) left a non-empty working-tree
    /// diff at run end. Read-only modes are supposed to produce only a report
    /// and touch no source; the runner reports a dirty tree as a hard
    /// containment violation rather than auto-reverting it, so the operator
    /// decides what to do with the stray changes. <see cref="TimelineEvent.Summary"/>
    /// carries the changed-file count; <see cref="TimelineEvent.Details"/> the
    /// mode and the (capped) file list. Emitted by the runner at run-finish.
    /// </summary>
    public const string ReadOnlyContainmentViolation = "read_only_containment_violation";
    /// <summary>
    /// The read-only execution-context snapshot for one run was captured
    /// (ASS-1739 / T1a): the context sources the CLI loaded beyond the prompt -
    /// memory / session paths, instruction-file chain, global config, MCP
    /// servers, plus model / permission mode / cwd. <see cref="TimelineEvent.Summary"/>
    /// carries a one-line count ("N sources, model X"); the canonical record is
    /// the run's <see cref="SessionEvent.ExecutionContext"/>, surfaced in full
    /// by the run-detail panel. Emitted by the runner at run-finish.
    /// </summary>
    public const string ExecutionContext = "execution_context";
    /// <summary>
    /// The task-spawner post-step judged this task's change set relevant to
    /// another project and created a follow-up card there (AGT-2028). Emitted on
    /// the SOURCE task so its history shows the hand-off ("Spawned WEB-123 in
    /// Website"); <see cref="TimelineEvent.Summary"/> reads the spawned key +
    /// target project and <see cref="TimelineEvent.Details"/> carries
    /// <c>targetProject</c> / <c>targetKey</c> / <c>targetJobId</c> / <c>reason</c>.
    /// The spawned card gets its own <see cref="PromptCreated"/> entry and a
    /// <c>relatedTo</c> reference back to this task. Reporting-only: the spawn
    /// never changes the source task's lane decision.
    /// </summary>
    public const string TaskSpawned = "task_spawned";
    /// <summary>A failed pipeline boundary raised or attached to an orchestrator follow-up task.</summary>
    public const string FailureInterventionRaised = "failure_intervention_raised";
    /// <summary>
    /// The task was completed out-of-band (operator chat, external agent, a
    /// remote host) and reconciled through
    /// <c>POST /api/tasks/{id}/external-completion</c> instead of a runner run.
    /// <see cref="TimelineEvent.Summary"/> reads "Completed externally by
    /// &lt;source&gt;"; <see cref="TimelineEvent.Details"/> carries the source and
    /// the target lane, and <see cref="TimelineEvent.PayloadRef"/> points at
    /// <c>results/deliverables.md</c>. This is the first-class ingest path for
    /// externally produced results described in
    /// <c>docs/concepts/out-of-band-task-completion.md</c> §3, so a card's
    /// history shows the external hand-off rather than ending in a corpse.
    /// </summary>
    public const string ExternalCompletion = "external_completion";
    /// <summary>
    /// AGT-2220: a completion claim was refused because its commits could not be
    /// proven against the target repository. Recorded so a refused phantom is
    /// visible in the card's history rather than silently absent.
    /// </summary>
    public const string DeliveryUnverified = "delivery_unverified";
    /// <summary>
    /// AGT-2202 compatibility event for a non-transactional caller that observed
    /// accepted work outside the integration branch. Transactional acceptance
    /// instead emits <see cref="IntegrationStarted"/>,
    /// <see cref="IntegrationSucceeded"/>, <see cref="IntegrationFailed"/>,
    /// or <see cref="IntegrationOverridden"/>
    /// while the task remains in Human Review until success.
    /// </summary>
    public const string IntegrationPendingWarning = "integration_pending_warning";
    /// <summary>
    /// Delivery integration queued a focused recovery steer round. The event
    /// may represent an operator action for a legacy conflict or the bounded
    /// automatic round used when a mechanical rebase cannot retain unambiguous
    /// SHA attribution. <see cref="TimelineEvent.Details"/> identifies whether
    /// the round was automatic and records its fenced delivery-chain identity.
    /// </summary>
    public const string IntegrationRecoveryQueued = "integration_recovery_queued";
    /// <summary>
    /// AGT-2861: a remote run ended without a recognized terminal outcome (the
    /// observed case is a hit run timeout) but its worktree had been salvaged,
    /// so the platform started one bounded automatic round to finish the
    /// salvaged delivery instead of parking it. <see cref="TimelineEvent.Details"/>
    /// carries the salvage ref and SHA, the round number and its maximum, the
    /// review-attempt epoch the budget is counted in, and the
    /// <c>run-timeout-with-salvage</c> reason.
    /// </summary>
    public const string ContinuationRoundStarted = "continuation_round_started";
    /// <summary>
    /// AGT-3056: a review fix round recorded the delivery the review graded as
    /// the base of its next worktree. <see cref="TimelineEvent.Details"/>
    /// carries the fenced result <c>ref</c> and <c>sha</c>, the
    /// <c>review-fix-round</c> reason, the <c>reviewAttemptId</c> and the
    /// entry point (<c>source</c>). Not a round of its own: the round budget
    /// counts the round's receipt, never this row.
    /// </summary>
    public const string ContinuationBaseRecorded = "continuation_base_recorded";
    /// <summary>
    /// AGT-3011: an operator sweep (<c>fix-rounds</c>, <c>gate-triage</c>,
    /// <c>salvage</c>) opened a round on this card. The receipt names the sweep
    /// and the exact failure (<c>subjectKey</c>) so the sweep never opens a
    /// second round for the same failure, across ticks and restarts, and the
    /// round budget it charged (<c>roundsUsed</c>/<c>roundsAllowed</c>).
    /// </summary>
    public const string OperatorSweepRoundStarted = "operator_sweep_round_started";
    /// <summary>
    /// A provider refused the model request and the platform continued a
    /// salvaged run on the explicitly declared sibling model.
    /// </summary>
    public const string ProviderRejectionContinuationStarted = "provider_rejection_continuation_started";
    /// <summary>
    /// The platform-owned acceptance rail accepted, requeued, or boundedly
    /// escalated this card without a session-bound orchestrator tick.
    /// </summary>
    public const string AcceptanceRailActed = "acceptance_rail_acted";
    /// <summary>
    /// AGT-2824: the bounded gate-environment ladder replayed the integration
    /// for an unchanged delivery SHA, reusing its passed review instead of
    /// spending a new review round. <see cref="TimelineEvent.Details"/> carries
    /// the delivery SHA, the rung, and whether a sweep or an operator drove it.
    /// </summary>
    public const string IntegrationGateEnvironmentRetried = "integration_gate_environment_retried";
    /// <summary>
    /// AGT-2824: the bounded gate-environment ladder is spent. The card keeps a
    /// parked reason naming the environment failure until an operator retries
    /// the integration explicitly.
    /// </summary>
    public const string IntegrationGateEnvironmentParked = "integration_gate_environment_parked";
    /// <summary>
    /// AGT-3002: the integration lane recorded whether a gate passed on the
    /// exact tree that carries a delivery the branch already contained.
    /// <see cref="TimelineEvent.Details"/> carries the state
    /// (<c>integrated-verified</c> or <c>integrated-unverified</c>), the SHA,
    /// the evidence, and the gate verdict when one ran.
    /// </summary>
    public const string IntegrationVerificationRecorded = "integration_verification_recorded";
    /// <summary>
    /// AGT-2853: the integration build/test gate re-ran exactly the tests that
    /// failed in its full run, they passed, and the gate stayed green.
    /// <see cref="TimelineEvent.Details"/> carries the quarantined test names,
    /// the gate that ran them, the tested SHA, and the shared
    /// <c>FlakyQuarantine</c> classification, so the flake list is visible on
    /// the card instead of being silently absorbed.
    /// </summary>
    public const string IntegrationGateFlakyRerun = "integration_gate_flaky_rerun";
    /// <summary>
    /// The integration gate's preparation found a cache-class failure,
    /// quarantined the affected immutable entry, and spent its one clean retry.
    /// This environment recovery is visible without becoming a review verdict.
    /// </summary>
    public const string IntegrationGatePreparationCacheRetried = "integration_gate_preparation_cache_retried";
    /// <summary>
    /// AGT-2849: a pre-develop build gate that owned a merge on the integration
    /// branch never reached a verdict because its process disappeared. Startup
    /// recovery says what it did about it - rolled the branch back to the exact
    /// pre-merge tip, resumed the merge because a durable verdict for that exact
    /// SHA existed, or escalated because the branch could not be repaired safely.
    /// <see cref="TimelineEvent.Details"/> carries the action, the gated SHA, the
    /// rollback anchor, and the integration branch.
    /// </summary>
    public const string IntegrationGateInterrupted = "integration_gate_interrupted";
    /// <summary>
    /// AGT-2220: the card's recorded <c>integrationBranch</c> disagreed with
    /// project truth when a review was claimed, so the review plane rewrote it.
    /// A stale field (still <c>refs/heads/main</c> after develop became the
    /// working branch) resolves the review baseline to an ancient merge-base.
    /// <see cref="TimelineEvent.Details"/> carries the previous branch, the new
    /// ref, and which source decided it.
    /// </summary>
    public const string IntegrationBranchCorrected = "integration_branch_corrected";
    /// <summary>
    /// AGT-2220: the same review infrastructure cause failed several linked
    /// attempts in a row, so the concrete diagnosis is named on the card instead
    /// of only a repeated classification. <see cref="TimelineEvent.Details"/>
    /// carries the classification, repeat count, attempt ids, and the base ref /
    /// base commit / step / command the runner reported.
    /// </summary>
    public const string ReviewInfrastructureRepeatDiagnosed = "review_infrastructure_repeat_diagnosed";
    /// <summary>
    /// AGT-2819: a deterministic review gate failed on the card AND on the merge
    /// base, so the failure belongs to the integration branch, not to the card.
    /// The card carries this entry instead of a product finding.
    /// <see cref="TimelineEvent.Details"/> carries the branch, step, command,
    /// merge-base commit, and the last commit the gate was measured green at.
    /// </summary>
    public const string IntegrationBranchGateDefect = "integration_branch_gate_defect";
    /// <summary>
    /// AGT-2841: a ReviewInfra outcome (AspectTimeout, ToolUnavailable,
    /// BaselineUnavailable, or another infrastructure classification) had
    /// retry budget left, so a replacement attempt was scheduled for the same
    /// delivery SHA after a bounded backoff instead of needing an operator
    /// move to get a new attempt. <see cref="TimelineEvent.Summary"/> carries
    /// the retry number, the budget, the delay, and the failure reason;
    /// <see cref="TimelineEvent.Details"/> carries the same facts plus the due
    /// time.
    /// </summary>
    public const string ReviewInfrastructureRetryScheduled = "review_infrastructure_retry_scheduled";
    /// <summary>
    /// AGT-W57: the fleet-wide cause breaker parked this card behind a cause
    /// card instead of retrying or escalating it. Details carry the cause key,
    /// the fingerprint and its parts, and the threshold numbers.
    /// </summary>
    public const string CauseBreakerWaiting = "cause_breaker_waiting";
    /// <summary>AGT-W57: the cause breaker closed and released this card with a freshly planned review.</summary>
    public const string CauseBreakerReleased = "cause_breaker_released";
    /// <summary>
    /// AGT-2863: the Remote Review verdict on this card was produced by a
    /// detached worker running an older agent-host release than the daemon that
    /// reported it, because a review-daemon restart adopts running workers
    /// instead of discarding their gate work. Reporting only - the verdict
    /// stands. <see cref="TimelineEvent.Summary"/> carries "graded by release
    /// &lt;old&gt;, current &lt;new&gt;"; <see cref="TimelineEvent.Details"/>
    /// carry <c>attemptId</c>, <c>workerReleaseId</c>, <c>daemonReleaseId</c>,
    /// <c>workerBinaryPath</c>, and <c>fence</c>.
    /// </summary>
    public const string ReviewGradedBySupersededRelease = "review_graded_by_superseded_release";
    /// AGT-2492: the recall sweep found that a parked card's recorded
    /// precondition no longer holds. <see cref="TimelineEvent.Summary"/> carries
    /// how long the card has been parked and why the blocker is considered gone;
    /// <see cref="TimelineEvent.Details"/> carries the lane, blocker type,
    /// condition kind, and age in seconds. Reporting only - the card is NOT
    /// re-queued, because "no auto rerun" remains the parker's decision.
    /// </summary>
    public const string ParkedBlockerResolved = "parked_blocker_resolved";
    /// <summary>
    /// A Remote Review executor acquired the lease of a ReviewAttempt
    /// (<c>AttemptLeaseDto.AcquiredAt</c> is the row <see cref="TimelineEvent.Ts"/>).
    /// Written by the claim routes so the review queue wait ends at the claim,
    /// not at the first projected step row. <see cref="TimelineEvent.RunId"/> is
    /// the ReviewAttempt id; <see cref="TimelineEvent.Details"/> carry the lease,
    /// executor, host, subject, and source run.
    /// </summary>
    public const string ReviewAttemptClaimed = "review_attempt_claimed";
    /// <summary>
    /// AGT-2827: a project's build profile or a review pipeline step changed
    /// while this task's ReviewAttempt was still queued (Pending, unclaimed), so
    /// its frozen <c>Plan</c> was rebuilt from the new settings before an
    /// executor could claim the stale one. A claimed (Leased) attempt already
    /// handed its plan to the executor and never emits this event - it keeps
    /// the plan it was claimed with. <see cref="TimelineEvent.Details"/> carries
    /// <c>attemptId</c> and <c>reason</c>.
    /// </summary>
    public const string ReviewAttemptReplanned = "review_attempt_replanned";
    /// <summary>
    /// AGT-2747: a user follow-up was admitted as a queued intent instead of a
    /// local run, because the card's lane, its delivery state, or its configured
    /// execution location forbade spawning a process here.
    /// <see cref="TimelineEvent.Details"/> carry the queue reason, the continue
    /// mode, and the lane the card was promoted from.
    /// </summary>
    public const string FollowUpQueued = "follow_up_queued";
    /// <summary>
    /// AGT-2747: a run carrying an unconsumed user follow-up was stopped (lane
    /// reconciliation, watchdog, quota cap), so the follow-up was written back
    /// to <c>pending-intent.json</c> before the process died.
    /// <see cref="TimelineEvent.Details"/> carry the stop reason and the mode.
    /// </summary>
    public const string FollowUpPreserved = "follow_up_preserved";
    /// <summary>
    /// A worker started with the exact saved follow-up prompt. The replayable
    /// stash is deleted and this receipt retains its hash, attribution, mode,
    /// timestamp, and <see cref="TimelineEvent.RunId"/>.
    /// </summary>
    public const string FollowUpConsumed = "follow_up_consumed";
    /// <summary>
    /// A queued follow-up became impossible to deliver because the task entered
    /// a terminal lane. Details retain its mode, author, timestamp, and the
    /// resolution <c>superseded-by-completion</c>.
    /// </summary>
    public const string FollowUpSuperseded = "follow_up_superseded";

    public const string DecisionRequested = "decision_requested";
    public const string DecisionDecided = "decision_decided";
    public const string DecisionReopened = "decision_reopened";
    /// <summary>The apply step updated linked cards or created cards from the chosen option.</summary>
    public const string DecisionApplied = "decision_applied";
    /// <summary>A pending decision passed its due date; the reminder named the blocked cards.</summary>
    public const string DecisionReminded = "decision_reminded";

    /// <summary>
    /// AGT-2870: an operator asked a remotely executed run to stop. The request
    /// is recorded here when it is accepted, not when the run ends; the owning
    /// runner reports the matching finish itself.
    /// <see cref="TimelineEvent.Details"/> carry the stop reason and the runner
    /// that holds the attempt.
    /// </summary>
    public const string RemoteStopRequested = "remote_stop_requested";
    /// <summary>Read-only receipt rebuilt from steering, attempt or settlement authority.</summary>
    public const string SteeringFeedback = "steering_feedback";

    /// <summary>
    /// A Global Orchestrator Watcher case (orchestrator-waechter dossier §10)
    /// recurred against a card that already has an open Watcher proposal, so
    /// W2 appended a comment here instead of drafting a second card. Details
    /// carry <c>caseId</c>, <c>fingerprint</c>, <c>detectorClass</c>, and
    /// <c>evidenceDigest</c>.
    /// </summary>
    public const string WatcherCaseLinked = "watcher_case_linked";

    /// <summary>
    /// AGT-3218: the monolith gate handler took a passed engine gate decision
    /// and dispatched the aspect review. Details carry <c>orchestrationRunId</c>;
    /// the applier canary reads it as the durable gate dispatch.
    /// </summary>
    public const string EngineGateDispatched = "engine_gate_dispatched";

    /// <summary>
    /// IP-14: an environment health incident or certificate warning that owns
    /// this card changed (reopened, recovered, certificate closer to expiry).
    /// Details carry <c>incidentId</c> or <c>certificateNotAfter</c>,
    /// <c>environmentId</c>, <c>checkId</c> and <c>change</c>.
    /// </summary>
    public const string EnvironmentHealthUpdated = "environment_health_updated";
}
