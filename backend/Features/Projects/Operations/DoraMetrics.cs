using AgentStudio.Deploy;

namespace AgentStudio.Projects;

public sealed record DoraSummary(int SuccessfulProductionDeploys, double? DeploysPerWeek,
    double? MedianCardToProductionHours, int CardsMeasured, double? ChangeFailureRate,
    double? MedianRecoveryHours, int RecoveriesMeasured);

/// <summary>Pure projection of terminal production runs. A failed run starts an outage; the next successful run closes it.</summary>
public static class DoraMetrics
{
    public static DoraSummary Calculate(IReadOnlyList<DeployEnvironment> environments,
        IReadOnlyList<Release> releases, IReadOnlyList<Deployment> deployments,
        IReadOnlyDictionary<string, DateTime> cardCreatedAt, DateTimeOffset since, DateTimeOffset until)
    {
        var production = environments.Where(item => item.Kind == EnvironmentKinds.Production)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var releaseById = releases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var history = deployments.Where(item => production.Contains(item.EnvironmentId) && item.FinishedAtUtc is not null)
            .OrderBy(item => item.FinishedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var runs = history.Where(item => item.FinishedAtUtc >= since.UtcDateTime
            && item.FinishedAtUtc <= until.UtcDateTime).ToArray();
        var changes = runs.Where(item => item.Trigger is DeploymentTriggers.Deploy or DeploymentTriggers.Redeploy).ToArray();
        var successes = changes.Where(item => item.State == DeploymentStates.Succeeded).ToArray();
        var failedChanges = changes.Where(item => item.State == DeploymentStates.Failed)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var environment in production)
        {
            Deployment? latestChange = null;
            foreach (var run in runs.Where(item => item.EnvironmentId == environment))
            {
                if (run.Trigger is DeploymentTriggers.Deploy or DeploymentTriggers.Redeploy
                    && run.State == DeploymentStates.Succeeded)
                    latestChange = run;
                if (run.Trigger is DeploymentTriggers.Rollback or DeploymentTriggers.AutomaticRollback
                    && run.State == DeploymentStates.Succeeded && latestChange is not null
                    && latestChange.ReleaseId != run.ReleaseId)
                    failedChanges.Add(latestChange.Id);
            }
        }
        var leads = new List<double>();
        var arrivedCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in history.Where(item => item.Trigger is DeploymentTriggers.Deploy or DeploymentTriggers.Redeploy
                     && item.State == DeploymentStates.Succeeded))
        {
            if (!releaseById.TryGetValue(run.ReleaseId, out var release)) continue;
            foreach (var key in release.CardKeys)
                if (arrivedCards.Add(key) && run.FinishedAtUtc >= since.UtcDateTime
                    && run.FinishedAtUtc <= until.UtcDateTime && cardCreatedAt.TryGetValue(key, out var created)
                    && created.Year >= 2000
                    && run.FinishedAtUtc!.Value >= created)
                {
                    leads.Add((run.FinishedAtUtc.Value - created).TotalHours);
                }
        }
        var recoveries = new List<double>();
        foreach (var environment in production)
        {
            DateTime? failureAt = null;
            foreach (var run in runs.Where(item => item.EnvironmentId == environment))
            {
                if (run.State == DeploymentStates.Failed)
                    failureAt ??= run.FinishedAtUtc;
                else if (failureAt is not null && run.State == DeploymentStates.Succeeded)
                {
                    recoveries.Add((run.FinishedAtUtc!.Value - failureAt.Value).TotalHours);
                    failureAt = null;
                }
            }
        }
        var weeks = (until - since).TotalDays / 7;
        var terminalChanges = changes.Count(item => item.State is DeploymentStates.Succeeded or DeploymentStates.Failed);
        return new(successes.Length, weeks > 0 ? successes.Length / weeks : null,
            Median(leads), leads.Count, terminalChanges > 0 ? (double)failedChanges.Count / terminalChanges : null,
            Median(recoveries), recoveries.Count);
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
