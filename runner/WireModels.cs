namespace AgentRunner;

// Protocol-v0 compatibility declarations for the co-hosted legacy backend.
// Separated Task Server protocols use the shared TaskServer.Contracts package.
// Delete these declarations when the published compatibility window drops v0;
// they must not become a second durable model or expand with new v1 features.
//
// The server serialises with the ASP.NET "web" defaults (camelCase names,
// case-insensitive binding), so plain PascalCase records serialise/deserialise
// cleanly in both directions via System.Text.Json with camelCase policy.

/// <summary>
/// Runner -> Server: register the runner as a client identity
/// (/api/clients/register). The server's X-Client-Id middleware rejects every
/// mutation (lease, log, artifact, completion) from an unregistered id
/// with 401 <c>client-unknown</c>, so the runner must register before it writes.
/// Registration is idempotent on <see cref="DisplayName"/> and the open-path
/// carve-out means it needs no prior identity itself.
/// </summary>
public sealed record ClientRegisterRequest(string DisplayName, string? Kind = null, string? Notes = null);

public sealed record RunnerGitCapabilityRequest(string Status, string? Detail, DateTime CheckedAt);

/// <summary>
/// Protocol-v0 projection of the existing typed runner-event endpoint. It is
/// used only to preserve protocol-novelty diagnostics while a co-hosted backend
/// remains inside the compatibility window.
/// </summary>
public sealed record RunnerEventIngestRequest(
    string TaskKey,
    string Kind,
    DateTime Timestamp,
    string? Message,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? EventId = null,
    string? Cli = null,
    string? Severity = null,
    string? Code = null);

/// <summary>Server projection of a registered client identity; only <see cref="Id"/> is used (as the X-Client-Id).</summary>
public sealed record ClientRegisterResponse(string Id, string DisplayName, string Kind);

/// <summary>
/// Server response from GET /api/clients/{id}. The runner only needs the
/// canonical id and lifecycle kind to prove that a configured identity exists
/// and is still active before it starts issuing mutations under that id.
/// </summary>
public sealed record ClientIdentityDetail(ClientIdentitySummary Identity);

public sealed record ClientIdentitySummary(string Id, string Kind);

/// <summary>Runner -> Server: acquire the fenced run lease for a task (/api/runner/lease/acquire).</summary>
public sealed record RunLeaseAcquireRequest(
    string TaskKey,
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null,
    string? RepositoryId = null,
    string? SourceRunAttemptId = null,
    string? IdempotencyKey = null,
    string? LeaseInstanceId = null);

/// <summary>Runner -> Server: heartbeat to extend the lease (/api/runner/lease/renew).</summary>
public sealed record RunLeaseHeartbeatRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    int? RequestedTtlSeconds = null,
    string? AttemptId = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null,
    RunnerProcessInventory? Inventory = null);

/// <summary>Runner -> Server: drop the lease when the run ends (/api/runner/lease/release).</summary>
public sealed record RunLeaseReleaseRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    string? AttemptId = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null,
    string? Outcome = null);

/// <summary>Server projection of the current lease holder + fencing token.</summary>
public sealed record RunLeaseInfoDto(
    string TaskKey,
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    string LeaseId,
    long FencingToken,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string? AttemptId = null,
    long AuthorityEpoch = 0)
{
    public DateTime LastHeartbeatAt { get; init; } = AcquiredAt;
    public string? ClientId { get; init; }
}

/// <summary>
/// Server reply to any lease operation. <see cref="Granted"/> is the boolean the
/// runner branches on (true means this runner holds the lease and may proceed);
/// <see cref="Outcome"/> is the discriminator used only for logging.
/// </summary>
public sealed record RunLeaseResponse(
    string Outcome,
    bool Granted,
    RunLeaseInfoDto? Lease,
    string? Message = null,
    IReadOnlyList<RunnerReconciliationAction>? ReconciliationActions = null);

public sealed record RunnerClaimRequest(
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null,
    HostTelemetrySample? Telemetry = null,
    int AvailableSlots = 1,
    int? ActiveSlots = null,
    string? IdempotencyKey = null,
    IReadOnlyList<string>? ActiveTaskKeys = null,
    RunnerProcessInventory? Inventory = null,
    RunnerProjectPreflightReport? ProjectPreflight = null,
    // Ceiling this daemon currently runs with, and when it adopted it. Pure
    // telemetry: the server owns the policy and echoes it back on every poll.
    int? EffectiveMaxParallelism = null,
    DateTime? EffectiveMaxParallelismAppliedAt = null,
    // RUNNER_MAX_PARALLELISM. Seeds the central host ceiling on first contact.
    int? BootstrapMaxParallelism = null,
    string? CapabilityInstanceId = null,
    IReadOnlyList<string>? RequiredCapabilities = null);

public sealed record RunnerProcessInventory(
    DateTime ObservedAt,
    IReadOnlyList<RunnerProcessInfo> Processes,
    IReadOnlyList<RunnerInvariantReport>? Reports = null,
    IReadOnlyList<string>? AcknowledgedActionIds = null);

