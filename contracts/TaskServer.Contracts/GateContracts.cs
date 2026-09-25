namespace AgentStudio.TaskServer.Contracts;

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

    public static bool IsTerminal(string state) => state is Passed or ProductFailed or InfraFailed or TimedOut or Cancelled;
}

public static class GateClassifications
{
    public const string ProductFailure = "ProductFailure";
    public const string NoEligibleGateExecutor = "NoEligibleGateExecutor";
    public const string ExecutionTimeout = "ExecutionTimeout";
    public const string LostLease = "LostLease";
    public const string MissingSnapshot = "MissingSnapshot";
    public const string ToolFailure = "ToolFailure";
    public const string CleanupFailure = "CleanupFailure";
    public const string GateInfra = "GateInfra";
}

public sealed record GateCommand(
    string StepId,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingSubdirectory,
    int DeadlineSeconds);

public sealed record GatePlan(
    string GateId,
    int Version,
    IReadOnlyList<GateCommand> Commands,
    string WorkingSubdirectory,
    int OverallDeadlineSeconds,
    IReadOnlyList<string> RequiredCapabilities,
    int MaxOutputBytes,
    string CleanupPolicy);

public sealed record CreateGateSubjectRequest(
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedSha,
    string? ResultRef,
    string? SourceBundleId,
    string? SourceBundleSha256,
    string PlanHash,
    string PolicyHash,
    int PipelineDefinitionVersion,
    string TestSelectionAuditDigest,
    GatePlan Plan,
    DateTime DispatchDeadline,
    int RetryBudget = 1);

public sealed record GateSubject(
    string SubjectId,
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedSha,
    string? ResultRef,
    string? SourceBundleId,
    string? SourceBundleSha256,
    string PlanHash,
    string PolicyHash,
    int PipelineDefinitionVersion,
    string TestSelectionAuditDigest,
    GatePlan Plan,
    DateTime DispatchDeadline,
    int RetryBudget,
    DateTime CreatedAt);

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
    DateTime? CleanedAt);

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
    string ResourceNamespace);

public sealed record GateCommandEvidence(
    string StepId,
    int? ExitCode,
    bool TimedOut,
    string OutputSha256,
    int OutputBytes,
    DateTime StartedAt,
    DateTime FinishedAt);

public sealed record GateReport(
    string Outcome,
    string? Classification,
    string? TestedSha,
    string? TestedTree,
    bool DirtyBefore,
    bool DirtyAfter,
    IReadOnlyList<GateCommandEvidence> Commands,
    string EnvironmentIdentity,
    string ToolchainIdentity,
    IReadOnlyDictionary<string, string> OutputDigests,
    IReadOnlyDictionary<string, string> ArtifactDigests,
    string CleanupStatus);

public sealed record GateClaimRequest(string ExecutorId, string InstanceId, int AvailableGateSlots = 1, int LeaseSeconds = 120);
public sealed record GateClaimResponse(string Status, GateSubject? Subject = null, GateAttempt? Attempt = null, GateLease? Lease = null, string? Message = null);
public sealed record GateAuthorityRequest(string ExecutorId, string InstanceId, string LeaseId, long Fence, long AuthorityEpoch);
public sealed record GatePhaseRequest(GateAuthorityRequest Authority, string State);
public sealed record GateRenewRequest(GateAuthorityRequest Authority, int LeaseSeconds = 120);
public sealed record SubmitGateReportRequest(GateAuthorityRequest Authority, GateReport Report, string IdempotencyKey);
public sealed record CancelGateRequest(string Reason);
public sealed record GateContainmentReceipt(
    GateAuthorityRequest PreviousAuthority,
    string CurrentInstanceId,
    string ResourceNamespace,
    bool NoProcesses,
    bool WorkspaceAbsent,
    DateTime ObservedAt,
    string IdempotencyKey);
public sealed record GateAttemptView(GateAttempt Attempt, GateLease? Lease, GateReport? Report);
public sealed record GateStatusView(
    GateSubject Subject,
    IReadOnlyList<GateAttemptView> Attempts,
    TimeSpan QueueAge,
    string? ActiveHost,
    string Phase,
    int AttemptCount,
    DateTime Deadline,
    string? TestedSha,
    string? TerminalOutcome,
    bool EvidenceAvailable);
