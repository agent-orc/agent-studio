namespace AgentStudio.TaskServer.Contracts;

/// <summary>WI-1 metadata policy. A refreshable login is durable even when its access token expires soon.</summary>
public static class CredentialLifecyclePolicy
{
    public const int DefaultRotationIntervalDays = 90;

    public static string Classify(CredentialInventoryItemDto item)
    {
        if (item.Refreshable) return "long-lived";
        if (item.ExpiresAt is { } expires && item.CreatedAt is { } created)
            return expires > created && expires - created <= TimeSpan.FromHours(24) ? "short-lived" : "long-lived";
        // These existing issuers impose a maximum of one hour or, for enrolment codes, 24 hours.
        return item.Kind is "github_app_token" or "attempt_repository_token" or "enrolment_code"
            ? "short-lived" : "long-lived";
    }

    /// <summary>Unknown creation remains unknown. First observation anchors only the reminder plan.</summary>
    public static CredentialInventoryItemDto Complete(CredentialInventoryItemDto item, DateTime firstObservedAt)
    {
        var interval = item.RotationIntervalDays ?? DefaultRotationIntervalDays;
        return item with
        {
            Holder = item.Holder ?? item.HostId,
            Target = item.Target ?? item.Repository ?? item.Kind,
            RotationIntervalDays = item.LifetimeClass == "long-lived" ? interval : null,
            RotationDueAt = item.LifetimeClass == "long-lived"
                ? item.RotationDueAt ?? (item.RotationBasisAt ?? item.CreatedAt ?? firstObservedAt).AddDays(interval) : null,
        };
    }

    public static IReadOnlyList<CredentialLifecycleWarningDto> Warnings(IReadOnlyList<CredentialInventoryItemDto> items, DateTime now)
    {
        var warnings = new List<CredentialLifecycleWarningDto>();
        foreach (var item in items.Where(item => item.Active && item.LifetimeClass == "long-lived" && !item.Id.StartsWith("required:", StringComparison.Ordinal)))
        {
            Add(item, "expiry", item.ExpiresAt);
            Add(item, "rotation", item.RotationDueAt);
        }
        return warnings;

        void Add(CredentialInventoryItemDto item, string subject, DateTime? due)
        {
            if (due is not { } date || date - now > TimeSpan.FromDays(14)) return;
            warnings.Add(new(item.Id, item.Holder!, item.Target!, subject, date,
                date - now <= TimeSpan.FromDays(3) ? 3 : 14));
        }
    }

    /// <summary>Capability advertisements prove login health, not creation, expiry or last real use.</summary>
    public static IReadOnlyList<CredentialInventoryItemDto> ProviderLogins(IReadOnlyList<RunnerCapabilitySnapshotDto> hosts)
        => hosts.SelectMany(host => host.Capabilities
            .Where(capability => capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal))
            .Select(capability =>
            {
                var provider = capability.Key["provider-auth:".Length..];
                return new CredentialInventoryItemDto($"provider-login:{host.RunnerId}:{provider}", "provider", host.HostId,
                    provider == "codex" ? "codex_chatgpt_login" : provider + "_login",
                    $"{provider} login on {host.Name}", [], null, null, null, null,
                    capability.IsFresh && capability.HealthState == "healthy" ? "ok" : "missing", false,
                    "Host-owned provider login. Refreshing an access token does not rotate this login.", [],
                    Holder: host.RunnerId, Target: provider, Refreshable: true);
            }))
            .DistinctBy(item => item.Id).ToArray();
}

public sealed record CredentialLifecycleWarningDto(string CredentialId, string Holder, string Target,
    string Subject, DateTime DueAt, int ThresholdDays);
