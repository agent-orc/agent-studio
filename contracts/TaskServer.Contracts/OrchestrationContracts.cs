namespace AgentStudio.TaskServer.Contracts;

public enum OrchestrationStage
{
    ReviewDecision,
    Council,
    PostProcessing,
    GateDispatch,
    CompletionJudge,
    OperatorSweep,
}

public enum OrchestrationAction
{
    Continue,
    Reissue,
    Escalate,
    Complete,
    Fail,
}

public static class OrchestrationDefaults
{
    public const int MaxReissueAttempts = 2;
    public const int MaxIdenticalAspectBlockRounds = 2;

    public static IReadOnlyList<OrchestrationStage> CreateStages()
        =>
        [
            OrchestrationStage.ReviewDecision,
            OrchestrationStage.Council,
            OrchestrationStage.PostProcessing,
            OrchestrationStage.GateDispatch,
            OrchestrationStage.CompletionJudge,
        ];
}

public sealed record ReviewOrchestrationGateDto(
    string StepId,
    string Aspect,
    string Status,
    string? Classification = null);

/// <summary>
/// Immutable handoff from one fenced Remote Review attempt into the
/// server-owned post-processing decision flow. The coding RunAttempt and
/// Result-SHA remain explicit so this payload cannot degrade into a legacy
/// task-folder or sidecar review subject.
/// </summary>
public sealed record ReviewOrchestrationPayloadDto(
    string RunAttemptId,
    string ReviewSubjectId,
    string ReviewAttemptId,
    string ResultSha,
    string ReviewPolicyHash,
    string ReviewReportSha256,
    string ReviewOutcome,
    string? FailureClassification,
    string? Summary,
    IReadOnlyList<ReviewVerdictDto> Verdicts,
    IReadOnlyList<ReviewOrchestrationGateDto> Gates,
    string? ResultRef = null,
    ReviewPlanDto? ReviewPlan = null);

public sealed record FlowDefinitionDto(
    string ProjectId,
    long Version,
    IReadOnlyList<OrchestrationStage> Stages,
    int MaxReissueAttempts,
    DateTime UpdatedAt);

public sealed record UpsertFlowDefinitionRequest(
    long? ExpectedVersion,
    IReadOnlyList<OrchestrationStage> Stages,
    int MaxReissueAttempts = OrchestrationDefaults.MaxReissueAttempts);

public sealed record CreateOrchestrationRunRequest(
    string TaskId,
    string PayloadJson,
    string IdempotencyKey,
    OrchestrationStage Stage = OrchestrationStage.ReviewDecision,
    string? ExpectedSourceState = null,
    string? AuthorityAttemptId = null,
    long AuthorityFence = 0,
    long AuthorityEpoch = 0);

public sealed record OrchestrationStageResultDto(
    long Sequence,
    OrchestrationStage Stage,
    OrchestrationAction Action,
    string OutputJson,
    DateTime CompletedAt);

public sealed record OrchestrationRunDto(
    string RunId,
    string ProjectId,
    string TaskId,
    long DefinitionVersion,
    string Status,
    OrchestrationStage CurrentStage,
    string PayloadJson,
    int ReissueAttempts,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<OrchestrationStageResultDto>? StageResults = null,
    long TaskVersion = 0);

public sealed record OrchestrationLeaseDto(
    string LeaseId,
    string RunId,
    string EngineId,
    string InstanceId,
    long Fence,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string Status);

public sealed record OrchestrationClaimRequest(
    string EngineId,
    string InstanceId,
    IReadOnlyList<OrchestrationStage> SupportedStages,
    int RequestedTtlSeconds = 120);

public sealed record OrchestrationClaimResponse(
    string Status,
    OrchestrationRunDto? Run = null,
    OrchestrationLeaseDto? Lease = null,
    string? Message = null);

public sealed record OrchestrationLeaseRenewRequest(
    string EngineId,
    string InstanceId,
    string LeaseId,
    long Fence,
    int RequestedTtlSeconds = 120);

public sealed record CompleteOrchestrationStageRequest(
    string EngineId,
    string InstanceId,
    string LeaseId,
    long Fence,
    OrchestrationStage Stage,
    OrchestrationAction Action,
    string OutputJson,
    string IdempotencyKey);

public sealed record ReleaseOrchestrationLeaseRequest(
    string EngineId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string Reason);
