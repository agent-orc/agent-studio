using AgentStudio.Review;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

public static class ReviewStatisticsEndpoints
{
    /// <summary>Adds per-step minutes and tokens, the fixed share f, the aspect share a and the wave census.</summary>
    public const string BaselineDetail = "baseline";

    public static void MapReviewStatisticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/token-usage/reviews", (
            string projectName, int? days, string? model, string? level, string? host, string? detail,
            TaskScannerService scanner, ReviewStatisticsService statistics, ReviewBaselineService baseline) =>
        {
            var project = scanner.GetWatchPaths().FirstOrDefault(path =>
                string.Equals(path.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return Results.NotFound(new { error = "Unknown project." });
            if (days is < 1 or > 90 || (model?.Length ?? 0) > 120 ||
                (level?.Length ?? 0) > 40 || (host?.Length ?? 0) > 120
                || !string.IsNullOrWhiteSpace(detail) && !string.Equals(detail.Trim(), BaselineDetail, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Invalid review statistics filter." });
            var result = statistics.Get(project.Name, project.Path, days ?? 30,
                string.IsNullOrWhiteSpace(model) ? null : model,
                string.IsNullOrWhiteSpace(level) ? null : level,
                string.IsNullOrWhiteSpace(host) ? null : host);
            // The S2 review baseline (AGT-3400) is opt-in: it reads every task's
            // pipeline record, so the default view keeps its cached projection.
            return Results.Ok(string.IsNullOrWhiteSpace(detail) ? result
                : result with { Baseline = baseline.Build(project.Name, project.Path, days ?? 30) });
        });
    }
}
