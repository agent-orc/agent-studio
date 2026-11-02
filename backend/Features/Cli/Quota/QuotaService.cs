using System.Collections.Concurrent;
using AgentStudio.Security;

namespace AgentStudio.Cli;

/// <summary>
/// Orchestrates the per-CLI <see cref="IQuotaProbe"/> instances:
/// <list type="bullet">
///   <item>In-memory cache keyed by CLI type, with a TTL (default 10 min).</item>
///   <item>Stale-while-revalidate: cached snapshot is returned immediately, a background re-probe
///         updates the cache when the entry is stale and not currently being refreshed.</item>
///   <item><see cref="ForceRefreshAsync"/> awaits the next probe and replaces the cached value.</item>
///   <item>Concurrent re-probes for the same CLI are coalesced via per-CLI locks.</item>
/// </list>
/// Probing is expensive (each call spawns a CLI in a PTY for several seconds), so callers
/// should rely on the cache and only force-refresh on user intent or after a job exits.
/// </summary>
public sealed class QuotaService
{
    private readonly ILogger<QuotaService> _logger;
    private readonly IReadOnlyDictionary<string, IQuotaProbe> _probes;
    private readonly ConcurrentDictionary<string, QuotaSnapshot> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte> _backgroundRefreshes = new();
    private readonly TimeSpan _ttl;
    private readonly QuotaCacheStore _store;
    private readonly CliVersionTracker? _versionTracker;
    private readonly QuotaHistoryStore? _history;
    private readonly bool _networked;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, QuotaSnapshot> _pendingHostDrops = new();
    private readonly Dictionary<string, DateTime> _seenHostObservations = new(StringComparer.Ordinal);
    private readonly object _hostIngestGate = new();

