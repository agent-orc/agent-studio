namespace AgentStudio.TaskServer.Contracts;

public static class GateCapabilities
{
    public const string Executor = "gate-executor";
    public const string GitMaterialization = "gate:git";
    public const string BundleMaterialization = "gate:source-bundle";
}

/// <summary>
/// What a gate subject verifies (DP-S4). <see cref="Run"/> is the coding-run
/// gate bound to a Task Server run. The verification kinds bind an exact
/// candidate from the monolith's merge, promotion or batch lane: their task and
/// run fields are opaque monolith references, and the Task Server owns only
/// the attempt, lease, fence and evidence.
/// </summary>
public static class GateSubjectKinds
{
    public const string Run = "run";
    public const string Merge = "merge";
    public const string Promotion = "promotion";
    public const string Batch = "batch";

    public static string Normalize(string? kind) => string.IsNullOrWhiteSpace(kind) ? Run : kind;

    public static bool IsKnown(string? kind) => Normalize(kind) is Run or Merge or Promotion or Batch;

    public static bool IsVerification(string? kind) => Normalize(kind) is Merge or Promotion or Batch;

    /// <summary>Verification kind of a monolith gate id, or null when the gate stays on its current path.</summary>
    public static string? ForGateId(string gateId) => gateId switch
    {
        "merge-into-develop-build-gate" => Merge,
        "merge-into-main" => Promotion,
        "batch-gate" or "batch-base" => Batch,
        _ => null,
    };
}

/// <summary>Bounds of the immutable gate bundle artifact endpoint.</summary>
public static class GateBundleLimits
{
    /// <summary>Same ceiling as the SSH bridge transport bundle.</summary>
    public const long MaxBytes = 1024L * 1024 * 1024;

    public static string Locator(string sha256) => "/api/v1/gates/bundles/" + sha256.ToLowerInvariant();
}

/// <summary>Content-addressed source bundle a host downloads from the gate authority.</summary>
public sealed record GateBundleLocator(string Sha256, long SizeBytes, string Locator);

/// <summary>
/// Host proof of what it executed: the frozen plan and policy digests it
/// checked, the materialized HEAD and, for a bundle subject, the downloaded
/// bundle digest and size.
/// </summary>
public sealed record GateSubjectProof(
    string PlanHash,
    string PolicyHash,
    string Head,
    string? BundleSha256 = null,
    long? BundleBytes = null);

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
    string CleanupPolicy,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? SubjectBindingDigest = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? SourcePaths = null);

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
    int MaxAttempts,
    string? BatchId = null,
    string? MembershipDigest = null,
    string? BaseSha = null,
    IReadOnlyList<string>? MemberRunIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Kind = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GateBundleLocator? Bundle = null);

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
    int MaxAttempts = 2,
    string? BatchId = null,
    string? MembershipDigest = null,
    string? BaseSha = null,
    IReadOnlyList<string>? MemberRunIds = null,
    string? Kind = null,
    GateBundleLocator? Bundle = null);

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
    int PortBase,
    int GrantHalfCores = 0,
    long AllocationVersion = 0);

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
    string CleanupStatus,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GateSubjectProof? Proof = null);

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
