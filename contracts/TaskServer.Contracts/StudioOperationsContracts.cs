namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The fenced Runner-operation dispatch contract behind Studio P2
/// "operations and insight" routes that need a checkout or CLI (drift and
/// security analysis, deployment and publish, wiki grading, design and
/// proposal generation, prompt/title generation). The Task Server never
/// performs this work in-process: it creates a durable, claimable
/// <see cref="StudioOperationDto"/>, a Runner claims it under the same
/// fenced lease shape as a coding or review attempt, executes the request
/// in a detached checkout, and reports back an immutable result. The Task
/// Server persists that result as the route's durable projection; it is the
/// only source of truth GET routes read from. This is additive to
/// <see cref="RunnerAttemptKinds"/>: a studio operation is never a task
/// run, carries no branch or push, and never appears on the board.
/// </summary>
public static class StudioOperationStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static readonly IReadOnlyCollection<string> Terminal = new[] { Succeeded, Failed, Canceled };
}

/// <summary>
/// The catalog of dispatchable kinds. Each P2 feature area that needs a
/// checkout or CLI owns exactly one constant here; the Runner-side executor
/// switches on it to pick the skill or tool to run.
/// </summary>
public static class StudioOperationKinds
{
    public const string DriftAdrCodeDrift = "drift.adr-code-drift";
    public const string DriftDocsMarketingDrift = "drift.docs-marketing-drift";
    public const string DriftSoftwareArchitectureDrift = "drift.software-architecture-drift";
    public const string DriftCodePatternDrift = "drift.code-pattern-drift";
    public const string SecurityAudit = "security.audit";
    public const string DeploymentCompile = "deployment.compile";
    public const string PublishPackage = "publish.package";
    public const string PublishWebsite = "publish.website";
    public const string WikiGradingRun = "wiki.grading-run";
    public const string DesignAction = "design.action";
    public const string ProposalsGenerate = "proposals.generate";
    public const string ProposalsRefineFeedback = "proposals.refine-feedback";
    public const string SkillReadinessFixTask = "skill-readiness.fix-task";
    public const string PromptEnhance = "prompt.enhance";
    public const string TitleGenerate = "title.generate";
}

public sealed record StudioOperationDto(
    string OperationId,
    string Kind,
    string? ProjectId,
    string? TaskId,
    string Status,
    string RequestJson,
    string? ResultJson,
    string? Error,
    string? Trigger,
    string? Severity,
    string? Topic,
    string? RunnerId,
    string? LeaseId,
    long Fence,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClaimedAt,
    DateTime? CompletedAt);

public sealed record StudioOperationAcceptedResponse(string OperationId, string Kind, string Status, DateTime CreatedAt);

public sealed record ClaimStudioOperationRequest(string RunnerId, string InstanceId, int RequestedTtlSeconds = 120);

public sealed record ClaimStudioOperationResponse(string Status, StudioOperationDto? Operation = null, string? Message = null);

public sealed record StudioOperationLeaseRequest(string RunnerId, string InstanceId, string LeaseId, long Fence);

public sealed record AppendStudioOperationEventRequest(
    string RunnerId, string InstanceId, string LeaseId, long Fence, string Kind, string PayloadJson);

public sealed record AppendStudioOperationArtifactRequest(
    string RunnerId, string InstanceId, string LeaseId, long Fence, string Name, string MediaType, string ContentJson);

public sealed record CompleteStudioOperationRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string Outcome,
    string? ResultJson = null,
    string? ErrorMessage = null);

public sealed record StudioOperationEventDto(long Cursor, string OperationId, DateTime OccurredAt, string Kind, string PayloadJson);

public sealed record StudioOperationArtifactDto(
    string ArtifactId, string OperationId, string Name, string MediaType, string ContentJson, DateTime CreatedAt);
