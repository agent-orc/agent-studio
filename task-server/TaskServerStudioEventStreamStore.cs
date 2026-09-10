using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The durable, cursor-ordered log backing <c>/hubs/v1/studio</c>. A
/// reconnecting client supplies the last cursor it saw and the Task Server
/// replays every event after it before subscribing the connection to live
/// delivery, so a period of Studio-detached execution never loses a
/// mutation. This table is a notification feed, not a second source of
/// truth: it is written after the owning domain mutation already committed.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioStreamEventDto> AppendStudioStreamEventAsync(
        string kind, string? projectId, string? taskId, object payload, CancellationToken ct)
    {
        StudioStreamEventDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = Iso(UtcNow);
            var payloadJson = JsonSerializer.Serialize(payload);
            await ExecuteAsync(connection, """
                INSERT INTO studio_stream_events(occurred_at, kind, project_id, task_id, payload_json)
                VALUES ($now, $kind, $project, $task, $payload);
                """, ct, transaction,
                ("$now", now), ("$kind", kind), ("$project", projectId), ("$task", taskId), ("$payload", payloadJson));
            var cursor = Convert.ToInt64(await ScalarAsync(connection, "SELECT last_insert_rowid();", ct, transaction));
            result = new StudioStreamEventDto(cursor, Parse(now), kind, projectId, taskId, payloadJson);
        }, ct);
        return result!;
    }

    public async Task<IReadOnlyList<StudioStreamEventDto>> ListStudioStreamEventsSinceAsync(
        long afterCursor, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var result = new List<StudioStreamEventDto>();
        await using var command = Command(connection, """
            SELECT cursor, occurred_at, kind, project_id, task_id, payload_json
              FROM studio_stream_events
             WHERE cursor > $after
             ORDER BY cursor
             LIMIT 2000;
            """, ("$after", afterCursor));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new StudioStreamEventDto(
                reader.GetInt64(0),
                Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }
        return result;
    }
}
