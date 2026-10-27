namespace AgentStudio.Git;

/// <summary>
/// Admin surface for the SLO acceptance in AGT-2726: per-endpoint p50/p95 and
/// spawn rate over the last hour (read straight off
/// <see cref="GitProcessTelemetry"/> - it is the source, no second counter),
/// plus per-repository index age from <see cref="GitStateIndexService"/> and
/// the read guard's per-route Git spawn debt (AGT-3321). A
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
        app.MapGet("/api/admin/git-telemetry", (GitStateIndexService indexer, TaskScannerService scanner,
                GitFactStore factStore) =>
            Results.Ok(BuildSnapshot(indexer, TimeProvider.System, scanner, factStore)));
    }

    internal static GitTelemetrySnapshot BuildSnapshot(GitStateIndexService indexer, TimeProvider timeProvider,
        TaskScannerService? scanner = null, GitFactStore? factStore = null)
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
        foreach (var repository in repositories.Where(repository => repository.ConsecutiveFailures > 0))
            warnings.Add($"Git state index for {repository.ProjectName} has failed {repository.ConsecutiveFailures} consecutive time(s).");
        var readGuard = new GitReadGuardSnapshot(GitReadRequestGuard.Mode, GitReadSpawnExceptionManifest.Digest(),
            GitReadSpawnExceptionManifest.Entries.Count, GitReadRequestGuard.Ledger.Snapshot());
        foreach (var route in readGuard.Routes.Where(route => route.UnlistedRequests > 0))
            warnings.Add($"{route.Method} {route.RouteTemplate} started Git on {route.UnlistedRequests} read request(s) without a frozen exception.");
        foreach (var route in readGuard.Routes.Where(route => route.BreachedRequests > 0))
            warnings.Add($"{route.Method} {route.RouteTemplate} exceeded its ceiling of {route.SpawnCeiling} Git spawns on {route.BreachedRequests} read request(s).");
        if (factStore?.LastHealthEvent is { } storeEvent)
            warnings.Add($"The Git fact store was corrupt ({storeEvent.Reason}) and was rebuilt; the old file is {storeEvent.MovedTo}.");

        return new GitTelemetrySnapshot(
            timeProvider.GetUtcNow(),
            StatsWindow,
            endpoints,
            repositories,
            totalSpawnsPerMinute,
            warnings,
            scanner is null ? [] : TaskProjectionCards.FindDuplicateIds(scanner.ScanAllJobsWithArchive()),
            FactStore: factStore is null ? null : new GitFactStoreStatus(factStore.DatabasePath, factStore.IsAvailable,
                factStore.LastHealthEvent),
            ReadGuard: readGuard);
    }
}

public sealed record GitTelemetrySnapshot(
    DateTimeOffset GeneratedAt,
    TimeSpan Window,
    IReadOnlyList<EndpointStats> Endpoints,
    IReadOnlyList<GitStateRepositoryStatus> Repositories,
    double TotalSpawnsPerMinute,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DuplicateTaskId> DuplicateTaskIds,
    GitFactStoreStatus? FactStore = null,
    GitReadGuardSnapshot? ReadGuard = null);

/// <summary>Where the Git fact store lives, whether it is in use, and its last typed health event.</summary>
public sealed record GitFactStoreStatus(string? Path, bool Available, GitFactStoreHealthEvent? LastHealthEvent);
