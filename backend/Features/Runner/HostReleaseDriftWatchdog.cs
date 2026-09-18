using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>One runner role compared against the Stable release.</summary>
public sealed record HostReleaseDriftEntry(
    string RunnerId,
    string Name,
    string HostId,
    string Role,
    Contract.RunnerReleaseIdentityDto? Release,
    string State,
    double? BehindByHours,
    double? BehindForHours,
    bool AlarmDue,
    string Reason,
    DateTime LastSeenAt,
    bool HeartbeatStale);

/// <summary>
/// What Execution Hosts renders: the Stable release every row is measured
/// against, and one row per registered runner role.
/// </summary>
public sealed record HostReleaseDriftSnapshot(
    DateTime ObservedAt,
    StableReleaseIdentity Stable,
    int BehindCount,
    IReadOnlyList<HostReleaseDriftEntry> Hosts);

/// <summary>
/// Periodically compares every registered runner role against the release this
/// server runs and raises one operator-feed alarm per host release that has
/// been behind for longer than the grace window (AGT-2826).
/// </summary>
/// <remarks>
/// The incident this closes (2026-09-15): Stable and the Task Server ran v0.3.0
/// while a runner host still ran an agent-host release from three weeks earlier.
/// The old host silently produced review material without a unified diff, and no
/// Studio surface showed the drift - it was found by reading the host symlink.
/// The comparison itself is pure (<see cref="HostReleaseDriftPolicy"/>); this
/// service owns only the clock, the registry read, and alarm de-duplication.
/// </remarks>
public sealed class HostReleaseDriftWatchdog : BackgroundService
{
    private const int DefaultIntervalSeconds = 300;
    /// <summary>A host that stopped reporting is an offline problem, not a drift problem.</summary>
    private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromMinutes(15);

    private readonly V1ReviewExecutorRegistry _registry;
    private readonly BuildIdentity _build;
    private readonly AgentMessageBusBridge _bus;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HostReleaseDriftWatchdog> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, (int Failures, DateTime RetryAt)> _pending = new(StringComparer.Ordinal);
    /// <summary>First observation of the current drift, keyed by runner and host release.</summary>
    private readonly Dictionary<string, DateTime> _behindSince = new(StringComparer.Ordinal);
    /// <summary>Alarm keys already sent, so one drift produces exactly one feed event.</summary>
    private readonly HashSet<string> _alarmed = new(StringComparer.Ordinal);
    private HostReleaseDriftSnapshot _current;

    public HostReleaseDriftWatchdog(
        V1ReviewExecutorRegistry registry,
        BuildIdentity build,
        AgentMessageBusBridge bus,
        IConfiguration configuration,
        ILogger<HostReleaseDriftWatchdog> logger)
    {
        _registry = registry;
        _build = build;
        _bus = bus;
        _configuration = configuration;
        _logger = logger;
        _current = new HostReleaseDriftSnapshot(
            DateTime.UtcNow,
            StableReleaseIdentity.FromBuildIdentity(build),
            0,
            []);
    }

    public HostReleaseDriftSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public TimeSpan Grace => TimeSpan.FromHours(Math.Clamp(
        _configuration.GetValue<int?>("HostReleaseDrift:GraceHours") ?? 24,
        1,
        24 * 30));

    public async Task<HostReleaseDriftSnapshot> RefreshAsync(
        DateTime? nowUtc = null,
        CancellationToken ct = default)
    {
        // Serialize refreshes across the hosted loop and callers while emission is in flight.
        await _refreshGate.WaitAsync(ct);
        try { return await RefreshCoreAsync(nowUtc, ct); }
        finally { _refreshGate.Release(); }
    }

    private async Task<HostReleaseDriftSnapshot> RefreshCoreAsync(DateTime? nowUtc, CancellationToken ct)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var stable = StableReleaseIdentity.FromBuildIdentity(_build);
        var grace = Grace;

