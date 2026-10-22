namespace AgentStudio.Deploy;

public sealed record DeliveryCard(string Key, string Title, string? ReleaseVersion, DateTime? DeployedAtUtc);
public sealed record DeliveryLane(string? EnvironmentId, string Name, int Order, IReadOnlyList<DeliveryCard> Cards);
public sealed record DeliveryLaneResponse(string ProjectId, IReadOnlyList<DeliveryLane> Lanes);

/// <summary>Projects immutable release card ranges onto the releases currently running in each environment.</summary>
public static class DeliveryLaneProjection
{
    public static DeliveryLaneResponse Project(string project, IReadOnlyList<DeployEnvironment> environments,
        IReadOnlyList<Release> releases, IReadOnlyList<Deployment> deployments,
        IReadOnlyDictionary<string, string> integratedCards)
    {
        var orderedReleases = releases.OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var cumulative = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in orderedReleases)
        {
            included.UnionWith(release.CardKeys);
            cumulative[release.Id] = new(included, StringComparer.OrdinalIgnoreCase);
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lanes = new List<DeliveryLane>();
        foreach (var environment in environments.OrderByDescending(item => item.Order))
        {
            var successful = EnvironmentStatusProjection.LastSucceeded(
                deployments.Where(item => item.EnvironmentId == environment.Id));
            var release = successful is null ? null : orderedReleases.FirstOrDefault(item => item.Id == successful.ReleaseId);
            var keys = release is null ? Array.Empty<string>() : cumulative[release.Id]
                .Where(assigned.Add).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            lanes.Add(new(environment.Id, environment.Name, environment.Order,
                keys.Select(key => new DeliveryCard(key, integratedCards.GetValueOrDefault(key) ?? key,
                    release!.Version, successful!.FinishedAtUtc)).ToArray()));
        }
        var waiting = integratedCards.Keys.Concat(included)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(key => !assigned.Contains(key))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(key => new DeliveryCard(key, integratedCards.GetValueOrDefault(key) ?? key, null, null)).ToArray();
        lanes.Add(new(null, "Waiting for release", -1, waiting));
        return new(project, lanes.OrderBy(item => item.Order).ToArray());
    }
}
