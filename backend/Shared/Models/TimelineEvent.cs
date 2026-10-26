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

    /// <summary>
    /// A service account or API token acting through the Studio API. It is
    /// never a human actor, so guards that need a person do not accept it.
    /// </summary>
    public static string ServiceAccount(string accountId) => $"service-account:{accountId}";

    /// <summary>
    /// True when an actor or lane-change cause names a person rather than an
    /// automated path. Used where a guard must stop an operator but must not
    /// break a runner, sweep, or orchestrator transition.
    /// </summary>
    public static bool IsHuman(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) return false;
        var value = actor.Trim();
        return value.Equals("human", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("human:", StringComparison.OrdinalIgnoreCase);
    }
}
