namespace AgentStudio.Deploy;

public sealed record CardEnvironmentBadge(string EnvironmentId, string EnvironmentName, string Kind,
    string State, string? ReleaseId, string? ReleaseVersion, DateTime? AtUtc);

public sealed record CardDeploymentView(string ProjectId, string CardKey,
    IReadOnlyList<CardEnvironmentBadge> Environments);

/// <summary>Projects immutable release card deltas onto the current environment history.</summary>
public static class CardDeploymentProjection
{
    public const string NotDeployed = "not-deployed";
    public const string RolledBack = "rolled-back";

    public static IReadOnlyList<CardDeploymentView> Project(string project,
        IReadOnlyList<DeployEnvironment> environments, IReadOnlyList<Release> releases,
        IReadOnlyList<Deployment> deployments, IEnumerable<string>? cardKeys = null)
    {
        var ordered = releases.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var index = ordered.Select((release, position) => (release.Id, position))
            .ToDictionary(item => item.Id, item => item.position, StringComparer.Ordinal);
        var keys = ordered.SelectMany(item => item.CardKeys).Concat(cardKeys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return keys.Select(key => new CardDeploymentView(project, key,
            environments.OrderBy(item => item.Order).Select(environment =>
            {
                var origin = Array.FindIndex(ordered, release => release.CardKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
                var runs = deployments.Where(item => item.EnvironmentId == environment.Id).ToArray();
                var last = runs.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).LastOrDefault();
                var success = EnvironmentStatusProjection.LastSucceeded(runs);
                var active = origin >= 0 && success is not null && index.TryGetValue(success.ReleaseId, out var activeIndex)
                    && activeIndex >= origin;
                var attempt = origin >= 0 && last is not null
                    && index.TryGetValue(last.ReleaseId, out var attemptIndex) && attemptIndex >= origin;
                var withdrawn = origin >= 0 && !active && last is { State: DeploymentStates.Succeeded,
                    Trigger: DeploymentTriggers.Rollback or DeploymentTriggers.AutomaticRollback }
                    && runs.Any(item => item.State == DeploymentStates.Succeeded
                        && index.TryGetValue(item.ReleaseId, out var position) && position >= origin);
                var withdrawnRun = withdrawn ? EnvironmentStatusProjection.LastSucceeded(runs.Where(item =>
                    index.TryGetValue(item.ReleaseId, out var position) && position >= origin)) : null;
                var selected = active ? success : attempt ? last : withdrawnRun;
                var release = selected is null ? null : ordered.FirstOrDefault(item => item.Id == selected.ReleaseId);
                var state = withdrawn ? RolledBack : active ? DeploymentStates.Succeeded
                    : attempt ? last!.State : NotDeployed;
                return new CardEnvironmentBadge(environment.Id, environment.Name, environment.Kind, state,
                    release?.Id, release?.Version, withdrawn ? last?.FinishedAtUtc : selected?.FinishedAtUtc ?? selected?.StartedAtUtc);
            }).ToArray())).ToArray();
    }
}
