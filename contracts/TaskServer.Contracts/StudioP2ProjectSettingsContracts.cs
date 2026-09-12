namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P2 "project settings, project CRUD,
/// ownership mappings, project URLs" bundle. The nine simple per-project
/// settings (auto-commit through quota-wait-policy) are modeled as columns
/// on a single settings row per project and are returned together as
/// <see cref="StudioProjectSettingsDto"/> after each individual PUT, so a
/// client that only ever calls one setter still sees the full effective
/// settings state (with sensible defaults for columns nobody has set yet).
/// </summary>
public sealed record SetAutoCommitRequest(bool Enabled);

public sealed record SetAutoPushStrategyRequest(string Strategy);

public sealed record SetCliContextModeRequest(string Mode);

public sealed record SetCliModeRequest(string Mode);

public sealed record SetCrashRecoveryRequest(bool Enabled);

public sealed record SetLaneSortStrategyRequest(string Strategy);

public sealed record SetMaxParallelismRequest(int MaxParallelism);

public sealed record SetOrchestratorModelRequest(string Model);

public sealed record SetQuotaWaitPolicyRequest(string PolicyJson);

public sealed record StudioProjectSettingsDto(
    string ProjectId,
    bool AutoCommit,
    string AutoPushStrategy,
    string CliContextMode,
    string CliMode,
    bool CrashRecoveryEnabled,
    string LaneSortStrategy,
    int MaxParallelism,
    string OrchestratorModel,
    string? QuotaWaitPolicyJson,
    DateTime UpdatedAt);

public sealed record UpdateStudioProjectRequest(string? Name);

public sealed record UpsertProjectOwnershipMappingRequest(string Pattern, string Owner);

public sealed record ProjectOwnershipMappingDto(
    string MappingId,
    string ProjectId,
    string Pattern,
    string Owner,
    DateTime UpdatedAt);

public sealed record CreateProjectUrlRequest(string Url, string? Label = null);

public sealed record UpdateProjectUrlRequest(string? Url, string? Label);

public sealed record ProjectUrlDto(
    string UrlId,
    string ProjectId,
    string Url,
    string? Label,
    DateTime CreatedAt,
    DateTime UpdatedAt);
