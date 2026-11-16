namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// AGT-3746: a gate run that could not reach the remote gate host is gate host
/// unavailability, not a verdict on its members. On 11 November 2026 the SSH
/// tunnel to the gate host refused connections for under four minutes, and
/// every in-flight gate parked its members in Escalated although their
/// reviews had passed. While the host is unavailable the members stay in
/// their batch or pending pool, one run at a time probes the host with
/// backoff, and only a long outage escalates, once for the gate.
/// <para>
/// AGT-3792: on 15 November 2026 the gate host refused SSH for more than eight
/// hours. Every probe run formed and abandoned another batch of the same four
/// members, and cards whose merge gate hit the outage stayed escalated after
/// it ended. The host is now marked unreachable after a few consecutive
/// transport failures of one kind, a transport probe (not a gate run) checks
/// it with backoff, and its recovery returns the cards the transport escalated.
/// </para>
/// </summary>
public static class GateHostOutagePolicy
{
    /// <summary>Configuration key of the outage length that escalates the gate.</summary>
    public const string EscalateMinutesConfigKey = "RemoteGate:HostOutageEscalateMinutes";

    /// <summary>Outage length after which the gate escalates once, unless configured.</summary>
    public static readonly TimeSpan DefaultEscalateAfter = TimeSpan.FromMinutes(60);

    /// <summary>Configuration key of the consecutive same-kind transport failures that mark the host unreachable.</summary>
    public const string UnreachableAfterConfigKey = "RemoteGate:HostUnreachableAfterFailures";

    /// <summary>
    /// AGT-3792: consecutive transport failures of one kind that mark the gate
    /// host unreachable, unless configured. One gate run spends three transport
    /// attempts, so a single exhausted run reaches it.
    /// </summary>
    public const int DefaultUnreachableAfterFailures = 3;

    /// <summary>Configuration key of the gate host setting the owner can point elsewhere.</summary>
    public const string SshHostConfigKey = "RemoteGate:SshHost";

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
        "No route to host",
        "Network is unreachable",
        "Could not resolve hostname",
    ];

    // AGT-3792: the kinds of a transport failure that say the host itself does
    // not answer. Order matters: a banner timeout also reads "timed out".
    private static readonly (string Marker, string Kind)[] TransportKinds =
    [
        ("Connection refused", "connection-refused"),
        ("banner exchange", "banner-timeout"),
        ("No route to host", "no-route"),
        ("Network is unreachable", "no-route"),
        ("Could not resolve hostname", "name-resolution"),
        ("Connection timed out", "connection-timeout"),
        ("Operation timed out", "connection-timeout"),
    ];

    /// <summary>
    /// True when a failed gate run names a transport failure to the gate host
    /// and no failing test or build item. A run with failing items reached a
    /// verdict and stays the members' red.
    /// </summary>
    public static bool IsHostUnavailable(bool failed, bool cancelled, string? evidence, int failingItems)
        => failed && !cancelled && failingItems == 0 && !string.IsNullOrEmpty(evidence)
           && HostUnavailableMarkers.Any(marker => evidence.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The kind of a transport failure that says the gate host does not
    /// answer (connection refused, banner timeout, no route, name resolution,
    /// connection timeout), or null for any other failure.
    /// </summary>
    public static string? TransportFailureKind(string? evidence)
    {
        if (string.IsNullOrEmpty(evidence)) return null;
        foreach (var (marker, kind) in TransportKinds)
            if (evidence.Contains(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        return null;
    }

    /// <summary>
    /// True when <paramref name="sameKindFailures"/> consecutive transport
    /// failures of one kind reached <paramref name="threshold"/>; a threshold
    /// below one counts as one.
    /// </summary>
    public static bool MarksUnreachable(int sameKindFailures, int threshold)
        => sameKindFailures >= Math.Max(1, threshold);

    /// <summary>A transport failure kind in plain words.</summary>
    public static string DescribeKind(string? kind) => kind switch
    {
        "connection-refused" => "the connection is refused",
        "banner-timeout" => "the connection times out during the SSH banner exchange",
        "no-route" => "there is no route to the host",
        "name-resolution" => "the host name does not resolve",
        "connection-timeout" => "the connection times out",
        _ => "the transport fails",
    };

    /// <summary>What the owner can do about an unreachable gate host, in plain words.</summary>
    public static string OwnerAdvice(string? host)
        => $"Bring the gate host {(string.IsNullOrWhiteSpace(host) ? "back" : host + " back")} so that its SSH "
           + $"service answers, or point the gate at another host with the {SshHostConfigKey} setting. "
           + "The gate resumes on its own once a probe answers.";

    /// <summary>
    /// AGT-3792: a card the merge gate escalated as <c>gate-environment-exhausted</c>
    /// because of the gate transport returns to its previous lane once the
    /// gate host answered after the escalation and no outage is open. Every
    /// other gate failure (red tests, conflicts, other environment kinds) stays.
    /// </summary>
    public static bool ReturnsTransportEscalation(bool escalatedByTransport, DateTimeOffset escalatedAtUtc,
        DateTimeOffset? hostReachedAtUtc, bool outageOpen)
        => escalatedByTransport && !outageOpen && hostReachedAtUtc is { } reached && reached > escalatedAtUtc;

    /// <summary>
    /// A gate replay waits while the gate host is unreachable: it could not
    /// reach the host and would only spend a rung of the bounded ladder.
    /// </summary>
    public static bool HoldsGateReplay(bool outageOpen) => outageOpen;

    public static TimeSpan ProbeDelay(int failedProbes)
        => ProbeBackoff[Math.Clamp(failedProbes - 1, 0, ProbeBackoff.Count - 1)];

    /// <summary>
    /// True when a gate run may start: no outage is open, or the open outage's
    /// next probe is due. Every other run waits, so one run probes at a time.
    /// </summary>
    public static bool MayStartRun(DateTimeOffset? outageSinceUtc, DateTimeOffset lastAttemptUtc,
        int failedProbes, DateTimeOffset nowUtc)
        => outageSinceUtc is null || nowUtc - lastAttemptUtc >= ProbeDelay(failedProbes);

    /// <summary>
    /// AGT-3792: with a transport probe, no gate run starts during an outage;
    /// the probe alone checks the host with the same backoff. Without one, a
    /// due run is the probe (<see cref="MayStartRun"/>).
    /// </summary>
    public static bool MayStartRun(DateTimeOffset? outageSinceUtc, DateTimeOffset lastAttemptUtc,
        int failedProbes, DateTimeOffset nowUtc, bool transportProbe)
        => outageSinceUtc is null || !transportProbe && MayStartRun(outageSinceUtc, lastAttemptUtc, failedProbes, nowUtc);

    /// <summary>True when the open outage's next transport probe is due.</summary>
    public static bool ProbeDue(DateTimeOffset? outageSinceUtc, DateTimeOffset lastAttemptUtc,
        int failedProbes, DateTimeOffset nowUtc)
        => outageSinceUtc is not null && nowUtc - lastAttemptUtc >= ProbeDelay(failedProbes);

    /// <summary>True once per outage, when it has lasted <paramref name="escalateAfter"/>.</summary>
    public static bool EscalatesGate(DateTimeOffset outageSinceUtc, DateTimeOffset nowUtc,
        TimeSpan escalateAfter, bool alreadyEscalated)
        => !alreadyEscalated && nowUtc - outageSinceUtc >= escalateAfter;

    /// <summary>The readable gate state while the host is unavailable.</summary>
    public static string WaitingReason(DateTimeOffset outageSinceUtc)
        => $"gate host unreachable since {outageSinceUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC; waiting";
}
