namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P2 "operations and insight" bus, token
/// usage, runtime event, and token pricing bundle. Bus messages, token
/// usage, and runtime events are projections over durable tables the Task
/// Server owns; a fenced Runner/engine principal appends rows through the
/// three ingestion requests below (scoped <see cref="TaskServerScopes.RunsWrite"/>),
/// and every frontend-facing route here only reads what has already been
/// persisted. Token pricing is pure in-process computation against a small
/// static rate table (see <c>TaskServerStudioP2InsightStore</c>) and has no
/// backing table at all.
/// </summary>

// ---- Bus messages -------------------------------------------------------

public sealed record BusMessageIngestRequest(
    string Text,
    string? Kind = null,
    string? Tag = null,
    string? Severity = null,
    string? ParticipantId = null,
    string? CorrelationId = null,
    string? JobId = null,
    string? RunId = null,
    string? Cli = null,
    string? Skill = null);

public sealed record BusMessageDto(
    string Id,
    string ProjectId,
    DateTime OccurredAt,
    string? Kind,
    string? Tag,
    string? Severity,
    string? ParticipantId,
    string? CorrelationId,
    string? JobId,
    string? RunId,
    string? Cli,
    string? Skill,
    string Text);

public sealed record BusMessageSummaryResponse(
    string ProjectId,
    int WindowHours,
    int TotalMessages,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> BySeverity,
    DateTime GeneratedAt);

public sealed record BusTokenAggregateResponse(
    string ProjectId,
    DateTime? Since,
    DateTime? Until,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    int RecordCount,
    DateTime GeneratedAt);

// ---- Token usage ---------------------------------------------------------

public sealed record TokenUsageIngestRequest(
    DateTime OccurredAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0,
    string? TaskId = null,
    string? RunId = null,
    string? Model = null);

public sealed record TokenUsageDto(
    string Id,
    string ProjectId,
    string? TaskId,
    string? RunId,
    DateTime OccurredAt,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens);

public sealed record ExpensiveTaskTokenUsageDto(
    string TaskId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    int RecordCount);

public sealed record ExpensiveTaskTokenUsageResponse(
    string ProjectId,
    IReadOnlyList<ExpensiveTaskTokenUsageDto> Tasks,
    DateTime GeneratedAt);

public sealed record TokenUsageDailyPointDto(
    string Date,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens);

public sealed record TokenUsageHeatmapResponse(
    string ProjectId,
    int Days,
    IReadOnlyList<TokenUsageDailyPointDto> Points,
    DateTime GeneratedAt);

public sealed record TokenUsagePipelineCostResponse(
    string ProjectId,
    int Days,
    IReadOnlyList<TokenUsageDailyPointDto> Points,
    DateTime GeneratedAt);

public sealed record TokenUsageSummaryResponse(
    string ProjectId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    int RecordCount,
    DateTime GeneratedAt);

// ---- Runtime events --------------------------------------------------------

public sealed record RuntimeEventIngestRequest(string Kind, string PayloadJson);

public sealed record RuntimeEventDto(
    string Id,
    string ProjectId,
    DateTime OccurredAt,
    string Kind,
    string PayloadJson);

public sealed record RuntimeEventListResponse(
    string ProjectId,
    IReadOnlyList<RuntimeEventDto> Events,
    DateTime GeneratedAt);

// ---- Token pricing ---------------------------------------------------------

/// <summary>
/// <c>task-server</c> cannot reference <c>backend</c> or the pinned
/// <c>TokenEconomy</c> package (an architecture test enforces the
/// boundary), so this calculator is a small, clearly-labeled placeholder
/// rate table rather than the real historical catalog documented in
/// <c>docs/system/domains/token-pricing.md</c>. It covers only the current
/// tiers named in <c>docs/system/domains/model-routing-policy.md</c>
/// (Sonnet 5, Opus 4.8, Haiku 4.5).
/// </summary>
public sealed record CalculateTokenPricingRequest(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0);

public sealed record TokenPricingBreakdownDto(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal InputCost,
    decimal OutputCost,
    decimal CacheReadCost,
    decimal CacheCreationCost,
    decimal TotalCost,
    string Currency,
    decimal InputRatePerMillionTokens,
    decimal OutputRatePerMillionTokens,
    decimal CacheReadRatePerMillionTokens,
    decimal CacheCreationRatePerMillionTokens,
    string PriceSource);
