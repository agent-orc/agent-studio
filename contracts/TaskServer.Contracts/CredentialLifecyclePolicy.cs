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

    /// <summary>
    /// Capability advertisements prove login health, not creation, expiry or last real use. A login belongs to the
    /// host, so several roles of one host yield one entry, and the kind matches the registry's kind for the provider.
    /// </summary>
    public static IReadOnlyList<CredentialInventoryItemDto> ProviderLogins(IReadOnlyList<RunnerCapabilitySnapshotDto> hosts)
        => hosts.SelectMany(host => host.Capabilities
                .Where(capability => capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal))
                .Select(capability => (Host: host, Provider: capability.Key["provider-auth:".Length..],
                    Healthy: capability.IsFresh && capability.HealthState == "healthy")))
            .GroupBy(login => (login.Host.HostId, login.Provider))
            .OrderBy(group => group.Key.HostId, StringComparer.Ordinal).ThenBy(group => group.Key.Provider, StringComparer.Ordinal)
            .Select(group =>
            {
                var (hostId, provider) = group.Key;
                var name = group.Select(login => login.Host.Name).Order(StringComparer.Ordinal).First();
                return new CredentialInventoryItemDto($"provider-login:{hostId}:{provider}", "provider", hostId,
                    ProviderLoginKind(provider), $"{provider} login on {name}", [], null, null, null, null,
                    group.Any(login => login.Healthy) ? "ok" : "missing", false,
                    "Host-owned provider login. Refreshing an access token does not rotate this login.", [],
                    Holder: hostId, Target: provider, Refreshable: true);
            })
            .ToArray();

    public static string ProviderLoginKind(string provider) => provider switch
    {
        "claude" => "claude_native_login",
        "codex" => "codex_chatgpt_login",
        _ => provider + "_login",
    };

    /// <summary>
    /// An active registry entry for the same host and kind already carries the login, so the capability entry is
    /// dropped to avoid a second warning. An inactive registry entry does not hide the advertised login.
    /// </summary>
    public static IReadOnlyList<CredentialInventoryItemDto> MergeProviderLogins(
        IReadOnlyList<CredentialInventoryItemDto> items, IReadOnlyList<CredentialInventoryItemDto> logins)
        => [.. items, .. logins.Where(login => !items.Any(item =>
            item.Active && item.HostId == login.HostId && item.Kind == login.Kind))];
}

public sealed record CredentialLifecycleWarningDto(string CredentialId, string Holder, string Target,
    string Subject, DateTime DueAt, int ThresholdDays);
