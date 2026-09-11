namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio "task metadata" P1 bundle: single-field
/// mutations on a task (project, cli type, epic, model, references, release,
/// tags, task type, thinking level, title). <c>title</c> and
/// <c>change-project</c> mutate the shared <c>tasks</c> row directly (and so
/// version against <see cref="TaskDto.Version"/>); every other field here has
/// no column on <c>tasks</c> and is tracked instead on the lazily-created
/// <c>task_studio_fields</c> row for the task, whose own <c>version</c> is
/// the optimistic-concurrency token those routes expect as
/// <c>ExpectedVersion</c> (0 means "no row yet / first write").
/// </summary>
public sealed record ChangeTaskProjectRequest(string ProjectId, long ExpectedVersion);

public sealed record SetTaskTitleRequest(string Title, long ExpectedVersion);

public sealed record SetTaskCliTypeRequest(string? CliType, long ExpectedVersion);
public sealed record SetTaskModelRequest(string? Model, long ExpectedVersion);
public sealed record SetTaskThinkingLevelRequest(string? ThinkingLevel, long ExpectedVersion);
public sealed record SetTaskTaskTypeRequest(string? TaskType, long ExpectedVersion);
public sealed record SetTaskReleaseRequest(string? Release, long ExpectedVersion);
public sealed record SetTaskEpicRequest(string? EpicId, long ExpectedVersion);

public sealed record SetTaskReferencesRequest(IReadOnlyList<string> ReferenceTaskIds, long ExpectedVersion);
public sealed record SetTaskTagsRequest(IReadOnlyList<string> TagIds, long ExpectedVersion);

/// <summary>
/// The lazily-created <c>task_studio_fields</c> row for a task: one shared
/// version-tracked record backing every single-field studio mutation that
/// does not otherwise have a home on <c>tasks</c> (including epic
/// assignment, whose actual membership lives in the separate
/// <c>epic_tasks</c> join table owned by the project-meta group).
/// </summary>
public sealed record TaskStudioFieldsDto(
    string TaskId,
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    string? TaskType,
    string? Release,
    string? EpicId,
    long Version,
    DateTime UpdatedAt);

public sealed record TaskReferencesDto(
    string TaskId,
    IReadOnlyList<string> ReferenceTaskIds,
    long Version,
    DateTime UpdatedAt);

public sealed record TaskTagsDto(
    string TaskId,
    IReadOnlyList<string> TagIds,
    long Version,
    DateTime UpdatedAt);
