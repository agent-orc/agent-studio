namespace AgentStudio.Shared;

/// <summary>
/// One row in <c>logs/timeline.jsonl</c>. The unified per-task ledger that
/// records every event in the task's lifetime so the operator never has to
/// stitch together <c>session-events.jsonl</c>, <c>pipeline-execution.json</c>,
/// <c>[orchestrator]</c>/<c>[supervisor]</c> chat lines, and lane moves to
/// answer "what happened to this card?". See ADR-0049.
///
/// <para>
/// The schema is deliberately small and the <see cref="Kind"/> enum is
/// deliberately closed. New event kinds are added by extending the enum +
/// a writer + a test in the same commit; no free-form <c>kind</c> string,
/// no <c>extras</c> bag. The point of the ledger is to enforce discipline,
/// not to be a sink for arbitrary observability.
/// </para>
///
/// <para>
/// This is a *derived* surface. Each event is mirrored by a producer that
/// already owns its own canonical record (the runner already writes
/// session-events.jsonl, the pipeline already writes pipeline-execution.json,
/// etc.). The timeline writer is a thin teeing call next to those producers
/// so the union ledger stays a single grep target.
/// </para>
/// </summary>
public sealed record TimelineEvent
{
    public DateTime Ts { get; init; }
    public string Kind { get; init; } = "";

    /// <summary>
    /// Coarse attribution. One of <c>agent</c>, <c>orchestrator</c>,
    /// <c>quality-loop</c>, <c>system</c>, or <c>human:&lt;email&gt;</c>.
    /// Drives the per-event glyph and the filter chips in the FE.
    /// </summary>
    public string Actor { get; init; } = "system";

    /// <summary>
    /// Optional run correlator. Populated for events that belong to one
    /// CLI invocation (<c>agent_run_started</c>, <c>agent_run_finished</c>,
    /// <c>post_step_*</c>). Today the value is the captured-session-id of
    /// the run when known, falling back to the started-marker timestamp so
    /// reissues stay distinguishable in the FE.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Optional path (relative to the job folder) to the artefact this
    /// event produced or references - <c>status.md</c>, <c>aspect-*.md</c>,
    /// the decision note. The FE expands a row to show this artefact
    /// inline. Null when the event has no body of its own.
    /// </summary>
    public string? PayloadRef { get; init; }

    /// <summary>One-line human-readable description. Renders directly in the FE row.</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Optional terse details bag. Use sparingly - prefer extending the
    /// closed schema with a typed event kind when a producer needs more
    /// than a one-liner. The dictionary is a JSON object on the wire.
    /// </summary>
    public Dictionary<string, string>? Details { get; init; }
}

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
    /// <summary>
    /// ADR-0052 multi-system follow-up: the Task Server granted, rejected, or
    /// released the fenced integration lease that serializes direct merges into
    /// a project's integration branch across runner machines.
    /// </summary>
    public const string IntegrationLease = "integration_lease";
    /// <summary>The CLI invocation ended; <see cref="TimelineEvent.Summary"/> carries the outcome.</summary>
    public const string AgentRunFinished = "agent_run_finished";
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
    /// <summary>
    /// A human deliberately moved a task out of human review or escalation,
    /// opening a fresh review-attempt epoch. Details carry the operator reason,
    /// new epoch, and rotated artefact count.
    /// </summary>
    public const string OperatorRequeued = "operator_requeued";
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
    /// <summary>The task's lane changed (any move).</summary>
    public const string LaneChanged = "lane_changed";
    /// <summary>
    /// An epic's planning/decomposition run (way 3) authored a sub-task list
    /// and the runner created those sub-tasks under the epic. The "Epic
    /// decomposition" step in the timeline/pipeline; <see cref="TimelineEvent.Summary"/>
    /// carries the created count and <see cref="TimelineEvent.Details"/> the
    /// target lane. Emitted by the runner, not the deterministic endpoint.
    /// </summary>
    public const string EpicDecomposed = "epic_decomposed";
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
    /// the round was automatic.
    /// </summary>
    public const string IntegrationRecoveryQueued = "integration_recovery_queued";
    /// <summary>
    /// The platform-owned acceptance rail accepted, requeued, or boundedly
    /// escalated this card without a session-bound orchestrator tick.
    /// </summary>
    public const string AcceptanceRailActed = "acceptance_rail_acted";
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
    /// AGT-2747: a run consumed a saved <c>pending-intent.json</c>. The consumed
    /// copy is retained as <c>pending-intent.consumed.json</c> and
    /// <see cref="TimelineEvent.RunId"/> names the run that took it, so an
    /// operator can prove a queued steer actually reached an agent.
    /// </summary>
    public const string FollowUpConsumed = "follow_up_consumed";
}

