using AgentStudio.Runner;

namespace AgentStudio.Operator;

/// <summary>Raises immediate attention when a differing review role does real work.</summary>
public sealed class OperatorReviewHostDriftFactSource(
    HostReleaseDriftWatchdog watchdog, V1ReviewExecutorRegistry registry,
    AttemptAuthorityService authority) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.ReviewHostDrift];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        var snapshot = watchdog.Current;
        var live = registry.ListCapabilitySnapshots().ToDictionary(host => host.RunnerId, StringComparer.OrdinalIgnoreCase);
        var hosts = snapshot.Hosts.Select(host =>
        {
            if (!live.TryGetValue(host.RunnerId, out var current)) return host with { HeartbeatStale = true };
            var verdict = HostReleaseDriftPolicy.Evaluate(current.Release, snapshot.Stable, nowUtc);
            return host with { Release = current.Release, State = verdict.State,
                LastSeenAt = current.LastSeenAt,
                HeartbeatStale = nowUtc - current.LastSeenAt.ToUniversalTime() > TimeSpan.FromMinutes(15) };
        }).ToArray();
        var work = authority.ListReviewHostWork().Where(item =>
            live.TryGetValue(item.RunnerId, out var role)
            && string.Equals(role.HostId, item.HostId, StringComparison.OrdinalIgnoreCase)
            && (item.ClaimedAt >= role.RegisteredAt || item.GradedAt >= role.RegisteredAt)).ToArray();
        return OperatorReviewHostDriftFacts.Project(snapshot with { Hosts = hosts }, work, nowUtc);
    }
}

public static class OperatorReviewHostDriftFacts
{
    public static IReadOnlyList<OperatorDecisionFact> Project(HostReleaseDriftSnapshot snapshot,
        IReadOnlyList<ReviewHostWork> work, DateTime nowUtc)
    {
        var facts = new List<OperatorDecisionFact>();
        foreach (var host in snapshot.Hosts.Where(host => host.Role == "review" && !host.HeartbeatStale))
        {
            var release = host.Release;
            var commitDiffers = host.State is HostReleaseDriftStates.Behind or HostReleaseDriftStates.Differs
                || (release?.Commit is { Length: > 0 } commit
                    && snapshot.Stable.Commit is { Length: > 0 } expectedCommit
                    && !HostReleaseDriftPolicy.CommitsMatch(commit, expectedCommit));
            var imageDiffers = host.ExpectedImageRef is { Length: > 0 } expected
                && !string.Equals(release?.ImageRef, expected, StringComparison.OrdinalIgnoreCase);
            if (!commitDiffers && !imageDiffers) continue;

            var claims = work.Where(item =>
                    string.Equals(item.RunnerId, host.RunnerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.HostId, host.HostId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (claims.Length == 0) continue;
            var cards = claims.Select(item => item.CardKey).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
            var graded = claims.Where(item => item.Graded).Select(item => item.CardKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
            var hostCommit = release?.Commit ?? "unknown";
            var stableCommit = snapshot.Stable.Commit ?? "unknown";
            var hostImage = release?.ImageRef ?? "unknown";
            var stableImage = host.ExpectedImageRef ?? "unknown";
            var reason = $"Review host {host.Name} ({host.HostId}) worked on {string.Join(", ", cards)} while running "
                + $"commit {hostCommit}, image {hostImage}; Stable expects commit {stableCommit}, image {stableImage}. "
                + $"Graded cards: {(graded.Length == 0 ? "none yet" : string.Join(", ", graded))}.";
            facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.ReviewHostDrift,
                $"{OperatorDecisionTypes.ReviewHostDrift}|{host.RunnerId}|{release?.ReleaseId}|{hostImage}",
                null, null, reason, claims.Min(item => item.ClaimedAt),
                [new OperatorEvidenceLink("Review host", $"/workspace/settings/environment?host={Uri.EscapeDataString(host.HostId)}")],
                cards, $"Review host {host.Name} differs from Stable"));
        }
        return facts;
    }
}
