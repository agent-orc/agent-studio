using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Durable post-work subjects (AGT-3171 DP-S8). One subject represents one
/// auto-review lane entry of one card. Exactly one authority holder, a backend
/// pass or a host claim, may process it at a time; lifecycle transitions stay
/// with the server.
/// </summary>
public static class PostWorkCapabilities
{
    public const string Executor = "post-work-executor";
}

public static class PostWorkModes
{
    public const string Off = "off";
    public const string Shadow = "shadow";
    public const string Canary = "canary";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Shadow => Shadow,
        Canary => Canary,
        _ => Off,
    };
}

public static class PostWorkOwners
{
    public const string Backend = "backend";
    public const string Host = "host";
}

public static class PostWorkStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const string Reported = "reported";
    public const string Applied = "applied";
    public const string Retired = "retired";

    public static bool IsClosed(string state) => state is Applied or Retired;
}

public static class PostWorkCohortPhases
{
    public const string None = "none";
    public const string Draining = "draining";
    public const string Host = "host";
    public const string RolledBack = "rolled-back";
}

public static class PostWorkOutcomes
{
    public const string Decided = "decided";
    public const string Deferred = "deferred";
    public const string Blocked = "blocked";

    public static bool IsKnown(string? value) => value is Decided or Deferred or Blocked;
}

public static class PostWorkExecutorKinds
{
    public const string Backend = "backend";
    public const string Host = "host";
    public const string Container = "container";
    public const string Process = "process";
}

public static class PostWorkReconcileActions
{
    public const string None = "none";
    public const string MarkApplied = "mark-applied";
    public const string Retire = "retire";
    public const string Hold = "hold";
    public const string RequeueBackend = "requeue-backend";
    public const string RetainDeferral = "retain-deferral";
    public const string ApplyHostReport = "apply-host-report";
    public const string AwaitHost = "await-host";
}

public sealed record PostWorkStep(
    string StepId,
    string FileName,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds);

public sealed record PostWorkArtifactReference(string Kind, string Locator, string? Sha256 = null);

public sealed record MaterializePostWorkSubjectRequest(
    string ProjectId,
    string TaskId,
    string LaneEntryId,
    string? SourceRunId,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<PostWorkArtifactReference> Artifacts,
    IReadOnlyList<PostWorkStep> Steps,
    string Source);

