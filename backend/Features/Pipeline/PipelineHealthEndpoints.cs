using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed record SetOperatorSweepPauseRequest(bool Paused);

public static class PipelineHealthEndpoints
{
    public static void MapPipelineHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/pipeline-health", (
            string projectName,
            IPipelineHealthSensor health,
            OperatorSweepService sweeps) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var snapshot = health.Snapshot(projectName);
            return snapshot is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(snapshot with { OperatorSweeps = sweeps.Snapshot(projectName) });
        });

        app.MapPut("/api/projects/{projectName}/operator-sweeps/{sweep}", (
            string projectName, string sweep, SetOperatorSweepPauseRequest request,
            TaskScannerService scanner, ProjectSettingsService settings,
            OperatorSweepService sweeps) =>
        {
            if (!OperatorSweepNames.All.Contains(sweep))
                return Results.BadRequest(new { error = "Unknown sweep." });
            if (!scanner.GetWatchPaths().Any(entry =>
                    string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase)))
                return Results.NotFound(new { error = "Unknown project." });
            settings.SetOperatorSweepPaused(projectName, sweep, request.Paused);
            return Results.Ok(sweeps.Snapshot(projectName));
        });
    }
}
