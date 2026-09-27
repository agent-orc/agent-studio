namespace AgentStudio.TaskServer.Contracts;

public static class GateCapabilities
{
    public const string Executor = "gate-executor";
    public const string GitMaterialization = "gate:git";
    public const string BundleMaterialization = "gate:source-bundle";
}

public static class GateStates
{
    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Materializing = "materializing";
    public const string Running = "running";
    public const string Reporting = "reporting";
    public const string Cleaning = "cleaning";
    public const string Passed = "passed";
    public const string ProductFailed = "product-failed";
    public const string InfraRetry = "infra-retry";
    public const string InfraFailed = "infra-failed";
    public const string TimedOut = "timed-out";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string state) => state is
        Passed or ProductFailed or InfraFailed or TimedOut or Cancelled;
}

public static class GateFailureClasses
{
    public const string ProductFailure = "ProductFailure";
    public const string NoEligibleGateExecutor = "NoEligibleGateExecutor";
    public const string ExecutionTimeout = "ExecutionTimeout";
    public const string LostLease = "LostLease";
    public const string SnapshotUnavailable = "SnapshotUnavailable";
    public const string ToolFailure = "ToolFailure";
    public const string CleanupFailure = "CleanupFailure";
    public const string GateInfra = "GateInfra";
}

public sealed record GateCommand(
    string StepId,
    string FileName,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds,
    string WorkingSubdirectory = "");

public sealed record GatePlan(
    string GateId,
    int Version,
    IReadOnlyList<GateCommand> Commands,
    string WorkingSubdirectory,
    int OverallTimeoutSeconds,
    IReadOnlyList<string> RequiredCapabilities,
    int MaxOutputBytes,
    string CleanupPolicy);

public sealed record GateSubject(
    string SubjectId,
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedSha,
    string? ResultRef,
    string? SourceBundleArtifactId,
    string? SourceBundleSha256,
    string PlanHash,
    string PolicyHash,
    string PipelineDefinitionVersion,
    string TestSelectionAuditDigest,
    GatePlan Plan,
    DateTime CreatedAt,
    DateTime DispatchDeadline,
    int MaxAttempts);

public sealed record CreateGateSubjectRequest(
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedSha,
    string? ResultRef,
    string? SourceBundleArtifactId,
    string? SourceBundleSha256,
    string PlanHash,
    string PolicyHash,
    string PipelineDefinitionVersion,
    string TestSelectionAuditDigest,
    GatePlan Plan,
    DateTime DispatchDeadline,
    int MaxAttempts = 2);

public sealed record GateAttempt(
    string AttemptId,
    string SubjectId,
    int AttemptNumber,
    string State,
    string? ExecutorId,
    string? HostId,
    string? FailureClassification,
    string? Outcome,
    DateTime CreatedAt,
    DateTime? ClaimedAt,
    DateTime? ReportedAt,
    DateTime? CleanedAt,
    long Fence,
    DateTime? Deadline);

public sealed record GateLease(
    string LeaseId,
    string AttemptId,
    string ExecutorId,
    string InstanceId,
    string HostId,
    long Fence,
    long AuthorityEpoch,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string ResourceNamespace,
    int PortBase);

public sealed record GateClaimRequest(
    string ExecutorId,
    string InstanceId,
    int AvailableGateSlots = 1,
    int RequestedTtlSeconds = 120);

public sealed record GateClaimResponse(
    string Status,
    GateSubject? Subject = null,
    GateAttempt? Attempt = null,
    GateLease? Lease = null,
    string? Message = null);

public sealed record GateAuthority(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    long AuthorityEpoch);

public sealed record GatePhaseRequest(GateAuthority Authority, string Phase);
public sealed record GateRenewRequest(GateAuthority Authority, int RequestedTtlSeconds = 120);

public sealed record GateContainmentRequest(
    string ExecutorId,
    string InstanceId,
    string HostId,
    long Fence,
    string ResourceNamespace,
    bool NoProcesses,
    bool WorkspaceRemoved);

public sealed record GateCommandEvidence(
    string StepId,
    int? ExitCode,
    bool TimedOut,
    string OutputSha256,
    string OutputExcerpt,
    DateTime StartedAt,
    DateTime FinishedAt);

public sealed record GateReport(
    string Outcome,
    string? FailureClassification,
    string TestedSha,
    string TestedTree,
    bool DirtyBefore,
    bool DirtyAfter,
    IReadOnlyList<GateCommandEvidence> Commands,
    string EnvironmentIdentity,
    string ToolchainIdentity,
    IReadOnlyList<string> OutputDigests,
    IReadOnlyList<string> ArtifactDigests,
    string CleanupStatus);

public sealed record SubmitGateReportRequest(GateAuthority Authority, GateReport Report);

public sealed record GateStatus(
    GateSubject Subject,
    IReadOnlyList<GateAttempt> Attempts,
    GateReport? Report,
    double QueueAgeSeconds,
    string? ActiveHost,
    string Phase,
    int AttemptCount,
    DateTime? Deadline,
    string? TestedSha,
    string? TerminalOutcome,
    bool EvidenceAvailable);