public sealed record RunnerProcessInfo(
    string RunId,
    string TaskKey,
    int Pid,
    string Cwd,
    DateTime StartedAt);

public sealed record RunnerInvariantReport(
    string ReportId,
    string Category,
    DateTime DetectedAt,
    string Action,
    string Detail,
    string? RunId = null,
    string? TaskKey = null,
    int? Pid = null);

public sealed record RunnerReconciliationAction(
    string ActionId,
    string Category,
    string Action,
    string Detail,
    int? Pid = null,
    string? RunId = null,
    string? TaskKey = null);

public sealed record RunnerProjectPreflightReport(
    string ProjectId,
    string RegistrationFingerprint,
    bool Succeeded,
    string Detail,
    DateTime CheckedAt,
    string FetchUrl,
    string PushUrl);

/// <summary>Thirty-second host snapshot piggybacked on the daemon claim poll.</summary>
public sealed record HostTelemetrySample(
    DateTime Timestamp,
    double? CpuPercent,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? SwapInBytesPerSecond,
    long? SwapOutBytesPerSecond,
    double? CpuStealPercent,
    double? IoWaitPercent,
    int CpuCores,
    int ActiveSlots,
    string TaskServerConnectionStatus = TaskServerConnectivityStates.Unknown,
    DateTime? TaskServerConnectionObservedAt = null,
    DateTime? TaskServerConnectionFailureStartedAt = null,
    int TaskServerConnectionConsecutiveFailures = 0,
    DateTime? TaskServerConnectionEscalatedAt = null,
    string? TaskServerConnectionLastError = null,
    DateTime? TaskServerConnectionLastRecoveredAt = null);

public enum RunnerClaimStatus { Claimed, Empty, PreflightRequired, PreflightFailed, Invalid }

/// <summary>
/// T0b — the claimed card's execution specification (CAR migration plan §3 T0b).
/// The server states which CLI, model and reasoning level the <b>card</b> chose;
/// <see cref="RunnerOptions.CliBin"/> / <see cref="RunnerOptions.CliArgs"/> stop
/// being the truth and become the fallback for whatever the spec leaves open.
///
/// <para>
/// Every field is optional. A server that predates T0b sends no spec at all, and
/// the runner then behaves exactly as it did before — that is what makes the
/// change safe to deploy server-first.
/// <see cref="PermissionMode"/> and <see cref="ContextMode"/> are transported and
/// logged but deliberately not turned into CLI flags yet: injecting a permission
/// flag or a clean config home remotely is a behaviour jump that belongs to T1
/// (and clean context additionally waits for CAR-B, or the host's own OAuth token
/// gets copied instead of linked).
/// </para>
/// </summary>
public sealed record RunSpecDto(
    string? CliType = null,
    string? Model = null,
    string? ThinkingLevel = null,
    string? PermissionMode = null,
    string? ContextMode = null,
    // Server-composed mode framing and prompt enrichment. Keeping both in one
    // field gives daemon claims and persisted slots one deterministic seam.
    string? ModeFraming = null);

public sealed record RunnerClaimResponse(
    RunnerClaimStatus Status,
    string? TaskKey = null,
    string? JobId = null,
    string? ProjectName = null,
    RunLeaseInfoDto? Lease = null,
    string? Message = null,
    string? ProjectId = null,
    string? RepositoryUrl = null,
    string? DefaultBranch = null,
    string? TaskKind = null,
    string? RunId = null,
    string? LeaseInstanceId = null,
    IReadOnlyList<RunnerReconciliationAction>? ReconciliationActions = null,
    string? RegistrationFingerprint = null,
    // Central host capacity, echoed on every poll whether or not work was
    // granted. The daemon adopts these instead of its own environment values.
    int? DesiredMaxParallelism = null,
    int? TargetLoadPercent = null,
    string? RampStrategy = null,
    string? AdmissionReason = null,
    // T0b: additive execution spec; an older server simply omits it.
    RunSpecDto? RunSpec = null);

public static class RemoteChatWorkKinds
{
    public const string Inspect = "project-chat-inspect";
    public const string Turn = "project-chat-turn";
}

public static class RemoteChatWorkClaimStatuses
{
    public const string Claimed = "claimed";
    public const string Empty = "empty";
}

public sealed record RemoteChatWorkClaimRequest(
    string RunnerId,
    string RunnerName,
    string Hostname);

public sealed record RemoteChatWorkClaimResponse(
    string Status,
    RemoteChatWorkItem? Work = null);

public sealed record RemoteChatWorkItem(
    string WorkId,
    string ClaimToken,
    string Kind,
    string ProjectId,
    string ProjectName,
    string RepositoryUrl,
    string DefaultBranch,
    string? Prompt,
    string? Model,
    string? ThinkingLevel,
    DateTime CreatedAt,
    DateTime ClaimExpiresAt);

