namespace AgentStudio.TaskServer.Contracts;

public static class OrchestratorContextKinds
{
    public const string Project = "project";
    public const string Task = "task";
    public const string Workbench = "workbench";
}

public static class OrchestratorContextVisibilityPolicy
{
    /// <summary>
    /// Only task contexts hide on archive. A Dossier (workbench) context has
    /// no task-server-owned lifecycle row to key off - its descriptor lives in
    /// the Studio-owned repository checkout, not this store - so it stays
    /// permanently visible, the same as a project context.
    /// </summary>
    public static bool IsHidden(string kind, string? taskState)
        => string.Equals(kind, OrchestratorContextKinds.Task, StringComparison.Ordinal)
           && string.Equals(taskState, "7-archive", StringComparison.Ordinal);
}

/// <summary>
/// Operator archive for chat sessions (AGT-3032). Archive is a separate,
/// operator-owned mark next to the automatic <see cref="OrchestratorContextVisibilityPolicy"/>
/// hide: the automatic hide is recomputed from the task lane on every read,
/// the operator mark persists until the operator unarchives the session or a
/// new turn lands in it.
/// </summary>
public static class OrchestratorContextArchivePolicy
{
    public const string RuntimeActive = "active";
    public const string RuntimeQueued = "queued";

    /// <summary>
    /// A session with a running or queued turn cannot be archived; it would
    /// vanish from the Active group while it is still producing output.
    /// Unarchive is always allowed.
    /// </summary>
    public static bool CanArchive(string? runtimeStatus)
        => !string.Equals(runtimeStatus, RuntimeActive, StringComparison.Ordinal)
           && !string.Equals(runtimeStatus, RuntimeQueued, StringComparison.Ordinal);

    /// <summary>
    /// The archive mark after an operator request, keeping the first archive
    /// time on repeats. A new turn clears the mark in the store directly.
    /// </summary>
    public static DateTime? ArchivedAtAfterRequest(DateTime? current, bool archived, DateTime now)
        => archived ? current ?? now : null;

    /// <summary>A session lands in the Archived group when either mark is set.</summary>
    public static bool IsArchived(DateTime? archivedAt, DateTime? hiddenAt)
        => archivedAt is not null || hiddenAt is not null;
}

public sealed record OrchestratorContextDto(
    string ContextKey,
    string Kind,
    string ProjectId,
    string ProjectName,
    string? TaskId,
    string? TaskKey,
    string Summary,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? HiddenAt,
    long TurnCount,
    string? Model = null,
    long CumulativeInputTokens = 0,
    long CumulativeOutputTokens = 0,
    long CumulativeCacheReadTokens = 0,
    long CumulativeCacheCreationTokens = 0,
    string? WorkbenchKey = null,
    DateTime? ArchivedAt = null);

/// <summary>Operator archive toggle for one chat session (AGT-3032).</summary>
public sealed record SetOrchestratorContextArchivedRequest(bool Archived);

public sealed record OrchestratorContextListResponse(
    IReadOnlyList<OrchestratorContextDto> Contexts);

public sealed record OrchestratorContextSourceReceiptDto(
    string SourceId,
    string Kind,
    string? Revision,
    string? Sha256,
    string Freshness,
    int IncludedCharacters,
    int EstimatedTokens,
    string Status,
    string? Reason = null);

public sealed record OrchestratorContextBudgetReceiptDto(
    int AutomaticSoftCapTokens,
    int AutomaticHardCapTokens,
    int TotalHardCapTokens,
    int EstimatedIncludedTokens);

public sealed record OrchestratorContextReceiptDto(
    string ReceiptId,
    string UserTurnId,
    string ContextKey,
    DateTime CapturedAt,
    OrchestratorContextBudgetReceiptDto Budget,
    IReadOnlyList<OrchestratorContextSourceReceiptDto> Sources);

public sealed record OrchestratorContextTokenUsageDto(
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens);

public sealed record OrchestratorChatMetadataDto(
    string? Model,
    string? Effort,
    string? ProviderThreadId,
    string? Host,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    decimal? Cost,
    string? Currency,
    string? PriceCatalogueVersion);

public sealed record OrchestratorContextAttachmentDto(
    string Alt,
    string RelativePath);

public sealed record OrchestratorContextTurnDto(
    string TurnId,
    DateTime CreatedAt,
    string Role,
    string Body,
    string? Model = null,
    OrchestratorContextTokenUsageDto? TokenUsage = null,
    string? ErrorMessage = null,
    string? ErrorDetail = null,
    IReadOnlyList<OrchestratorContextAttachmentDto>? Attachments = null,
    OrchestratorContextReceiptDto? Receipt = null,
    OrchestratorChatMetadataDto? Metadata = null);

public sealed record OrchestratorContextTranscriptResponse(
    OrchestratorContextDto Context,
    IReadOnlyList<OrchestratorContextTurnDto> Turns);

public sealed record AppendOrchestratorContextTurnRequest(
    OrchestratorContextTurnDto Turn);

public sealed record ImportLegacyOrchestratorChatRequest(
    string SourceSha256,
    IReadOnlyList<OrchestratorContextTurnDto> Turns);

public sealed record ImportLegacyOrchestratorChatResponse(
    string ContextKey,
    int Imported,
    int AlreadyPresent,
    int Rejected);
