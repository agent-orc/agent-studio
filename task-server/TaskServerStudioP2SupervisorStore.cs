using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backing store for the Studio P2 supervisor/insight bundle: the
/// accepted-integration pipeline alert, cycle-time and throughput derived
/// from the durable <c>audit</c> ledger, the regression radar, supervisor
/// intervention actions built on the existing lease-release authority path,
/// the small <c>studio_supervisor_state</c> counters table, the project-
/// filtered replay of <c>studio_stream_events</c>, a bounded queue-health
/// repair pass, and a minimal test-run ledger. None of this introduces a
/// second source of truth for task or run state - it reads the tables the
/// rest of the Task Server already writes.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly IReadOnlyDictionary<string, int> SupervisorLaneRank = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [StudioTaskLanes.Backlog] = 0,
        [StudioTaskLanes.Ready] = 1,
        [StudioTaskLanes.Progress] = 2,
        [StudioTaskLanes.AutoReview] = 3,
        [StudioTaskLanes.HumanReview] = 4,
        [StudioTaskLanes.Escalated] = 5,
        [StudioTaskLanes.Completed] = 6,
        [StudioTaskLanes.Archive] = 7,
    };

    internal async Task ApplyStudioP2SupervisorMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_supervisor_state(
                project_id TEXT PRIMARY KEY,
                pickup_paused INTEGER NOT NULL DEFAULT 0,
                intervention_count INTEGER NOT NULL DEFAULT 0,
                last_intervention_kind TEXT,
                last_intervention_at TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_test_runs(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                task_id TEXT,
                run_id TEXT,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                outcome TEXT,
                summary_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_studio_test_runs_project_started
                ON studio_test_runs(project_id, started_at DESC);
            """, ct);
    }

    // ---- #1 accepted-integration-alert (not project-scoped) ----------------

    public async Task<AcceptedIntegrationAlertResponse> GetAcceptedIntegrationAlertAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT run_id, error, updated_at
              FROM result_finalizations
             WHERE status = 'failed'
             ORDER BY updated_at DESC
             LIMIT 1;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new AcceptedIntegrationAlertResponse(false, null, null);
        var runId = reader.GetString(0);
        var error = reader.IsDBNull(1) ? null : reader.GetString(1);
        var detectedAt = Parse(reader.GetString(2));
        return new AcceptedIntegrationAlertResponse(
            true,
            string.IsNullOrWhiteSpace(error) ? $"Result finalization failed for run '{runId}'." : error,
            detectedAt);
    }

    // ---- #2/#3 cycle-time ---------------------------------------------------

    public async Task<CycleTimeAggregateResponse> GetProjectCycleTimeAsync(
        string projectIdentity, string? window, string? detail, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (!TryParseSupervisorWindow(window, out var span, out var normalizedWindow))
            throw new ArgumentException($"Invalid window '{window}'. Use 7d, 30d, or all.");
        var includeTransitions = string.Equals(detail?.Trim(), "transitions", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(detail) && !includeTransitions)
            throw new ArgumentException($"Invalid detail '{detail}'. Use transitions or omit.");

        var now = UtcNow;
        var cutoff = span is null ? (DateTime?)null : now - span.Value;
        var tasks = await ListTasksAsync(project.ProjectId, ct);

        await using var connection = await OpenReadyAsync(ct);
        var auditByTask = await ReadTaskAuditRowsByProjectAsync(connection, project.ProjectId, ct);

        var summaries = new List<CycleTimeTaskSummary>();
        foreach (var task in tasks)
        {
            if (cutoff is not null && task.CreatedAt < cutoff.Value) continue;
            auditByTask.TryGetValue(task.TaskId, out var rows);
            summaries.Add(BuildTaskCycleTime(task, rows ?? [], includeTransitions, now));
        }

        var stageTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        foreach (var stage in summary.StageDurations)
            stageTotals[stage.State] = stageTotals.GetValueOrDefault(stage.State) + stage.Seconds;

        var completedDurations = summaries
            .Where(summary => summary.CycleTimeSeconds is not null)
            .Select(summary => summary.CycleTimeSeconds!.Value)
            .OrderBy(value => value)
            .ToList();

        return new CycleTimeAggregateResponse(
            project.ProjectId,
            normalizedWindow,
            now,
            summaries.Count,
            completedDurations.Count,
            Median(completedDurations),
            completedDurations.Count == 0 ? null : completedDurations.Average(),
            completedDurations.Count == 0 ? null : completedDurations.Max(),
            stageTotals
                .Select(pair => new CycleTimeStageDuration(pair.Key, pair.Value))
                .OrderBy(item => item.State, StringComparer.Ordinal)
                .ToList(),
            summaries);
    }

    public async Task<CycleTimeTaskResponse> GetTaskCycleTimeAsync(
        string projectIdentity, string taskKey, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var task = await GetTaskAsync(project.ProjectId, taskKey, ct)
            ?? throw new KeyNotFoundException($"Task '{taskKey}' was not found.");
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        var rows = await ReadTaskAuditRowsForTaskAsync(connection, task.TaskId, ct);
        return new CycleTimeTaskResponse(BuildTaskCycleTime(task, rows, includeTransitions: true, now));
    }

    // ---- #8 regression-radar -------------------------------------------------

    public async Task<RegressionRadarResponse> GetRegressionRadarAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT a.target_id, t.task_key, t.title, a.occurred_at, a.actor_id, a.detail_json
              FROM audit a
              JOIN tasks t ON t.id = a.target_id
             WHERE a.target_type = 'task' AND a.action = 'task.moved' AND t.project_id = $project
             ORDER BY a.occurred_at;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var regressions = new List<RegressionEventDto>();
        while (await reader.ReadAsync(ct))
        {
            var (from, to) = ReadMoveDetail(reader.GetString(5));
            if (from is null || to is null) continue;
            if (!SupervisorLaneRank.TryGetValue(from, out var fromRank)) continue;
            if (!SupervisorLaneRank.TryGetValue(to, out var toRank)) continue;
            // A regression is any move to a lane earlier in the pipeline than the
            // one the task left, e.g. auto-review sent back to progress, or a
            // completed task reissued back into ready.
            if (toRank >= fromRank) continue;
            regressions.Add(new RegressionEventDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Parse(reader.GetString(3)), from, to, reader.GetString(4)));
        }

        var repeatOffenders = regressions
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);

        return new RegressionRadarResponse(
            project.ProjectId,
            UtcNow,
            regressions.Count,
            repeatOffenders,
            regressions.OrderByDescending(item => item.OccurredAt).Take(50).ToList());
    }

    // ---- #9 throughput --------------------------------------------------------

    public async Task<ThroughputResponse> GetThroughputAsync(string projectIdentity, string? window, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (!TryParseSupervisorWindow(window, out var span, out var normalizedWindow))
            throw new ArgumentException($"Invalid window '{window}'. Use 7d, 30d, or all.");
        var now = UtcNow;
        var cutoff = span is null ? (DateTime?)null : now - span.Value;

        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT a.occurred_at, a.detail_json
              FROM audit a
              JOIN tasks t ON t.id = a.target_id
             WHERE a.target_type = 'task' AND a.action = 'task.moved' AND t.project_id = $project
             ORDER BY a.occurred_at;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var perDay = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        while (await reader.ReadAsync(ct))
        {
            var (_, to) = ReadMoveDetail(reader.GetString(1));
            if (!string.Equals(to, StudioTaskLanes.Completed, StringComparison.Ordinal)) continue;
            var occurredAt = Parse(reader.GetString(0));
            if (cutoff is not null && occurredAt < cutoff.Value) continue;
            var day = occurredAt.ToString("yyyy-MM-dd");
            perDay[day] = perDay.GetValueOrDefault(day) + 1;
            total++;
        }

        return new ThroughputResponse(
            project.ProjectId,
            normalizedWindow,
            now,
            total,
            perDay.Select(pair => new ThroughputPeriodDto(pair.Key, pair.Value)).ToList());
    }

    // ---- #4/#5 supervisor run interventions ------------------------------------

    public Task<SupervisorInterventionResponse> CancelActiveRunAsync(string projectIdentity, string actorId, CancellationToken ct)
        => InterveneOnActiveRunsAsync(projectIdentity, "cancel-run", "cancelled", actorId, ct);

    public Task<SupervisorInterventionResponse> ForceFailActiveRunAsync(string projectIdentity, string actorId, CancellationToken ct)
        => InterveneOnActiveRunsAsync(projectIdentity, "force-fail", "failed", actorId, ct);

    private async Task<SupervisorInterventionResponse> InterveneOnActiveRunsAsync(
        string projectIdentity, string kind, string outcome, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        RequireWritable();

        var active = new List<(string RunId, string RunnerId, string InstanceId, string LeaseId, long Fence)>();
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, """
            SELECT l.run_id, l.runner_id, l.instance_id, l.lease_id, l.fence
              FROM leases l
              JOIN tasks t ON t.id = l.task_id
             WHERE t.project_id = $project AND l.status = 'active';
            """, ("$project", project.ProjectId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                active.Add((
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4)));
            }
        }

        // Each release goes through the existing, fully-audited lease-release
        // authority path one run at a time (it owns its own write transaction),
        // so a supervisor sweep never nests inside another write transaction.
        var affected = new List<string>();
        foreach (var lease in active)
        {
            try
            {
                await ReleaseLeaseAsync(
                    lease.RunId,
                    new LeaseReleaseRequest(lease.RunnerId, lease.InstanceId, lease.LeaseId, lease.Fence, outcome),
                    actorId,
                    ct);
                affected.Add(lease.RunId);
            }
            catch (TaskServerConflictException)
            {
                // The lease moved on between the read above and the release (e.g.
                // the runner released it itself concurrently). A best-effort,
                // bounded supervisor sweep continues with the remaining runs.
            }
            catch (KeyNotFoundException)
            {
                // The lease disappeared between the read and the release.
            }
        }

        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await RecordSupervisorInterventionAsync(connection, transaction, project.ProjectId, kind, Iso(now), ct);
            await AuditAsync(connection, transaction, actorId, $"supervisor.intervene.{kind}", "project", project.ProjectId,
                JsonSerializer.Serialize(new { affectedRunCount = affected.Count, runIds = affected }), ct);
        }, ct);

        return new SupervisorInterventionResponse(project.ProjectId, kind, affected.Count, affected, now);
    }

    // ---- #6/#7 supervisor pickup pause/resume -----------------------------------

    public Task<SupervisorPickupStateResponse> PausePickupAsync(string projectIdentity, string actorId, CancellationToken ct)
        => SetPickupPausedAsync(projectIdentity, true, actorId, ct);

    public Task<SupervisorPickupStateResponse> ResumePickupAsync(string projectIdentity, string actorId, CancellationToken ct)
        => SetPickupPausedAsync(projectIdentity, false, actorId, ct);

    private async Task<SupervisorPickupStateResponse> SetPickupPausedAsync(
        string projectIdentity, bool paused, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        RequireWritable();
        var kind = paused ? "pause-pickup" : "resume";
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_supervisor_state(
                    project_id, pickup_paused, intervention_count, last_intervention_kind, last_intervention_at, updated_at)
                VALUES ($project, $paused, 1, $kind, $now, $now)
                ON CONFLICT(project_id) DO UPDATE SET
                    pickup_paused = $paused,
                    intervention_count = intervention_count + 1,
                    last_intervention_kind = $kind,
                    last_intervention_at = $now,
                    updated_at = $now;
                """, ct, transaction,
                ("$project", project.ProjectId), ("$paused", paused ? 1 : 0), ("$kind", kind), ("$now", Iso(now)));
            await AuditAsync(connection, transaction, actorId, $"supervisor.intervene.{kind}", "project", project.ProjectId,
                "{}", ct);
        }, ct);
        return new SupervisorPickupStateResponse(project.ProjectId, paused, now);
    }

    // ---- #10 meta-cycle ---------------------------------------------------------

    public async Task<SupervisorMetaCycleResponse> GetSupervisorMetaCycleAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var state = await ReadSupervisorStateAsync(connection, project.ProjectId, ct);
        return new SupervisorMetaCycleResponse(
            project.ProjectId, state.InterventionCount, state.LastInterventionKind, state.LastInterventionAt,
            state.PickupPaused, UtcNow);
    }

    // ---- #11 observation ----------------------------------------------------------

    public async Task<SupervisorObservationResponse> GetSupervisorObservationAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var activeRunCount = Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM leases l JOIN tasks t ON t.id = l.task_id
             WHERE t.project_id = $project AND l.status = 'active';
            """, ct, ("$project", project.ProjectId)));

        var queueDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = Command(connection,
            "SELECT state, COUNT(*) FROM tasks WHERE project_id = $project GROUP BY state;",
            ("$project", project.ProjectId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                queueDepth[reader.GetString(0)] = Convert.ToInt32(reader.GetInt64(1));
        }

        var state = await ReadSupervisorStateAsync(connection, project.ProjectId, ct);
        return new SupervisorObservationResponse(project.ProjectId, activeRunCount, queueDepth, state.PickupPaused, UtcNow);
    }

    // ---- #12 recent-events ---------------------------------------------------------

    public async Task<SupervisorRecentEventsResponse> GetSupervisorRecentEventsAsync(
        string projectIdentity, int? limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var take = Math.Clamp(limit ?? 50, 1, 500);
        var events = await ListStudioStreamEventsSinceAsync(0, ct);
        var filtered = events
            .Where(item => string.Equals(item.ProjectId, project.ProjectId, StringComparison.Ordinal))
            .OrderByDescending(item => item.Cursor)
            .Take(take)
            .ToList();
        return new SupervisorRecentEventsResponse(project.ProjectId, filtered);
    }

    // ---- #13 queue-health/repair -----------------------------------------------------

    public async Task<QueueHealthRepairResponse> RepairQueueHealthAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        RequireWritable();
        var now = UtcNow;
        var leasesMarkedUnknown = 0;
        var tasksRequeued = 0;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            // Leases past their expiry that are still marked active belong to a
            // runner the Task Server has lost contact with. This mirrors the
            // boot-time "process-unknown" sweep, scoped to this project only.
            leasesMarkedUnknown = await ExecuteAsync(connection, """
                UPDATE leases
                   SET status = 'process-unknown'
                 WHERE status = 'active'
                   AND expires_at <= $now
                   AND task_id IN (SELECT id FROM tasks WHERE project_id = $project);
                """, ct, transaction, ("$now", Iso(now)), ("$project", project.ProjectId));

            // A task stuck in progress with no active lease (its lease expired,
            // was just repaired above, or was released without a matching task
            // transition) is requeued so a runner can reclaim it.
            tasksRequeued = await ExecuteAsync(connection, """
                UPDATE tasks
                   SET state = '2-ready', version = version + 1, updated_at = $now
                 WHERE project_id = $project
                   AND state = '3-progress'
                   AND NOT EXISTS (
                       SELECT 1 FROM leases l WHERE l.task_id = tasks.id AND l.status = 'active');
                """, ct, transaction, ("$now", Iso(now)), ("$project", project.ProjectId));

            await RecordSupervisorInterventionAsync(connection, transaction, project.ProjectId, "queue-health-repair", Iso(now), ct);
            await AuditAsync(connection, transaction, actorId, "supervisor.queue-health.repaired", "project", project.ProjectId,
                JsonSerializer.Serialize(new { leasesMarkedUnknown, tasksRequeued }), ct);
        }, ct);
        return new QueueHealthRepairResponse(project.ProjectId, leasesMarkedUnknown, tasksRequeued, now);
    }

    // ---- #14 test-runs (+ ingestion plumbing) -----------------------------------------

    public async Task<TestRunDto> IngestTestRunAsync(string projectIdentity, IngestTestRunRequest request, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        RequireWritable();
        var id = StableOrGeneratedId(request.TestRunId, "trn");
        var startedAt = request.StartedAt ?? UtcNow;
        var summaryJson = request.Summary is null ? null : JsonSerializer.Serialize(request.Summary);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_test_runs(id, project_id, task_id, run_id, started_at, finished_at, outcome, summary_json)
                VALUES ($id, $project, $task, $run, $started, $finished, $outcome, $summary)
                ON CONFLICT(id) DO UPDATE SET
                    task_id = excluded.task_id,
                    run_id = excluded.run_id,
                    started_at = excluded.started_at,
                    finished_at = excluded.finished_at,
                    outcome = excluded.outcome,
                    summary_json = excluded.summary_json;
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$task", request.TaskId), ("$run", request.RunId),
                ("$started", Iso(startedAt)),
                ("$finished", request.FinishedAt is null ? null : Iso(request.FinishedAt.Value)),
                ("$outcome", request.Outcome), ("$summary", summaryJson));
            await AuditAsync(connection, transaction, actorId, "supervisor.test-run.ingested", "project", project.ProjectId,
                JsonSerializer.Serialize(new { testRunId = id, request.TaskId, request.RunId, request.Outcome }), ct);
        }, ct);
        return new TestRunDto(id, project.ProjectId, request.TaskId, request.RunId, startedAt, request.FinishedAt, request.Outcome, summaryJson);
    }

    public async Task<TestRunListResponse> GetProjectTestRunsAsync(string projectIdentity, int? limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var take = Math.Clamp(limit ?? 50, 1, 500);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_id, run_id, started_at, finished_at, outcome, summary_json
              FROM studio_test_runs
             WHERE project_id = $project
             ORDER BY started_at DESC, id DESC
             LIMIT $limit;
            """, ("$project", project.ProjectId), ("$limit", (long)take));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var runs = new List<TestRunDto>();
        while (await reader.ReadAsync(ct))
        {
            runs.Add(new TestRunDto(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return new TestRunListResponse(project.ProjectId, runs);
    }

    // ---- shared helpers -----------------------------------------------------------------

    private readonly record struct SupervisorAuditRow(DateTime OccurredAt, string Action, string DetailJson);

    private static async Task<Dictionary<string, List<SupervisorAuditRow>>> ReadTaskAuditRowsByProjectAsync(
        SqliteConnection connection, string projectId, CancellationToken ct)
    {
        var result = new Dictionary<string, List<SupervisorAuditRow>>(StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT a.target_id, a.occurred_at, a.action, a.detail_json
              FROM audit a
              JOIN tasks t ON t.id = a.target_id
             WHERE a.target_type = 'task' AND t.project_id = $project
             ORDER BY a.target_id, a.sequence;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var taskId = reader.GetString(0);
            if (!result.TryGetValue(taskId, out var rows))
                result[taskId] = rows = [];
            rows.Add(new SupervisorAuditRow(Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }
        return result;
    }

    private static async Task<List<SupervisorAuditRow>> ReadTaskAuditRowsForTaskAsync(
        SqliteConnection connection, string taskId, CancellationToken ct)
    {
        var rows = new List<SupervisorAuditRow>();
        await using var command = Command(connection, """
            SELECT occurred_at, action, detail_json
              FROM audit
             WHERE target_type = 'task' AND target_id = $task
             ORDER BY sequence;
            """, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new SupervisorAuditRow(Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    /// <summary>
    /// Approximation: only audit actions that carry a resolvable target lane
    /// advance the cycle-time checkpoint (creation, an explicit move, a
    /// generic update that changed <c>state</c>, or a start/continue request,
    /// which always lands the task back in <c>2-ready</c>). Actions with no
    /// lane effect (stop-requested, moved-to-top, deleted) do not close a
    /// segment early. The final segment always runs to <paramref name="now"/>,
    /// so <c>Sum(StageDurations) == ElapsedSeconds</c> holds for every task.
    /// </summary>
    private static CycleTimeTaskSummary BuildTaskCycleTime(
        TaskDto task, IReadOnlyList<SupervisorAuditRow> auditRows, bool includeTransitions, DateTime now)
    {
        var boundaries = new List<(DateTime At, string State)>();
        var transitions = includeTransitions ? new List<CycleTimeTaskTransition>() : null;
        foreach (var row in auditRows)
        {
            var state = ResolveCycleTimeState(row.Action, row.DetailJson);
            transitions?.Add(new CycleTimeTaskTransition(row.OccurredAt, row.Action, state));
            if (state is not null) boundaries.Add((row.OccurredAt, state));
        }
        if (boundaries.Count == 0) boundaries.Add((task.CreatedAt, task.State));
        if (boundaries[0].At > task.CreatedAt) boundaries.Insert(0, (task.CreatedAt, boundaries[0].State));

        var stageDurations = new Dictionary<string, double>(StringComparer.Ordinal);
        DateTime? completedAt = null;
        for (var i = 0; i < boundaries.Count; i++)
        {
            var (start, state) = boundaries[i];
            var end = i + 1 < boundaries.Count ? boundaries[i + 1].At : now;
            var seconds = Math.Max(0, (end - start).TotalSeconds);
            stageDurations[state] = stageDurations.GetValueOrDefault(state) + seconds;
            if (completedAt is null && string.Equals(state, StudioTaskLanes.Completed, StringComparison.Ordinal))
                completedAt = start;
        }

        return new CycleTimeTaskSummary(
            task.TaskId,
            task.TaskKey,
            task.Title,
            task.State,
            completedAt is not null,
            task.CreatedAt,
            completedAt,
            completedAt is null ? null : (completedAt.Value - task.CreatedAt).TotalSeconds,
            (now - task.CreatedAt).TotalSeconds,
            stageDurations
                .Select(pair => new CycleTimeStageDuration(pair.Key, pair.Value))
                .OrderBy(item => item.State, StringComparer.Ordinal)
                .ToList(),
            transitions);
    }

    private static string? ResolveCycleTimeState(string action, string detailJson)
    {
        try
        {
            using var document = JsonDocument.Parse(detailJson);
            var root = document.RootElement;
            return action switch
            {
                "task.created" => root.TryGetProperty("State", out var created)
                    ? created.GetString() : StudioTaskLanes.Backlog,
                "task.moved" => root.TryGetProperty("to", out var to) ? to.GetString() : null,
                "task.updated" => root.TryGetProperty("State", out var updated) ? updated.GetString() : null,
                "task.start-requested" or "task.continue-requested" => StudioTaskLanes.Ready,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? From, string? To) ReadMoveDetail(string detailJson)
    {
        try
        {
            using var document = JsonDocument.Parse(detailJson);
            var root = document.RootElement;
            var from = root.TryGetProperty("from", out var fromElement) ? fromElement.GetString() : null;
            var to = root.TryGetProperty("to", out var toElement) ? toElement.GetString() : null;
            return (from, to);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static double? Median(IReadOnlyList<double> sortedValues)
    {
        if (sortedValues.Count == 0) return null;
        var mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 1
            ? sortedValues[mid]
            : (sortedValues[mid - 1] + sortedValues[mid]) / 2.0;
    }

    private static bool TryParseSupervisorWindow(string? window, out TimeSpan? span, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(window) ? "30d" : window.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "7d": span = TimeSpan.FromDays(7); return true;
            case "30d": span = TimeSpan.FromDays(30); return true;
            case "all": span = null; return true;
            default: span = null; return false;
        }
    }

    private readonly record struct SupervisorStateSnapshot(
        bool PickupPaused, long InterventionCount, string? LastInterventionKind, DateTime? LastInterventionAt);

    private static async Task<SupervisorStateSnapshot> ReadSupervisorStateAsync(
        SqliteConnection connection, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT pickup_paused, intervention_count, last_intervention_kind, last_intervention_at
              FROM studio_supervisor_state
             WHERE project_id = $project;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new SupervisorStateSnapshot(false, 0, null, null);
        return new SupervisorStateSnapshot(
            reader.GetInt64(0) != 0,
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : Parse(reader.GetString(3)));
    }

    private static async Task RecordSupervisorInterventionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string kind, string now, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO studio_supervisor_state(
                project_id, pickup_paused, intervention_count, last_intervention_kind, last_intervention_at, updated_at)
            VALUES ($project, 0, 1, $kind, $now, $now)
            ON CONFLICT(project_id) DO UPDATE SET
                intervention_count = intervention_count + 1,
                last_intervention_kind = $kind,
                last_intervention_at = $now,
                updated_at = $now;
            """, ct, transaction, ("$project", projectId), ("$kind", kind), ("$now", now));
}
