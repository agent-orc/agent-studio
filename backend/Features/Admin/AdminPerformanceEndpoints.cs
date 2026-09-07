using AgentStudio.Git;

namespace AgentStudio.Admin;

/// <summary>
/// Read-only performance surface for the Admin page (AGT-2726): per-endpoint
/// latency percentiles over the last hour, the git spawn rate, and how old each
/// repository's indexed state is. Every number comes from
/// <see cref="GitProcessTelemetry"/> and <see cref="GitStateIndex"/>; there is
/// deliberately no second counter to keep in step with them.
/// </summary>
public static class AdminPerformanceEndpoints
{
    public static void MapAdminPerformanceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/performance/git-state", (GitStateIndex index) =>
        {
            var telemetry = GitProcessTelemetry.Summarize();
            var groupedP95 = telemetry.Endpoints
                .FirstOrDefault(endpoint => endpoint.Label == "tasks/grouped")?.P95Ms;

            return Results.Ok(new GitStatePerformanceResponse(
                telemetry.WindowStartUtc,
                telemetry.WindowEndUtc,
                telemetry.Endpoints,
                index.Status(),
                index.RecentRuns(),
                telemetry.Spawns,
                telemetry.SpawnsPerMinute,
                GitStateSloPolicy.Evaluate(groupedP95, telemetry.SpawnsPerMinute)));
        });
    }
}

/// <summary>The Admin performance panel's payload.</summary>
public sealed record GitStatePerformanceResponse(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<GitTelemetryEndpointSummary> Endpoints,
    IReadOnlyList<GitIndexRepositoryStatus> Repositories,
    IReadOnlyList<GitIndexRunRecord> RecentRuns,
    int Spawns,
    double SpawnsPerMinute,
    IReadOnlyList<GitStateSloWarning> Warnings);