/// <summary>
/// Closed vocabulary for <c>lane_changed.details.cause</c>: why a lane change
/// happened, written by the transition site that knows. The ids are the
/// taxonomy of the cycle-time lane-transition analysis
/// (<c>AgentStudio.Projects.TransitionCauses</c> aliases these constants), so a
/// reader can take the field as exact and only infer from neighbouring ledger
/// rows for rows written before the field existed. Forward causes name the
/// pipeline hand-off that moved the task on; backward causes answer why it
/// fell back. A short free-text qualifier travels in
/// <c>details.causeDetail</c> (quality-loop cause id, integration outcome,
/// escalation category, retry counter); the operator prose stays in
/// <c>details.reason</c>.
/// </summary>
public static class LaneChangeCauses
{
    /// <summary>Key of the cause id in <see cref="TimelineEvent.Details"/>.</summary>
    public const string DetailKey = "cause";
    /// <summary>Key of the short qualifier in <see cref="TimelineEvent.Details"/>.</summary>
    public const string DetailQualifierKey = "causeDetail";

    // forward / lateral
    public const string Promoted = "promoted";
    public const string Claimed = "claimed";
    public const string Delivered = "delivered";
    public const string ExternalCompletion = "external-completion";
    public const string ReviewVerdict = "review-verdict";
    public const string Escalated = "escalated";
    public const string OperatorDecision = "operator-decision";
    public const string Accepted = "accepted";
    public const string Archived = "archived";
    public const string OperatorMove = "operator-move";
    public const string SystemMove = "system-move";

    // backward
    public const string GateFailure = "gate-failure";
    public const string QualityLoop = "quality-loop";
    public const string IntegrationRecovery = "integration-recovery";
    public const string ReviewInfrastructure = "review-infrastructure";
    public const string LeaseRecovery = "lease-recovery";
    public const string ClaimEnvironmentRetry = "claim-environment-retry";
    public const string RunnerRequeue = "runner-requeue";
    public const string AcceptanceIntegrationFailed = "acceptance-integration-failed";
    public const string CompletedReopen = "completed-reopen";
    public const string EscalationRequeue = "escalation-requeue";
    public const string OperatorRequeue = "operator-requeue";
    public const string Unclassified = "unclassified";

    /// <summary>The closed set; a reader ignores any other value of <see cref="DetailKey"/>.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Promoted, Claimed, Delivered, ExternalCompletion, ReviewVerdict, Escalated, OperatorDecision,
        Accepted, Archived, OperatorMove, SystemMove,
        GateFailure, QualityLoop, IntegrationRecovery, ReviewInfrastructure, LeaseRecovery, ClaimEnvironmentRetry,
        RunnerRequeue, AcceptanceIntegrationFailed, CompletedReopen, EscalationRequeue, OperatorRequeue, Unclassified,
    };

    /// <summary>
    /// Cause of a move a human initiated (board drag, lane button, API move),
    /// derived from the lane pair: an operator never states the taxonomy id,
    /// so the writer derives it the same way the analysis would.
    /// </summary>
    public static string ForOperatorMove(string? from, string? to)
    {
        var fromLevel = Level(from);
        var toLevel = Level(to);
        if (fromLevel >= 0 && toLevel >= 0 && toLevel < fromLevel)
        {
            return from switch
            {
                TaskStates.Completed => CompletedReopen,
                TaskStates.Escalated => EscalationRequeue,
                TaskStates.HumanReview or TaskStates.AutoReview => OperatorRequeue,
                _ => OperatorMove,
            };
        }

        return to switch
        {
            TaskStates.Escalated => Escalated,
            TaskStates.HumanReview => from == TaskStates.Escalated ? OperatorDecision : OperatorMove,
            TaskStates.Completed => Accepted,
            TaskStates.Archive => Archived,
            TaskStates.Ready or TaskStates.Preparation or TaskStates.OrchestratorPrep => Promoted,
            _ => OperatorMove,
        };
    }

    /// <summary>Lane level for direction; sub-lanes share the level of their parent lane.</summary>
    public static int Level(string? lane) => lane switch
    {
        TaskStates.Backlog => 0,
        TaskStates.Preparation or TaskStates.OrchestratorPrep => 1,
        TaskStates.Ready => 2,
        TaskStates.Progress or TaskStates.FailedPickup or TaskStates.CodeNotComplete => 3,
        TaskStates.AutoReview => 4,
        TaskStates.HumanReview or TaskStates.Escalated => 5,
        TaskStates.Completed => 6,
        TaskStates.Archive => 7,
        _ => -1,
    };
}

/// <summary>
/// Conventional <see cref="TimelineEvent.Actor"/> values. Producers pass
/// these constants instead of free-form strings so the filter chips on
/// the FE Timeline tab line up reliably.
/// </summary>
public static class TimelineActors
{
    public const string Agent = "agent";
    public const string Orchestrator = "orchestrator";
    public const string QualityLoop = "quality-loop";
    public const string System = "system";
    /// <summary>
    /// Work that arrived from outside the local runner: an operator chat, an
    /// external agent, or a remote host. Used by the out-of-band completion
    /// ingest path (<c>external_completion</c> timeline events) so the Timeline
    /// filter chips can tell externally produced results from runner activity.
    /// </summary>
    public const string External = "external";
    public static string Human(string email) => string.IsNullOrWhiteSpace(email) ? "human" : $"human:{email}";
}
