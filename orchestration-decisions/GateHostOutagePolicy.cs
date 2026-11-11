namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// AGT-3746: a gate run that could not reach the remote gate host is gate host
/// unavailability, not a verdict on its members. On 11 November 2026 the SSH
/// tunnel to the gate host refused connections for under four minutes, and
/// every in-flight gate parked its members in Escalated although their
/// reviews had passed. While the host is unavailable the members stay in
/// their batch or pending pool, one run at a time probes the host with
/// backoff, and only a long outage escalates, once for the gate.
/// </summary>
public static class GateHostOutagePolicy
{
    /// <summary>Configuration key of the outage length that escalates the gate.</summary>
    public const string EscalateMinutesConfigKey = "RemoteGate:HostOutageEscalateMinutes";

    /// <summary>Outage length after which the gate escalates once, unless configured.</summary>
    public static readonly TimeSpan DefaultEscalateAfter = TimeSpan.FromMinutes(60);

    /// <summary>Wait before the next probe, by the number of failed probes; the last entry repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> ProbeBackoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
    ];

    // Evidence of a connection to the gate host that failed before or while
    // the worker ran. Origin fetch failures (unable to access, ECONNRESET) are
    // repository transport, judged by the gate failure triage as before.
    private static readonly string[] HostUnavailableMarkers =
    [
        "Remote gate transport failure",
        "ssh exited 255",
        "ssh: connect to host",
        "Connection refused",
        "Connection timed out",
    ];

    /// <summary>
    /// True when a failed gate run names a transport failure to the gate host
    /// and no failing test or build item. A run with failing items reached a
    /// verdict and stays the members' red.
    /// </summary>
    public static bool IsHostUnavailable(bool failed, bool cancelled, string? evidence, int failingItems)
        => failed && !cancelled && failingItems == 0 && !string.IsNullOrEmpty(evidence)
           && HostUnavailableMarkers.Any(marker => evidence.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static TimeSpan ProbeDelay(int failedProbes)
        => ProbeBackoff[Math.Clamp(failedProbes - 1, 0, ProbeBackoff.Count - 1)];

    /// <summary>
    /// True when a gate run may start: no outage is open, or the open outage's
    /// next probe is due. Every other run waits, so one run probes at a time.
    /// </summary>
    public static bool MayStartRun(DateTimeOffset? outageSinceUtc, DateTimeOffset lastAttemptUtc,
        int failedProbes, DateTimeOffset nowUtc)
        => outageSinceUtc is null || nowUtc - lastAttemptUtc >= ProbeDelay(failedProbes);

    /// <summary>True once per outage, when it has lasted <paramref name="escalateAfter"/>.</summary>
    public static bool EscalatesGate(DateTimeOffset outageSinceUtc, DateTimeOffset nowUtc,
        TimeSpan escalateAfter, bool alreadyEscalated)
        => !alreadyEscalated && nowUtc - outageSinceUtc >= escalateAfter;

    /// <summary>The readable gate state while the host is unavailable.</summary>
    public static string WaitingReason(DateTimeOffset outageSinceUtc)
        => $"gate host unreachable since {outageSinceUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC; waiting";
}
