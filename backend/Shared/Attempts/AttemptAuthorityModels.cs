namespace AgentStudio.Shared;

public enum AttemptLifecycleState
{
    Pending,
    Leased,
    Completed,
    Failed,
    Cancelled,
    Superseded,
}

public enum ReviewTerminalOutcome
{
    InfrastructureFailure,
    ProductFailure,
    Inconclusive,
    Pass,
    Cancellation,
    Superseded,
}

public sealed record AttemptLeaseDto(
    string LeaseId,
    long Fence,
    long AuthorityEpoch,
    string ExecutorId,
    string HostId,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    DateTime LastHeartbeat,
    string? ExecutorDisplayName = null,
    string? BackendName = null,
    int ProcessId = 0,
    string? ClientId = null,
    string? LeaseInstanceId = null);

public sealed record ReviewSubjectDto(
    string SubjectId,
    string RepositoryId,
    string ExpectedResultSha,
    string SourceRunAttemptId,
    string TaskRequirementsHash,
    string ReviewPolicyHash,
    IReadOnlyList<string> EvidenceDigestInputs,
    DateTime CreatedAt,
    string? RepositoryUrl = null,
    string? ResultRef = null,
    AgentStudio.TaskServer.Contracts.ReviewPlanDto? Plan = null);

public sealed record RunAttemptDto(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string? SourceAttemptId,
    AttemptLifecycleState State,
    AttemptLeaseDto? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    string? ResultSha,
    string? TerminalOutcome,
    string? TerminalReason,
    IReadOnlyList<string> EvidenceDigests,
    AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope? ResultEnvelope = null,
    string? ResultEnvelopeDigest = null);

public sealed record ReviewAttemptDto(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string SourceRunAttemptId,
    string? SourceReviewAttemptId,
    ReviewSubjectDto Subject,
    AttemptLifecycleState State,
    AttemptLeaseDto? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    ReviewTerminalOutcome? Outcome,
    string? FailureClassification,
    string? TestedResultSha,
    string? TerminalReason,
    IReadOnlyList<ReviewReportDeliveryDto> Reports,
    AgentStudio.TaskServer.Contracts.ReviewPlanDto? EffectivePlan = null);

/// <summary>
/// Result of resolving a queued review immediately before the authority mints
/// its lease. A deferred result leaves the attempt pending and lets the claim
/// scan consider another eligible attempt. An admitted plan is persisted with
/// the new fence, so the executor sees one fixed command set for that lease.
/// </summary>
public sealed record ReviewClaimPreparation(
    bool CanClaim,
    AgentStudio.TaskServer.Contracts.ReviewPlanDto? EffectivePlan = null,
    string? Message = null);

public sealed record ReviewReportDeliveryDto(
    string IdempotencyKey,
    long Fence,
    long AuthorityEpoch,
    string MaterializedResultSha,
    ReviewTerminalOutcome Outcome,
    string? FailureClassification,
    string? Reason,
    AttemptWriteStatus AuthorityStatus,
    DateTime ReceivedAt);

public sealed record AttemptWriteReference(
    string AttemptId,
    long Fence,
    long AuthorityEpoch,
    string IdempotencyKey);

public enum AttemptWriteStatus
{
    Accepted,
    Duplicate,
    Invalid,
    NotFound,
    StaleFence,
    AuthorityEpochMismatch,
    LeaseExpired,
    Superseded,
    SubjectMismatch,
    InvalidState,
}

public sealed record AttemptWriteResult(
    AttemptWriteStatus Status,
    string AttemptId,
    string? Message = null,
    RunAttemptDto? RunAttempt = null,
    ReviewAttemptDto? ReviewAttempt = null)
{
    public bool Accepted => Status is AttemptWriteStatus.Accepted or AttemptWriteStatus.Duplicate;
}

public sealed record CreateReviewAttemptRequest(
    string TaskKey,
    string RepositoryId,
    string ExpectedResultSha,
    string SourceRunAttemptId,
    string TaskRequirementsHash,
    string ReviewPolicyHash,
    IReadOnlyList<string>? EvidenceDigestInputs,
    string IdempotencyKey,
    string? SourceReviewAttemptId = null,
    string? RepositoryUrl = null,
    string? ResultRef = null,
    AgentStudio.TaskServer.Contracts.ReviewPlanDto? Plan = null);

public sealed record ClaimReviewAttemptRequest(
    string AttemptId,
    string ExecutorId,
    string HostId,
    string IdempotencyKey,
    int? RequestedTtlSeconds = null);

public sealed record RenewAttemptLeaseRequest(
    AttemptWriteReference Write,
    string ExecutorId,
    int? RequestedTtlSeconds = null);

/// <summary>
/// Named settlement contract for a fenced RunAttempt. Integration identity,
/// executor ownership, and the immutable result envelope travel as one object
/// so adding a field cannot shift a positional service call.
/// </summary>
public sealed record SettleRunAttemptRequest
{
    public required AttemptWriteReference Write { get; init; }
    public required string Outcome { get; init; }
    public string? ResultSha { get; init; }
    public string? Reason { get; init; }
    public string? ExecutorId { get; init; }
    public string? LeaseId { get; init; }
    public string? ExpectedTaskKey { get; init; }
    public bool RequireResultSha { get; init; } = true;
    public AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope? ResultEnvelope { get; init; }
    public string? ResultEnvelopeDigest { get; init; }
}

public sealed record SettleReviewAttemptRequest(
    AttemptWriteReference Write,
    string MaterializedResultSha,
    ReviewTerminalOutcome Outcome,
    string? FailureClassification = null,
    string? Reason = null);

public sealed record AttemptAuthorityProjection(
    string TaskKey,
    long AuthorityEpoch,
    RunAttemptDto? CurrentRunAttempt,
    ReviewSubjectDto? CurrentReviewSubject,
    ReviewAttemptDto? CurrentReviewAttempt,
    IReadOnlyList<RunAttemptDto> RunAttempts,
    IReadOnlyList<ReviewAttemptDto> ReviewAttempts,
    bool LegacyTask);
