namespace AgentStudio.Runner;

public sealed record OperatorSweepPauseRequest(string? Reason);

/// <summary>
/// Operator control and health projection of the operator sweeps (AGT-3011).
/// Pause and resume are per project and per sweep and persist across restarts.
/// </summary>
public static class OperatorSweepEndpoints
{
    public static void MapOperatorSweepEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/operator-sweeps", (
            string projectName,
            TaskScannerService scanner,
            OperatorSweepService sweeps) =>
        {
            var project = ResolveProject(scanner, projectName);
            return project is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(sweeps.Project(project));
        });

        app.MapPost("/api/projects/{projectName}/operator-sweeps/{sweep}/pause", (
            string projectName,
            string sweep,
            OperatorSweepPauseRequest? request,
            TaskScannerService scanner,
            OperatorSweepService sweeps,
            HttpContext context) =>
            SetPaused(projectName, sweep, paused: true, request?.Reason, scanner, sweeps, context));

        app.MapPost("/api/projects/{projectName}/operator-sweeps/{sweep}/resume", (
            string projectName,
            string sweep,
            TaskScannerService scanner,
            OperatorSweepService sweeps,
            HttpContext context) =>
            SetPaused(projectName, sweep, paused: false, null, scanner, sweeps, context));
    }

    private static IResult SetPaused(
        string projectName,
        string sweep,
        bool paused,
        string? reason,
        TaskScannerService scanner,
        OperatorSweepService sweeps,
        HttpContext context)
    {
        var project = ResolveProject(scanner, projectName);
        if (project is null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
        var kind = OperatorSweepKinds.Normalize(sweep);
        if (kind is null)
            return Results.BadRequest(new
            {
                error = $"Unknown operator sweep '{sweep}'. Expected one of: {string.Join(", ", OperatorSweepKinds.All)}.",
            });
        if (reason is { Length: > 500 })
            return Results.BadRequest(new { error = "The pause reason is limited to 500 characters." });

        var clientId = context.Items["ClientId"] as string ?? context.Request.Headers["X-Client-Id"].FirstOrDefault();
        sweeps.SetPaused(project, kind, paused, TimelineActors.Human(clientId ?? string.Empty), reason);
        return Results.Ok(sweeps.Project(project));
    }

    private static string? ResolveProject(TaskScannerService scanner, string projectName)
        => scanner.GetWatchPaths()
            .FirstOrDefault(entry => string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase))
            ?.Name;
}
