namespace AgentStudio.TaskServer.Contracts;

public static class HostOrchestratorContract
{
    public const string Current = "host-orchestrator/v1";
    public const string MinimumSupported = Current;
    public const string MaximumSupported = Current;

    public static bool Supports(string? version)
        => string.Equals(version, Current, StringComparison.Ordinal);

    public static bool Overlaps(string? minimum, string? maximum)
        => Supports(minimum) && Supports(maximum);
}

public sealed record HostCapacityDto(
    int Configured,
    int Effective,
    int Active,
    int Queued,
    int Free);

public sealed record HostCapabilityDto(
    string Kind,
    string Status,
    string? Scope = null,
    string? Reason = null,
    DateTime? ObservedAt = null);

public sealed record HostWorkStatusDto(
    string PermitId,
    string TaskId,
    string TaskKey,
    string RunId,
    string LeaseId,
    long Fence,
    string Phase,
    int? QueuePosition,
    int? ProcessId,
    DateTime AcceptedAt,
    DateTime LastActivityAt);

public sealed record HostPostProcessingStatusDto(
    string StepExecutionId,
    string RunId,
    string StepId,
    long ClaimFence,
    string Status,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    DateTime LastActivityAt);

public sealed record HostFaultDto(
    string Code,
    string Scope,
    string Message,
    string? RecoveryHint = null);

public sealed record HostReportRequest(
    string SchemaVersion,
    string HostId,
    string InstanceId,
    long Sequence,
    DateTime ObservedAt,
    HostCapacityDto Capacity,
    IReadOnlyList<HostCapabilityDto> Capabilities,
    IReadOnlyList<HostWorkStatusDto> Work,
    IReadOnlyList<HostPostProcessingStatusDto> PostProcessing,
    IReadOnlyList<HostFaultDto> Faults,
    IReadOnlyList<string>? AcknowledgedCommands = null);

public sealed record HostContractRangeDto(string Minimum, string Maximum);

public sealed record WorkPermitDto(
    string PermitId,
    TaskDto Task,
    long PolicyVersion,
    DateTime ExpiresAt);

public sealed record HostReportResponse(
    string Status,
    long AcceptedSequence,
    HostContractRangeDto Contract,
    long PolicyVersion,
    string Mode,
    IReadOnlyList<WorkPermitDto> AvailableWork,
    IReadOnlyList<string> Commands);

public sealed record WorkPermitAcceptRequest(
    string SchemaVersion,
    string HostId,
    string InstanceId,
    string RunnerId,
    long ReportSequence,
    long PolicyVersion,
    string IdempotencyKey,
    int RequestedTtlSeconds = 120);

public sealed record PostStepPlanDto(
    string StepExecutionId,
    string RunId,
    string StepId,
    string EligibleRunnerId,
    string Status);

public static class HostPostStepIds
{
    public const string WorktreeContainment = "post-worktree-containment";
    public const string ResultFinalization = "post-result-finalization";
}

public sealed record WorkPermitAcceptanceDto(
    string Status,
    string PermitId,
    RunDto Run,
    TaskDto Task,
    LeaseDto Lease,
    DateTime OfflineAuthorityDeadline,
    IReadOnlyList<PostStepPlanDto> PostProcessingPlan,
    SessionContinuationLedgerEntry? PreviousSession = null,
    MechanicalRoundDelta? MechanicalDelta = null,
    MechanicalFreshRunRoute? MechanicalFreshRoute = null,
    string? ContinuationBaseRef = null,
    string? ContinuationBaseSha = null);

public sealed record RunReconcileRequest(
    string SchemaVersion,
    string HostId,
    string InstanceId,
    string RunnerId,
    string LeaseId,
    long Fence,
    long ReportSequence,
    int RequestedTtlSeconds = 120,
    string? LeaseInstanceId = null);

public sealed record RunReconcileResponse(
    string Status,
    LeaseDto Lease,
    long AcceptedSequence);

public sealed record PostStepClaimRequest(
    string SchemaVersion,
    string HostId,
    string InstanceId,
    string RunnerId,
    string LeaseId,
    long RunFence,
    long ReportSequence,
    string IdempotencyKey,
    string? LeaseInstanceId = null);

public sealed record PostStepClaimResponse(
    string Status,
    PostStepPlanDto Step,
    long ClaimFence);

public sealed record PostStepCompleteRequest(
    string SchemaVersion,
    string HostId,
    string InstanceId,
    string RunnerId,
    string LeaseId,
    long RunFence,
    long ClaimFence,
    string Outcome,
    IReadOnlyList<string> ArtifactHashes,
    string IdempotencyKey,
    string? LeaseInstanceId = null);

public sealed record PostStepCompleteResponse(
    string Status,
    PostStepPlanDto Step,
    string Outcome,
    IReadOnlyList<string> ArtifactHashes);

public sealed record HostProjectionDto(
    string RunnerId,
    string HostId,
    string InstanceId,
    long Sequence,
    DateTime ObservedAt,
    DateTime ReceivedAt,
    HostCapacityDto Capacity,
    IReadOnlyList<HostCapabilityDto> Capabilities,
    IReadOnlyList<HostWorkStatusDto> Work,
    IReadOnlyList<HostPostProcessingStatusDto> PostProcessing,
    IReadOnlyList<HostFaultDto> Faults);
