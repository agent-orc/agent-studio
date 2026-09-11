namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the G7 "task run/attempt history" P1 Studio bundle:
/// versioned task history and attempts projections reshaped from the same
/// durable runs/events/artifacts state the P0 core-attach bundle already
/// reads via <c>TaskServerStore.GetTaskHistoryAsync</c> and
/// <c>TaskServerStore.ListAttemptsAsync</c>. Almost everything here is a
/// read-only reprojection; <see cref="ClaudeSessionInfoDto"/> and
/// <see cref="TaskPlanDto"/> back two genuinely new but empty-by-default
/// surfaces (<c>task_claude_sessions</c>, <c>task_plans</c>).
/// </summary>
public static class StudioTaskTimelineEntryKinds
{
    public const string RunStarted = "run-started";
    public const string RunFinished = "run-finished";
    public const string Event = "event";
    public const string ArtifactCreated = "artifact-created";
    public const string AuditAction = "audit-action";
}

public sealed record StudioTaskTimelineEntryDto(
    string Kind,
    DateTime OccurredAt,
    string Summary,
    string? RunId = null,
    string? EventKind = null,
    string? ArtifactId = null,
    string? ArtifactName = null,
    string? AuditAction = null);

public sealed record StudioTaskTimelineResponse(IReadOnlyList<StudioTaskTimelineEntryDto> Entries);

public sealed record StudioAgentWorkDetailResponse(
    string? RunId,
    IReadOnlyList<EventDto> AgentEvents);

public sealed record StudioAgentWorkSummaryResponse(
    string? RunId,
    int AgentMessageCount,
    int ToolTraceCount,
    int RunnerTraceCount,
    DateTime? FirstEventAt,
    DateTime? LastEventAt);

/// <summary>
/// Durable identity fields carried by the run's own <c>runs</c> row
/// (<c>result_sha</c>, <c>repository_id</c>, <c>repository_url</c>,
/// <c>result_ref</c>). The Task Server has no filesystem/git access to a
/// Runner's workspace, so this is never a computed commit list -- only
/// whatever of these columns is populated.
/// </summary>
public sealed record RunCommitsDto(
    string RunId,
    int RunIndex,
    string? ResultSha,
    string? RepositoryId,
    string? RepositoryUrl,
    string? ResultRef);

/// <summary>
/// <see cref="Available"/> is <c>false</c> (not a 404, not a fabricated
/// diff) when the run legitimately has no diff artifact yet.
/// </summary>
public sealed record RunDiffResponse(
    bool Available,
    string? ArtifactId = null,
    string? Name = null,
    string? MediaType = null,
    string? ContentBase64 = null);

/// <summary>
/// Same "honest absence" shape as <see cref="RunDiffResponse"/>, for a
/// files-changed-list artifact.
/// </summary>
public sealed record RunFilesResponse(
    bool Available,
    string? ArtifactId = null,
    string? Name = null,
    string? MediaType = null,
    string? ContentBase64 = null);

public sealed record RunContextTurnDto(string TurnId, DateTime CreatedAt, string Role, string Body);

/// <summary>
/// The run's associated orchestrator context turns, best-effort bounded by
/// the run's own <c>created_at</c>/<c>finished_at</c> window.
/// <see cref="Available"/> is <c>false</c> when the task has no 'task' kind
/// orchestrator context, or that context has no turns in the run's window.
/// </summary>
public sealed record RunContextResponse(
    bool Available,
    string? ContextKey = null,
    IReadOnlyList<RunContextTurnDto>? Turns = null);

/// <summary>
/// Session-level metadata for a task's Claude CLI usage. Backed by the new
/// <c>task_claude_sessions</c> table. Nothing populates this table yet in
/// this P1 slice, so a missing row is a legitimate empty state (a
/// <c>null</c> response body), never an error.
/// </summary>
public sealed record ClaudeSessionInfoDto(
    string TaskId,
    string? SessionId,
    string? Model,
    DateTime UpdatedAt);

/// <summary>
/// The task's plan text. Backed by the new <c>task_plans</c> table.
/// GET-only in this bundle; no writer route is included here.
/// </summary>
public sealed record TaskPlanDto(
    string TaskId,
    string? PlanMarkdown,
    int Version,
    DateTime UpdatedAt);

public sealed record StepPromptDto(string StepId, IReadOnlyList<string> Prompts);

public sealed record StepPromptsResponse(IReadOnlyList<StepPromptDto> Steps);
