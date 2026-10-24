using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>
/// Studio's record that an environment runs on a host. It outlives the
/// declaration until the host confirms the removal, so Studio keeps sending
/// the removal signal and keeps the environment's domains reserved meanwhile.
/// <see cref="Kind"/> is the environment kind of its last declaration.
/// <see cref="RemovalDecision"/> says why Studio sends the removal:
/// <c>operator</c> (with <see cref="RemovalActor"/>), <c>tombstone</c>, or
/// <c>grace</c>; while it is null a missing placement sends nothing.
/// </summary>
public sealed record RunTargetPlacement(string EnvironmentId, string HostId, DateTime? MissingSince = null,
    string? Kind = null, string? RemovalDecision = null, string? RemovalActor = null, DateTime? RemovalDecidedAt = null);

/// <summary>An edge-hosted environment a complete read places on the polling host.</summary>
public sealed record RunTargetHostedEnvironment(string Id, string Kind);

/// <summary>
/// A placement the declarations no longer show on its host, as the
/// environments API lists it: <c>pending-confirmation</c> (production or stage
/// waiting for an operator), <c>pending-grace</c> (kind other within
/// <see cref="RunTargetDesiredStatePolicy.RemovalGrace"/>), <c>removing</c>
/// (sent to the host), or <c>removed</c> (the host confirmed it).
/// </summary>
public sealed record RunTargetRemovalRecord(string EnvironmentId, string HostId, string? Kind, string Status,
    DateTime? MissingSince, string? Decision, string? Actor, DateTime? DecidedAt, DateTime? RemovedAt = null);

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
    /// How long an environment of kind other must stay undeclared for its host
    /// before Studio sends the removal, so a checkout caught in the middle of an
    /// update takes nothing down. Production and stage never leave on time alone.
    /// </summary>
    public static readonly TimeSpan RemovalGrace = TimeSpan.FromMinutes(10);

    /// <summary>Removal records kept per project for the environments API.</summary>
    public const int MaxRemovalRecords = 20;

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
        DeployJob? openJob, IReadOnlyList<DeploymentHistoryItem> history, DeployJob? deployedJob = null)
    {
        var snapshot = openJob ?? deployedJob;
        var hosting = snapshot?.Hosting ?? declaration.Hosting
            ?? throw new ArgumentException("Only edge-hosted environments have a run-target spec.", nameof(declaration));
        var artifact = openJob?.Artifact
            ?? history.LastOrDefault(item => item.Status is "healthy" or "rolled-back")?.Artifact;
        return new(project, declaration.Id, declaration.Kind, artifact, snapshot?.ContainerPort ?? declaration.ContainerPort,
            hosting.HealthPath, hosting.Domains, hosting.Edge, hosting.Variables, snapshot?.SecretRefs ?? declaration.SecretRefs,
            hosting.Resources, openJob?.Id, openJob?.LeaseToken, openJob?.IsRollback ?? false);
    }

    /// <summary>
    /// Runs only on a complete read of one project's declarations. Hosted
    /// environments are placed. A placed environment that is no longer hosted
    /// is pending removal; Studio sends the removal once a decision exists: a
    /// declaration tombstone for its id, an operator confirmation
    /// (<see cref="ConfirmRemoval"/>), or, for kind other only,
    /// <see cref="RemovalGrace"/>. Deleting, renaming, or retargeting a
    /// production or stage declaration therefore removes nothing by itself.
    /// Placements on other hosts are left alone.
    /// </summary>
    public static RunTargetPlacementDecision Place(IReadOnlyList<RunTargetPlacement> placements, string hostId,
        IReadOnlyList<RunTargetHostedEnvironment> hosted, IReadOnlySet<string> tombstones, DateTime now)
    {
        var result = new List<RunTargetPlacement>();
        var removals = new List<string>();
        foreach (var placement in placements)
        {
            var declared = hosted.FirstOrDefault(item => item.Id == placement.EnvironmentId);
            if (placement.HostId != hostId) result.Add(placement);
            else if (declared is not null)
                result.Add(new(placement.EnvironmentId, hostId, Kind: declared.Kind));
            else
            {
                var missing = placement with { MissingSince = placement.MissingSince ?? now };
                if (missing.RemovalDecision is null && tombstones.Contains(missing.EnvironmentId))
                    missing = missing with { RemovalDecision = "tombstone", RemovalDecidedAt = now };
                else if (missing.RemovalDecision is null && !RequiresConfirmation(missing.Kind)
                    && now - missing.MissingSince >= RemovalGrace)
                    missing = missing with { RemovalDecision = "grace", RemovalDecidedAt = now };
                result.Add(missing);
                if (AcceptsRemoval(missing)) removals.Add(placement.EnvironmentId);
            }
        }
        foreach (var item in hosted.Where(item => !result.Any(placement => placement.HostId == hostId
                     && placement.EnvironmentId == item.Id)))
            result.Add(new(item.Id, hostId, Kind: item.Kind));
        return new(result, removals);
    }

    /// <summary>Production, stage, and placements recorded before kinds were kept leave a host only on a decision.</summary>
    public static bool RequiresConfirmation(string? kind) => kind is not "other";

    /// <summary>A host may confirm a removal only for a placement Studio is removing.</summary>
    public static bool AcceptsRemoval(RunTargetPlacement placement)
        => placement.MissingSince is not null && placement.RemovalDecision is not null;

    /// <summary>The operator's decision for a pending removal; null when the placement is not pending.</summary>
    public static RunTargetPlacement? ConfirmRemoval(RunTargetPlacement placement, string actor, DateTime now)
        => placement.MissingSince is null || placement.RemovalDecision is not null ? null
            : placement with { RemovalDecision = "operator", RemovalActor = actor, RemovalDecidedAt = now };

    /// <summary>Pending and sent removals first, then the latest confirmed ones.</summary>
    public static IReadOnlyList<RunTargetRemovalRecord> Removals(IReadOnlyList<RunTargetPlacement> placements,
        IReadOnlyList<RunTargetRemovalRecord> removed)
        => placements.Where(item => item.MissingSince is not null)
            .Select(item => Record(item, item.RemovalDecision is not null ? "removing"
                : RequiresConfirmation(item.Kind) ? "pending-confirmation" : "pending-grace"))
            .Concat(removed.AsEnumerable().Reverse())
            .ToArray();

    public static RunTargetRemovalRecord Removed(RunTargetPlacement placement, DateTime now)
        => Record(placement, "removed") with { RemovedAt = now };

    /// <summary>Domains under one of the project's operator-owned suffixes; no suffix allows no domain.</summary>
    public static IReadOnlyList<string> AllowedDomains(IReadOnlyList<string> domains, IReadOnlyList<string>? suffixes)
        => domains.Where(domain => (suffixes ?? []).Any(suffix => domain == suffix
            || domain.EndsWith("." + suffix, StringComparison.Ordinal))).ToArray();

    /// <summary>Lowercases and checks an operator's suffix list; null when an entry is not a domain.</summary>
    public static IReadOnlyList<string>? NormalizeDomainSuffixes(IEnumerable<string>? suffixes)
    {
        var result = new List<string>();
        foreach (var suffix in suffixes ?? [])
        {
            var value = (suffix ?? "").Trim().ToLowerInvariant();
            if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[2..];
            if (!RunTargetHostingDeclaration.Domain().IsMatch(value)) return null;
            if (!result.Contains(value)) result.Add(value);
        }
        return result;
    }

    private static RunTargetRemovalRecord Record(RunTargetPlacement placement, string status)
        => new(placement.EnvironmentId, placement.HostId, placement.Kind, status, placement.MissingSince,
            placement.RemovalDecision, placement.RemovalActor, placement.RemovalDecidedAt);

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
