using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3948: one runner identity whose provider-auth binding has read
/// indeterminate for longer than <see cref="ProviderAuthLatchPolicy.Threshold"/>
/// while the CLI's own status command still confirms an active session.
/// </summary>
public sealed record ProviderAuthLatch(
    string RunnerId,
    string HostId,
    string Role,
    string CapabilityKey,
    DateTime IndeterminateSince,
    string? Detail,
    string OperatorAction);

/// <summary>
/// AGT-3948: names the latched provider-auth state that stood the fleet still
/// on 13 October (AGT-3119) and 17 November 2026. The runner now sends its own
/// recovery request, so a hit here means that recovery did not succeed and a
/// daemon restart is the known operator action.
/// </summary>
public static class ProviderAuthLatchPolicy
{
    public const string Cause = "provider-auth-latched";
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<ProviderAuthLatch> Detect(
        IEnumerable<Contract.RunnerCapabilitySnapshotDto> hosts, DateTime nowUtc, TimeSpan? threshold = null)
    {
        var limit = threshold ?? Threshold;
        return hosts
            .SelectMany(host => host.Capabilities
                .Where(capability => capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal)
                    && capability.FreshUntil > nowUtc
                    && capability.StatusSessionActive == true
                    && (capability.HealthOutcome ?? capability.Signal) == "indeterminate"
                    && capability.Since is { } since
                    && nowUtc - since.ToUniversalTime() >= limit)
                .Select(capability => new ProviderAuthLatch(
                    host.RunnerId,
                    host.HostId,
                    host.Capabilities.Any(item => item.Key == Contract.CapabilityProtocol.ReviewExecutor)
                        ? "review" : "coding",
                    capability.Key,
                    capability.Since!.Value.ToUniversalTime(),
                    capability.Detail,
                    OperatorAction(host.HostId))))
            .OrderBy(latch => latch.IndeterminateSince)
            .ThenBy(latch => latch.HostId, StringComparer.Ordinal)
            .ThenBy(latch => latch.RunnerId, StringComparer.Ordinal)
            .ToArray();
    }

    public static string OperatorAction(string hostId) => $"restart the runner daemon on host {hostId}";

    /// <summary>One operator sentence per latch; the probe detail carries no token values.</summary>
    public static string Describe(ProviderAuthLatch latch)
        => $"{latch.CapabilityKey} on {latch.RunnerId} (host {latch.HostId}) has been indeterminate since "
           + $"{latch.IndeterminateSince:yyyy-MM-dd HH:mm} UTC while the status command confirms an active session; "
           + $"{latch.OperatorAction}.";

    /// <summary>The distinct actions for a set of latches, in host order.</summary>
    public static string? OperatorActions(IEnumerable<ProviderAuthLatch> latches)
    {
        var hosts = latches.Select(latch => latch.HostId).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        return hosts.Length == 0 ? null : string.Join("; ", hosts.Select(OperatorAction));
    }
}
