namespace AgentStudio.Deploy;

/// <summary>
/// Title and prompt of the backlog card for one failed Production run. Pure.
/// A release lists only the cards integrated since the previous release, so
/// a deploy card collects every release since the last one that was healthy
/// in Production: a release that never reached Production still carries
/// cards that went live in this jump (F15). A failed rollback deployed no new
/// change, so its card says it is a rollback and points at the failure it
/// tried to undo.
/// </summary>
public static class ProductionFailureCard
{
    public sealed record Content(string Title, string Prompt);

    public static Content Build(string project, DeploymentRecord run, IReadOnlyList<DeploymentRecord> runs,
        IReadOnlyCollection<Release> releases, string logExcerpt)
    {
        var byId = releases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var release = byId[run.ReleaseId];
        var earlier = runs.Where(item => item.Id != run.Id && item.EnvironmentId == run.EnvironmentId
                && item.Tenant == run.Tenant && item.CreatedAtUtc <= run.CreatedAtUtc)
            .ToArray();
        var tenant = run.Tenant is null ? "" : $"\nTenant: {run.Tenant}";
        var log = $"\n\nRecent deployment log:\n{logExcerpt}\n";
        return IsRollback(run)
            ? Rollback(run, earlier, byId, release, tenant, log)
            : Deploy(project, run, earlier, releases, byId, release, tenant, log);
    }

    public static bool IsRollback(DeploymentRecord run)
        => run.Trigger is DeploymentTriggers.Rollback or DeploymentTriggers.AutomaticRollback;

    private static Content Deploy(string project, DeploymentRecord run, IReadOnlyList<DeploymentRecord> earlier,
        IReadOnlyCollection<Release> releases, IReadOnlyDictionary<string, Release> byId, Release release,
        string tenant, string log)
    {
        var healthy = EnvironmentStatusProjection.CurrentRelease(
            earlier.Select(item => DeployService.ToContract(project, item)), byId);
        var cards = releases
            .Where(item => (healthy is null || EnvironmentStatusProjection.IsNewer(item, healthy))
                && !EnvironmentStatusProjection.IsNewer(item, release))
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
            .SelectMany(item => item.CardKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var list = cards.Length == 0 ? "none" : string.Join(", ", cards);
        var since = healthy is null
            ? $"Last healthy release in Production: none\nCards in every release up to {release.Version}: {list}"
            : $"Last healthy release in Production: {healthy.Version} (`{healthy.Id}`)"
              + $"\nCards added since {healthy.Version}: {list}";
        return new($"Investigate Production deployment of release {release.Version}",
            $"# Production deployment failed\n\nRelease: {release.Version} (`{release.Id}`)\nDeployment: `{run.Id}`"
            + tenant + "\n" + since + log);
    }

    private static Content Rollback(DeploymentRecord run, IReadOnlyList<DeploymentRecord> earlier,
        IReadOnlyDictionary<string, Release> byId, Release release, string tenant, string log)
    {
        var from = earlier.Where(item => !IsRollback(item) && item.StartedAtUtc is not null)
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
            .LastOrDefault();
        var requested = run.Trigger == DeploymentTriggers.AutomaticRollback
            ? "automatically after a failed deployment" : $"by {run.RequestedBy ?? "unknown"}";
        var origin = from is null ? ""
            : $"\nRolled back from: release {byId.GetValueOrDefault(from.ReleaseId)?.Version ?? from.ReleaseId}"
              + $", deployment `{from.Id}` ({from.State})"
              + (Failed(from) ? $"\nFailure card: `{CardId(from.Id)}`" : "");
        return new($"Rollback to release {release.Version} failed in Production",
            $"# Production rollback failed\n\nRollback target: release {release.Version} (`{release.Id}`)"
            + $"\nDeployment: `{run.Id}`\nRequested: {requested}" + tenant + origin + log);
    }

    /// <summary>A run that gets a failure card when it ran in Production.</summary>
    public static bool Failed(DeploymentRecord run)
        => run.State == DeploymentStates.Failed
           || (run.State == DeploymentStates.Cancelled && run.InterventionDecision == "abort"
               && run.FailedStep is not null);

    public static string CardId(string deploymentId) => "production-deploy-failed-" + deploymentId;
}
