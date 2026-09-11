using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Read-only Studio projections: the lane-grouped board and the per-project
/// runner status view. Both fold together facts that already exist in the
/// durable tables rather than tracking a second copy of task or run state.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioBoardResponse> GetStudioBoardAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var lanes = StudioTaskLanes.All.ToDictionary(lane => lane, _ => new List<TaskDto>(), StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
              FROM tasks
             ORDER BY state, rank, task_key;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var task = ReadTask(reader);
            if (lanes.TryGetValue(task.State, out var list)) list.Add(task);
        }
        return new StudioBoardResponse(
            lanes[StudioTaskLanes.Backlog],
            lanes[StudioTaskLanes.Ready],
            lanes[StudioTaskLanes.Progress],
            lanes[StudioTaskLanes.AutoReview],
            lanes[StudioTaskLanes.HumanReview],
            lanes[StudioTaskLanes.Escalated],
            lanes[StudioTaskLanes.Completed],
            lanes[StudioTaskLanes.Archive],
            UtcNow);
    }

    public async Task<StudioRunnerStatusResponse> GetStudioRunnerStatusAsync(CancellationToken ct)
    {
        var hosts = await ListHostProjectionsAsync(ct);
        var work = hosts.SelectMany(host => host.Work.Select(item => (Host: host, Work: item))).ToList();
        var projects = new Dictionary<string, (string Name, List<StudioActiveRunDto> Runs)>(StringComparer.Ordinal);
        if (work.Count > 0)
        {
            await using var connection = await OpenReadyAsync(ct);
            foreach (var (host, item) in work)
            {
                var project = await ReadTaskProjectAsync(connection, item.TaskId, ct);
                if (project is null) continue;
                var (projectId, projectName) = project.Value;
                if (!projects.TryGetValue(projectId, out var bucket))
                {
                    bucket = (projectName, []);
                    projects[projectId] = bucket;
                }
                bucket.Runs.Add(new StudioActiveRunDto(
                    item.TaskId, item.TaskKey, item.RunId, host.RunnerId, host.HostId, item.Phase, item.LastActivityAt));
            }
        }
        return new StudioRunnerStatusResponse(
            projects.Select(pair => new StudioProjectRunnerStatus(pair.Key, pair.Value.Name, pair.Value.Runs)).ToList(),
            UtcNow);
    }

    private static async Task<(string ProjectId, string ProjectName)?> ReadTaskProjectAsync(
        SqliteConnection connection, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT p.id, p.name FROM tasks t JOIN projects p ON p.id = t.project_id WHERE t.id = $task;
            """, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }
}
