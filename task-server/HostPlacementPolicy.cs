using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal sealed record HostPlacementDecision(
    bool Eligible,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Rejected,
    bool WarmPreferenceActive)
{
    public string Status => Eligible && !WarmPreferenceActive ? "eligible" :
        WarmPreferenceActive ? "warm-preference" : "rejected";
}

/// <summary>Pure claim eligibility. A mirror never becomes a hard capability.</summary>
internal static class HostPlacementPolicy
{
    public static HostPlacementDecision Decide(
        HostPlacementAdvertisement? host,
        string role,
        string repositoryId,
        IReadOnlyList<string> requirements,
        DateTime queuedAt,
        DateTime now,
        TimeSpan warmWindow,
        bool anotherEligibleWarmHost,
        bool allocationAcknowledged,
        bool grantAvailable)
    {
        var matched = new List<string>();
        var rejected = new List<string>();
        if (host is null)
            rejected.Add("host-advertisement-missing");
        else
        {
            Match("role:" + role, host.Roles.Contains(role, StringComparer.Ordinal));
            Match("repository:" + repositoryId,
                host.RepositoryIds.Contains(repositoryId, StringComparer.Ordinal));
            foreach (var requirement in requirements.Distinct(StringComparer.Ordinal))
            {
                var parts = requirement.Split(':', 2);
                if (parts.Length != 2) continue;
                var ok = parts[0] switch
                {
                    "os" => host.OperatingSystem == parts[1],
                    "arch" => host.Architecture == parts[1],
                    "executor" => host.Executors.Contains(parts[1], StringComparer.Ordinal),
                    "toolchain" => host.ToolchainDigests.Contains(parts[1], StringComparer.Ordinal),
                    "provider-auth" => host.ProviderBindings.Contains(parts[1], StringComparer.Ordinal),
                    "repository" => host.RepositoryIds.Contains(parts[1], StringComparer.Ordinal),
                    "role" => host.Roles.Contains(parts[1], StringComparer.Ordinal),
                    _ => true,
                };
                if (parts[0] is "os" or "arch" or "executor" or "toolchain" or "provider-auth" or "repository" or "role")
                    Match(requirement, ok);
            }
        }
        Match("allocation-version", allocationAcknowledged);
        Match("host-grant", grantAvailable);
        var warm = rejected.Count == 0
                   && host is not null
                   && !host.WarmRepositoryIds.Contains(repositoryId, StringComparer.Ordinal)
                   && anotherEligibleWarmHost
                   && now - queuedAt < warmWindow;
        return new HostPlacementDecision(rejected.Count == 0, matched, rejected, warm);

        void Match(string key, bool ok)
        {
            if (ok) matched.Add(key);
            else rejected.Add(key + "-mismatch");
        }
    }
}
