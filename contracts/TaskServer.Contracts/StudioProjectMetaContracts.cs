namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the G2 "project meta" P1 bundle: tags, per-project autonomy/execution-runner settings,
/// and thin pipeline projections layered on top of the existing
/// orchestration <see cref="FlowDefinitionDto"/> (see <c>OrchestrationContracts.cs</c>).
/// </summary>
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
