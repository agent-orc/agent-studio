using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Projects only registry-backed, fresh and independently identified probe evidence.</summary>
public static class ProviderProbeComparisonPolicy
{
    public static ProviderProbeComparisonResponseDto Find(
        string runnerId, string provider, string? generation, string effectiveSource,
        string failureSignature,
        IReadOnlyList<RunnerCapabilitySnapshotDto> runners,
        IReadOnlyList<CredentialRegistryRecordDto> credentials,
        DateTimeOffset now)
    {
        var empty = new ProviderProbeComparisonResponseDto(null, null);
        if (provider is not ("codex" or "claude")
            || failureSignature is not ("unauthorized" or "unauthorized:service-account-shaped")
            || string.IsNullOrWhiteSpace(generation)) return empty;
        var target = runners.SingleOrDefault(item => item.RunnerId == runnerId && item.Status == "active");
        if (target is null) return empty;
        var targetCredential = CredentialFor(target.HostId, provider, generation, credentials);
        if (targetCredential is null || targetCredential.Locator.Adapter != effectiveSource)
            return empty;
        var service = provider == "codex" ? "codex-exec" : "claude-code";
        foreach (var candidate in runners.Where(item => item.RunnerId != runnerId
                     && item.HostId != target.HostId && item.Status == "active"))
        {
            var capability = candidate.Capabilities.SingleOrDefault(item =>
                item.Key == CapabilityProtocol.ProviderAuthentication(provider));
            if (capability is null || !capability.IsFresh
                || capability.EvidenceExcerpt != failureSignature
                || capability.Signal is not ("indeterminate" or "provider_incident")
                || capability.LastRealSuccessAt is not { } lastSuccess
                || capability.AdvertisedAt > now.AddMinutes(1)
                || now - capability.AdvertisedAt > TimeSpan.FromMinutes(10)
                || lastSuccess > capability.AdvertisedAt
                || capability.AdvertisedAt - lastSuccess > TimeSpan.FromMinutes(10)) continue;
            var independent = CredentialFor(candidate.HostId, provider,
                capability.CredentialGeneration, credentials);
            if (independent is null || independent.CredentialId == targetCredential.CredentialId)
                continue;
            if (capability.EffectiveSource != independent.Locator.Adapter)
                continue;
            return new(targetCredential.CredentialId,
                new(provider, service, "minimal-text-v1", failureSignature,
                    new DateTimeOffset(DateTime.SpecifyKind(lastSuccess, DateTimeKind.Utc)),
                    new DateTimeOffset(DateTime.SpecifyKind(capability.AdvertisedAt, DateTimeKind.Utc)),
                    true, true, candidate.HostId, independent.CredentialId, true));
        }
        return new(targetCredential.CredentialId, null);
    }

    private static CredentialRegistryRecordDto? CredentialFor(
        string hostId, string provider, string? generation,
        IReadOnlyList<CredentialRegistryRecordDto> credentials)
    {
        if (string.IsNullOrWhiteSpace(generation)) return null;
        var matches = credentials.Where(item => item.HostId == hostId
            && item.Provider == provider && item.Generation == generation
            && item.Locator.EffectiveSource == "active").Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
