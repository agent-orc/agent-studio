namespace AgentRunner;

/// <summary>
/// Refreshes the provider-auth probes a claim answer asked for. The coding
/// claim asks after a provider-auth refusal; since AGT-3840 an operator can
/// ask for one key from Execution Hosts, and the review claim carries it too.
/// The caller re-advertises afterwards, which also re-runs every other probe.
/// </summary>
public static class ProviderAuthReprobe
{
    public static async Task RefreshAsync(
        RunnerOptions options,
        IReadOnlyList<string> capabilities,
        Action<string> log,
        CancellationToken ct,
        ProviderAuthProbe? probe = null)
    {
        foreach (var capability in capabilities.Distinct(StringComparer.Ordinal))
        {
            var match = RunnerCapabilityProbe.CodingCliBinaries(options)
                .FirstOrDefault(item => string.Equals(
                    AgentStudio.TaskServer.Contracts.CapabilityProtocol.ProviderAuthentication(item.CliType),
                    capability,
                    StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(match.Binary))
            {
                log($"capability reprobe-request capability={capability} probe=advertisement");
                continue;
            }
            var refreshed = await (probe ?? ProviderAuthProbe.Shared).RefreshAsync(match.Binary, ct);
            log(
                $"provider-auth reprobe-request capability={capability} "
                + $"status={refreshed.Status} detail={refreshed.Detail}");
        }
    }
}
