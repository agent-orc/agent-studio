using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <param name="ReleaseId">The release that brought the card; null in the integrated column.</param>
/// <param name="RolledBack">A rollback withdrew the release that brought the card into this environment.</param>
public sealed record DeliveryColumnCard(string Key, string? ReleaseId, string? ReleaseVersion, bool RolledBack);

public sealed record DeliveryColumnRelease(string Id, string Version, string Channel, DateTime CreatedAtUtc);

public sealed record DeliveryColumnRun(string Id, string ReleaseId, string State, string Trigger, DateTime? AtUtc);

public sealed record DeliveryColumnApproval(string DeploymentId, string ReleaseId, string? ReleaseVersion,
    DateTime RequestedAtUtc);

/// <summary>
/// One column of the board's Delivery section (AGT-3276 D5 L-B). The header
/// fields are null in the integrated column. <c>Landed</c> lists the cards of
/// the current release and the releases before it, newest first, bounded to
/// <see cref="DeliveryColumnsResponse.ReleaseDepth"/> releases; in the
/// integrated column it lists the cards on the integration branch that no
/// release names yet. <c>Waiting</c> is the environment's
/// <see cref="EnvironmentStatus.CardsSinceLastDeploy"/>. <c>RolledBack</c>
/// lists the cards whose release a rollback withdrew here.
/// </summary>
public sealed record DeliveryColumn(string Id, string? EnvironmentId, string Name, string? Kind, int Order,
    DeliveryColumnRelease? CurrentRelease, DeliveryColumnRun? LastRun, string? Health,
    DeliveryColumnApproval? PendingApproval, IReadOnlyList<DeliveryColumnCard> Landed,
    IReadOnlyList<DeliveryColumnCard> Waiting, IReadOnlyList<DeliveryColumnCard> RolledBack);

public sealed record DeliveryColumnsResponse(string ProjectId, int ReleaseDepth, IReadOnlyList<DeliveryColumn> Columns);

/// <summary>
/// Projects releases and deploy runs into the Delivery section: an integrated
/// column, then one column per declared environment in lifecycle order. It
/// composes <see cref="EnvironmentStatusProjection"/> (header and waiting
/// cards) with <see cref="CardDeploymentProjection"/> (rolled-back cards).
/// Release membership follows creation order, the same order the status
/// projection uses, so no git is read. Lane states are never touched. Pure.
/// </summary>
public static class DeliveryColumnProjection
{
    public const string IntegratedColumnId = "integrated";
    public const int DefaultReleaseDepth = 5;
    public const int MaxReleaseDepth = 50;

    /// <param name="lifecycles">
    /// The lifecycle phases of each release by release id. A release whose
    /// phases do not name an environment, such as a hotfix that skips Stage,
    /// never waits there. A release without phases waits everywhere.
    /// </param>
    /// <param name="integratedCardKeys">
    /// Archive-inclusive keys of cards verified on the integration branch.
    /// </param>
    public static DeliveryColumnsResponse Project(string project, IReadOnlyList<DeployEnvironment> environments,
        IReadOnlyList<Release> releases, IReadOnlyList<Deployment> deployments,
        IReadOnlyDictionary<string, string> hostHealth,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectLifecyclePhase>> lifecycles,
        IEnumerable<string> integratedCardKeys, int releaseDepth = DefaultReleaseDepth)
    {
        var depth = Math.Clamp(releaseDepth, 1, MaxReleaseDepth);
        var ordered = environments.Where(item => item.Kind != EnvironmentKinds.Preview)
            .OrderBy(item => item.Order).ToArray();
        var newestFirst = releases.OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
        // A card belongs to the oldest release that names it.
        var origin = new Dictionary<string, Release>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in newestFirst.Reverse())
            foreach (var key in release.CardKeys)
                origin.TryAdd(key, release);

        var statuses = EnvironmentStatusProjection.Project(ordered, releases, deployments, hostHealth);
        var badges = CardDeploymentProjection.Project(project, ordered, releases, deployments, null,
            (from, target) => !EnvironmentStatusProjection.IsNewer(from, target));

        var columns = new List<DeliveryColumn>
        {
            new(IntegratedColumnId, null, "Integrated", null, -1, null, null, null, null,
                integratedCardKeys.Where(key => !origin.ContainsKey(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                    .Select(key => new DeliveryColumnCard(key, null, null, false)).ToArray(),
                [], []),
        };
        foreach (var status in statuses)
        {
            var environment = status.Environment;
            var rolledBack = badges
                .Select(view => (view.CardKey, Badge: view.Environments.First(item => item.EnvironmentId == environment.Id)))
                .Where(item => item.Badge.State == CardDeploymentProjection.RolledBack)
                .Select(item => new DeliveryColumnCard(item.CardKey, item.Badge.ReleaseId, item.Badge.ReleaseVersion, true))
                .ToArray();
            var withdrawn = rolledBack.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = status.CurrentRelease;
            var landed = current is null ? [] : newestFirst
                .Where(release => !EnvironmentStatusProjection.IsNewer(release, current))
                .Take(depth)
                .SelectMany(release => release.CardKeys.Order(StringComparer.OrdinalIgnoreCase)
                    .Select(key => new DeliveryColumnCard(key, release.Id, release.Version, false)))
                .DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var waiting = status.CardsSinceLastDeploy
                .Select(key => (Key: key, Release: origin[key]))
                .Where(item => Passes(lifecycles, item.Release.Id, environment))
                .OrderByDescending(item => item.Release.CreatedAtUtc)
                .ThenByDescending(item => item.Release.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new DeliveryColumnCard(item.Key, item.Release.Id, item.Release.Version,
                    withdrawn.Contains(item.Key)))
                .ToArray();
            var run = status.CurrentDeployment;
            var approval = deployments.Where(item => item.EnvironmentId == environment.Id
                    && item.State == DeploymentStates.AwaitingApproval)
                .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
                .LastOrDefault();
            columns.Add(new(environment.Id, environment.Id, environment.Name, environment.Kind, environment.Order,
                current is null ? null : new(current.Id, current.Version, current.Channel, current.CreatedAtUtc),
                run is null ? null : new(run.Id, run.ReleaseId, run.State, run.Trigger,
                    run.FinishedAtUtc ?? run.StartedAtUtc ?? run.CreatedAtUtc),
                status.Health,
                approval is null ? null : new(approval.Id, approval.ReleaseId,
                    newestFirst.FirstOrDefault(item => item.Id == approval.ReleaseId)?.Version, approval.CreatedAtUtc),
                landed, waiting, rolledBack));
        }
        return new(project, depth, columns);
    }

    private static bool Passes(IReadOnlyDictionary<string, IReadOnlyList<ProjectLifecyclePhase>> lifecycles,
        string releaseId, DeployEnvironment environment)
        => !lifecycles.TryGetValue(releaseId, out var phases) || phases.Count == 0
            || StandardLifecyclePolicy.PhaseFor(phases, environment) is not null;
}
