using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

public static class ReviewStatisticsEndpoints
{
    public static void MapReviewStatisticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/token-usage/reviews", (
            string projectName, int? days, string? model, string? level, string? host,
            TaskScannerService scanner, ReviewStatisticsService statistics) =>
        {
            var project = scanner.GetWatchPaths().FirstOrDefault(path =>
                string.Equals(path.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return Results.NotFound(new { error = "Unknown project." });
            if (days is < 1 or > 90 || (model?.Length ?? 0) > 120 ||
                (level?.Length ?? 0) > 40 || (host?.Length ?? 0) > 120)
                return Results.BadRequest(new { error = "Invalid review statistics filter." });
            return Results.Ok(statistics.Get(project.Name, project.Path, days ?? 30,
                string.IsNullOrWhiteSpace(model) ? null : model,
                string.IsNullOrWhiteSpace(level) ? null : level,
                string.IsNullOrWhiteSpace(host) ? null : host));
        });
    }
}
