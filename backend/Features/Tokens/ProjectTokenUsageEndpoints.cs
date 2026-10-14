

namespace AgentStudio.Tokens;

/// <summary>
/// Read surface for the project Token Usage panel (slice 8 of the
/// quality-system mockup, docs/mockups/quality-system/, "Token Usage"
/// surface). Four routes that all read the same project's
/// canonical token aggregation surface:
///
/// <list type="bullet">
///   <item>Summary: lifetime + rolling 24h / 7d totals plus the Job / Supporting
///   / Orchestrator category split (taxonomy.md vocabulary).</item>
///   <item>Heatmap: rows = jobs (most expensive first), columns = days.</item>
///   <item>Expensive jobs: top N jobs by total tokens.</item>
///   <item>Job detail: per-run breakdown with deltas for one job.</item>
/// </list>
///
/// Visibility, not enforcement (Critical Boundaries): these endpoints read a
/// deduplicated union of historical bus telemetry and durable task token
/// receipts; no token totals influence scheduling or job state.
/// </summary>
public static class ProjectTokenUsageEndpoints
{
    public static void MapProjectTokenUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/token-usage/summary", (
            string projectName,
            TaskScannerService scanner,
            ITokenAggregator tokens) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(tokens.ProjectSummary(projectName, entry.Path));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/heatmap", (
            string projectName,
            int? days,
            TaskScannerService scanner,
            ITokenAggregator tokens) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(tokens.ProjectHeatmap(projectName, entry.Path,
                days ?? ProjectTokenUsageFold.DefaultHeatmapDays));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/expensive", (
            string projectName,
            int? limit,
            TaskScannerService scanner,
            ITokenAggregator tokens) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var list = tokens.ProjectExpensiveJobs(projectName, entry.Path,
                limit ?? ProjectTokenJobFold.DefaultExpensiveLimit);
            return Results.Ok(new { project = projectName, jobs = list });
        });

        // Per-step-kind pipeline cost over time ("how it develops"): walks
        // the project's task folders, prices each pipeline-execution.json
        // through the single TokenPricing table, returns a per-day series
        // per step kind. Separate, cached path from the per-task Overview
        // poll (PipelineCostCalculator) so the poll never triggers a scan.
        app.MapGet("/api/projects/{projectName}/token-usage/pipeline-cost", (
            string projectName,
            int? days,
            TaskScannerService scanner,
            ProjectPipelineCostService pipelineCost) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(pipelineCost.Build(projectName, entry.Path,
                days ?? ProjectPipelineCostService.DefaultDays));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/job/{jobId}", (
            string projectName,
            string jobId,
            TaskScannerService scanner,
            ITokenAggregator tokens) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            if (string.IsNullOrWhiteSpace(jobId))
                return Results.BadRequest(new { error = "job id required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var detail = tokens.ProjectJobDetail(projectName, entry.Path, jobId);
            if (detail is null)
                return Results.NotFound(new { error = $"No token activity recorded for job '{jobId}'." });
            return Results.Ok(detail);
        });
    }
}
