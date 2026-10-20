using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>
/// Studio's record that an environment runs on a host. It outlives the
/// declaration until the host confirms the removal, so Studio keeps sending
/// the removal signal and keeps the environment's domains reserved meanwhile.
/// </summary>
public sealed record RunTargetPlacement(string EnvironmentId, string HostId, DateTime? MissingSince = null);

public sealed record RunTargetPlacementDecision(IReadOnlyList<RunTargetPlacement> Placements, IReadOnlyList<string> Removals);

/// <summary>First claim of a domain on one host.</summary>
public sealed record RunTargetDomainClaim(string Project, string EnvironmentId, DateTime ClaimedAt);

public sealed record RunTargetDomainCandidate(string Project, string EnvironmentId, IReadOnlyList<string> Domains);

public enum RunTargetLeaseAction { Claim, Renew, Expire }

/// <summary>
/// Pure decisions for the run-target protocol: which artifact an environment
/// should run, when an environment leaves a host, which environment owns a
/// domain, how long a deploy job lease lasts, and which parts of a host
/// observation Studio keeps.
/// </summary>
public static partial class RunTargetDesiredStatePolicy
{
    public const int MaxEventsPerEnvironment = 20;

    /// <summary>
    /// How long an environment must stay undeclared for its host before Studio
    /// sends the removal, so a checkout caught in the middle of an update takes
    /// nothing down.
    /// </summary>
    public static readonly TimeSpan RemovalGrace = TimeSpan.FromMinutes(10);

    /// <summary>Longest time a run target may hold one deploy job, counted from its first claim.</summary>
    public static readonly TimeSpan MaxJobLease = TimeSpan.FromMinutes(30);

    /// <summary>A lease the host has not renewed for this long is handed out again with a new token.</summary>
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    /// <summary>An unchanged observation is stored again only to refresh its time, at most this often.</summary>
    public static readonly TimeSpan ObservedRefresh = TimeSpan.FromMinutes(1);

