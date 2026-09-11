namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Contracts for the P1 "G8_TaskReview" Studio route bundle: task-level code
/// review findings, the task's view of the project pipeline (layered with
/// this task's own step-run requests), the regression radar derived from
/// execution-outcome history, review-evidence acknowledgement/follow-up, and
/// durable task summaries. See <c>StudioTaskReviewEndpoints.cs</c> and
/// <c>TaskServerStudioTaskReviewStore.cs</c> for the route mapping and
/// persistence respectively.
/// </summary>

// ---- code-review findings -------------------------------------------------

public sealed record SubmitCodeReviewFindingRequest(
    string FileName,
    string Body,
    string? Severity = null,
    string? FindingId = null);

/// <summary>Full finding shape, returned for a single file's findings.</summary>
public sealed record CodeReviewFindingDto(
    string FindingId,
    string TaskId,
    string FileName,
    string Body,
    string? Severity,
    DateTime CreatedAt,
    long Version);

/// <summary>
/// List-shaped summary (no body) for the task-wide findings list, per the
/// route's "not full body" guidance.
/// </summary>
public sealed record CodeReviewFindingSummaryDto(
    string FindingId,
    string FileName,
    string? Severity,
    DateTime CreatedAt);

public sealed record CodeReviewFileFindingsResponse(
    string TaskId,
    string FileName,
    IReadOnlyList<CodeReviewFindingDto> Findings);

public sealed record CodeReviewFindingListResponse(
    string TaskId,
    IReadOnlyList<CodeReviewFindingSummaryDto> Findings);

/// <summary>
/// Global (not task-scoped) default review policy template. Neither
/// <c>ReviewGradingPolicy</c> nor <c>ReviewPlanResourcePolicy</c> models a
/// "default severities/scope" concept directly (the former is a pure
/// verdict-token-to-grade mapping used post-hoc when grading a completed
/// review, the latter is CPU-throttling for .NET test commands), so this
/// wraps the verdict tokens the grading policy already recognizes
/// (<see cref="ReviewGradingPolicy"/>) as the default severity vocabulary,
/// plus a small static default scope. This is a legitimate configuration
/// default with no natural existing backing store.
/// </summary>
public sealed record CodeReviewDefaultsDto(
    IReadOnlyList<string> SeverityLevels,
    string DefaultSeverity,
    IReadOnlyList<string> DefaultScope);

// ---- pipeline --------------------------------------------------------------

/// <summary>
/// The latest recorded step-run request for one pipeline step of a task, if
/// any row exists for it in <c>task_pipeline_step_runs</c>.
/// </summary>
public sealed record TaskPipelineStepViewDto(
    string StepId,
    string? LatestRunId,
    string? LatestStatus,
    string? LatestActorId,
    DateTime? LatestRequestedAt,
    DateTime? LatestCompletedAt);

public sealed record TaskPipelineViewDto(
    string TaskId,
    string ProjectId,
    long? FlowDefinitionVersion,
    IReadOnlyList<TaskPipelineStepViewDto> Steps);

public sealed record RunPipelineStepRequest(string? Note = null);

/// <summary>
/// A durable REQUEST to run a pipeline step for a task. The Task Server has
/// no direct access to a Runner or task workspace, so this row only ever
/// starts life at <c>status = "requested"</c>; it is never flipped to a
/// synthetic "completed" state by this store. Actual execution is a
/// Runner's job via the existing claim/lease flow, out of scope here.
/// </summary>
public sealed record TaskPipelineStepRunDto(
    string Id,
    string TaskId,
    string StepId,
    string Status,
    string? ActorId,
    DateTime RequestedAt,
    DateTime? CompletedAt);

// ---- regression radar -------------------------------------------------------

/// <summary>
/// One execution attempt whose already-classified <see cref="ExecutionOutcomeDecision"/>
/// is flagged as a regression signal: not an infrastructure hiccup, and not a
/// successful completion. See <see cref="RegressionRadarDto"/> for the
/// derivation rule.
/// </summary>
public sealed record RegressionRadarEntryDto(
    string RunId,
    ExecutionOutcomeKind Outcome,
    ExecutionRecoveryAction RecoveryAction,
    OutcomeConfidence Confidence,
    string? Detail,
    DateTime? FinishedAt);

/// <summary>
/// Task-scoped regression summary, computed on read from
/// <c>ListAttemptsAsync</c>'s already-classified outcomes
/// (<see cref="ExecutionOutcomeDecision"/>). No new schema; a run is
/// considered "regression-flagged" when its decision is not an
/// infrastructure outcome (<c>IsInfrastructureOutcome == false</c>) and did
/// not successfully complete -- i.e. <c>ExplicitAgentBlocker</c> or
/// <c>ProtocolInconclusive</c> today. This is this route's own derivation
/// choice (the shared classifier does not yet expose a first-class
/// "regression" flag) and is intentionally computed fresh on every read
/// rather than persisted as a second source of truth.
/// </summary>
public sealed record RegressionRadarDto(
    string TaskId,
    bool HasRegressions,
    int TotalAttempts,
    IReadOnlyList<RegressionRadarEntryDto> FlaggedAttempts);

// ---- review evidence actions -----------------------------------------------

public sealed record ReviewEvidenceActionRequest(string? Note = null);

public sealed record ReviewEvidenceActionDto(
    string Id,
    string TaskId,
    string EvidenceId,
    string Action,
    string? Note,
    string? ActorId,
    DateTime CreatedAt);

// ---- task summaries ----------------------------------------------------------

public sealed record InterimSummaryRequest(string SummaryMarkdown);

public sealed record RegenerateSummaryRequest(string? Reason = null);

public sealed record TaskSummaryDto(
    string TaskId,
    string? SummaryMarkdown,
    string Kind,
    int RegenerationRequestedCount,
    long Version,
    DateTime UpdatedAt);
