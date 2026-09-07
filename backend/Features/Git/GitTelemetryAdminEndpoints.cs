namespace AgentStudio.Git;

/// <summary>
/// Admin surface for the SLO acceptance in AGT-2726: per-endpoint p50/p95 and
/// spawn rate over the last hour (read straight off
/// <see cref="GitProcessTelemetry"/> - it is the source, no second counter),
/// plus per-repository index age from <see cref="GitStateIndexService"/>. A
/// warning fires when <c>tasks/grouped</c> p95 exceeds one second or total
/// spawns exceed 20 per minute, matching the target this feature was built to
/// hold under the replayed trigger pattern.
/// </summary>
public static class GitTelemetryAdminEndpoints
{
    internal static readonly TimeSpan StatsWindow = TimeSpan.FromHours(1);
    private const double TasksGroupedP95WarnMs = 1000;
    private const double TotalSpawnsPerMinuteWarnThreshold = 20;

    public static void MapGitTelemetryAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/git-telemetry", (GitStateIndexService indexer) =>
            Results.Ok(BuildSnapshot(indexer, TimeProvider.System)));
    }

    internal static GitTelemetrySnapshot BuildSnapshot(GitStateIndexService indexer, TimeProvider timeProvider)
    {
        var endpoints = GitProcessTelemetry.GetStatsSnapshot(StatsWindow, timeProvider);
        var repositories = indexer.GetRepositoryStatuses(timeProvider);
        var totalSpawnsPerMinute = endpoints.Sum(e => e.SpawnsPerMinute);

        var warnings = new List<string>();
        var tasksGrouped = endpoints.FirstOrDefault(e => e.Label == "tasks/grouped");
        if (tasksGrouped is { P95Ms: > TasksGroupedP95WarnMs })
        {
            warnings.Add($"tasks/grouped p95 is {tasksGrouped.P95Ms:F0}ms, over the {TasksGroupedP95WarnMs:F0}ms target.");
        }
        if (totalSpawnsPerMinute > TotalSpawnsPerMinuteWarnThreshold)
        {
            warnings.Add($"Git spawns are averaging {totalSpawnsPerMinute:F1}/min, over the {TotalSpawnsPerMinuteWarnThreshold:F0}/min target.");
        }

        return new GitTelemetrySnapshot(
            timeProvider.GetUtcNow(),
            StatsWindow,
            endpoints,
            repositories,
            totalSpawnsPerMinute,
            warnings);
    }
}

public sealed record GitTelemetrySnapshot(
    DateTimeOffset GeneratedAt,
    TimeSpan Window,
    IReadOnlyList<EndpointStats> Endpoints,
    IReadOnlyList<GitStateRepositoryStatus> Repositories,
    double TotalSpawnsPerMinute,
    IReadOnlyList<string> Warnings);
