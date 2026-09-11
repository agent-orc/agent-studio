using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The task/board half of global search. The legacy <c>/api/search</c> is a
/// mixed contract: its <c>tasks</c> domain reads the in-memory task index
/// (durable, Task-Server-appropriate state) while its <c>commits</c>,
/// <c>files</c>, <c>dossiers</c>, and <c>wiki</c> domains spawn <c>git</c> or
/// read the project's local checkout (dev-seat-only). Per the split recorded
/// in <c>docs/studio-route-ownership/routes.json</c>, this store answers only
/// the <c>tasks</c> domain; the repository domains stay on the dev-seat
/// <c>GET /api/search/repository</c> route the connector serves locally.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioSearchResponse> SearchTasksAsync(string? query, int limit, CancellationToken ct)
    {
        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length < 2) return new StudioSearchResponse(trimmed, []);

        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT t.id, t.task_key, t.title, t.state, p.id, p.name
              FROM tasks t
              JOIN projects p ON p.id = t.project_id
             WHERE t.task_key LIKE $pattern ESCAPE '\' OR t.title LIKE $pattern ESCAPE '\'
             ORDER BY CASE WHEN t.task_key = $exact THEN 0 ELSE 1 END, t.updated_at DESC
             LIMIT $limit;
            """,
            ("$pattern", $"%{EscapeLike(trimmed)}%"), ("$exact", trimmed), ("$limit", (long)Math.Clamp(limit, 1, 100)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<StudioSearchTaskItem>();
        while (await reader.ReadAsync(ct))
            items.Add(new StudioSearchTaskItem(
                "tasks", reader.GetString(5), reader.GetString(4), reader.GetString(2),
                reader.GetString(3), reader.GetString(1), reader.GetString(3), reader.GetString(1)));
        return new StudioSearchResponse(trimmed, items);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
