namespace AgentStudio.Supervisor;

/// <summary>
/// Severity of a supervisor advisory. Informational by default; the supervisor
/// is advisory-first, so even High does not by itself trigger an automatic
/// emergency primitive. Auto-intervention is a separate, opt-in concern.
/// </summary>
public enum SupervisorSeverity
{
    Info,
    Warn,
    High
}

/// <summary>
/// Source of a supervisor event. Used to filter events out of subsequent
/// observation input so the supervisor never reads its own writes back as
/// new signals (feedback-loop guard).
/// </summary>
public enum SupervisorSource
{
    HardCheck,
    SoftReasoning,
    User,
    AutoIntervention
}

/// <summary>
/// Kinds of pre-emptive control actions the supervisor can invoke against the
/// orchestrator. Each one is logged with a typed reason; routine outcome
/// handling stays with <see cref="Runner.RunOutcomePolicy"/>.
/// </summary>
public enum SupervisorInterventionKind
{
    CancelRun,
    PausePickup,
    ForceFail,
    Resume
}

/// <summary>
/// One typed advisory written to <c>logs/meta/&lt;project&gt;/observations.jsonl</c>.
/// Append-only. Severity is informational; the auto-intervention policy
/// (separate component) decides whether any advisory should be promoted to
/// an emergency primitive.
/// </summary>
public sealed record SupervisorAdvisory(
    DateTime CreatedAt,
    string Project,
    SupervisorSeverity Severity,
    SupervisorSource Source,
    string Topic,
    string Message,
    string? JobId = null);

/// <summary>
/// One typed intervention record written to <c>logs/meta/&lt;project&gt;/interventions.jsonl</c>.
/// The orchestrator runner is the single authority for state transitions; this
/// record captures the supervisor's intent and the reason, plus a copy of the
/// effect after the runner applies it.
/// </summary>
public sealed record SupervisorIntervention(
    DateTime CreatedAt,
    string Project,
    SupervisorInterventionKind Kind,
    SupervisorSource Source,
    string Reason,
    string? JobId = null,
    TimeSpan? PauseTtl = null);

/// <summary>
/// Read-only snapshot of one project's runtime state, returned by
/// <see cref="ISupervisor.ObserveAsync"/>. Pure data; the supervisor never
/// learns project state any other way.
/// </summary>
public sealed record SupervisorObservation(
    DateTime CapturedAt,
    string Project,
    string RunnerStatus,
    string? CurrentJobId,
    string? CurrentRunState,
    DateTime? LastProgressAt,
    SupervisorQuotaWindow? Quota,
    IReadOnlyList<SupervisorRecentDecision> RecentDecisions,
    IReadOnlyList<string> RecentAgentSamples,
    SupervisorErrorCounts ErrorCounts)
{
    public IReadOnlyList<string> Omissions { get; init; } = [];
}

public sealed record SupervisorQuotaWindow(
    string Cli,
    double UsedFraction,
    DateTime? ResetAt);

public sealed record SupervisorRecentDecision(
    DateTime At,
    string Kind,
    string Summary,
    string? EvidenceRef = null);

public sealed record SupervisorErrorCounts(
    int CliErrorsLastHour,
    int OrchestratorErrorsLastHour,
    int RunFailuresLastHour);

/// <summary>
/// The supervisor's surface against the orchestrator. Default behaviour is
/// cooperative signalling via <see cref="AdviseAsync"/>; the four pre-emptive
/// methods exist for the "agent is clearly broken / costing real money / about
/// to do harm" case and each one writes its own typed log entry.
/// </summary>
/// <remarks>
/// Implementations live under <see cref="Supervisor"/>:
/// <list type="bullet">
/// <item><description><c>ProjectObservationService</c> backs <see cref="ObserveAsync"/>.</description></item>
/// <item><description><c>SupervisorInterventionService</c> backs the four primitives.</description></item>
/// <item><description><c>HardHealthCheckHostedService</c> and <c>SoftReasoningHostedService</c> are the two writers of advisories.</description></item>
/// </list>
/// The full design rationale is in <c>docs/research/orchestrator-meta-loop-analysis-2026-05-04.md</c>.
/// </remarks>
public interface ISupervisor
{
    Task<SupervisorObservation> ObserveAsync(string project, CancellationToken ct);

    Task AdviseAsync(SupervisorAdvisory advisory, CancellationToken ct);

    Task CancelRunAsync(string project, string jobId, string reason, SupervisorSource source, CancellationToken ct);

    Task PausePickupAsync(string project, string reason, TimeSpan? ttl, SupervisorSource source, CancellationToken ct);

    Task ForceFailAsync(string project, string jobId, string reason, SupervisorSource source, CancellationToken ct);

    Task ResumeAsync(string project, string reason, SupervisorSource source, CancellationToken ct);
}
