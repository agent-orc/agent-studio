namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Outcomes an integration executor reports for one accepted delivery
/// (AGT-3061). The Task Server applies the lane through
/// <c>IntegrationResultPolicy</c>.
/// </summary>
public static class IntegrationOutcomeKinds
{
    /// <summary>The executor published a new integration tip that contains the result SHA.</summary>
    public const string Integrated = "integrated";

    /// <summary>The integration ref already contained the result SHA; nothing was pushed.</summary>
    public const string AlreadyIntegrated = "already-integrated";

    /// <summary>The result does not merge cleanly into the integration ref.</summary>
    public const string Conflict = "conflict";

    /// <summary>The non-force push was rejected because the integration ref moved.</summary>
    public const string StaleBase = "stale-base";

    /// <summary>A red develop incident covers paths changed by this delivery; keep it queued.</summary>
    public const string BlockedFailingArea = "blocked-failing-area";

    public const string Failed = "failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Integrated, AlreadyIntegrated, Conflict, StaleBase, BlockedFailingArea, Failed], StringComparer.Ordinal);
}

public static class IntegrationCapabilities
{
    /// <summary>Registration capability of a runner host that executes integration commands.</summary>
    public const string Executor = "integration-executor";
}

public static class IntegrationCommandStates
{
    public const string Queued = "queued";
    public const string Leased = "leased";
    public const string Integrated = "integrated";
    public const string Escalated = "escalated";
    public const string Superseded = "superseded";
}

/// <summary>
/// One durable integration command: publish the accepted, fenced result of a
/// review-passed delivery to the project's integration ref. Created by the
/// Task Server when it applies an engine AcceptAndIntegrate decision under the
/// reviewed-publication delivery policy; executed by a runner host that holds
/// the project's publication lease.
/// </summary>
public sealed record IntegrationCommandDto(
    string IntegrationId,
    string ProjectId,
    string TaskId,
    string OrchestrationRunId,
    string SourceRunId,
    string ReviewAttemptId,
    string RepositoryId,
    string RepositoryUrl,
    string IntegrationRef,
    string ResultRef,
    string ResultSha,
    string Status,
    int Attempts,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? IntegratedSha = null,
    string? LastOutcome = null,
    string? Reason = null);

/// <summary>
/// The single-holder lease on one project's integration ref. Every grant
/// increments the fence; a report is accepted only from the current holder.
/// </summary>
public sealed record PublicationLeaseDto(
    string LeaseId,
    string ProjectId,
    string IntegrationRef,
    string IntegrationId,
    string RunnerId,
    string InstanceId,
    long Fence,
    DateTime AcquiredAt,
    DateTime ExpiresAt);

public sealed record IntegrationClaimRequest(
    string RunnerId,
    string InstanceId,
    int RequestedTtlSeconds = 600);

public sealed record IntegrationClaimResponse(
    string Status,
    IntegrationCommandDto? Command = null,
    PublicationLeaseDto? Lease = null,
    string? Message = null);

/// <summary>
/// Extends the current publication lease while its holder still fetches,
/// merges, or pushes. Only the holder of the current fence can renew it, and
/// only before it expires.
/// </summary>
public sealed record IntegrationLeaseRenewRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    int RequestedTtlSeconds = 600);

public sealed record IntegrationReportRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    string Outcome,
    string? IntegratedSha = null,
    string? Detail = null);

public sealed record IntegrationReportResponse(
    IntegrationCommandDto Command,
    string? TaskState,
    string Reason);