public sealed record RemoteChatWorkRenewRequest(
    string WorkId,
    string ClaimToken,
    string RunnerId);

public sealed record RemoteChatWorkCompletionRequest(
    string WorkId,
    string ClaimToken,
    string RunnerId,
    bool Success,
    string? ReplyText,
    string? Model,
    OrchestratorTokenUsage? TokenUsage,
    string? ErrorMessage,
    ChatExecutionContext? ExecutionContext);

public sealed record OrchestratorTokenUsage
{
    public string? Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheCreationTokens { get; init; }
}

public sealed record ChatExecutionContext(
    string ExecutionKind,
    string HostName,
    string? RepoPath,
    string? Branch,
    string? HeadSha,
    string State,
    DateTime CapturedAt);

public sealed record RemoteEpicPlanningPromptRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    string WorkingDirectory);

public sealed record RemoteEpicPlanningPromptResponse(
    string Prompt,
    string? CliType,
    string? Model,
    string? ThinkingLevel);

/// <summary>Runner -> Server: fenced normal completion after the remote CLI exits.</summary>
public sealed record RemoteRunCompletionRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    string Outcome,
    string? Reason = null,
    string? Source = null,
    int? ExitCode = null,
    string? SalvageBranch = null,
    string? SalvageCommitSha = null,
    string? SalvageBranchUrl = null,
    string? ResultSha = null,
    string? AttemptChainId = null,
    string? SalvageResolution = null,
    string? SalvageLocalCommitSha = null,
    string? SalvageRecoveryBranch = null,
    string? SalvageRecoveryCommitSha = null,
    string? SalvageRecoveryBranchUrl = null,
    string? SalvageAuthoritativeBaseBranch = null,
    string? SalvageAuthoritativeBaseSha = null,
    string? Repository = null,
    // AGT-2178: additive Epic-planning fields; salvage fields above stay intact.
    IReadOnlyList<string>? OutputLines = null,
    bool SourceMutated = false,
    string? AttemptId = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null,
    IReadOnlyList<string>? GateItems = null,
    AgentStudio.TaskServer.Contracts.ExecutionOutcomeDecision? OutcomeDecision = null,
    string? BaseSha = null,
    string? ImmutableResultRef = null,
    string? ArtifactManifestDigest = null,
    string? IntegrationBranch = null);

public sealed record RemoteRunCompletionResponse(
    string TaskKey,
    string Outcome,
    string TargetState,
    string? Message = null,
    string? RunAttemptId = null,
    string? ReviewAttemptId = null,
    string? ReviewSubjectId = null,
    string? FailureClassification = null);

/// <summary>One consolidated output line, shaped to the server's CliOutputLine JSON.</summary>
public sealed record CliOutputLine(DateTime Timestamp, string Stream, string Text);

/// <summary>Runner -> Server: append output lines to the task's durable cli-output.log (/api/runner/logs).</summary>
public sealed record LogIngestRequest(
    string TaskKey,
    List<CliOutputLine> Lines,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? AttemptId = null,
    long? Fence = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null);

public sealed record LogIngestResponse(string TaskKey, int Appended, string? Message = null);

/// <summary>One evidence file uploaded into the task's results/ folder.</summary>
public sealed record RunnerArtifactUpload(string Path, string ContentBase64);

/// <summary>Runner -> Server: upload base64 result files (/api/runner/artifacts).</summary>
public sealed record ArtifactIngestRequest(
    string TaskKey,
    List<RunnerArtifactUpload> Artifacts,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? AttemptId = null,
    long? Fence = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null,
    bool FinalizeResult = false);

public sealed record ArtifactIngestResponse(
    string TaskKey,
    int Uploaded,
    List<string> Files,
    string? Message = null,
    string? CommitSha = null,
    string? CommitStatus = null,
    bool ResultDocumentGenerated = false,
    string? ResultDocumentStatus = null);

/// <summary>One delivered artifact recorded by the external-completion endpoint.</summary>
public sealed record ExternalDeliverable(string? Path = null, string? Url = null, string? Note = null);

/// <summary>
/// Runner -> Server: reconcile the task the runner finished out-of-band
/// (/api/tasks/{jobId}/external-completion). This is how the remote run's result
/// re-enters the local board: the server writes status.md + deliverables,
/// terminalises the lifecycle, and moves the lane.
/// </summary>
public sealed record ExternalCompletionRequest(
    string? Summary,
    List<ExternalDeliverable>? Deliverables = null,
    string? Source = null,
    string? TargetState = null,
    List<string>? GateItems = null,
    // AGT-2220: the structured delivery claim. The runner's own ls-remote proof
    // is no longer trusted as prose in Summary - the server re-verifies these two
    // fields against the target repository before it stamps anything.
    string? ResultSha = null,
    string? ResultRef = null,
    string? BaseSha = null);

public sealed record ExternalCompletionResponse(
    string? JobId = null,
    string? TargetState = null,
    string? Source = null,
    string? EvidenceCommitSha = null);
