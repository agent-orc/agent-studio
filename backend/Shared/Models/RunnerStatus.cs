namespace AgentStudio.Shared;

public record WatchPathEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RootPath { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
}

public record CliExecution
{
    public string JobId { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTime StartedAt { get; init; }
    public string Status { get; init; } = "";      // running | completed | failed | cancelled
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>
    /// Canonical terminal run outcome once known: success, failed, noop,
    /// blocked, needs-input, interrupted, or unknown. Null while running and
    /// on legacy in-memory records.
    /// </summary>
    public string? RunOutcome { get; init; }
}

public static class TaskIdentity
{
    public static string CreateKey(string watchPath, string jobId) => $"{watchPath}::{jobId}";
}

/// <summary>
/// Read-time visibility projection (ASS-1751) that disambiguates the three ways
/// a <c>3-progress</c> task can look "untouched" on the board: a live run that
/// occupies a slot, a failed run waiting out the rapid-crash backoff before it
/// can be re-picked, and an orphan whose run was killed by a backend restart and
/// has not been re-picked yet. Purely additive and never persisted - it is
/// folded onto <see cref="TaskInfo"/> at endpoint-read time from the project
/// runner's in-memory state and only for Progress-lane tasks. Carries NO
/// behavior; the UI renders a small, quiet status pill from it.
/// </summary>
public record TaskRunActivity
{
    /// <summary>One of the <see cref="TaskRunActivityKinds"/> constants.</summary>
    public string Kind { get; init; } = TaskRunActivityKinds.NoActiveRun;
    /// <summary>OS process id of the live run; set only when <see cref="Kind"/> is <see cref="TaskRunActivityKinds.Active"/>.</summary>
    public int? ProcessId { get; init; }
    /// <summary>UTC instant the rapid-crash backoff expires; set only when <see cref="Kind"/> is <see cref="TaskRunActivityKinds.FailedBackoff"/>.</summary>
    public DateTime? BackoffUntil { get; init; }
    /// <summary>Consecutive fail-without-progress attempts recorded by the runner for this task (0 when none).</summary>
    public int Attempt { get; init; }
    /// <summary>One-line last-error summary mirrored from <see cref="TaskOutcomeIssue.Summary"/>; null when no issue is known.</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// String constants for <see cref="TaskRunActivity.Kind"/>. Kept as literals so
/// the JSON wire format is stable and the frontend can switch on the same
/// tokens. See <see cref="TaskRunActivityClassifier"/> for the rules that pick
/// one.
/// </summary>
public static class TaskRunActivityKinds
{
    /// <summary>The run process is alive and occupies a parallelism slot.</summary>
    public const string Active = "active";
    /// <summary>Last run failed and a rapid-crash backoff is still in effect; the task waits for re-pickup.</summary>
    public const string FailedBackoff = "failed-backoff";
    /// <summary>Last run failed (or a fail-without-progress attempt is recorded) but no backoff is active and nothing is running.</summary>
    public const string FailedIdle = "failed-idle";
    /// <summary>No live run, no backoff, no recorded failure - e.g. an orphan after a backend restart awaiting re-pickup.</summary>
    public const string NoActiveRun = "no-active-run";
}

/// <summary>
/// In-memory facts a project runner exposes about one task's current run, the
/// raw input to <see cref="TaskRunActivityClassifier"/>. All fields are cleared
/// on a backend restart (the recovery boundary), so an orphaned task naturally
/// classifies as <see cref="TaskRunActivityKinds.NoActiveRun"/>.
/// </summary>
public readonly record struct RunActivityFacts(bool SlotActive, DateTime? BackoffUntil, int ConsecutiveFailures);

/// <summary>
/// Pure rules that map the runner's in-memory <see cref="RunActivityFacts"/>
/// (plus the read-time execution status and outcome issue) onto a
/// <see cref="TaskRunActivity"/>. Kept side-effect-free and standalone so the
/// three-state classification is directly unit-testable without spinning up a
/// runner. ASS-1751.
/// </summary>
public static class TaskRunActivityClassifier
{
    /// <summary>
    /// Classify a Progress-lane task. Precedence: a live slot wins (active),
    /// then a live CLI execution still reporting <c>running</c> even when the
    /// in-memory slot registry has lost track of it (active — the post-restart
    /// desync guard, ASS-1753), then an unexpired backoff (failed-backoff),
    /// then any evidence of a prior failure (failed-idle), else no-active-run.
    /// <paramref name="now"/> is injected so tests are deterministic.
    /// </summary>
    public static TaskRunActivity Classify(
        RunActivityFacts facts,
        CliExecution? execution,
        TaskOutcomeIssue? outcomeIssue,
        DateTime now)
    {
        var attempt = facts.ConsecutiveFailures < 0 ? 0 : facts.ConsecutiveFailures;
        var lastError = string.IsNullOrWhiteSpace(outcomeIssue?.Summary) ? null : outcomeIssue!.Summary;

        // A slot wins, but a live execution that is still "running" is an
        // equally authoritative "this task is alive" signal. After a backend
        // restart the in-memory slot registry can be empty while the CLI router
        // still tracks a live execution (or a recovery-resume re-booked the run
        // a tick later) - trusting only the slot would paint a running task as
        // "no active run". Prefer the live signal from either source. ASS-1753.
        var executionRunning = string.Equals(execution?.Status, "running", StringComparison.OrdinalIgnoreCase);
        if (facts.SlotActive || executionRunning)
        {
            return new TaskRunActivity
            {
                Kind = TaskRunActivityKinds.Active,
                ProcessId = execution is { ProcessId: > 0 } ? execution.ProcessId : null,
                Attempt = attempt,
                LastError = lastError,
            };
        }

        if (facts.BackoffUntil is { } until && until > now)
        {
            return new TaskRunActivity
            {
                Kind = TaskRunActivityKinds.FailedBackoff,
                BackoffUntil = until,
                Attempt = attempt,
                LastError = lastError,
            };
        }

        var execFailed = string.Equals(execution?.Status, "failed", StringComparison.OrdinalIgnoreCase);
        if (execFailed || attempt > 0)
        {
            return new TaskRunActivity
            {
                Kind = TaskRunActivityKinds.FailedIdle,
                Attempt = attempt,
                LastError = lastError,
            };
        }

        return new TaskRunActivity
        {
            Kind = TaskRunActivityKinds.NoActiveRun,
            Attempt = attempt,
            LastError = lastError,
        };
    }
}

public record RunnerStatus
{
    public Dictionary<string, ProjectRunnerStatus> Projects { get; init; } = new();
    /// <summary>
    /// Active local control-plane CLI repair failures. Healthy CLIs and
    /// completed repairs are intentionally omitted.
    /// </summary>
    public IReadOnlyList<LocalCliRepairStatus> CliRepairs { get; init; } = [];
}

/// <summary>Active bounded npm-shim self-heal failure for one local CLI.</summary>
public sealed record LocalCliRepairStatus
{
    public string CliType { get; init; } = "";
    /// <summary>
    /// <c>failed</c> for a repair that did not restore the CLI, or
    /// <c>detected</c> for a broken install that is journalled on sight and is
    /// waiting for its repair attempt. Healthy outcomes are not projected.
    /// </summary>
    public string Outcome { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record QuotaFallbackStatus(string CliType, string? Model, string? Reason, DateTime? ActivatedAt = null);

/// <summary>Read-time projection of a task's intentional quota-reset wait.</summary>
public sealed record QuotaWaitStatus(
    string CliType,
    DateTime StartedAt,
    DateTime ResetAt,
    int ThresholdMinutes,
    string Reason,
    string Scope = "quota");

public record ProjectRunnerStatus
{
    public string ProjectName { get; init; } = "";
    public string Mode { get; init; } = "manual";
    public string? ActiveJobId { get; init; }
    public CliExecution? ActiveExecution { get; init; }
    /// <summary>Effective fallback model while an active run is quota-routed.</summary>
    public string? QuotaFallbackModel { get; init; }
    /// <summary>Quota window/cap explanation for the active fallback.</summary>
    public string? QuotaFallbackReason { get; init; }
    /// <summary>
    /// Pickup-eligible Ready tasks in canonical runner order. This list is the
    /// source for queue counts and one-based task positions; gated Ready tasks
    /// are deliberately absent.
    /// </summary>
    public List<string> QueuedJobIds { get; init; } = [];
    /// <summary>
    /// Reason recorded the last time the runner mode changed. Mirrors the
    /// <c>reason</c> argument to <see cref="AgentStudio.Runner.ProjectRunner.SetMode"/>;
    /// surfaces so the board can distinguish operator-initiated
    /// <c>manual</c> / <c>paused</c> transitions ("api-toggle", "api: POST /api/runner/{project}/stop")
    /// from system-initiated ones ("auto-failure circuit-breaker", "capture-fail circuit-breaker",
    /// "cross-slug infra circuit-breaker", "supervisor pause") in the lane pill chip.
    /// </summary>
    public string? ModeReason { get; init; }
    /// <summary>
    /// UTC timestamp when the mode last changed. Null on legacy in-memory
    /// records (before the backend started recording it).
    /// </summary>
    public DateTime? ModeChangedAt { get; init; }
    /// <summary>
    /// Coarse classification of where the current mode came from. One of
    /// <c>user</c>, <c>circuit-breaker</c>, <c>supervisor</c>, <c>system</c>.
    /// Derived from <see cref="ModeReason"/> at SetMode time so the frontend
    /// does not have to re-implement the heuristic on every render.
    /// </summary>
    public string? ModeSource { get; init; }
    /// <summary>
    /// Current automatic breaker state. Null when no breaker is active;
    /// <c>cooldown</c> when the global auto-failure safety net paused the
    /// project temporarily and will auto-resume.
    /// </summary>
    public string? BreakerState { get; init; }
    /// <summary>
    /// UTC instant when the global breaker cooldown expires and the runner may
    /// restore <c>auto-continuous</c>.
    /// </summary>
    public DateTime? BreakerCooldownUntil { get; init; }
    /// <summary>
    /// Human-readable reason for the active global breaker cooldown.
    /// </summary>
    public string? BreakerReason { get; init; }
    /// <summary>
    /// Account-level provider capabilities currently paused. These are scoped
    /// by CLI, so a Claude limit does not make Codex cards ineligible.
    /// </summary>
    public IReadOnlyList<ProviderLimitStatus> ProviderLimits { get; init; } = [];
    /// <summary>
    /// Number of global breaker trips since this runner instance started. Used
    /// to explain exponential cooldown backoff.
    /// </summary>
    public int BreakerTripCount { get; init; }
    /// <summary>
    /// Backend role assigned via <c>Runner:Role</c> config; one of
    /// <c>orchestrator</c> (the default — picks tasks automatically when mode is
    /// <c>auto-*</c>) or <c>test-subject</c> (pickup loop is structurally
    /// disabled — only explicit start endpoints can drive a job). The dev
    /// backend ships as <c>test-subject</c> so a shared workspace does not
    /// produce a parallel pickup race against stable.
    /// </summary>
    public string Role { get; init; } = "orchestrator";
    /// <summary>
    /// Mode the operator asked for while a job was still running. Non-null only
    /// when a <c>PUT /api/runner/{project}/mode</c> with <c>manual</c> /
    /// <c>paused</c> arrived while tasks were active. Auto admission closes at
    /// once; the runner applies the value after the request-time active set drains.
    /// </summary>
    public string? PendingMode { get; init; }
    /// <summary>
    /// Job id of the sole remaining snapshot task. Null while multiple tasks remain.
    /// </summary>
    public string? PendingModeWillApplyAfter { get; init; }
    /// <summary>Remaining tasks from the active snapshot captured by the deferred request.</summary>
    public int PendingModeActiveTaskCount { get; init; }
    /// <summary>Title of the sole remaining snapshot task, when exactly one remains.</summary>
    public string? PendingModeActiveTaskTitle { get; init; }
    /// <summary>
    /// ADR-0052: the project's configured concurrency cap (clamped to
    /// <c>&gt;= 1</c>). <c>1</c> is the sequential default. Surfaced so the
    /// project view can render slot occupancy ("<see cref="OccupiedSlots"/> /
    /// MaxParallelism") next to the lane pill.
    /// </summary>
    public int MaxParallelism { get; init; } = 1;
    /// <summary>
    /// ADR-0052: how many of the <see cref="MaxParallelism"/> slots are
    /// currently filled by a running task. <c>0</c> when idle, <c>1</c> while a
    /// task is active under the sequential model.
    /// </summary>
    public int OccupiedSlots { get; init; }
    /// <summary>
    /// ADR-0052: the most recent pick-gate rationale
    /// (<see cref="AgentStudio.Runner.SlotAdmission.Reason"/>) the
    /// runner recorded when it admitted a task into a slot. Mirrors the
    /// <c>runner_slot_admission</c> timeline event so the UI can show "why this
    /// task was picked" without re-deriving it.
    /// </summary>
    public string? LastPickReason { get; init; }
}

// CliOutputLine now comes from the CodingAgentRunner package (aliased in the csproj).
