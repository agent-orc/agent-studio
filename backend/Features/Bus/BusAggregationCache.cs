using System.Collections.Concurrent;

namespace AgentStudio.Bus;

/// <summary>
/// Token-spend rollup served by <c>GET /api/bus/{project}/token-aggregate</c>.
/// Aggregation dimensions:
/// <list type="bullet">
///   <item><c>byModel</c>:        sum per model id</item>
///   <item><c>byParticipant</c>:  sum per participantId</item>
///   <item><c>byDay</c>:          sum per UTC calendar day</item>
/// </list>
/// </summary>
public sealed record TokenAggregateResponse(
    string Project,
    long TotalMessages,
    DateTime? Since,
    DateTime? Until,
    IReadOnlyList<TokenAggregateBucket> ByModel,
    IReadOnlyList<TokenAggregateBucket> ByParticipant,
    IReadOnlyList<TokenAggregateBucket> ByDay,
    TokenAggregateTotals Totals);

public sealed record TokenAggregateBucket(
    string Key,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Messages,
    double? Dollars);

public sealed record TokenAggregateTotals(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Messages,
    double? Dollars);

/// <summary>
/// Performance-critical aggregation layer over <see cref="AgentMessageBusStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strategy.</b> Each (workspace, project) pair owns a small set of
/// <see cref="Dictionary{TKey, TValue}"/> tallies (by model, by participant,
/// by UTC date). Tallies are updated incrementally on <see cref="OnAppended"/>
/// and on first-access from the store's existing projection. Requests then
/// return a snapshot in O(buckets) - no message scan.
/// </para>
/// <para>
/// <b>Concurrency.</b> Each per-(workspace, project) state holds a single
/// <see cref="object"/> lock; updates and snapshots both acquire it. Snapshots
/// copy out the dictionaries so callers see a stable view even if writes
/// continue. Lock contention is small: <see cref="OnAppended"/> does O(1) work
/// and snapshots are infrequent (UI polling).
/// </para>
/// <para>
/// <b>Filtering.</b> When the caller provides since/until, the cache cannot
/// return its pre-computed buckets - those cover the full lifetime. The
/// fallback is a single pass over the store's projection; still much cheaper
/// than the per-request scan we had before because the projection is in
/// memory. The unfiltered case (the common UI call) is the O(1) path.
/// </para>
/// </remarks>
public sealed class BusAggregationCache
{
    private readonly AgentMessageBusStore _store;
    private readonly ConcurrentDictionary<Key, State> _states = new();

