namespace AgentStudio.Deploy;

/// <summary>
/// Projects releases and runs into one <see cref="EnvironmentStatus"/> per
/// environment, the Octopus project-dashboard card. Pure.
/// </summary>
public static class EnvironmentStatusProjection
{
    /// <param name="hostHealth">
    /// Latest AGT-3176 host history status per environment id
    /// (<c>healthy</c>, <c>failed</c> or <c>unknown</c>). It also covers work
    /// that no release describes, such as an automatic rollback to an artifact
    /// from before releases existed.
    /// </param>
    public static IReadOnlyList<EnvironmentStatus> Project(
        IReadOnlyList<DeployEnvironment> environments,
        IReadOnlyList<Release> releases,
        IReadOnlyList<Deployment> deployments,
        IReadOnlyDictionary<string, string> hostHealth)
    {
        var ordered = environments.OrderBy(item => item.Order).ToArray();
        var releaseById = releases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var newestFirst = releases.OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
        return ordered.Select((environment, index) =>
        {
            var runs = deployments.Where(item => item.EnvironmentId == environment.Id).ToArray();
            var current = CurrentRelease(runs, releaseById);
            var latest = runs.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).LastOrDefault();
            var previous = index == 0 ? null : ordered[index - 1];
            var newer = newestFirst.FirstOrDefault(release =>
                (current is null || IsNewer(release, current))
                && (previous is null || SucceededIn(deployments, release.Id, previous.Id)));
            var cards = newer is null ? [] : releases
                .Where(release => (current is null || IsNewer(release, current)) && !IsNewer(release, newer))
                .OrderBy(release => release.CreatedAtUtc)
                .SelectMany(release => release.CardKeys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var health = hostHealth.TryGetValue(environment.Id, out var status) ? status switch
            {
                "healthy" => EnvironmentHealth.Healthy,
                "failed" => EnvironmentHealth.Unhealthy,
                _ => EnvironmentHealth.Unknown,
            } : EnvironmentHealth.Unknown;
            return new EnvironmentStatus(environment, current, latest, newer, cards, health);
        }).ToArray();
    }

    /// <summary>The release of the latest successful run, rollbacks included.</summary>
    public static Release? CurrentRelease(IEnumerable<Deployment> environmentRuns, IReadOnlyDictionary<string, Release> releases)
    {
        var run = LastSucceeded(environmentRuns);
        return run is not null && releases.TryGetValue(run.ReleaseId, out var release) ? release : null;
    }

    public static Deployment? LastSucceeded(IEnumerable<Deployment> environmentRuns)
        => environmentRuns.Where(item => item.State == DeploymentStates.Succeeded)
            .OrderBy(item => item.FinishedAtUtc).ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal).LastOrDefault();

    /// <summary>
    /// The rollback target: the release of the latest successful run whose
    /// release is older than the current one. Null when there is none.
    /// </summary>
    public static Release? PreviousRelease(IEnumerable<Deployment> environmentRuns, IReadOnlyDictionary<string, Release> releases)
    {
        var runs = environmentRuns.ToArray();
        var current = CurrentRelease(runs, releases);
        if (current is null) return null;
        return runs.Where(item => item.State == DeploymentStates.Succeeded)
            .OrderBy(item => item.FinishedAtUtc).ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => releases.GetValueOrDefault(item.ReleaseId))
            .LastOrDefault(release => release is not null && IsNewer(current, release));
    }

    public static ReleasePosition Position(Release release, Release? current)
        => current is null ? ReleasePosition.NoCurrent
            : release.Id == current.Id ? ReleasePosition.Current
            : IsNewer(release, current) ? ReleasePosition.Newer
            : ReleasePosition.Older;

    public static bool SucceededIn(IEnumerable<Deployment> deployments, string releaseId, string environmentId)
        => deployments.Any(item => item.ReleaseId == releaseId && item.EnvironmentId == environmentId
            && item.State == DeploymentStates.Succeeded);

    /// <summary>
    /// Releases order by creation, then id. <see cref="DeployService.CreateRelease"/>
    /// refuses a commit that is an ancestor of the newest release's commit, so
    /// creation order never contradicts commit history.
    /// </summary>
    public static bool IsNewer(Release candidate, Release than)
        => candidate.CreatedAtUtc > than.CreatedAtUtc
           || candidate.CreatedAtUtc == than.CreatedAtUtc && string.CompareOrdinal(candidate.Id, than.Id) > 0;
}
