namespace AgentStudio.Operator;

public sealed record DeployPushFact(string Project, string JobId, DateTimeOffset SinceUtc);
public sealed record DeployFlightFact(string Project, string BatchId, string Phase);
public sealed record DeployCommitFact(string Project, string CardKey, string StepId);
public sealed record DeployPreflightFacts(
    IReadOnlyList<DeployPushFact> PendingPushes,
    IReadOnlyList<DeployFlightFact> Flights,
    IReadOnlyList<DeployCommitFact> ActiveCommits);

public sealed record DeployPreflightBlocker(string Code, string Project, string Subject, string Detail);

/// <summary>Server-side restart and deploy gate over the existing batch and pipeline facts.</summary>
public static class DeployPreflightPolicy
{
    public static IReadOnlyList<DeployPreflightBlocker> Check(DeployPreflightFacts facts, DateTimeOffset now)
    {
        var blockers = new List<DeployPreflightBlocker>();
        blockers.AddRange(facts.PendingPushes.Select(push => new DeployPreflightBlocker(
            "pending-push", push.Project, push.JobId,
            $"Integration push has been pending since {push.SinceUtc:O} ({Math.Max(0, (now - push.SinceUtc).TotalMinutes):F1} minutes).")));
        blockers.AddRange(facts.Flights.Where(flight =>
                string.Equals(flight.Phase, "publishing", StringComparison.OrdinalIgnoreCase))
            .Select(flight => new DeployPreflightBlocker("flight-publishing", flight.Project,
                flight.BatchId, "A batch flight is publishing.")));
        blockers.AddRange(facts.ActiveCommits.Select(commit => new DeployPreflightBlocker(
            "card-mid-commit", commit.Project, commit.CardKey,
            $"Pipeline step {commit.StepId} is running.")));
        return blockers.OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Subject, StringComparer.Ordinal).ToArray();
    }
}

public sealed record DeployHostVersion(string HostId, string Version);
public sealed record DeployPlanStep(int Order, string Kind, string Version, int ProtocolVersion,
    IReadOnlyList<DeployHostVersion>? Hosts = null);
public sealed record DeployPlan(string Id, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    string CurrentVersion, string TargetVersion, int ProtocolVersion,
    IReadOnlyList<DeployPlanStep> Steps);

/// <summary>A bounded update window with the documented server-first order.</summary>
public static class DeployPlanPolicy
{
    public static DeployPlan Create(DateTimeOffset now, TimeSpan window, string currentVersion,
        string targetVersion, int protocolVersion, IReadOnlyList<DeployHostVersion> hosts)
    {
        if (window < TimeSpan.FromMinutes(5) || window > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(window));
        if (string.IsNullOrWhiteSpace(targetVersion)) throw new ArgumentException("Target version is required.", nameof(targetVersion));
        return new DeployPlan("dpl_" + Guid.NewGuid().ToString("N"), now, now.Add(window),
            currentVersion, targetVersion, protocolVersion,
            [
                new(1, "drain", currentVersion, protocolVersion),
                new(2, "task-server", targetVersion, protocolVersion),
                new(3, "readiness", targetVersion, protocolVersion),
                new(4, "engine", targetVersion, protocolVersion),
                new(5, "hosts", targetVersion, protocolVersion, hosts),
            ]);
    }

    public static bool IsExpired(DeployPlan plan, DateTimeOffset now) => now >= plan.ExpiresAtUtc;
}
