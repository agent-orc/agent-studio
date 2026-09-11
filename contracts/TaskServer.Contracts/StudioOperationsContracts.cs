namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P2 "operations and insight" Studio bundle: bus,
/// runtime, cycle time, tokens, deployment, security review, analysis,
/// drift, supervisor, and recovery projections, plus the project-settings
/// and admin mutations in the same bundle. See
/// docs/operations/setup/task-server.md, "Studio operations-and-insight
/// bundle (P2)".
/// </summary>
public static class StudioOperationDomains
{
    public const string Analysis = "analysis";
    public const string Drift = "drift";
    public const string Security = "security";
    public const string Design = "design";
    public const string Proposals = "proposals";
    public const string Publish = "publish";
    public const string Deployment = "deployment";
    public const string SkillReadiness = "skill-readiness";
    public const string WikiGrading = "wiki-grading";
    public const string AdminPrompts = "admin-prompts";
}

public static class StudioOperationStatuses
{
    public const string Dispatched = "dispatched";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Aborted = "aborted";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>
/// One durable row in the <c>studio_operations</c> ledger: a generated
/// report, review, proposal, or other artifact that either completed
/// synchronously (a pure, deterministic computation) or was dispatched to a
/// Runner through the existing fenced task/run lifecycle. <see cref="TaskId"/>
/// is the dispatched task; its current run and artifacts (if any) are the
/// durable source of the generated content, reachable through the existing
/// <c>/api/v1/projects/{projectId}/tasks/{taskId}/attempts</c> and run
/// artifact routes rather than a second copy stored here.
/// </summary>
public sealed record StudioOperationDto(
    string Id,
    string? ProjectId,
    string Domain,
    string Kind,
    string Title,
    string? TaskId,
    string Status,
    string? ResultJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string CreatedBy);

public sealed record StudioOperationListResponse(IReadOnlyList<StudioOperationDto> Operations);

/// <summary>Generic request shape for an action that must run through a fenced Runner dispatch.</summary>
public sealed record DispatchStudioOperationRequest(string? Prompt, string? Scope, IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record StudioOperationDecisionRequest(string Decision, string? Reason = null);

// --- Analysis --------------------------------------------------------------

public sealed record AnalysisScheduleDto(string ProjectId, string CronExpression, bool Enabled, DateTime UpdatedAt);
public sealed record UpdateAnalysisScheduleRequest(string CronExpression, bool Enabled);

// --- Drift -------------------------------------------------------------------

public sealed record ArchitectureElementDto(string ModelId, string ElementId, string Status, DateTime UpdatedAt, string UpdatedBy);
public sealed record ArchitectureElementListResponse(IReadOnlyList<ArchitectureElementDto> Elements);
public sealed record UpdateArchitectureElementStatusRequest(string Status);
public sealed record CodePatternDriftRuleDto(string RuleId, string Title, string Description, string Severity);
public sealed record CodePatternDriftRulesResponse(IReadOnlyList<CodePatternDriftRuleDto> Rules);
public sealed record CodePatternDriftFinding(string Path, string RuleId, string Detail);
public sealed record CodePatternDriftRequest(IReadOnlyList<CodePatternDriftFile> Files);
public sealed record CodePatternDriftFile(string Path, string Content);
public sealed record CodePatternDriftResponse(IReadOnlyList<CodePatternDriftFinding> Findings, DateTime EvaluatedAt);

// --- Security ------------------------------------------------------------

public sealed record SecurityBaselineResponse(string ProjectId, string? ContentMarkdown, DateTime? UpdatedAt);
public sealed record SecurityAuditRequest(string? Scope);

// --- Bus / runtime / tokens ------------------------------------------------

public sealed record StudioBusMessageDto(
    long Id, string ProjectId, string? TaskId, string Channel, string Role, string Kind, string Summary, DateTime OccurredAt);
public sealed record StudioBusMessageListResponse(IReadOnlyList<StudioBusMessageDto> Messages);
public sealed record StudioBusSummaryResponse(string ProjectId, int MessageCount, DateTime? LastActivityAt, IReadOnlyDictionary<string, int> CountsByKind);
public sealed record StudioBusTokenAggregateResponse(string ProjectId, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record StudioRuntimeEventDto(long Id, string ProjectId, string Kind, string Summary, DateTime OccurredAt);
public sealed record StudioRuntimeEventListResponse(IReadOnlyList<StudioRuntimeEventDto> Events);

public sealed record StudioTokenUsageSummaryResponse(string ProjectId, long InputTokens, long OutputTokens, decimal CostUsd, int SampleCount);
public sealed record StudioTokenUsageHeatmapEntry(DateOnly Day, long InputTokens, long OutputTokens, decimal CostUsd);
public sealed record StudioTokenUsageHeatmapResponse(string ProjectId, IReadOnlyList<StudioTokenUsageHeatmapEntry> Days);
public sealed record StudioTokenUsageExpensiveEntry(string TaskId, string? TaskKey, decimal CostUsd, long InputTokens, long OutputTokens);
public sealed record StudioTokenUsageExpensiveResponse(string ProjectId, IReadOnlyList<StudioTokenUsageExpensiveEntry> Tasks);
public sealed record StudioTokenUsageJobResponse(string ProjectId, string TaskId, long InputTokens, long OutputTokens, decimal CostUsd, int SampleCount);
public sealed record StudioTokenUsagePipelineCostEntry(string Stage, long InputTokens, long OutputTokens, decimal CostUsd);
public sealed record StudioTokenUsagePipelineCostResponse(string ProjectId, IReadOnlyList<StudioTokenUsagePipelineCostEntry> Stages);
public sealed record TokenPricingCalculateRequest(string Model, long InputTokens, long OutputTokens);
public sealed record TokenPricingCalculateResponse(string Model, long InputTokens, long OutputTokens, decimal CostUsd);

// --- Cycle time / throughput ------------------------------------------------

public sealed record CycleTimeTransitionDto(string FromState, string ToState, DateTime OccurredAt);
public sealed record CycleTimeSummaryDto(int SampleCount, double AverageHours, double MedianHours);
public sealed record ProjectCycleTimeResponse(string ProjectId, string Window, CycleTimeSummaryDto Summary);
public sealed record TaskCycleTimeResponse(string ProjectId, string TaskId, IReadOnlyList<CycleTimeTransitionDto> Transitions);
public sealed record ThroughputEntry(DateOnly Day, int CompletedCount);
public sealed record ProjectThroughputResponse(string ProjectId, IReadOnlyList<ThroughputEntry> Days);

// --- Supervisor --------------------------------------------------------------

public sealed record SupervisorObservationResponse(
    string ProjectId, int ActiveRunCount, int StaleLeaseCount, bool PickupPaused, DateTime ObservedAt);
public sealed record SupervisorMetaCycleResponse(string ProjectId, int CompletedRunSampleCount, double AverageRunMinutes);
public sealed record SupervisorRecentEventDto(DateTime OccurredAt, string ActorId, string Action, string TargetType, string TargetId);
public sealed record SupervisorRecentEventsResponse(IReadOnlyList<SupervisorRecentEventDto> Events);
public sealed record SupervisorInterveneRequest(string? RunId, string? Reason);
public sealed record SupervisorInterveneResponse(string ProjectId, string Action, string? RunId, DateTime AppliedAt);

// --- Crash recovery ------------------------------------------------------

public sealed record CrashRecoveryPendingDto(
    string Id, string ProjectId, string TaskId, string RunId, DateTime DetectedAt, string Reason);
public sealed record CrashRecoveryPendingListResponse(IReadOnlyList<CrashRecoveryPendingDto> Pending);
public sealed record CrashRecoveryDecisionResponse(string RunId, string Decision, DateTime DecidedAt);

// --- Queue health --------------------------------------------------------

public sealed record QueueHealthRepairResponse(string ProjectId, int ReleasedLeaseCount, DateTime RepairedAt);

// --- Project settings and profile ------------------------------------------

public sealed record UpdateProjectRequest(string? Name);
public sealed record BooleanSettingRequest(bool Value);
public sealed record StringSettingRequest(string Value);
public sealed record IntegerSettingRequest(int Value);
public sealed record PublishAutomationRequest(bool Enabled, string? Target);

public sealed record StudioProjectUrlDto(string Id, string ProjectId, string Url, string? Label, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record StudioProjectUrlListResponse(IReadOnlyList<StudioProjectUrlDto> Urls);
public sealed record CreateProjectUrlRequest(string Url, string? Label);
public sealed record UpdateProjectUrlRequest(string Url, string? Label);

public sealed record StudioOwnershipMappingDto(string Id, string ProjectId, string Pattern, string Owner, DateTime UpdatedAt);
public sealed record UpdateOwnershipMappingRequest(string Pattern, string Owner);

public sealed record StudioProjectSnapshotResponse(
    string ProjectId,
    string ProjectName,
    IReadOnlyDictionary<string, int> TaskCountsByLane,
    StudioProjectSettingsDto Settings,
    DateTime GeneratedAt);

public sealed record StudioProjectSettingsDto(
    bool? AutoCommit,
    string? AutoPushStrategy,
    string? CliContextMode,
    string? CliMode,
    bool? CrashRecoveryEnabled,
    string? LaneSortStrategy,
    int? MaxParallelism,
    string? OrchestratorModel,
    string? QuotaWaitPolicy,
    bool PickupPaused,
    DateTime? UpdatedAt);

// --- Watch paths, CLI, admin ------------------------------------------------

public sealed record StudioWatchPathDto(string Name, string Pattern, string? ProjectId, DateTime CreatedAt);
public sealed record StudioWatchPathListResponse(IReadOnlyList<StudioWatchPathDto> WatchPaths);
public sealed record CreateWatchPathRequest(string Name, string Pattern, string? ProjectId);

public sealed record CliEconomyModeRequest(bool Enabled);
public sealed record CliQuotaCapsRequest(IReadOnlyDictionary<string, int> DailyCapsByModel);
public sealed record CliQuotaModelRoutesRequest(IReadOnlyDictionary<string, string> RoutesByCapability);
public sealed record CliQuotaWaitPolicyRequest(string Policy);

public sealed record AdminOrchestratorConfigRequest(string? DefaultModel, string? DefaultThinkingLevel);

public sealed record StudioPromptDto(string Name, string Content, DateTime UpdatedAt, string UpdatedBy);
public sealed record UpdatePromptRequest(string Content);
public sealed record PromptPreviewRequest(IReadOnlyDictionary<string, string>? Variables = null);
public sealed record PromptPreviewResponse(string Name, string Rendered);
public sealed record PromptReviewResponse(string Name, string OperationId, string Status);
public sealed record PromptReviewAllResponse(IReadOnlyList<PromptReviewResponse> Reviews);

// --- Design ----------------------------------------------------------------

public sealed record DesignActionRequest(string Action, string? Prompt);
public sealed record DesignCouncilAcceptRequest(string? Reason);
public sealed record DesignOverviewResponse(string ProjectId, int CouncilCount, int ActionCount, DateTime? LastActivityAt);
public sealed record DesignReferenceDto(string Title, string Url);
public sealed record DesignReferencesResponse(IReadOnlyList<DesignReferenceDto> References);

// --- Proposals ---------------------------------------------------------------

public sealed record GenerateProposalsRequest(string? Prompt);
public sealed record RefineProposalsFeedbackRequest(string Feedback);
public sealed record ProposalDecisionRequest(string Decision);

// --- Publish / deployment ---------------------------------------------------

public sealed record PublishPackageRequest(string? Notes);
public sealed record PublishWebsiteRequest(string? Notes);
public sealed record DeploymentCompileRequest(string? Target);
public sealed record DeploymentSummaryResponse(string ProjectId, string? LastStatus, DateTime? LastCompiledAt);

// --- Skill readiness / wiki grading / component routing / text helpers ----

public sealed record SkillReadinessFixTaskRequest(string TaskKey, string? Prompt);
public sealed record WikiGradingRunRequest(string? Scope);
public sealed record WikiGradingAbortRequest(string OperationId);

public sealed record ComponentRoutingResolveRequest(string Path);
public sealed record ComponentRoutingResolveResponse(string Path, string? Owner, string? MatchedPattern);

public sealed record PromptEnhanceRequest(string Text);
public sealed record PromptEnhanceResponse(string Original, string Enhanced);
public sealed record TitleGenerateRequest(string Text);
public sealed record TitleGenerateResponse(string Title);

// --- Regression radar / test runs / visual evidence / pipeline alert -------

public sealed record RegressionRadarEntryDto(long Id, string Summary, string Severity, DateTime OccurredAt);
public sealed record RegressionRadarResponse(IReadOnlyList<RegressionRadarEntryDto> Findings);

public sealed record TestRunEntryDto(long Id, string Summary, string Outcome, DateTime OccurredAt);
public sealed record TestRunListResponse(IReadOnlyList<TestRunEntryDto> Runs);

public sealed record VisualEvidenceItemDto(long Id, string Summary, DateTime OccurredAt, bool Acknowledged);
public sealed record VisualEvidenceListResponse(IReadOnlyList<VisualEvidenceItemDto> Items);
public sealed record AcknowledgeVisualEvidenceResponse(long Id, DateTime AcknowledgedAt);

public sealed record PipelineAcceptedIntegrationAlertResponse(bool HasAlert, string? Summary, DateTime? OccurredAt);
