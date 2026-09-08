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
    string? WorkbenchKey = null);

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
    OrchestratorContextReceiptDto? Receipt = null);

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
