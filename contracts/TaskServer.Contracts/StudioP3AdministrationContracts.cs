using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P3 "administration and long tail" bundle
/// (docs/studio-route-ownership/index.html): the 24 read-only routes left
/// over once P0-P2 covered every write path and every other projection.
/// Every route here reads state a P0-P2 store already owns (or a small,
/// clearly-scoped-down static catalogue vendored alongside it); none of
/// them add a new write surface.
/// </summary>
public sealed record PromptOverrideListResponse(IReadOnlyList<PromptOverrideDto> Items);

/// <summary>
/// Review-coverage roll-up over the durable override rows Task Server
/// actually stores. Unlike the legacy backend's coverage report (a live
/// scan of every runtime prompt template file plus an inline-instruction
/// source scan), this is scoped to what the P2 <c>studio_admin_prompts</c>
/// table can answer: how many stored overrides have been reviewed at least
/// once. Task Server does not ship the runtime prompt template tree, so
/// the "which templates are template-backed vs still inline" half of the
/// legacy report has no durable analogue here.
/// </summary>
public sealed record PromptOverrideCoverageResponse(int TotalOverrides, int ReviewedOverrides, int PendingReviewOverrides);

public sealed record AutoReviewActiveItemDto(string TaskId, string TaskKey, string Title, string ProjectId, DateTime SinceAt);

/// <summary>
/// The active half of the legacy auto-review status projection: every task
/// currently sitting in the auto-review lane, durably read off <c>tasks</c>.
/// The legacy in-memory "accept / reissue / escalate counts since last
/// tick" rolling counter has no durable Task Server equivalent and is
/// intentionally omitted.
/// </summary>
public sealed record AutoReviewStatusResponse(IReadOnlyList<AutoReviewActiveItemDto> Active, int ActiveCount);

public sealed record ModelRoutingTierDto(string Id, int Rank, string Model, string ThinkingLevel, int EstimatedSavingsPercent);

public sealed record ModelRoutingTaskTypeDefaultDto(string TaskType, string Tier, string? HardFloorTier, int Score);

/// <summary>
/// The static, versioned model-routing policy (vendored alongside the same
/// document backend/Policies/model-routing-policy.v1.json ships) plus the
/// one durable per-installation override Task Server owns: economy mode.
/// </summary>
public sealed record ModelRoutingPolicyDto(
    string Version,
    string WikiPath,
    IReadOnlyList<ModelRoutingTierDto> Tiers,
    IReadOnlyList<ModelRoutingTaskTypeDefaultDto> TaskTypeDefaults,
    bool EconomyMode);

/// <summary>
/// A policy-tier recommendation for one task type, computed purely from the
/// static policy document. The legacy route additionally probes the live
/// CLI model catalogue for the requested <c>cliType</c>; Task Server cannot
/// spawn a CLI process (<c>ArchitectureBoundaryTests</c>), so this is the
/// policy-only recommendation without live availability refinement.
/// </summary>
public sealed record ModelRoutingRecommendationDto(string TaskType, string Tier, string Model, string ThinkingLevel, int Score);

/// <summary>One resolved value plus the full set of choices a picker should offer.</summary>
public sealed record ResolvedOptionListDto(string Resolved, IReadOnlyList<string> Available);

public sealed record ProjectQuotaWaitPolicyDto(string? PolicyJson, DateTime UpdatedAt);

/// <summary>
/// Metadata for the proposal that owns an evidence path, keyed off the
/// proposal's own reported payload. Task Server has no durable byte-content
/// store for proposal evidence images yet (the legacy route streams an
/// image file from the project's git checkout, which Task Server does not
/// have authority over) - this surfaces the owning proposal and its raw
/// payload so a caller can decide there is no content to fetch rather than
/// silently 404ing on a proposal that genuinely exists.
/// </summary>
public sealed record ProposalEvidenceDto(string ProposalId, string RelPath, JsonElement Payload);

public sealed record PublishPanelDto(
    string TargetId, bool AutomationEnabled, string? LatestOperationId, string? LatestStatus,
    string? ResultJson, DateTime? CompletedAt);

public sealed record PublishRunStatusDto(
    string TargetId, string State, string? OperationId, string? ResultJson, string? Error, DateTime? CompletedAt);

public sealed record ReviewDecisionPendingItemDto(string TaskId, string TaskKey, string Title, string? Reason, DateTime SinceAt);

public sealed record ReviewDecisionsPendingResponse(IReadOnlyList<ReviewDecisionPendingItemDto> Items);

public sealed record WikiGradingStatusDto(string State, string? OperationId, string? ResultJson, string? Error, DateTime? CompletedAt);

public sealed record PipelineCatalogueStepDto(string Id, string Title, string Kind);

/// <summary>
/// The static per-type step catalogue only: id, title, kind. The legacy
/// route additionally resolves live repository-stack detection and
/// per-project step overrides into each entry; Task Server has no checkout
/// authority to detect a stack, and per-project step overrides already have
/// their own dedicated P1 routes (docs/operations/setup/task-server.md,
/// "Studio task-detail-and-hosts bundle (P1)", group G2).
/// </summary>
public sealed record PipelineCatalogueTypeDto(string PipelineType, IReadOnlyList<PipelineCatalogueStepDto> Steps);

public sealed record PipelineCatalogueResponse(IReadOnlyList<PipelineCatalogueTypeDto> Types);

public sealed record AllProjectSettingsResponse(IReadOnlyDictionary<string, StudioProjectSettingsDto> Projects);

public sealed record StudioSearchTaskItemDto(string TaskId, string TaskKey, string Title, string ProjectId, string State);

/// <summary>
/// Task-domain-only global search: the Task-Server-authoritative half of
/// the legacy mixed <c>/api/search</c> contract. Dossier, wiki, commit, and
/// file matches all read a project's git checkout and stay on the dev-seat
/// connector (see <c>backend/Features/Search/GlobalSearchEndpoints.cs</c>,
/// the <c>/api/search/repository</c> route).
/// </summary>
public sealed record StudioSearchResponse(string Query, IReadOnlyList<StudioSearchTaskItemDto> Tasks);

public sealed record WatchPathListResponse(IReadOnlyList<WatchPathDto> Items);
