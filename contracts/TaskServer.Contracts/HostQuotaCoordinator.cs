using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Single active probe and one cached reading for each provider account.</summary>
public sealed class HostQuotaCoordinator
{
    public const int DefaultTtlSeconds = 600;
    private static readonly TimeSpan ProbeLease = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FailedHostCooldown = TimeSpan.FromMinutes(5);
    private static readonly Regex Digest = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
    private static readonly Regex SafeLabel = new("^[A-Za-z0-9 ()-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex SafeReset = new("^[A-Za-z0-9_ /:(),+.-]{1,120}$", RegexOptions.Compiled);
    private static readonly Regex SafeVersion = new("^[A-Za-z0-9 .+-]{1,80}$", RegexOptions.Compiled);
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _subscriptions = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public HostQuotaCoordinator(int ttlSeconds = DefaultTtlSeconds)
        => _ttl = TimeSpan.FromSeconds(ttlSeconds);

    public static bool Valid(string provider, string subscriptionKey)
        => provider is "claude" or "codex" && Digest.IsMatch(subscriptionKey);

    public HostQuotaClaimResponse Claim(string runnerId, HostQuotaClaimRequest request,
        DateTime nowUtc, bool hasHealthyCapability, IReadOnlySet<string> healthyRunnerIds)
    {
        if (!Valid(request.Provider, request.SubscriptionKey))
            return new(false, null, null, "Invalid provider or subscription digest.");
        if (!hasHealthyCapability)
            return new(false, null, null, $"No healthy host holds a {request.Provider} sign-in.");
        lock (_gate)
        {
            var state = Get(request.Provider, request.SubscriptionKey);
            if (state.Reading is { } reading && nowUtc - reading.ObservedAt < _ttl && reading.Error is null)
                return new(false, null, reading.ObservedAt + _ttl, "Subscription reading is fresh.");
            if (state.FailedHosts.TryGetValue(runnerId, out var failedUntil) && failedUntil > nowUtc)
                return new(false, null, failedUntil, "This host's last probe failed; another healthy host may take over.");
            if (state.Lease is { } lease && lease.ExpiresAt > nowUtc
                && healthyRunnerIds.Contains(lease.RunnerId))
                return new(false, null, lease.ExpiresAt, "Another host is probing this subscription.");
            var probeId = Guid.NewGuid().ToString("N");
            state.Lease = new Lease(runnerId, request.InstanceId, probeId, nowUtc + ProbeLease);
            return new(true, probeId, null, null);
        }
    }

    public bool Report(string runnerId, HostQuotaReportRequest report, DateTime nowUtc, bool hasHealthyCapability)
    {
        if (!Valid(report.Provider, report.SubscriptionKey)
            || report.Windows.Count > 12 || report.Windows.Any(w => !SafeLabel.IsMatch(w.Label)
                || w.ResetLabel is { } reset && !SafeReset.IsMatch(reset)
                || w.UsedPct is < 0 or > 1000)
            || report.Plan is { } plan && !SafeVersion.IsMatch(plan)
            || report.CliVersion is { } version && !SafeVersion.IsMatch(version)
            || report.Error is { Length: > 120 }
            || report.ObservedAt > nowUtc.AddMinutes(1)
            || report.ObservedAt < nowUtc.AddMinutes(-2)) return false;
        lock (_gate)
        {
            var state = Get(report.Provider, report.SubscriptionKey);
            if (state.Lease is not { } lease || lease.RunnerId != runnerId
                || lease.InstanceId != report.InstanceId || lease.ProbeId != report.ProbeId
                || lease.ExpiresAt <= nowUtc || !hasHealthyCapability) return false;
            state.Lease = null;
            if (report.Error is { Length: > 0 })
            {
                state.FailedHosts[runnerId] = nowUtc + FailedHostCooldown;
                state.Reading = state.Reading is { } prior
                    ? prior with { Error = SafeError(report.Provider, report.Error), ProbeFailedAt = report.ObservedAt }
                    : new(report.Provider, report.SubscriptionKey, runnerId, report.ObservedAt,
                        null, null, [], SafeError(report.Provider, report.Error), report.ObservedAt);
            }
            else
            {
                state.FailedHosts.Remove(runnerId);
                state.Reading = new(report.Provider, report.SubscriptionKey, runnerId, report.ObservedAt,
                    report.CliVersion, report.Plan, report.Windows, null);
            }
            return true;
        }
    }

    public IReadOnlyList<HostQuotaReadingDto> Readings()
    {
        lock (_gate) return _subscriptions.Values.Where(s => s.Reading is not null)
            .Select(s => s.Reading!).ToArray();
    }

    private State Get(string provider, string key)
    {
        var index = provider + ":" + key;
        if (!_subscriptions.TryGetValue(index, out var state))
            _subscriptions[index] = state = new State();
        return state;
    }

    private static string SafeError(string provider, string error)
        => error.Contains("sign", StringComparison.OrdinalIgnoreCase)
            || error.Contains("auth", StringComparison.OrdinalIgnoreCase)
            ? $"No healthy host holds a {provider} sign-in."
            : error.Contains("CLI", StringComparison.OrdinalIgnoreCase)
                ? $"No healthy reporting host has a {provider} CLI installed."
            : $"A {provider} host quota probe failed.";

    private sealed class State
    {
        public HostQuotaReadingDto? Reading;
        public Lease? Lease;
        public Dictionary<string, DateTime> FailedHosts { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Lease(string RunnerId, string InstanceId, string ProbeId, DateTime ExpiresAt);
}
