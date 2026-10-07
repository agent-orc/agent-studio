using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    /// <summary>
    /// Rebuilds the task status and receipt history from transactional authority.
    /// Replayed commands occupy one row, and the current fact is selected only
    /// from the current task or attempt generation.
    /// </summary>
    public async Task<SteeringFeedbackResponseDto?> GetSteeringFeedbackAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct);
        if (task is null) return null;
        await using var connection = await OpenReadyAsync(ct);
        return await ReadSteeringFeedbackAsync(connection, task, ct);
    }

    private static async Task<SteeringFeedbackResponseDto> ReadSteeringFeedbackAsync(
        SqliteConnection connection, TaskDto task, CancellationToken ct)
    {
        var facts = new List<SteeringFeedbackReceiptDto>();
        var currentRunId = Convert.ToString(await ScalarAsync(connection,
            "SELECT id FROM runs WHERE task_id = $task ORDER BY COALESCE(fence, 0) DESC, created_at DESC LIMIT 1;",
            ct, ("$task", task.TaskId)));
        var currentReviewId = Convert.ToString(await ScalarAsync(connection,
            "SELECT id FROM review_attempts WHERE task_id = $task ORDER BY created_at DESC, id DESC LIMIT 1;",
            ct, ("$task", task.TaskId)));
        static string Bound(string value)
        {
            var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= 240 ? line : line[..240];
        }

        await using (var command = Command(connection, """
            SELECT command_id, action, result_task_version, result_state, reason, accepted_at
              FROM steering_actions WHERE task_id = $task ORDER BY accepted_at, command_id;
            """, ("$task", task.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var commandId = reader.GetString(0);
                facts.Add(new($"action:{commandId}", "action", "admitted", Parse(reader.GetString(5)),
                    task.TaskId, commandId, null, null, null, Bound(reader.GetString(4)),
                    reader.GetInt64(2) == task.Version && reader.GetString(3) == task.State));
            }

        await using (var command = Command(connection, """
            SELECT command_id, status, reason, accepted_at, run_id, fence, consumed_at
              FROM continuation_intents WHERE task_id = $task ORDER BY round;
            """, ("$task", task.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var commandId = reader.GetString(0);
                var status = reader.GetString(1);
                var runId = reader.IsDBNull(4) ? null : reader.GetString(4);
                var state = status switch
                {
                    "queued" => "requested", "claimed" => "admitted",
                    "consumed" => "consumed", "superseded" => "rejected", _ => "unresolved",
                };
                facts.Add(new($"continuation:{commandId}", "continuation", state,
                    reader.IsDBNull(6) ? Parse(reader.GetString(3)) : Parse(reader.GetString(6)),
                    task.TaskId, commandId, runId, runId, null, Bound(reader.GetString(2)),
                    status is "queued" or "claimed" || runId is not null && runId == currentRunId));
            }

        await using (var command = Command(connection, """
            SELECT r.id, r.status, h.host_id, r.fence, r.created_at, r.started_at, r.finished_at
              FROM runs r LEFT JOIN runners h ON h.id = r.runner_id
             WHERE r.task_id = $task ORDER BY r.created_at, r.id;
            """, ("$task", task.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var runId = reader.GetString(0);
                var status = reader.GetString(1);
                var state = status switch
                {
                    "running" => "running", "completed" => "consumed",
                    "process-unknown" or "interrupted" => "unresolved",
                    "failed" or "stopped" => "rejected", _ => "admitted",
                };
                var at = reader.IsDBNull(6) ? reader.IsDBNull(5) ? Parse(reader.GetString(4))
                    : Parse(reader.GetString(5)) : Parse(reader.GetString(6));
                var runnerId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var incident = status == "process-unknown"
                    ? RouteIncidentId(runnerId, at, runId) : null;
                facts.Add(new($"run:{runId}", "run", state,
                    at,
                    task.TaskId, null, runId, runId, incident, Bound(status),
                    runId == currentRunId));
            }

        await using (var command = Command(connection, """
            SELECT a.id, a.status, a.outcome, a.created_at, a.reported_at,
                   s.source_run_id, a.host_id, a.report_idempotency_key
              FROM review_attempts a JOIN review_subjects s ON s.id = a.subject_id
             WHERE a.task_id = $task ORDER BY a.created_at, a.id;
            """, ("$task", task.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var attemptId = reader.GetString(0);
                var status = reader.GetString(1);
                var runId = reader.GetString(5);
                var state = status switch
                {
                    "queued" => "requested", "leased" => "running",
                    "reported" or "cleaned" => reader.IsDBNull(2) || reader.GetString(2) == "Pass"
                        ? "consumed" : "rejected",
                    "process-unknown" or "cleanup-failed" => "unresolved",
                    "superseded" => "rejected", _ => "admitted",
                };
                var at = reader.IsDBNull(4) ? Parse(reader.GetString(3)) : Parse(reader.GetString(4));
                var hostId = reader.IsDBNull(6) ? null : reader.GetString(6);
                facts.Add(new($"review:{attemptId}", "review", state,
                    at,
                    task.TaskId, null, attemptId, runId,
                    status == "process-unknown" ? RouteIncidentId(hostId, at, attemptId) : null,
                    Bound(reader.IsDBNull(2) ? status : reader.GetString(2)),
                    attemptId == currentReviewId,
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

        // A later accepted completion resolves an older route-loss observation.
        // Keep its incident identity for historical drill-down, but remove the
        // acute disposition from that generation.
        foreach (var kind in new[] { "run", "review" })
        {
            var completions = facts.Where(fact => fact.Kind == kind && fact.State == "consumed")
                .Select(fact => fact.OccurredAt).ToArray();
            for (var index = 0; index < facts.Count; index++)
                if (facts[index].Kind == kind && facts[index].State == "unresolved"
                    && completions.Any(at => at > facts[index].OccurredAt))
                    facts[index] = facts[index] with { State = "recovered", Current = false };
        }

        var history = facts.DistinctBy(fact => fact.Identity, StringComparer.Ordinal)
            .OrderBy(fact => fact.OccurredAt).ThenBy(fact => fact.Identity, StringComparer.Ordinal).ToArray();
        var current = history.Where(fact => fact.Current)
            .OrderByDescending(fact => fact.OccurredAt).ThenByDescending(fact => fact.Identity, StringComparer.Ordinal)
            .FirstOrDefault();
        return new(task.TaskId, task.TaskKey, task.State, current, history);
    }

    private static string RouteIncidentId(string? runnerId, DateTime at, string fallbackAttemptId)
    {
        if (string.IsNullOrWhiteSpace(runnerId)) return $"route:attempt:{fallbackAttemptId}";
        var utc = at.ToUniversalTime();
        var bucket = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour,
            utc.Minute / 15 * 15, 0, DateTimeKind.Utc);
        return $"route:{runnerId}:{bucket:yyyyMMddHHmm}";
    }

}
