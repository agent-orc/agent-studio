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
    public const string FailedRolledBack = "failed-rolled-back";

    public static IReadOnlyList<CardDeploymentView> Project(string project,
        IReadOnlyList<DeployEnvironment> environments, IReadOnlyList<Release> releases,
        IReadOnlyList<Deployment> deployments, IEnumerable<string>? integratedCardKeys = null,
        Func<Release, Release, bool>? releaseContains = null)
    {
        var ordered = releases.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var byId = ordered.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var membership = ordered.ToDictionary(target => target.Id,
            target => ordered.Where(origin => origin.Id == target.Id || releaseContains?.Invoke(origin, target) == true)
                .SelectMany(origin => origin.CardKeys).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
        var keys = ordered.SelectMany(item => item.CardKeys).Concat(integratedCardKeys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var histories = environments.OrderBy(item => item.Order).Select(environment =>
        {
            var runs = deployments.Where(item => item.EnvironmentId == environment.Id)
                .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            return (Environment: environment, Runs: runs,
                Succeeded: runs.Where(item => item.State == DeploymentStates.Succeeded).ToArray());
        }).ToArray();
        return keys.Select(key => new CardDeploymentView(project, key, histories.Select(history =>
        {
            var (environment, runs, succeeded) = history;
            var last = runs.LastOrDefault();
            var success = succeeded.LastOrDefault();
            bool HasCard(Deployment run) => membership.TryGetValue(run.ReleaseId, out var cards) && cards.Contains(key);
            var failureIndex = Array.FindLastIndex(runs, run => run.State == DeploymentStates.Failed && HasCard(run));
            var failure = failureIndex < 0 ? null : runs[failureIndex];
            var successIndex = Array.FindLastIndex(runs, run => run.State == DeploymentStates.Succeeded && HasCard(run));
            var unresolvedFailure = failure is not null && failureIndex > successIndex;
            var firstSuccessAfterFailure = unresolvedFailure ? runs.Skip(failureIndex + 1)
                .FirstOrDefault(run => run.State == DeploymentStates.Succeeded) : null;
            var failureRolledBack = firstSuccessAfterFailure?.Trigger is DeploymentTriggers.Rollback
                or DeploymentTriggers.AutomaticRollback;
            var active = !unresolvedFailure && success is not null && HasCard(success);
            var attempt = last is not null && HasCard(last);
            var withdrawn = !active && succeeded.Select((run, index) => (run, index))
                .Any(item => item.index > 0 && !HasCard(item.run)
                    && HasCard(succeeded[item.index - 1])
                    && item.run.Trigger is DeploymentTriggers.Rollback or DeploymentTriggers.AutomaticRollback);
            var includedRun = withdrawn ? succeeded.LastOrDefault(HasCard) : null;
            var selected = unresolvedFailure ? failure : active ? success : withdrawn ? includedRun : attempt ? last : null;
            var release = selected is not null && byId.TryGetValue(selected.ReleaseId, out var found) ? found : null;
            var state = unresolvedFailure ? failureRolledBack ? FailedRolledBack : DeploymentStates.Failed
                : active ? DeploymentStates.Succeeded : withdrawn ? RolledBack
                : attempt ? last!.State : NotDeployed;
            return new CardEnvironmentBadge(environment.Id, environment.Name, environment.Kind, state,
                release?.Id, release?.Version, withdrawn && !unresolvedFailure ? success?.FinishedAtUtc
                    : selected?.FinishedAtUtc ?? selected?.StartedAtUtc);
        }).ToArray())).ToArray();
    }
}
