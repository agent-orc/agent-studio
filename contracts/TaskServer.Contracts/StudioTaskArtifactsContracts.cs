namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P1 "task-owned files, attachments, artifacts,
/// screenshots, and results" Studio bundle (group G6). Run artifacts
/// themselves keep using the existing <see cref="ArtifactDto"/> /
/// <see cref="ArtifactContentDto"/> types - these routes only add a
/// task-scoped view over them (joined through <c>runs.task_id</c>) plus two
/// genuinely new task-owned resources: attachments and versioned files.
/// Distinct from the P0 orchestrator-chat attachments (<see
/// cref="ChatAttachmentUploadResponse"/>), which are project-scoped and
/// backed by a different store.
/// </summary>
public sealed record TaskAttachmentDto(
    string TaskId,
    string FileName,
    string MediaType,
    string Sha256,
    long SizeBytes,
    DateTime CreatedAt);

public sealed record UpdateTaskFileRequest(
    string ContentBase64,
    long ExpectedVersion,
    string? MediaType = null);

public sealed record TaskFileDto(
    string TaskId,
    string Path,
    string MediaType,
    long Version,
    long SizeBytes,
    DateTime UpdatedAt);

public sealed record TaskFileRevisionDto(
    long Version,
    DateTime UpdatedAt,
    string? ActorId);

public sealed record TaskFileHistoryResponse(
    string TaskId,
    string Path,
    IReadOnlyList<TaskFileRevisionDto> Revisions);

/// <summary>
/// Light summary of a task's most recent run, backing the legacy
/// <c>/api/tasks/{taskId}/output</c> route. Deliberately thin - full history
/// (all runs, all events) already exists via
/// <c>GetTaskHistoryAsync</c>/<c>ListAttemptsAsync</c>.
/// </summary>
public sealed record TaskOutputDto(
    RunDto? LatestRun,
    int EventCount,
    DateTime? LastEventAt);
