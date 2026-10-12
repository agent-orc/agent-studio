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
/// Operator archive decisions for an orchestrator chat context. Archiving is a
/// display marker only: it never deletes turns, and a new turn clears it. It is
/// independent of <see cref="OrchestratorContextVisibilityPolicy"/>, which
/// hides task contexts whose task reached <c>7-archive</c>.
/// </summary>
public static class OrchestratorContextArchivePolicy
{
    public const string RuntimeActive = "active";
    public const string RuntimeQueued = "queued";

    /// <summary>
    /// Only project, task, and workbench contexts are archivable. The global
    /// context is a singleton surface, so it has nothing to archive.
    /// </summary>
    public static bool IsArchivableKind(string? kind)
        => kind is OrchestratorContextKinds.Project
            or OrchestratorContextKinds.Task
            or OrchestratorContextKinds.Workbench;

    /// <summary>
    /// A context with a running or queued turn cannot be archived: the turn
    /// would immediately unarchive it again and the operator would lose sight
    /// of live work. Idle, parked, completed, and failed contexts are archivable.
    /// </summary>
    public static bool CanArchive(string? runtimeStatus)
        => !string.Equals(runtimeStatus, RuntimeActive, StringComparison.Ordinal)
           && !string.Equals(runtimeStatus, RuntimeQueued, StringComparison.Ordinal);

    /// <summary>Unarchiving is always allowed; archiving follows <see cref="CanArchive"/>.</summary>
    public static bool CanSetArchived(bool archived, string? runtimeStatus)
        => !archived || CanArchive(runtimeStatus);

    /// <summary>
    /// The archive marker after an archive-state request: keep an existing
    /// marker when archiving again (idempotent), stamp <paramref name="now"/>
    /// on a fresh archive, and clear it on unarchive.
    /// </summary>
    public static DateTime? NextArchivedAt(DateTime? current, bool archived, DateTime now)
        => archived ? current ?? now : null;
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

/// <summary>
/// Operator archive toggle for one orchestrator context, addressed by its
/// canonical key (<c>project:{name}</c>, <c>task:{name}/{key}</c>, or
/// <c>workbench:{name}/{key}</c>).
/// </summary>
public sealed record SetOrchestratorContextArchivedRequest(
    string ContextKey,
    bool Archived);

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
