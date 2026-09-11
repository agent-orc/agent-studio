namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P2 "operations and insight" supervisor
/// bundle: the accepted-integration pipeline alert, per-project cycle-time
/// and throughput derived from the durable <c>audit</c> ledger, the
/// regression radar, supervisor intervention actions (cancel/force-fail/
/// pause-pickup/resume), the meta-cycle and observation summaries, the
/// replayed recent-events feed, the queue-health repair pass, and the
/// small test-run ingestion/listing pair. Every response here is computed
/// from data the Task Server already durably records rather than a second,
/// parallel tracking table - see the remarks on each record for exactly
/// which source table backs it.
/// </summary>
public sealed record AcceptedIntegrationAlertResponse(bool HasAlert, string? Detail, DateTime? DetectedAt);

/// <summary>One bucket of time an audit-derived checkpoint attributes to a lane state.</summary>
public sealed record CycleTimeStageDuration(string State, double Seconds);

/// <summary>One resolvable audit checkpoint behind a task's cycle-time row (only present when <c>detail=transitions</c> was requested).</summary>
public sealed record CycleTimeTaskTransition(DateTime OccurredAt, string Action, string? State);

/// <summary>
/// One task's cycle-time row. <see cref="CycleTimeSeconds"/> is the time from
/// creation to the task's first entry into <c>6-completed</c> and is null
/// for a task that has never completed. <see cref="ElapsedSeconds"/> is
/// always the time from creation to now (or, for an archived/removed
/// history, to the last observed checkpoint) and always equals the sum of
/// <see cref="StageDurations"/> - that invariant is what a caller can use
/// to sanity-check the breakdown.
/// </summary>
public sealed record CycleTimeTaskSummary(
    string TaskId,
    string TaskKey,
    string Title,
    string CurrentState,
    bool Completed,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    double? CycleTimeSeconds,
    double ElapsedSeconds,
    IReadOnlyList<CycleTimeStageDuration> StageDurations,
    IReadOnlyList<CycleTimeTaskTransition>? Transitions);

/// <summary>
/// <c>GET /api/v1/studio/projects/{project}/cycle-time?window=7d|30d|all&amp;detail=transitions</c>.
/// <see cref="StageTotals"/> sums <see cref="CycleTimeTaskSummary.StageDurations"/>
/// across every task in <see cref="Tasks"/>. Median/average/max cover only
/// tasks that have completed at least once within the window.
/// </summary>
public sealed record CycleTimeAggregateResponse(
    string ProjectId,
    string Window,
    DateTime GeneratedAt,
    int TaskCount,
    int CompletedTaskCount,
    double? MedianCycleTimeSeconds,
    double? AverageCycleTimeSeconds,
    double? MaxCycleTimeSeconds,
    IReadOnlyList<CycleTimeStageDuration> StageTotals,
    IReadOnlyList<CycleTimeTaskSummary> Tasks);

/// <summary><c>GET /api/v1/studio/projects/{project}/cycle-time/tasks/{taskKey}</c>. Always includes the transition list.</summary>
public sealed record CycleTimeTaskResponse(CycleTimeTaskSummary Task);

/// <summary>
/// One backward lane transition (e.g. auto-review sent back to progress, or
/// a completed task reissued) read from a <c>task.moved</c> audit row whose
/// target lane ranks behind its source lane.
/// </summary>
public sealed record RegressionEventDto(
    string TaskId,
    string TaskKey,
    string Title,
    DateTime OccurredAt,
    string FromState,
    string ToState,
    string ActorId);

/// <summary><c>GET /api/v1/studio/projects/{project}/regression-radar</c>.</summary>
public sealed record RegressionRadarResponse(
    string ProjectId,
    DateTime GeneratedAt,
    int RegressionCount,
    int TasksWithRepeatRegressions,
    IReadOnlyList<RegressionEventDto> Recent);

/// <summary>Count of tasks that first reached <c>6-completed</c> on a given calendar day (UTC).</summary>
public sealed record ThroughputPeriodDto(string PeriodStart, int CompletedCount);

/// <summary><c>GET /api/v1/studio/projects/{project}/throughput?window=7d|30d|all</c>.</summary>
public sealed record ThroughputResponse(
    string ProjectId,
    string Window,
    DateTime GeneratedAt,
    int TotalCompleted,
    IReadOnlyList<ThroughputPeriodDto> Periods);

/// <summary>
/// Result of a supervisor cancel-run or force-fail sweep: every run holding
/// an active lease against one of this project's tasks was released with
/// the matching outcome via the existing lease-release authority path.
/// </summary>
public sealed record SupervisorInterventionResponse(
    string ProjectId,
    string Action,
    int AffectedRunCount,
    IReadOnlyList<string> RunIds,
    DateTime OccurredAt);

/// <summary>Result of a supervisor pause-pickup or resume action.</summary>
public sealed record SupervisorPickupStateResponse(string ProjectId, bool PickupPaused, DateTime OccurredAt);

/// <summary><c>GET /api/v1/studio/supervisor/{project}/meta-cycle</c>: counters kept on <c>studio_supervisor_state</c>.</summary>
public sealed record SupervisorMetaCycleResponse(
    string ProjectId,
    long InterventionCount,
    string? LastInterventionKind,
    DateTime? LastInterventionAt,
    bool PickupPaused,
    DateTime GeneratedAt);

/// <summary>
/// <c>GET /api/v1/studio/supervisor/{project}/observation</c>: active run
/// count from <c>leases</c>/<c>tasks</c>, queue depth by lane from
/// <c>tasks</c>, and the pickup-paused flag from <c>studio_supervisor_state</c>.
/// </summary>
public sealed record SupervisorObservationResponse(
    string ProjectId,
    int ActiveRunCount,
    IReadOnlyDictionary<string, int> QueueDepthByState,
    bool PickupPaused,
    DateTime GeneratedAt);

/// <summary>
/// <c>GET /api/v1/studio/supervisor/{project}/recent-events</c>: the
/// project-filtered tail of the existing durable <c>studio_stream_events</c>
/// feed, newest first.
/// </summary>
public sealed record SupervisorRecentEventsResponse(string ProjectId, IReadOnlyList<StudioStreamEventDto> Events);

/// <summary>
/// <c>POST /api/v1/studio/projects/{project}/queue-health/repair</c>:
/// counts of what the bounded, project-scoped repair pass actually changed.
/// </summary>
public sealed record QueueHealthRepairResponse(
    string ProjectId,
    int LeasesMarkedUnknown,
    int TasksRequeued,
    DateTime OccurredAt);

/// <summary>
/// <c>POST /api/v1/studio/test-runs/{project}/ingest</c> request body. Not
/// part of the 102-route P2 bundle - plumbing so the paired GET route has
/// real rows to list, the same way board attachments back the chat surface.
/// </summary>
public sealed record IngestTestRunRequest(
    string? TestRunId = null,
    string? TaskId = null,
    string? RunId = null,
    DateTime? StartedAt = null,
    DateTime? FinishedAt = null,
    string? Outcome = null,
    object? Summary = null);

public sealed record TestRunDto(
    string TestRunId,
    string ProjectId,
    string? TaskId,
    string? RunId,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? Outcome,
    string? SummaryJson);

/// <summary><c>GET /api/v1/studio/projects/{project}/test-runs</c>: newest first.</summary>
public sealed record TestRunListResponse(string ProjectId, IReadOnlyList<TestRunDto> Runs);