    public QuotaService(
        ILogger<QuotaService> logger,
        IEnumerable<IQuotaProbe> probes,
        IConfiguration configuration,
        QuotaCacheStore store,
        CliVersionTracker? versionTracker = null,
        QuotaHistoryStore? history = null,
        TimeProvider? clock = null)
    {
        _logger = logger;
        _probes = probes.ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
        var ttlSec = int.TryParse(configuration["Quota:TtlSeconds"], out var t) ? t : 600;
        _ttl = TimeSpan.FromSeconds(ttlSec);
        _store = store;
        _versionTracker = versionTracker;
        _history = history;
        _networked = !SecurityProfiles.IsLocal(configuration);
        _clock = clock ?? TimeProvider.System;

        // Hydrate the in-memory cache from disk on startup so the
        // header / strip have something to render before the first
        // probe completes (probes take 30+ seconds per CLI). Stale
        // detection is the consumer's responsibility via TtlSeconds.
        try
        {
            foreach (var snap in _store.Read())
            {
                if (string.IsNullOrWhiteSpace(snap.CliType)) continue;
                _cache[snap.SubscriptionKey is { Length: > 0 }
                    ? snap.CliType + ":" + snap.SubscriptionKey : snap.CliType] = snap;
                _versionTracker?.Seed(snap.CliType, snap.CliVersion);
                RecordHistory(snap);
            }
            _logger.LogInformation("Hydrated quota cache from disk ({Count} snapshots).", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate quota cache from disk");
        }
    }

    public IReadOnlyCollection<string> Probes => _probes.Keys.ToList();

    /// <summary>Cache TTL exposed so callers can render a "stale" badge.</summary>
    public TimeSpan Ttl => _ttl;

    public QuotaReport GetCached()
    {
        return new QuotaReport
        {
            TtlSeconds = (int)_ttl.TotalSeconds,
            Snapshots = _probes.Keys.SelectMany(k =>
            {
                if (!_networked) return [_cache.TryGetValue(k, out var local)
                    ? local : new QuotaSnapshot { CliType = k }];
                if (k == CliTypes.Gemini) return [new QuotaSnapshot
                {
                    CliType = k, FetchedAt = default,
                    Error = "Gemini subscription quota is not available from execution hosts."
                }];
                var hosted = _cache.Where(entry => entry.Key.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => _clock.GetUtcNow().UtcDateTime - entry.Value.FetchedAt > _ttl
                        && entry.Value.Error is null
                        ? entry.Value with
                        {
                            Error = $"No recent reading from a healthy {k} host sign-in.",
                            ProbeFailedAt = entry.Value.FetchedAt + _ttl
                        }
                        : entry.Value).ToArray();
                if (hosted.Length > 0) return hosted;
                var prior = _cache.TryGetValue(k, out var old) ? old : new QuotaSnapshot { CliType = k };
                return [prior with
                {
                    RawSample = null,
                    Error = $"No healthy reporting host has supplied a {k} sign-in reading."
                }];
            }).ToList()
        };
    }

    /// <summary>
    /// Returns the in-memory snapshot for one CLI without triggering any
    /// refresh. Used by the cap-enforcement code path which must be cheap and
    /// non-blocking - it runs on every pickup tick.
    /// </summary>
    public QuotaSnapshot? GetCachedFor(string cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        if (_networked)
        {
            var hosted = _cache.Where(entry => entry.Key.StartsWith(cliType + ":", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Value)
                .Where(snapshot => _clock.GetUtcNow().UtcDateTime - snapshot.FetchedAt <= _ttl
                    && snapshot.Error is null).ToArray();
            // The existing admission contract has no subscription selector. With
            // multiple accounts, no provider-wide percentage is authoritative.
            return hosted.Length == 1 ? hosted[0] : null;
        }
        return _cache.TryGetValue(cliType, out var s) ? s : null;
    }

    /// <summary>Accept a parsed host reading through the same cache, gate and history path.</summary>
    public void IngestHostReading(AgentStudio.TaskServer.Contracts.HostQuotaReadingDto reading)
    {
        if (!_networked || !AgentStudio.TaskServer.Contracts.HostQuotaCoordinator.Valid(
                reading.Provider, reading.SubscriptionKey)) return;
        var key = reading.Provider + ":" + reading.SubscriptionKey;
        lock (_hostIngestGate)
        {
        var reportAt = reading.ProbeFailedAt ?? reading.ObservedAt;
        if (_seenHostObservations.TryGetValue(key, out var seen) && seen >= reportAt)
            return;
        _seenHostObservations[key] = reportAt;
        _cache.TryGetValue(key, out var previous);
        var source = reading.Provider == CliTypes.Claude ? "host /usage" : "host /status";
        var candidate = new QuotaSnapshot
        {
            CliType = reading.Provider,
            SubscriptionKey = reading.SubscriptionKey,
            FetchedAt = reading.ObservedAt,
            CliVersion = reading.CliVersion,
            Plan = reading.Plan,
            Source = source,
            Windows = reading.Windows.Select(window => new QuotaWindow
            {
                Label = window.Label, UsedPct = window.UsedPct, Unit = "%",
                ResetAt = window.ResetAt, ResetLabel = window.ResetLabel
            }).ToList(),
            Error = reading.Error,
            ProbeFailedAt = reading.ProbeFailedAt,
        };
        if (candidate.Error is not null)
        {
            candidate = RetainLastGoodOnFailure(previous, candidate);
        }
        else
        {
            var suspicion = QuotaPlausibilityGate.Evaluate(previous, candidate, _clock.GetUtcNow().UtcDateTime);
            if (suspicion.Suspicious)
            {
                if (_pendingHostDrops.TryGetValue(key, out var pending)
                    && QuotaPlausibilityGate.AreConsistent(pending, candidate))
                {
                    _pendingHostDrops.TryRemove(key, out _);
                }
                else
                {
                    _pendingHostDrops[key] = candidate;
                    candidate = (previous ?? candidate) with
                    {
                        Suspicious = true, SuspiciousReason = suspicion.Reason
                    };
                }
            }
            else _pendingHostDrops.TryRemove(key, out _);
            if (!candidate.Suspicious)
                candidate = QuotaWindowProjection.AnchorWindowStarts(previous, candidate, reading.ObservedAt);
        }
        _cache[key] = candidate;
        _versionTracker?.Observe(reading.Provider, candidate.CliVersion, "host-quota-report");
        PersistCache();
        RecordHistory(candidate);
        }
    }

    /// <summary>
    /// Returns the cached snapshot for every probe immediately, kicking off a background
    /// re-probe for any entry that is missing or older than the TTL.
    /// </summary>
    public QuotaReport GetWithBackgroundRefresh(CancellationToken ct = default)
    {
        if (_networked) return GetCached();
        // The HTTP request token is deliberately not passed to the refresh.
        // A disconnected client must not cancel shared cache maintenance, and
        // no synchronous CLI startup work may run on the GET request thread.
        _ = ct;
        foreach (var k in _probes.Keys)
        {
            var stale = !_cache.TryGetValue(k, out var s)
                || (DateTime.UtcNow - (s.ProbeFailedAt ?? s.FetchedAt)) > _ttl;
            if (stale) QueueBackgroundRefresh(k);
        }
        return GetCached();
    }

    private void QueueBackgroundRefresh(string cliType)
    {
        if (!_backgroundRefreshes.TryAdd(cliType, 0)) return;

        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(cliType, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background quota refresh for {Cli} failed", cliType); }
            finally { _backgroundRefreshes.TryRemove(cliType, out _); }
        });
    }

    /// <summary>Force a re-probe of every CLI and await all of them.</summary>
    public async Task<QuotaReport> RefreshAllAsync(CancellationToken ct = default)
    {
        if (_networked) return GetCached();
        await Task.WhenAll(_probes.Keys.Select(k => RefreshAsync(k, ct)));
        return GetCached();
    }

    public async Task<QuotaSnapshot?> RefreshAsync(string cliType, CancellationToken ct = default)
    {
        if (_networked) return GetCachedFor(cliType);
        if (!_probes.TryGetValue(cliType, out var probe)) return null;
        var sem = _locks.GetOrAdd(cliType, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
        {
            // Another probe for this CLI is already running — let it win, return cached.
            _cache.TryGetValue(cliType, out var existing);
            return existing;
        }
        try
        {
            _cache.TryGetValue(cliType, out var previous);
            var snap = await ProbeOnceAsync(probe, ct);
            _versionTracker?.Observe(cliType, snap.CliVersion, "quota-probe");
            if (!string.IsNullOrWhiteSpace(snap.Error))
            {
                snap = RetainLastGoodOnFailure(previous, snap);
            }
            else
            {
                snap = await ReconcileSuspiciousDropAsync(cliType, probe, previous, snap, ct);
                // Keep this order: AnchorWindowStarts treats a weekly drop as an observed
                // reset, which is only safe once the gate above has confirmed the drop.
                snap = QuotaWindowProjection.AnchorWindowStarts(previous, snap, DateTime.UtcNow);
            }
            _cache[cliType] = snap;
            PersistCache();
            RecordHistory(snap);
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota probe for {Cli} threw", cliType);
            // Latch a prior suspicious flag onto the error snapshot. A probe that
            // fails right after a ground-truth invalidation (AGT-2064) must not
            // silently drop the block and re-open the admission gate.
            _cache.TryGetValue(cliType, out var prior);
            var failed = new QuotaSnapshot
            {
                CliType = cliType,
                Error = NormalizeProbeError(ex.Message),
                Suspicious = prior?.Suspicious ?? false,
                SuspiciousReason = prior?.Suspicious == true ? prior.SuspiciousReason : null
            };
            var snap = RetainLastGoodOnFailure(prior, failed);
            _cache[cliType] = snap;
            PersistCache();
            return snap;
        }
        finally { sem.Release(); }
    }

    private static async Task<QuotaSnapshot> ProbeOnceAsync(IQuotaProbe probe, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        return await probe.ProbeAsync(cts.Token);
    }

    private static QuotaSnapshot RetainLastGoodOnFailure(QuotaSnapshot? previous, QuotaSnapshot failed)
    {
        var error = NormalizeProbeError(failed.Error);
        var failedAt = failed.ProbeFailedAt ?? failed.FetchedAt;
        var hasLastGood = previous != null
            && (previous.Windows.Count > 0 || !string.IsNullOrWhiteSpace(previous.Plan));

        if (!hasLastGood)
        {
            return failed with
            {
                Error = error,
                ProbeFailedAt = failedAt
            };
        }

        return previous! with
        {
            CliVersion = failed.CliVersion ?? previous.CliVersion,
            ProbeFailedAt = failedAt,
            Error = error,
            RawSample = failed.RawSample ?? previous.RawSample,
            Suspicious = previous.Suspicious || failed.Suspicious,
            SuspiciousReason = failed.SuspiciousReason ?? previous.SuspiciousReason
        };
    }

    private static string NormalizeProbeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "Quota probe failed.";
        return error.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation was cancelled", StringComparison.OrdinalIgnoreCase)
            ? "Quota probe timed out before the CLI panel rendered."
            : error;
    }

    /// <summary>
    /// AGT-2064 plausibility gate. A single probe that shows a window jumping
    /// DOWN by more than the threshold with no reset to explain it is not
    /// trusted on its own: re-probe immediately and only accept the drop when a
    /// second, independent measurement agrees. If the confirmation disagrees,
    /// the first reading was a transient glitch - keep the previous
    /// (still-blocking) value and flag it suspicious so the admission gate stays
    /// conservative until a clean reading arrives.
    /// </summary>
    private async Task<QuotaSnapshot> ReconcileSuspiciousDropAsync(
        string cliType, IQuotaProbe probe, QuotaSnapshot? previous, QuotaSnapshot candidate, CancellationToken ct)
    {
        var suspicion = QuotaPlausibilityGate.Evaluate(previous, candidate, DateTime.UtcNow);
        if (!suspicion.Suspicious) return candidate;

        _logger.LogWarning(
            "quota_snapshot_suspicious cli={Cli} reason={Reason}: re-probing to confirm before trusting the drop",
            cliType, suspicion.Reason);

        var confirm = await ProbeOnceAsync(probe, ct);
        if (QuotaPlausibilityGate.AreConsistent(candidate, confirm))
        {
            _logger.LogInformation(
                "quota_snapshot_confirmed cli={Cli}: two consistent probes agree, accepting the new value", cliType);
            return confirm;
        }

        _logger.LogWarning(
            "quota_snapshot_glitch_discarded cli={Cli} reason={Reason}: confirmation probe disagreed, holding prior snapshot (suspicious) so admission stays conservative",
            cliType, suspicion.Reason);
        return (previous ?? candidate) with
        {
            Suspicious = true,
            SuspiciousReason = suspicion.Reason,
            FetchedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Ground-truth override (AGT-2064): a live launch just died with a
    /// usage-limit error, which is proof the cached snapshot is wrong no matter
    /// how green it looks - the error text is the evidence. Flag the cached
    /// snapshot suspicious immediately so the admission gate stops trusting it,
    /// then re-probe right now instead of waiting out the (10-minute) TTL.
    /// Returns the re-probe task so callers that care (tests) can await it;
    /// the runner fires it and forgets.
    /// </summary>
    public Task InvalidateForGroundTruthLimit(string cliType, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return Task.CompletedTask;

        var existing = _cache.TryGetValue(cliType, out var s) ? s : new QuotaSnapshot { CliType = cliType };
        _cache[cliType] = existing with
        {
            CliType = cliType,
            Suspicious = true,
            SuspiciousReason = reason,
            FetchedAt = DateTime.UtcNow
        };
        PersistCache();
        _logger.LogWarning(
            "quota_snapshot_invalidated cli={Cli} reason={Reason}: launch hit a usage limit, re-probing now (bypassing TTL)",
            cliType, reason);

        return RefreshAsync(cliType, ct);
    }

    /// <summary>
    /// Append a trusted snapshot to the quota history (AGT-3001). The store
    /// skips failed, suspicious, and already-recorded snapshots itself.
    /// </summary>
    private void RecordHistory(QuotaSnapshot snapshot)
    {
        if (_history is null) return;
        try { _history.Record(snapshot); }
        catch (Exception ex) { _logger.LogDebug(ex, "Quota history record failed"); }
    }

    /// <summary>
    /// Push the current in-memory cache to disk. Best-effort and async-
    /// safe; failures are logged inside the store and never thrown.
    /// </summary>
    private void PersistCache()
    {
        try { _store.Write(_cache.Values); }
        catch (Exception ex) { _logger.LogDebug(ex, "Quota cache persist failed"); }
    }
}
