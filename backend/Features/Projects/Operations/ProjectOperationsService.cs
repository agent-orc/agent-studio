using AgentStudio.Deploy;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

public sealed record OperationsMarker(string DeploymentId, string ReleaseId, string Version,
    DateTime AtUtc, string Trigger, string State, IReadOnlyList<string> CardKeys);

public sealed record EnvironmentOperationsView(DeployEnvironment Environment, bool TelemetryAvailable,
    OperationsMetrics? Metrics, IReadOnlyList<OperationsMarker> Markers);

public sealed record ProjectOperationsView(string Project, DateTimeOffset Since, DateTimeOffset Until,
    IReadOnlyList<EnvironmentOperationsView> Environments, DoraSummary Dora);

public sealed record OperationsLogResult(bool TelemetryAvailable, IReadOnlyList<OperationsLogLine> Lines);

public sealed class ProjectOperationsService(DeployService deploy, TaskScannerService scanner,
    IOperationsTelemetryReader telemetry, TimeProvider clock)
{
    public ProjectOperationsView? Read(string project)
    {
        var environments = deploy.Environments(project);
        var releases = deploy.Releases(project);
        var deployments = deploy.Deployments(project, null);
        if (environments is null || releases is null || deployments is null) return null;
        var until = clock.GetUtcNow();
        var since = until.AddDays(-30);
        var releaseById = releases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var cards = scanner.ScanAllJobsWithArchive()
            .Where(item => string.Equals(item.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Key) ? item.Id : item.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(item => item.CreatedAt), StringComparer.OrdinalIgnoreCase);
        var rows = environments.Select(environment =>
        {
            var metrics = telemetry.ReadMetrics(project, environment.Id, since, until);
            return new EnvironmentOperationsView(environment, metrics is not null, metrics,
                CompletedMarkers(environment.Id, deployments, releaseById, since, until));
        }).ToArray();
        return new(project, since, until, rows,
            DoraMetrics.Calculate(environments, releases, deployments, cards, since, until));
    }

    public static IReadOnlyList<OperationsMarker> CompletedMarkers(string environmentId,
        IReadOnlyList<Deployment> deployments, IReadOnlyDictionary<string, Release> releases,
        DateTimeOffset since, DateTimeOffset until) => deployments
        .Where(run => run.EnvironmentId == environmentId && run.State == DeploymentStates.Succeeded
            && run.Trigger is (DeploymentTriggers.Deploy or DeploymentTriggers.Redeploy
                or DeploymentTriggers.Rollback or DeploymentTriggers.AutomaticRollback)
            && run.FinishedAtUtc >= since.UtcDateTime && run.FinishedAtUtc <= until.UtcDateTime)
        .OrderByDescending(run => run.FinishedAtUtc).ThenByDescending(run => run.Id, StringComparer.Ordinal)
        .Select(run => new OperationsMarker(run.Id, run.ReleaseId,
            releases.TryGetValue(run.ReleaseId, out var release) ? release.Version : run.ReleaseId,
            run.FinishedAtUtc!.Value, run.Trigger, run.State, release?.CardKeys ?? []))
        .ToArray();

    public OperationsLogResult? SearchLogs(string project, string environmentId, DateTimeOffset? since,
        DateTimeOffset? until, string? level, string? releaseId, string? text, int? limit)
    {
        if (deploy.Environments(project) is not { } environments) return null;
        if (!environments.Any(item => item.Id == environmentId)) return null;
        var end = until ?? clock.GetUtcNow();
        var start = since ?? end.AddHours(-24);
        if (start > end || end - start > TimeSpan.FromDays(30))
            throw new ArgumentException("Log search must use an ordered range of at most 30 days.");
        if (text?.Length > 200 || releaseId?.Length > 100 || level?.Length > 30)
            throw new ArgumentException("A log search filter is too long.");
        var query = new OperationsLogQuery(project, environmentId, start, end,
            string.IsNullOrWhiteSpace(level) ? null : level,
            string.IsNullOrWhiteSpace(releaseId) ? null : releaseId,
            string.IsNullOrWhiteSpace(text) ? null : text, Math.Clamp(limit ?? 100, 1, 100));
        var lines = telemetry.SearchLogs(query);
        return new(lines is not null, lines?.Take(query.Limit).ToArray() ?? []);
    }
}