    [GeneratedRegex(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z")]
    private static partial Regex Digest();

    /// <summary>
    /// An open deploy job wins: its artifact is what the host must converge to
    /// and report on. Without one, the environment keeps the last artifact that
    /// was reported healthy or rolled back. A failed first deploy leaves nothing
    /// to run.
    /// </summary>
    public static RunTargetEnvironmentSpec Project(string project, EnvironmentDeclaration declaration,
        DeployJob? openJob, IReadOnlyList<DeploymentHistoryItem> history)
    {
        var hosting = declaration.Hosting
            ?? throw new ArgumentException("Only edge-hosted environments have a run-target spec.", nameof(declaration));
        var artifact = openJob?.Artifact
            ?? history.LastOrDefault(item => item.Status is "healthy" or "rolled-back")?.Artifact;
        return new(project, declaration.Id, declaration.Kind, artifact, declaration.ContainerPort,
            hosting.HealthPath, hosting.Domains, hosting.Edge, hosting.Variables, declaration.SecretRefs,
            hosting.Resources, openJob?.Id, openJob?.LeaseToken, openJob?.IsRollback ?? false);
    }

    /// <summary>
    /// Runs only on a complete read of one project's declarations. Hosted
    /// environments are placed; a placed environment that is no longer hosted
    /// gets a removal once it has been missing for <see cref="RemovalGrace"/>.
    /// Placements on other hosts are left alone.
    /// </summary>
    public static RunTargetPlacementDecision Place(IReadOnlyList<RunTargetPlacement> placements, string hostId,
        IReadOnlyCollection<string> hostedIds, DateTime now)
    {
        var result = new List<RunTargetPlacement>();
        var removals = new List<string>();
        foreach (var placement in placements)
        {
            if (placement.HostId != hostId) result.Add(placement);
            else if (hostedIds.Contains(placement.EnvironmentId)) result.Add(placement with { MissingSince = null });
            else
            {
                var missing = placement with { MissingSince = placement.MissingSince ?? now };
                result.Add(missing);
                if (AcceptsRemoval(missing, now)) removals.Add(placement.EnvironmentId);
            }
        }
        foreach (var id in hostedIds.Where(id => !result.Any(item => item.HostId == hostId && item.EnvironmentId == id)))
            result.Add(new(id, hostId));
        return new(result, removals);
    }

    /// <summary>A host may confirm a removal only for a placement Studio is removing.</summary>
    public static bool AcceptsRemoval(RunTargetPlacement placement, DateTime now)
        => placement.MissingSince is { } since && now - since >= RemovalGrace;

    /// <summary>
    /// The first environment that claims a domain on a host keeps it while it
    /// still declares it. An owner Studio cannot see in this poll keeps it when
    /// <paramref name="unseenOwnerKeeps"/> says the host may still route it
    /// (skipped project, pending removal). New claims are taken in project and
    /// environment order.
    /// </summary>
    public static Dictionary<string, RunTargetDomainClaim> ClaimDomains(
        IReadOnlyDictionary<string, RunTargetDomainClaim> claims, IReadOnlyList<RunTargetDomainCandidate> hosted,
        Func<RunTargetDomainClaim, bool> unseenOwnerKeeps, DateTime now)
    {
        var result = new Dictionary<string, RunTargetDomainClaim>(StringComparer.Ordinal);
        foreach (var (domain, claim) in claims)
        {
            var owner = hosted.FirstOrDefault(item => IsOwner(claim, item.Project, item.EnvironmentId));
            if (owner is not null ? owner.Domains.Contains(domain, StringComparer.Ordinal) : unseenOwnerKeeps(claim))
                result[domain] = claim;
        }
        foreach (var candidate in hosted.OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal))
        {
            foreach (var domain in candidate.Domains)
                result.TryAdd(domain, new(candidate.Project, candidate.EnvironmentId, now));
        }
        return result;
    }

    /// <summary>Keeps the domains the environment owns and lists the ones another environment claimed first.</summary>
    public static RunTargetEnvironmentSpec ApplyClaims(RunTargetEnvironmentSpec spec,
        IReadOnlyDictionary<string, RunTargetDomainClaim> claims)
    {
        bool Owned(string domain) => claims.TryGetValue(domain, out var claim)
            && IsOwner(claim, spec.Project, spec.EnvironmentId);
        return spec with
        {
            Domains = spec.Domains.Where(Owned).ToArray(),
            RefusedDomains = spec.Domains.Where(domain => !Owned(domain)).ToArray(),
        };
    }

    /// <summary>
    /// Each poll claims a queued job, renews a held lease, or hands a lapsed
    /// one out again. A job held for <see cref="MaxJobLease"/> without a report
    /// expires, so a host that cannot converge (edge down) cannot keep it forever.
    /// </summary>
    public static RunTargetLeaseAction Lease(DeployJob job, DateTime now)
    {
        if (job.Status != "claimed") return RunTargetLeaseAction.Claim;
        if (job.FirstClaimedAt is { } first && now - first >= MaxJobLease)
            return RunTargetLeaseAction.Expire;
        return job.LeaseToken is not null && job.ClaimedAt >= now - LeaseTimeout
            ? RunTargetLeaseAction.Renew : RunTargetLeaseAction.Claim;
    }

    /// <summary>A new observation replaces the stored one when it differs, or once <see cref="ObservedRefresh"/> has passed.</summary>
    public static bool Supersedes(RunTargetObservation? stored, RunTargetObservation next)
        => stored is null || next.ObservedAt - stored.ObservedAt >= ObservedRefresh
            || JsonSerializer.Serialize(stored with { ObservedAt = default })
                != JsonSerializer.Serialize(next with { ObservedAt = default });

    /// <summary>
    /// The host rolled back from the deployed artifact after a crash loop and
    /// Studio has not seen that event before. The host keeps its last events,
    /// so an event already stored must not roll back a later redeploy of the
    /// same artifact.
    /// </summary>
    public static bool NewCrashRollback(RunTargetObservation? stored, RunTargetObservation next, string? deployed)
        => deployed is not null && next.Events.Any(item => item.Kind == "crash-rollback" && item.Artifact == deployed
            && !(stored?.Events ?? []).Contains(item));

    private static bool IsOwner(RunTargetDomainClaim claim, string project, string environmentId)
        => string.Equals(claim.Project, project, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.EnvironmentId, environmentId, StringComparison.Ordinal);

    /// <summary>
    /// The host's report is untrusted text. Keep only the closed vocabulary,
    /// digests, and a bounded event list, so no host output reaches Studio.
    /// </summary>
    public static RunTargetObservation? Sanitize(RunTargetObservation observation, DateTime receivedAt)
    {
        if (!RunTargetVocabulary.States.Contains(observation.State)) return null;
        string? Artifact(string? value) => value is not null && Digest().IsMatch(value) ? value : null;
        var events = (observation.Events ?? [])
            .Where(item => RunTargetVocabulary.EventKinds.Contains(item.Kind))
            .TakeLast(MaxEventsPerEnvironment)
            .Select(item => new RunTargetEvent(item.At > receivedAt ? receivedAt : item.At, item.Kind, Artifact(item.Artifact)))
            .ToArray();
        return new(observation.Project, observation.EnvironmentId, observation.State,
            Artifact(observation.RunningArtifact), Artifact(observation.PreviousArtifact),
            Math.Clamp(observation.Restarts, 0, 1_000_000),
            observation.ObservedAt > receivedAt ? receivedAt : observation.ObservedAt, events);
    }
}