public sealed record PostWorkSubject(
    string SubjectId,
    string IdempotencyKey,
    string ProjectId,
    string TaskId,
    string LaneEntryId,
    string? SourceRunId,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<PostWorkArtifactReference> Artifacts,
    IReadOnlyList<PostWorkStep> Steps,
    string PolicyDigest,
    string Owner,
    string State,
    string CohortPhase,
    string? CohortId,
    long LastFence,
    long AuthorityEpoch,
    string? DeferredReason,
    DateTime? RetryAt,
    int DeferralCount,
    string? Decision,
    string? DecisionReason,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? HostProposal,
    string ShadowStatus,
    IReadOnlyList<string> ShadowReasons,
    string? BackendShadowOutcome,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PostWorkLease(
    string AttemptId,
    string SubjectId,
    string LeaseId,
    string ExecutorKind,
    string ExecutorId,
    string InstanceId,
    string? HostId,
    long Fence,
    long AuthorityEpoch,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    long AllocationVersion,
    int GrantHalfCores);

public sealed record PostWorkAuthority(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    long AuthorityEpoch);

public sealed record PostWorkClaimRequest(
    string ExecutorId,
    string InstanceId,
    int RequestedTtlSeconds,
    long RuntimeCapacityAppliedVersion);

public sealed record PostWorkClaimResponse(
    string Status,
    PostWorkSubject? Subject = null,
    PostWorkLease? Lease = null,
    string? Message = null,
    IReadOnlyList<string>? RejectedReasons = null);

public sealed record PostWorkRenewRequest(PostWorkAuthority Authority, int RequestedTtlSeconds);

public sealed record PostWorkStepEvidence(
    string StepId,
    string Status,
    int? ExitCode,
    long DurationMs,
    string? OutputSha256,
    string? OutputTail);

public sealed record PostWorkReport(
    string ProposedOutcome,
    string Reason,
    DateTime? RetryAt,
    string ExecutorKind,
    IReadOnlyList<PostWorkStepEvidence> Evidence,
    string CleanupStatus);

public sealed record SubmitPostWorkReportRequest(PostWorkAuthority Authority, PostWorkReport Report);

/// <summary>A backend pass asks the same authority before it may process a card.</summary>
public sealed record BeginBackendPassRequest(
    string IdempotencyKey,
    string BackendInstanceId,
    string PassId,
    int LeaseSeconds);

public sealed record BackendPassDecision(
    bool Admitted,
    string Reason,
    PostWorkSubject? Subject = null,
    PostWorkLease? Lease = null);

public sealed record CompleteBackendPassRequest(
    string BackendInstanceId,
    string LeaseId,
    long Fence,
    string Outcome,
    string Reason,
    DateTime? RetryAt);

public sealed record PostWorkLaneFact(
    string ProjectId,
    string TaskId,
    string LaneEntryId,
    bool InAutoReviewLane,
    bool DecisionRecorded);

public sealed record ReconcilePostWorkRequest(
    string BackendInstanceId,
    IReadOnlyList<string> ProjectIds,
    IReadOnlyList<PostWorkLaneFact> LaneFacts);

public sealed record PostWorkReconcileAction(
    string SubjectId,
    string IdempotencyKey,
    string ProjectId,
    string TaskId,
    string Action,
    string Reason,
    DateTime? RetryAt = null);

public sealed record ReconcilePostWorkResponse(IReadOnlyList<PostWorkReconcileAction> Actions);

public sealed record StartPostWorkCohortRequest(string CohortId, string ProjectId, IReadOnlyList<string> TaskIds);

public sealed record AdvancePostWorkCohortRequest(IReadOnlyList<PostWorkLaneFact> LaneFacts);

public sealed record RollbackPostWorkCohortRequest(string Reason, IReadOnlyList<PostWorkLaneFact> LaneFacts);

public sealed record PostWorkCohortEvent(DateTime At, string Event, string? SubjectId, string? Detail);

public sealed record PostWorkCohortStatus(
    string CohortId,
    string ProjectId,
    IReadOnlyList<string> TaskIds,
    string Phase,
    int Draining,
    int HostOwned,
    int Closed,
    IReadOnlyList<PostWorkReconcileAction> Actions,
    IReadOnlyList<PostWorkCohortEvent> Record);

public sealed record PostWorkShadowSummary(
    int Subjects,
    int ShadowEligible,
    int ShadowRejected,
    int BackendDecided,
    int BackendDeferred,
    int Differences,
    IReadOnlyList<PostWorkSubject> Recent);

/// <summary>Stable keys and digests shared by the backend, Task Server and host.</summary>
public static class PostWorkIdentity
{
    private static readonly JsonSerializerOptions DigestJson = new(JsonSerializerDefaults.Web);

    /// <summary>One key per card lane entry; a re-entry into auto-review is a new subject.</summary>
    public static string IdempotencyKey(string projectId, string taskId, string laneEntryId)
        => "pw_" + Hex(projectId + "\n" + taskId + "\n" + laneEntryId)[..40];

    public static string PolicyDigest(
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<PostWorkArtifactReference> artifacts,
        IReadOnlyList<PostWorkStep> steps)
        => "sha256:" + Hex(JsonSerializer.Serialize(new
        {
            capabilities = requiredCapabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            artifacts,
            steps,
        }, DigestJson));

    public static string ReportDigest(PostWorkReport report)
        => Hex(JsonSerializer.Serialize(report, DigestJson));

    private static string Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
