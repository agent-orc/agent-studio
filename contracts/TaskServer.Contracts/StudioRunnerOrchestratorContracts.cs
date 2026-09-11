using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P1 "G3_RunnerOrchestrator" Studio route bundle:
/// raw-context-key orchestrator chat, per-project runner mode/start/stop,
/// the orchestrator log and its human override channel, pending decisions,
/// token usage summaries (live and TTL-cached), the cross-project
/// orchestrator activity feed, the auto-review queue projection, and queue
/// starvation detection. These are additive to the P0 "core-attach" bundle
/// in <c>StudioContracts.cs</c> and reuse its orchestrator-context and
/// orchestrator-session shapes wherever the underlying storage is shared
/// (see <see cref="OrchestratorContextTranscriptResponse"/> and
/// <see cref="OrchestratorSessionListResponse"/> in
/// <c>OrchestratorContextContracts.cs</c>).
/// </summary>
public sealed record RunnerOrchestratorChatMessageResponse(
    OrchestratorContextTurnDto Turn,
    OrchestratorContextTranscriptResponse Transcript);

public sealed record UpdateRunnerModeRequest(string? Mode, long ExpectedVersion);

public sealed record RunnerProjectStateDto(
    string ProjectId,
    string? Mode,
    bool Running,
    long Version,
    DateTime UpdatedAt);

public sealed record OrchestratorLogEntryDto(
    string Id,
    string ProjectId,
    JsonElement Entry,
    bool IsOverride,
    string? ActorId,
    DateTime CreatedAt);

public sealed record OrchestratorLogResponse(IReadOnlyList<OrchestratorLogEntryDto> Entries);

public sealed record OrchestratorLogOverrideRequest(JsonElement Entry);

public sealed record PendingDecisionDto(
    string Id,
    string ProjectId,
    string Description,
    DateTime CreatedAt,
    DateTime? ResolvedAt);

public sealed record PendingDecisionListResponse(IReadOnlyList<PendingDecisionDto> Decisions);

public sealed record AutoReviewQueueEntryDto(
    string AttemptId,
    string SubjectId,
    string TaskId,
    string TaskKey,
    string ProjectId,
    int AttemptNumber,
    string Status,
    string? ExecutorId,
    DateTime CreatedAt);

public sealed record AutoReviewQueueResponse(IReadOnlyList<AutoReviewQueueEntryDto> Entries);

public sealed record TokenSummaryDto(
    string ProjectId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TurnCount);

public sealed record TokenSummaryAggregateDto(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TurnCount,
    IReadOnlyList<TokenSummaryDto> ByProject);

public sealed record CachedTokenSummaryAggregateResponse(
    TokenSummaryAggregateDto Summary,
    DateTime ComputedAt);

public sealed record OrchestratorFeedEntryDto(
    string Kind,
    string RefId,
    string? ProjectId,
    string Text,
    DateTime OccurredAt);

public sealed record OrchestratorFeedResponse(IReadOnlyList<OrchestratorFeedEntryDto> Entries);

public sealed record QueueStarvationTaskDto(
    string TaskId,
    string TaskKey,
    string ProjectId,
    DateTime CreatedAt,
    double AgeMinutes);

public sealed record QueueStarvationResponse(int Count, IReadOnlyList<QueueStarvationTaskDto> Tasks);
