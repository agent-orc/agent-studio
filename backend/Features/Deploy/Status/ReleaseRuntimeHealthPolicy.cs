namespace AgentStudio.Deploy;

/// <summary>An incident a release had while it ran in an environment.</summary>
/// <param name="Source"><c>health-check</c> (a health-check incident) or <c>crash-loop</c> (the host saw the release crash-loop).</param>
public sealed record ReleaseRuntimeIncident(string Source, DateTime StartedAtUtc, string Detail);

/// <summary>
/// The durable runtime health of one release in one environment: <c>healthy</c>
/// once a passing health probe saw it run, <c>incident</c> once it had an
/// incident while running. An incident is never cleared again.
/// </summary>
public sealed record ReleaseRuntimeHealthRecord(string ReleaseId, string EnvironmentId, string Health,
    DateTime ObservedAtUtc, ReleaseRuntimeIncident? Incident = null);

/// <summary>What the health checks and the deploy host report for one environment now.</summary>
/// <param name="HealthyProbeAtUtc">The latest passing probe while no incident is open, if any.</param>
public sealed record RuntimeHealthObservation(string EnvironmentId, IReadOnlyList<RuntimeIncidentObservation> Incidents,
    DateTime? HealthyProbeAtUtc);

/// <param name="Artifact">The artifact the host named, for a crash loop; null when the incident belongs to the release running at its start.</param>
public sealed record RuntimeIncidentObservation(string Source, DateTime StartedAtUtc, string Detail, string? Artifact = null);

/// <summary>
/// F13: the deploy-time health step only says that a release started. This
/// policy records what happened while it ran, and picks the rollback target
/// from it: the newest older release without an incident, or, when every
/// older release had one, the release before the current one marked as such,
/// so a rollback never lands silently on a release that already failed. Pure.
/// </summary>
public static class ReleaseRuntimeHealthPolicy
{
    public const string Healthy = "healthy";
    public const string Incident = "incident";
    public const string Unknown = "unknown";
    public const string HealthCheckSource = "health-check";
    public const string CrashLoopSource = "crash-loop";

    /// <summary>Adds what the observations show to the ledger; existing incidents stay untouched.</summary>
    public static (IReadOnlyList<ReleaseRuntimeHealthRecord> Records, bool Changed) Observe(
        IReadOnlyList<ReleaseRuntimeHealthRecord> existing, IReadOnlyList<Deployment> deployments,
        IReadOnlyDictionary<string, Release> releases, IReadOnlyList<RuntimeHealthObservation> observations)
    {
        var records = existing.ToList();
        var changed = false;
        foreach (var observation in observations)
        {
            var runs = deployments.Where(item => item.EnvironmentId == observation.EnvironmentId && item.Tenant is null)
                .ToArray();
            foreach (var incident in observation.Incidents.OrderBy(item => item.StartedAtUtc))
            {
                var release = incident.Artifact is { } artifact
                    ? LatestWithArtifact(runs, releases, artifact)
                    : RunningAt(runs, releases, incident.StartedAtUtc);
                if (release is null) continue;
                var index = records.FindIndex(item => item.ReleaseId == release.Id
                    && item.EnvironmentId == observation.EnvironmentId);
                if (index >= 0 && records[index].Health == Incident) continue;
                var record = new ReleaseRuntimeHealthRecord(release.Id, observation.EnvironmentId, Incident,
                    incident.StartedAtUtc, new(incident.Source, incident.StartedAtUtc, incident.Detail));
                if (index >= 0) records[index] = record;
                else records.Add(record);
                changed = true;
            }
            if (observation.HealthyProbeAtUtc is not { } probe) continue;
            var current = EnvironmentStatusProjection.CurrentRelease(runs, releases);
            if (current is null || RunningAt(runs, releases, probe)?.Id != current.Id
                || records.Any(item => item.ReleaseId == current.Id && item.EnvironmentId == observation.EnvironmentId))
                continue;
            records.Add(new(current.Id, observation.EnvironmentId, Healthy, probe));
            changed = true;
        }
        return (records, changed);
    }

    /// <summary>
    /// The rollback target of one environment, from its runs. The candidates
    /// are the releases older than the current one, latest successful run
    /// first, as in <see cref="EnvironmentStatusProjection.PreviousRelease"/>.
    /// </summary>
    public static RollbackTarget? Target(IEnumerable<Deployment> environmentRuns,
        IReadOnlyDictionary<string, Release> releases, IReadOnlyList<ReleaseRuntimeHealthRecord> health)
    {
        var runs = environmentRuns.ToArray();
        var current = EnvironmentStatusProjection.CurrentRelease(runs, releases);
        if (current is null) return null;
        var candidates = runs.Where(EnvironmentStatusProjection.DeployedSuccessfully)
            .OrderByDescending(item => item.FinishedAtUtc).ThenByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Select(item => (Run: item, Release: releases.GetValueOrDefault(item.ReleaseId)))
            .Where(item => item.Release is not null && EnvironmentStatusProjection.IsNewer(current, item.Release))
            .DistinctBy(item => item.Release!.Id)
            .ToArray();
        if (candidates.Length == 0) return null;
        var skipped = new List<string>();
        foreach (var (run, release) in candidates)
        {
            var record = Find(health, release!.Id, run.EnvironmentId);
            if (record?.Health == Incident)
            {
                skipped.Add(release.Version);
                continue;
            }
            return new(release, run.FinishedAtUtc, record?.Health ?? Unknown, null, skipped);
        }
        var (firstRun, first) = candidates[0];
        return new(first!, firstRun.FinishedAtUtc, Incident, Find(health, first!.Id, firstRun.EnvironmentId)?.Incident, []);
    }

    /// <summary>The release of the latest successful run that had finished at <paramref name="atUtc"/>.</summary>
    public static Release? RunningAt(IEnumerable<Deployment> environmentRuns, IReadOnlyDictionary<string, Release> releases,
        DateTime atUtc)
        => EnvironmentStatusProjection.CurrentRelease(environmentRuns.Where(item => item.FinishedAtUtc <= atUtc), releases);

    private static Release? LatestWithArtifact(IEnumerable<Deployment> environmentRuns,
        IReadOnlyDictionary<string, Release> releases, string artifact)
        => environmentRuns.Where(EnvironmentStatusProjection.DeployedSuccessfully)
            .OrderBy(item => item.FinishedAtUtc).ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => releases.GetValueOrDefault(item.ReleaseId))
            .LastOrDefault(release => release?.ArtifactDigests.Contains(artifact, StringComparer.Ordinal) == true);

    private static ReleaseRuntimeHealthRecord? Find(IReadOnlyList<ReleaseRuntimeHealthRecord> health, string releaseId,
        string environmentId)
        => health.FirstOrDefault(item => item.ReleaseId == releaseId && item.EnvironmentId == environmentId);
}
