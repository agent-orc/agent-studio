using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Supervisor observation, meta-cycle, recent-events, and intervention
/// projections, plus crash-recovery and queue-health remediation. Every
/// projection here is computed from the durable <c>runs</c>, <c>leases</c>,
/// and <c>audit</c> tables rather than a second supervisor log; every
/// intervention is a bounded mutation of those same tables.
/// </summary>
public sealed partial class TaskServerStore
{
    // --- Observation / meta-cycle / recent events --------------------------------

    public async Task<SupervisorObservationResponse> GetSupervisorObservationAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var settings = await GetStudioProjectSettingsAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var activeRuns = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT count(*) FROM runs r JOIN tasks t ON t.id = r.task_id
             WHERE t.project_id = $project AND r.status = 'running';
            """, ct, ("$project", project.ProjectId)));
        var staleLeases = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT count(*) FROM leases l JOIN tasks t ON t.id = l.task_id
             WHERE t.project_id = $project AND l.status = 'active' AND l.expires_at < $now;
            """, ct, ("$project", project.ProjectId), ("$now", Iso(UtcNow))));
        return new SupervisorObservationResponse(
            project.ProjectId, (int)activeRuns, (int)staleLeases, settings.PickupPaused, UtcNow);
    }

    public async Task<SupervisorMetaCycleResponse> GetSupervisorMetaCycleAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT started_at, finished_at FROM runs r JOIN tasks t ON t.id = r.task_id
             WHERE t.project_id = $project AND r.status = 'succeeded' AND r.started_at IS NOT NULL AND r.finished_at IS NOT NULL
             ORDER BY r.finished_at DESC LIMIT 50;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var minutes = new List<double>();
        while (await reader.ReadAsync(ct))
            minutes.Add((Parse(reader.GetString(1)) - Parse(reader.GetString(0))).TotalMinutes);
        return new SupervisorMetaCycleResponse(project.ProjectId, minutes.Count, minutes.Count == 0 ? 0 : minutes.Average());
    }

    public async Task<SupervisorRecentEventsResponse> GetSupervisorRecentEventsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT a.occurred_at, a.actor_id, a.action, a.target_type, a.target_id
              FROM audit a
              JOIN tasks t ON t.id = a.target_id AND a.target_type = 'task'
             WHERE t.project_id = $project
             ORDER BY a.sequence DESC LIMIT 100;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SupervisorRecentEventDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new SupervisorRecentEventDto(
                Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return new SupervisorRecentEventsResponse(result);
    }

    // --- Interventions -------------------------------------------------------

    public async Task<SupervisorInterveneResponse> CancelRunAsync(string projectIdentity, SupervisorInterveneRequest request, string actorId, CancellationToken ct)
        => await InterveneOnRunAsync(projectIdentity, request, "cancelled", "supervisor.cancel-run", actorId, ct);

    public async Task<SupervisorInterveneResponse> ForceFailRunAsync(string projectIdentity, SupervisorInterveneRequest request, string actorId, CancellationToken ct)
        => await InterveneOnRunAsync(projectIdentity, request, "failed", "supervisor.force-fail", actorId, ct);

    private async Task<SupervisorInterveneResponse> InterveneOnRunAsync(
        string projectIdentity, SupervisorInterveneRequest request, string terminalStatus, string action, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.RunId)) throw new ArgumentException("A runId is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var run = await ReadRunForProjectAsync(connection, transaction, project.ProjectId, request.RunId!, ct)
                ?? throw new KeyNotFoundException("Run was not found in this project.");
            await ExecuteAsync(connection, """
                UPDATE runs SET status = $status, finished_at = COALESCE(finished_at, $now) WHERE id = $run;
                UPDATE leases SET status = 'released' WHERE run_id = $run AND status = 'active';
                """, ct, transaction, ("$status", terminalStatus), ("$now", Iso(now)), ("$run", request.RunId));
            await AuditAsync(connection, transaction, actorId, action, "run", request.RunId!,
                JsonSerializer.Serialize(new { request.Reason }), ct);
        }, ct);
        return new SupervisorInterveneResponse(project.ProjectId, action, request.RunId, now);
    }

    public async Task<SupervisorInterveneResponse> PausePickupAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        await SetPickupPausedAsync(projectIdentity, true, actorId, ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        return new SupervisorInterveneResponse(project.ProjectId, "supervisor.pause-pickup", null, UtcNow);
    }

    public async Task<SupervisorInterveneResponse> ResumePickupAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        await SetPickupPausedAsync(projectIdentity, false, actorId, ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        return new SupervisorInterveneResponse(project.ProjectId, "supervisor.resume", null, UtcNow);
    }

    private static async Task<RunDto?> ReadRunForProjectAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string runId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT r.id, r.task_id, r.status, r.runner_id, r.fence, r.created_at, r.started_at, r.finished_at
              FROM runs r JOIN tasks t ON t.id = r.task_id
             WHERE r.id = $run AND t.project_id = $project;
            """, transaction, ("$run", runId), ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new RunDto(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
            Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)));
    }

    // --- Crash recovery ------------------------------------------------------

    public async Task<CrashRecoveryPendingListResponse> ListCrashRecoveryPendingAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT l.run_id, t.project_id, l.task_id, l.expires_at
              FROM leases l
              JOIN tasks t ON t.id = l.task_id
             WHERE l.status = 'active' AND l.expires_at < $now
               AND l.run_id NOT IN (SELECT run_id FROM studio_crash_recovery_decisions);
            """, ("$now", Iso(UtcNow)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CrashRecoveryPendingDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new CrashRecoveryPendingDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(0),
                Parse(reader.GetString(3)), "Lease expired without a terminal run outcome."));
        return new CrashRecoveryPendingListResponse(result);
    }

    public Task<CrashRecoveryDecisionResponse> CommitCrashRecoveryAsync(string runId, string actorId, CancellationToken ct) =>
        DecideCrashRecoveryAsync(runId, "committed", actorId, ct);

    public Task<CrashRecoveryDecisionResponse> DismissCrashRecoveryAsync(string runId, string actorId, CancellationToken ct) =>
        DecideCrashRecoveryAsync(runId, "dismissed", actorId, ct);

    private async Task<CrashRecoveryDecisionResponse> DecideCrashRecoveryAsync(string runId, string decision, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_crash_recovery_decisions(run_id, decision, decided_by, decided_at)
            VALUES ($run, $decision, $actor, $now)
            ON CONFLICT(run_id) DO UPDATE SET decision = excluded.decision, decided_by = excluded.decided_by, decided_at = excluded.decided_at;
            """, ct, ("$run", runId), ("$decision", decision), ("$actor", actorId), ("$now", Iso(now)));
        if (decision == "committed")
            await ExecuteAsync(connection, "UPDATE leases SET status = 'released' WHERE run_id = $run AND status = 'active';", ct, ("$run", runId));
        return new CrashRecoveryDecisionResponse(runId, decision, now);
    }

    // --- Queue health ----------------------------------------------------------

    public async Task<QueueHealthRepairResponse> RepairQueueHealthAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = UtcNow;
        var released = 0;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            released = await ExecuteAsync(connection, """
                UPDATE leases SET status = 'released'
                 WHERE status = 'active' AND expires_at < $now
                   AND task_id IN (SELECT id FROM tasks WHERE project_id = $project);
                """, ct, transaction, ("$now", Iso(now)), ("$project", project.ProjectId));
            await AuditAsync(connection, transaction, actorId, "queue-health.repair", "project", project.ProjectId,
                JsonSerializer.Serialize(new { released }), ct);
        }, ct);
        return new QueueHealthRepairResponse(project.ProjectId, released, now);
    }
}