    public BusAggregationCache(AgentMessageBusStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Hook called by <see cref="AgentMessageBusStore.AppendAsync"/> after a
    /// successful write. Updates the per-project tallies in O(1).
    /// </summary>
    public void OnAppended(string workspaceRoot, AgentMessage message)
    {
        if (message?.Tokens is null) return;
        if (string.IsNullOrWhiteSpace(message.Project)) return;
        var state = GetOrLoadState(workspaceRoot, message.Project!);
        lock (state.Sync)
        {
            state.ApplyLocked(message);
        }
    }

    /// <summary>
    /// Return the aggregate snapshot for one project. Optional time window
    /// filters via since/until; the unfiltered path is O(1) over pre-computed
    /// tallies.
    /// </summary>
    public TokenAggregateResponse Aggregate(
        string workspaceRoot,
        string project,
        DateTime? since,
        DateTime? until,
        CancellationToken ct = default)
    {
        if (since is null && until is null)
        {
            var state = GetOrLoadState(workspaceRoot, project);
            return state.SnapshotFull(project);
        }
        return ComputeFiltered(workspaceRoot, project, since, until, ct);
    }

    /// <summary>Clear cached state for one (workspace, project) pair. Tests and
    /// projection-invalidation paths call this so a re-load fully rebuilds.</summary>
    public void Invalidate(string workspaceRoot, string project)
    {
        _states.TryRemove(new Key(workspaceRoot, project), out _);
    }

    private State GetOrLoadState(string workspaceRoot, string project)
    {
        return _states.GetOrAdd(new Key(workspaceRoot, project), _ => BackfillState(workspaceRoot, project));
    }

    private State BackfillState(string workspaceRoot, string project)
    {
        var state = new State();
        try
        {
            // Touch the store so its in-memory projection is loaded.
            // The store's Recent() walks the same list we want; we just
            // reuse it instead of re-reading disk.
            var all = _store.Query(workspaceRoot, project, new AgentMessageQuery());
            lock (state.Sync)
            {
                foreach (var m in all)
                {
                    if (m.Tokens != null) state.ApplyLocked(m);
                }
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "BusAggregationCache: Backfill is best-effort; an empty state is fine - future appends");
            // Backfill is best-effort; an empty state is fine - future appends
            // will populate it. We do not want a startup-time disk hiccup to
            // poison the cache.
        }
        return state;
    }

    private TokenAggregateResponse ComputeFiltered(
        string workspaceRoot,
        string project,
        DateTime? since,
        DateTime? until,
        CancellationToken ct)
    {
        var msgs = _store.Query(workspaceRoot, project, new AgentMessageQuery(Since: since, Until: until), ct);
        var local = new State();
        lock (local.Sync)
        {
            foreach (var m in msgs)
            {
                if (m.Tokens != null) local.ApplyLocked(m);
            }
        }
        return local.SnapshotFull(project) with { Since = since, Until = until };
    }

    private readonly record struct Key(string WorkspaceRoot, string Project);

    /// <summary>
    /// One per-project tally bundle. All fields are read and written under
    /// <see cref="Sync"/>; snapshotting copies the dictionaries so the lock
    /// is held briefly even when there are thousands of buckets.
    /// </summary>
    private sealed class State
    {
        public readonly object Sync = new();
        private readonly Dictionary<string, Bucket> _byModel = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Bucket> _byParticipant = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Bucket> _byDay = new(StringComparer.Ordinal);
        // Dedup set: the OnAppended hook fires AFTER the store updated its
        // projection, so a backfill triggered by GetOrLoad on the very first
        // hook call would otherwise double-count the in-flight message. We
        // pay one HashSet<string> entry per token-usage message - cheap
        // compared to the AgentMessage record we already keep in the store.
        private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
        private long _totalMessages;
        private readonly Totals _totals = new();

        public void ApplyLocked(AgentMessage m)
        {
            if (!_seenIds.Add(m.Id)) return; // already counted
            var t = StoredUsageNormalization.Normalize(m.Tokens!);
            _totalMessages++;
            _totals.Add(t);

            var modelKey = string.IsNullOrWhiteSpace(t.Model) ? "(unknown)" : t.Model!;
            GetOrAdd(_byModel, modelKey).Add(t);
            GetOrAdd(_byParticipant, m.ParticipantId).Add(t);
            var day = m.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd");
            GetOrAdd(_byDay, day).Add(t);
        }

        public TokenAggregateResponse SnapshotFull(string project)
        {
            return new TokenAggregateResponse(
                Project: project,
                TotalMessages: _totalMessages,
                Since: null,
                Until: null,
                ByModel: ToBucketList(_byModel),
                ByParticipant: ToBucketList(_byParticipant),
                ByDay: ToDayList(_byDay),
                Totals: _totals.ToWire(_totalMessages));
        }

        private static Bucket GetOrAdd(Dictionary<string, Bucket> map, string key)
        {
            if (!map.TryGetValue(key, out var b))
            {
                b = new Bucket();
                map[key] = b;
            }
            return b;
        }

        private static IReadOnlyList<TokenAggregateBucket> ToBucketList(Dictionary<string, Bucket> map)
        {
            // Sort by total tokens desc so callers get the heaviest first.
            return map
                .Select(kv => kv.Value.ToWire(kv.Key))
                .OrderByDescending(b => b.Input + b.Output + b.CacheRead + b.CacheWrite)
                .ToList();
        }

        private static IReadOnlyList<TokenAggregateBucket> ToDayList(Dictionary<string, Bucket> map)
        {
            // Days: chronological for charting.
            return map
                .Select(kv => kv.Value.ToWire(kv.Key))
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .ToList();
        }

        private sealed class Bucket
        {
            public long Input, Output, CacheRead, CacheWrite, Messages;
            public double? Dollars;
            public void Add(AgentMessageTokens t)
            {
                Input += t.Input;
                Output += t.Output;
                CacheRead += t.CacheRead ?? 0;
                CacheWrite += t.CacheWrite ?? 0;
                Messages++;
                if (t.Dollars is { } d) Dollars = (Dollars ?? 0) + d;
            }
            public TokenAggregateBucket ToWire(string key) => new(key, Input, Output, CacheRead, CacheWrite, Messages, Dollars);
        }

        private sealed class Totals
        {
            public long Input, Output, CacheRead, CacheWrite;
            public double? Dollars;
            public void Add(AgentMessageTokens t)
            {
                Input += t.Input;
                Output += t.Output;
                CacheRead += t.CacheRead ?? 0;
                CacheWrite += t.CacheWrite ?? 0;
                if (t.Dollars is { } d) Dollars = (Dollars ?? 0) + d;
            }
            public TokenAggregateTotals ToWire(long messages) => new(Input, Output, CacheRead, CacheWrite, messages, Dollars);
        }
    }
}
