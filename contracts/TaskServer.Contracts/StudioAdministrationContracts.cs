namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio "administration and long tail" bundle
/// (P3): orchestrator/supervisor config, the runtime prompt catalog,
/// auto-review status, CLI quota/model-routing policy, per-project CLI
/// and lane-sort settings, the pipeline catalogue, and the task-results
/// half of global search. Durable state lives in the generic
/// <c>studio_settings</c> table; nothing here depends on a live CLI probe,
/// a local git checkout, or a legacy hosted-service tick loop.
/// </summary>
public sealed record StudioOrchestratorConfigOption(
    string Key,
    string Group,
    string Label,
    string Description,
    string Type,
    object? DefaultValue,
    object? CurrentValue,
    object? ActiveValue,
    bool HasOverride,
    bool RestartRequired,
    string SourceFile,
    IReadOnlyList<string>? EnumOptions = null,
    bool AppliesImmediately = true,
    string? RestartRequiredReason = null);

public sealed record StudioOrchestratorConfigSnapshot(
    IReadOnlyList<StudioOrchestratorConfigOption> Options,
    string OverrideFilePath,
    bool OverrideFileExists);

public sealed record StudioPromptCallAnalytics(
    bool IsDead,
    int TotalCalls = 0,
    int Calls7d = 0,
    string? LastCalledAt = null,
    long InputTokens = 0,
    decimal CostUsd = 0,
    decimal CostUsd7d = 0,
    int UnpricedCalls = 0,
    int UnpricedCalls7d = 0,
    int CurrentVersionCalls = 0);

public sealed record StudioPromptCatalogItem(
    string Name,
    string Title,
    string Description,
    string Group,
    string PromptClass,
    bool HasDefault,
    bool HasOverride,
    bool DefaultChangedSinceOverride,
    IReadOnlyList<string> Slots,
    int UsageCount,
    string? ReviewStatus,
    int ReviewFindingCount,
    int ProjectOverrideCount,
    StudioPromptCallAnalytics Calls);

public sealed record StudioPromptCatalogResponse(
    IReadOnlyList<StudioPromptCatalogItem> Items,
    string OverrideDirectory,
    string? TelemetryPath,
    int DeadPromptDays,
    string CostDisclaimer,
    IReadOnlyList<object> OrphanedOverrides);

public sealed record StudioPromptDetail(
    string Name,
    string Title,
    string Description,
    string Group,
    string PromptClass,
    bool HasDefault,
    bool HasOverride,
    string? DefaultContent,
    string? OverrideContent,
    string EffectiveContent,
    string? DefaultSha,
    bool DefaultChangedSinceOverride,
    string? OverrideUpdatedAt,
    IReadOnlyList<string> Slots,
    string CostDisclaimer,
    StudioPromptCallAnalytics Calls,
    IReadOnlyList<object> Usages,
    IReadOnlyList<object> ProjectOverrides);

public sealed record StudioAutoReviewActivity(string Project, string JobId, string Step, DateTime StartedAt);

public sealed record StudioAutoReviewStatus(
    DateTime? LastTickAt,
    int Accept,
    int Reissue,
    int Escalate,
    int AspectsRun,
    int Pending,
    string? CurrentJob,
    string? CurrentProject,
    IReadOnlyList<StudioAutoReviewActivity> ActiveJobs);

public sealed record StudioModelRoutingPolicyRow(string Tier, string Model, string? ThinkingLevel);

public sealed record StudioModelRoutingPolicyView(
    bool EconomyMode,
    string PolicyVersion,
    IReadOnlyList<StudioModelRoutingPolicyRow> Rows);

public sealed record StudioModelRoutingRecommendation(
    string Model,
    string? ThinkingLevel,
    string Tier,
    string TaskType,
    bool EconomyDowngraded,
    string PolicyVersion,
    string PolicyWikiPath);

public sealed record StudioCliQuotaCaps(int DefaultCapPct, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Caps);

public sealed record StudioCliQuotaWaitPolicy(bool Enabled, int ThresholdMinutes);

public sealed record StudioProjectCliQuotaWaitPolicy(
    bool Enabled,
    int ThresholdMinutes,
    string Source,
    bool? ProjectEnabled,
    int? ProjectThresholdMinutes,
    bool GlobalEnabled,
    int GlobalThresholdMinutes);

public sealed record StudioCliModelRouteProfile(
    string CliType,
    string? PrimaryModel,
    string? PrimaryThinkingLevel,
    string? FallbackCliType,
    string? FallbackModel,
    string? FallbackThinkingLevel,
    bool IsFallbackDerived);

public sealed record StudioCliModelRoutesResponse(IReadOnlyDictionary<string, StudioCliModelRouteProfile> Profiles);

public sealed record StudioCliModeResolution(string Mode, string Source, IReadOnlyList<string> Args);

public sealed record StudioProjectCliModesResponse(
    IReadOnlyDictionary<string, StudioCliModeResolution> Resolved,
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlyList<string> Available);

public sealed record StudioCliContextModeResolution(string Mode, string Source, bool Supported);

public sealed record StudioProjectCliContextModesResponse(
    IReadOnlyDictionary<string, StudioCliContextModeResolution> Resolved,
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlyList<string> Available);

public sealed record StudioLaneSortStrategiesResponse(
    IReadOnlyDictionary<string, string> Resolved,
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlyList<string> Available);

public sealed record StudioProjectSettingsEntry(
    bool AutoCommit,
    bool CrashRecoveryEnabled,
    string AutoPushStrategy,
    string? RunnerMode,
    string PickupMode,
    string ExecutionLocation,
    string? ExecutionRunner,
    bool RemoteExecutionEnabled,
    string IntegrationBranch,
    int MaxParallelism,
    string? OrchestratorModel,
    IReadOnlyDictionary<string, string> LaneSortStrategies,
    IReadOnlyDictionary<string, StudioCliModeResolution> CliModes);

public sealed record StudioPipelineCatalogueStep(
    string Id,
    string DisplayName,
    string Kind,
    string? Phase,
    bool UsesModel,
    bool UsesPrompt,
    bool SupportsMode,
    bool CanDisable,
    bool DefaultEnabled,
    bool SupportsCondition,
    bool Applicable = true,
    string AppliesTo = "any");

public sealed record StudioPipelineCatalogue(
    string PipelineId,
    string? PipelineType,
    IReadOnlyList<string> DetectedStacks,
    IReadOnlyList<StudioPipelineCatalogueStep> Steps);

public sealed record StudioSearchTaskItem(
    string Domain,
    string ProjectName,
    string ProjectId,
    string Title,
    string Subtitle,
    string? TaskKey,
    string? Lane,
    string? ReferenceKey);

public sealed record StudioSearchResponse(string Query, IReadOnlyList<StudioSearchTaskItem> Tasks);