        var entries = new List<HostReleaseDriftEntry>();
        var alarms = new List<HostReleaseDriftEntry>();
        lock (_gate)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in _registry.ListCapabilitySnapshots())
            {
                var key = DriftKey(snapshot.RunnerId, snapshot.Release);
                live.Add(key);
                var behindSince = _behindSince.TryGetValue(key, out var since) ? since : now;
                var verdict = HostReleaseDriftPolicy.Evaluate(
                    snapshot.Release,
                    stable,
                    now,
                    behindSince,
                    grace);

                if (verdict.State == HostReleaseDriftStates.Behind)
                {
                    _behindSince[key] = behindSince;
                }
                else
                {
                    // Recovered: forget the observation window and re-arm the alarm.
                    _behindSince.Remove(key);
                    _alarmed.Remove(key);
                    _pending.Remove(key);
                }

                var stale = now - snapshot.LastSeenAt.ToUniversalTime() > HeartbeatStaleAfter;
                var entry = new HostReleaseDriftEntry(
                    snapshot.RunnerId,
                    snapshot.Name,
                    snapshot.HostId,
                    RoleOf(snapshot),
                    snapshot.Release,
                    verdict.State,
                    verdict.BehindBy?.TotalHours,
                    verdict.BehindFor?.TotalHours,
                    verdict.AlarmDue && !stale,
                    verdict.Reason,
                    snapshot.LastSeenAt,
                    stale);
                entries.Add(entry);
                if (entry.AlarmDue && !_alarmed.Contains(key)
                    && (!_pending.TryGetValue(key, out var pending) || now >= pending.RetryAt))
                    alarms.Add(entry);
            }

            // A host that upgraded, retired, or re-registered with a new release
            // must be able to alarm again if it ever falls behind once more.
            foreach (var stalekey in _behindSince.Keys.Where(k => !live.Contains(k)).ToList())
                _behindSince.Remove(stalekey);
            _alarmed.RemoveWhere(k => !live.Contains(k));
            foreach (var key in _pending.Keys.Where(k => !live.Contains(k)).ToList())
                _pending.Remove(key);

            _current = new HostReleaseDriftSnapshot(
                now,
                stable,
                entries.Count(entry => entry.State == HostReleaseDriftStates.Behind),
                entries);
        }

        var failures = 0;
        Exception? lastFailure = null;
        foreach (var alarm in alarms)
        {
            var key = DriftKey(alarm.RunnerId, alarm.Release);
            try
            {
                await _bus.EmitHostReleaseDriftAsync(
                    alarm.HostId, alarm.RunnerId, alarm.Role,
                    alarm.Release?.ReleaseId ?? "unknown", stable.Version,
                    alarm.BehindForHours ?? 0, alarm.Reason, ct);
                _alarmed.Add(key);
                _pending.Remove(key);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var attempts = _pending.TryGetValue(key, out var pending) ? pending.Failures + 1 : 1;
                // 30 seconds initially, doubling to a five-minute ceiling; no inline retry loop.
                var delay = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(attempts - 1, 4))));
                _pending[key] = (Math.Min(attempts, 5), now + delay);
                failures++;
                lastFailure = ex;
            }
        }
        if (failures > 0)
            _logger.LogWarning(lastFailure,
                "host-release-drift-emission-failed count={FailedCount}; pending alarms will retry", failures);

        return Current;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("HostReleaseDrift:IntervalSeconds") ?? DefaultIntervalSeconds,
            30,
            60 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            await RefreshSafelyAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("host-release-drift-watchdog-stopped");
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "host-release-drift-watchdog-failed");
        }
    }

    private static string DriftKey(string runnerId, Contract.RunnerReleaseIdentityDto? release)
        => $"{runnerId}|{release?.ReleaseId ?? "unknown"}";

    private static string RoleOf(Contract.RunnerCapabilitySnapshotDto snapshot)
        => snapshot.Capabilities.Any(capability =>
            string.Equals(capability.Key, Contract.CapabilityProtocol.ReviewExecutor, StringComparison.Ordinal))
            ? "review"
            : "coding";
}
