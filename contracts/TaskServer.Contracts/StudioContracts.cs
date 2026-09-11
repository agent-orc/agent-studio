namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio "core-attach" bundle: human login/session
/// bootstrap, the board projection, task lifecycle actions, orchestrator
/// chat and context digests, runner status, and the replayable studio event
/// stream. These are additive to the machine-principal bearer model in
/// <see cref="TaskServerScopes"/>; a human studio session is a second,
/// nested identity layer carried above the connector's own bearer call.
/// </summary>
public static class StudioUserRoles
{
    public const string Owner = "owner";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
}

public sealed record StudioBootstrapRequest(string Username, string Password, string? DisplayName = null);
public sealed record StudioLoginRequest(string Username, string Password);
public sealed record StudioChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record StudioAuthUserDto(
    string UserId,
    string Username,
    string DisplayName,
    string Role,
    IReadOnlyList<string> ProjectIds,
    bool Disabled,
    bool MustChangePassword);

public sealed record StudioAuthStatusDto(
    bool BootstrapRequired,
    bool Authenticated,
    StudioAuthUserDto? User = null);

public sealed record StudioAuthSessionDto(
    string SessionToken,
    string CsrfToken,
    StudioAuthStatusDto Status);

public sealed record StudioBoardResponse(
    IReadOnlyList<TaskDto> Backlog,
    IReadOnlyList<TaskDto> Ready,
    IReadOnlyList<TaskDto> Progress,
    IReadOnlyList<TaskDto> AutoReview,
    IReadOnlyList<TaskDto> HumanReview,
    IReadOnlyList<TaskDto> Escalated,
    IReadOnlyList<TaskDto> Completed,
    IReadOnlyList<TaskDto> Archive,
    DateTime GeneratedAt);

public sealed record StudioActiveRunDto(
    string TaskId,
    string TaskKey,
    string RunId,
    string RunnerId,
    string HostId,
    string Phase,
    DateTime ObservedAt);

public sealed record StudioProjectRunnerStatus(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<StudioActiveRunDto> ActiveRuns);

public sealed record StudioRunnerStatusResponse(
    IReadOnlyList<StudioProjectRunnerStatus> Projects,
    DateTime ObservedAt);

public sealed record MoveTaskRequest(string TargetState, int? TargetIndex = null, string? Reason = null);
public sealed record StartTaskRequest(string? Model = null, string? CliType = null, string? ThinkingLevel = null);

public sealed record ContinueTaskRequest(
    string Prompt,
    string? Model = null,
    string? CliType = null,
    string? ThinkingLevel = null,
    string? Mode = null);

public sealed record StopTaskRequest(string? Reason = null);

public sealed record TaskLifecycleResponse(TaskDto Task, RunDto? Run = null);
public sealed record MoveTaskResponse(TaskDto Task, int Position);

public sealed record StudioOrchestratorChatMessageRequest(
    string Text,
    IReadOnlyList<OrchestratorContextAttachmentDto>? Attachments = null,
    string? Model = null,
    string? ThinkingLevel = null);

public sealed record OrchestratorChatResponse(
    string Project,
    OrchestratorContextTurnDto Turn,
    IReadOnlyList<OrchestratorContextTurnDto> Turns);

public sealed record OrchestratorChatTranscriptResponse(
    string Project,
    IReadOnlyList<OrchestratorContextTurnDto> Turns);

public sealed record ChatAttachmentUploadResponse(string FileName, string RelativePath, string Url);

public sealed record OrchestratorContextDigestSourceDto(
    string Name,
    string Status,
    DateTime? CapturedAt,
    string? Detail);

public sealed record StudioOrchestratorContextDigestResponse(
    string ContextKey,
    DateTime CapturedAt,
    string Digest,
    IReadOnlyList<OrchestratorContextDigestSourceDto> Sources);

public sealed record OrchestratorSessionDto(
    string ContextKey,
    string Kind,
    string ProjectId,
    string? TaskKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Model,
    long CumulativeInputTokens,
    long CumulativeOutputTokens,
    long CumulativeCacheReadTokens,
    long CumulativeCacheCreationTokens,
    long Calls,
    DateTime LastUsedAt,
    string Summary,
    DateTime? HiddenAt,
    string RuntimeStatus,
    int QueuePosition);

public sealed record OrchestratorSessionListResponse(IReadOnlyList<OrchestratorSessionDto> Sessions);

/// <summary>
/// One durable, cursor-ordered entry in the studio event stream backing
/// <c>/hubs/v1/studio</c>. A reconnecting client supplies the last cursor it
/// saw; the Task Server replays every event after it before subscribing the
/// connection to live delivery, so a Studio-detached period never loses a
/// mutation.
/// </summary>
public sealed record StudioStreamEventDto(
    long Cursor,
    DateTime OccurredAt,
    string Kind,
    string? ProjectId,
    string? TaskId,
    string PayloadJson);

public static class StudioStreamEventKinds
{
    public const string TaskCreated = "task.created";
    public const string TaskUpdated = "task.updated";
    public const string TaskMoved = "task.moved";
    public const string TaskDeleted = "task.deleted";
    public const string TaskStarted = "task.started";
    public const string TaskStopped = "task.stopped";
    public const string TaskContinued = "task.continued";
    public const string OrchestratorChatAppended = "orchestrator-chat.appended";
}
