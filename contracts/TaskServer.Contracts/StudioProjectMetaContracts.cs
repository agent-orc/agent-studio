namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the G2 "project meta" P1 bundle: epics and their
/// linked sub-tasks, tags, per-project autonomy/execution-runner settings,
/// and thin pipeline projections layered on top of the existing
/// orchestration <see cref="FlowDefinitionDto"/> (see <c>OrchestrationContracts.cs</c>).
/// There is no P1 route that creates an epic - only list/detail/sub-task-create
/// and the completed-count projection are in scope - so epic creation is a
/// store-level capability only (<c>TaskServerStore.CreateEpicAsync</c>), used by
/// tests and by whichever later slice adds the authoring route.
/// </summary>
public static class EpicStates
{
    public const string Open = "open";
    public const string Done = "done";
}

public sealed record EpicDto(
    string EpicId,
    string ProjectId,
    string Title,
    string State,
    long Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateEpicRequest(string Title, string? EpicId = null, string State = EpicStates.Open);

public sealed record EpicTaskSummaryDto(string TaskId, string TaskKey, string Title, string State);

public sealed record EpicDetailDto(EpicDto Epic, int TaskCount, IReadOnlyList<EpicTaskSummaryDto> Tasks);

public sealed record EpicListResponse(IReadOnlyList<EpicDto> Epics);

/// <summary>
/// The request body for <c>POST /api/v1/studio/epics/{epicId}/sub-tasks</c> is
/// the existing <see cref="CreateTaskRequest"/> verbatim - a "sub-task" is a
/// normal task linked to its epic through the <c>epic_tasks</c> join table,
/// not a distinct task shape.
/// </summary>
public sealed record EpicSubTaskResponse(EpicDto Epic, TaskDto Task);

public sealed record EpicCompletedCountResponse(int Count);

public static class TagIdPrefixes
{
    public const string Tag = "tag";
}

public sealed record TagDto(string TagId, string Name, string? Color, long Version, DateTime CreatedAt);

public sealed record TagListResponse(IReadOnlyList<TagDto> Tags);

public sealed record CreateTagRequest(string Name, string? Color = null, string? TagId = null);

public sealed record DeleteTagResponse(bool Deleted, string TagId);

public sealed record ProjectAutonomyDto(string ProjectId, string AutonomyJson, long Version, DateTime UpdatedAt);

public sealed record UpdateProjectAutonomyRequest(string AutonomyJson, long? ExpectedVersion);

/// <summary>
/// Autonomy and execution-runner both persist on the single
/// <c>project_studio_settings</c> row per project and share its version
/// counter for simplicity - the legacy routes do not document an
/// independence requirement between the two settings, so this keeps one
/// conflict model instead of two.
/// </summary>
public sealed record ProjectExecutionRunnerDto(string ProjectId, string? ExecutionRunnerId, long Version, DateTime UpdatedAt);

public sealed record UpdateProjectExecutionRunnerRequest(string? ExecutionRunnerId, long? ExpectedVersion);

/// <summary>
/// One entry in a project's pipeline, projected from one element of the
/// underlying <see cref="FlowDefinitionDto"/>.<c>Stages</c> array.
/// <see cref="StepId"/> is the <c>OrchestrationStage</c> enum name (e.g.
/// <c>"ReviewDecision"</c>); the flow definition has no other per-step
/// configurable field, so there is no separate step "config" payload here.
/// </summary>
public sealed record PipelineStepDto(string StepId, int Order);

public sealed record PipelineHealthResponse(
    string ProjectId,
    long Version,
    int StepCount,
    IReadOnlyList<string> MissingConfigStepIds,
    bool Healthy,
    DateTime UpdatedAt);

public sealed record PipelineDefinitionResponse(
    string ProjectId,
    long Version,
    IReadOnlyList<PipelineStepDto> Steps,
    int MaxReissueAttempts,
    DateTime UpdatedAt);

public sealed record UpsertPipelineStepRequest(string StepId, int? Order, long? ExpectedVersion);

public sealed record UpdatePipelineStepOrderRequest(IReadOnlyList<string> StepIds, long? ExpectedVersion);

public sealed record PipelineStepProbeResultDto(bool Ok, string Detail);
