namespace AgentStudio.Projects;

/// <summary>
/// <c>GET /api/projects/{projectName}/cycle-time?window=1d|7d|30d|all[&amp;detail=transitions]</c>:
/// per-stage cycle-time aggregates (count, median, p90, max), the lane transition
/// summary (matrix, dwell, bounce causes, top loops), and the per-task rows
/// behind them; <c>detail=transitions</c> adds every task's transition list.
/// <c>GET /api/projects/{projectName}/cycle-time/tasks/{taskKey}</c>: one task
/// with its full transition list, independent of the window. The project handle
/// accepts the watch-path name, the registry id (<c>PROJ-NNN</c>), or the short code.
/// </summary>
public static class ProjectCycleTimeEndpoints
{
    public static void MapProjectCycleTimeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/cycle-time", (
            string projectName,
            string? window,
            string? detail,
            ProjectCycleTimeService cycleTime) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            if (!ProjectCycleTimeService.TryParseWindow(window, out _, out _))
                return Results.BadRequest(new { error = $"Invalid window '{window}'. Use 1d, 7d, 30d, or all." });
            if (!string.IsNullOrWhiteSpace(detail)
                && !string.Equals(detail.Trim(), ProjectCycleTimeService.TransitionsDetail, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Invalid detail '{detail}'. Use transitions or omit." });

            var response = cycleTime.Build(projectName, window, detail: detail);
            return response is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(response);
        });

        app.MapGet("/api/projects/{projectName}/cycle-time/tasks/{taskKey}", (
            string projectName,
            string taskKey,
            ProjectCycleTimeService cycleTime) =>
        {
            if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(taskKey))
                return Results.BadRequest(new { error = "project and taskKey required" });

            var result = cycleTime.BuildTask(projectName, taskKey);
            if (result is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var (response, exclusion) = result.Value;
            return response is null
                ? Results.NotFound(new { error = $"Task '{taskKey}' has no cycle-time row", reason = exclusion })
                : Results.Ok(response);
        });
    }
}
