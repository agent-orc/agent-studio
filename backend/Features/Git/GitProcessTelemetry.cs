using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Git;

/// <summary>
/// Ambient, per-request accounting of the two expensive things a docs/git-info
/// request does: git subprocess spawns and doc-file reads. Started as the
/// MEASURE half of AGT-2007 (git-info requests were slow and it was not obvious
/// why, because a single request quietly forks many serial git processes and on
/// Windows a bare spawn already costs ~70-100ms) and extended for AGT-2013 (the
/// wiki tree reads one file per doc-node for title extraction, so a request can
/// silently open hundreds of files). A request opens a <see cref="BeginRequest"/>
/// scope; every <see cref="GitService"/> spawn records its subcommand and
/// duration, and every doc-file read records via <see cref="RecordFileRead"/>,
/// into the ambient scope; the scope logs a rollup on dispose - "how many git
/// processes ran and how many files were read for this request, and where the
/// wall-time went".
///
/// <para>
/// The scope flows through the async/thread-pool boundary via
/// <see cref="AsyncLocal{T}"/>, so a request that fans its independent reads
/// out across the thread pool (see <c>GitService.RunGitParallel</c>) still
/// accounts every spawn against the originating request.
/// </para>
/// </summary>
public static class GitProcessTelemetry
{
    private static readonly AsyncLocal<GitRequestScope?> _current = new();

    /// <summary>
    /// A single git spawn slower than this is logged at Warning even when no
    /// request scope is active, so a pathological command (a huge diff, a
    /// hung fetch) is always visible in the logs.
    /// </summary>
    private const long SlowSpawnWarnMs = 1500;

    /// <summary>
    /// Process-wide logger for out-of-scope slow-spawn warnings. Set once from
    /// the <see cref="GitService"/> constructor; the request rollup uses the
    /// per-request logger passed to <see cref="BeginRequest"/> instead.
    /// </summary>
    internal static ILogger? Logger;

    /// <summary>
    /// Opens a per-request git measurement scope. Dispose logs the rollup:
    /// total spawn count, summed git wall-time, request wall-time, and a
    /// per-subcommand breakdown. Nestable - the inner scope restores the outer
    /// on dispose. Set <paramref name="includeNested"/> on a boundary scope when
    /// its rollup must include Git work recorded by nested feature scopes.
    /// </summary>
    public static IDisposable BeginRequest(
        string label,
        ILogger logger,
        bool includeNested = false,
        TimeProvider? timeProvider = null)
    {
        var scope = new GitRequestScope(label, logger, _current.Value, includeNested,
            timeProvider ?? TimeProvider.System);
        _current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Records one completed git spawn against the ambient request scope (a
    /// no-op when nothing is measuring) and warns on a pathologically slow
    /// individual spawn.
    /// </summary>
    internal static void Record(string command, long elapsedMs, int exitCode, bool timedOut = false)
    {
        _current.Value?.Add(command, elapsedMs, timedOut);
        if (elapsedMs >= SlowSpawnWarnMs)
        {
            Logger?.LogWarning(
                "git slow-spawn command={Command} elapsedMs={ElapsedMs} exit={Exit}",
                command, elapsedMs, exitCode);
        }
    }

    /// <summary>
    /// Records <paramref name="count"/> doc-file reads against the ambient
    /// request scope (a no-op when nothing is measuring). Callers increment only
    /// when a file is actually opened - a cache hit that skips the read must not
    /// - so the rollup's <c>files=</c> count is a faithful measure of how much
    /// disk work a request did, and drops to zero once the wiki caches are warm.
    /// </summary>
    internal static void RecordFileRead(int count = 1)
    {
        if (count > 0) _current.Value?.AddFileReads(count);
    }

    /// <summary>
    /// Diagnostic/test hook: the ambient scope's running tally
    /// (spawn count, summed git ms, doc-file reads), or null when nothing is
    /// measuring.
    /// </summary>
    internal static (int Spawns, long GitMs, int FileReads)? CurrentTally()
        => _current.Value is { } s ? (s.Spawns, s.GitMs, s.FileReads) : null;

    internal static int CurrentTimeouts() => _current.Value?.Timeouts ?? 0;

    /// <summary>
    /// Diagnostic hook for a long-running scope (the git-state indexer's
    /// per-repository run): the single subcommand that has accumulated the
    /// most wall-time so far in the ambient scope, or null when nothing is
    /// measuring or no spawn has been recorded yet.
    /// </summary>
    internal static (string Command, int Count, long Ms)? CurrentSlowestCommand()
        => _current.Value?.SlowestCommand();

    /// <summary>
    /// Rolling per-label request stats (AGT-2726): every scope rollup is also
    /// recorded here so an Admin surface can compute p50/p95 and a spawn rate
    /// without a second counter. This is purely additive bookkeeping over the
    /// same <see cref="GitRequestScope.Dispose"/> rollup already logged; nothing
    /// about the log line changes.
    ///
    /// <para>
    /// AGT-3083: counts and spawn totals come from per-minute buckets, so a busy
    /// label is counted exactly for the whole window. Only the wall-time
    /// percentiles read the bounded reservoir of the newest
    /// <see cref="PercentileSampleLimit"/> rollups. Before this, the reservoir
    /// was also the count, and a label above about 33 calls per minute was
    /// understated by the fraction of the hour it had dropped.
    /// </para>
    /// </summary>
    internal const int PercentileSampleLimit = 2000;

    /// <summary>Minute buckets older than this are dropped; it bounds memory per label.</summary>
    internal static readonly TimeSpan BucketRetention = TimeSpan.FromHours(6);

    private static readonly ConcurrentDictionary<string, LabelStats> _rollups =
        new(StringComparer.Ordinal);

    private readonly record struct RollupSample(DateTimeOffset At, long WallMs);

    private static void RecordRollup(string label, int spawns, long gitMs, long wallMs, TimeProvider timeProvider)
        => _rollups.GetOrAdd(label, static _ => new LabelStats())
            .Record(timeProvider.GetUtcNow(), spawns, gitMs, wallMs);

    /// <summary>
    /// Per-label rollup stats over the requested trailing <paramref name="window"/>,
    /// used by the Admin git-telemetry surface. <c>SampleCount</c>,
    /// <c>TotalSpawns</c> and <c>SpawnsPerMinute</c> count every rollup in the
    /// window at minute resolution (a minute that overlaps the window counts
    /// whole). p50/p95 are computed over wall time from the bounded reservoir;
    /// <c>PercentileSampleCount</c> says how many rollups they were taken from.
    /// <c>SpawnsPerMinute</c> normalizes the spawn total to a per-minute rate
    /// so windows of different length compare evenly.
    /// </summary>
    public static IReadOnlyList<EndpointStats> GetStatsSnapshot(TimeSpan window, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var cutoff = now - window;
        var results = new List<EndpointStats>();
        foreach (var (label, stats) in _rollups)
        {
            var read = stats.Read(cutoff);
            if (read.Count == 0) continue;
            var wallTimes = read.WallTimes.Select(ms => (double)ms).OrderBy(v => v).ToArray();
            var minutes = Math.Max(window.TotalMinutes, 1.0 / 60);
            results.Add(new EndpointStats(
                label,
                read.Count,
                Percentile(wallTimes, 0.50),
                Percentile(wallTimes, 0.95),
                read.Spawns,
                read.Spawns / minutes,
                wallTimes.Length));
        }
        return results.OrderByDescending(r => r.P95Ms).ToArray();
    }

    /// <summary>Exact minute totals plus the bounded percentile reservoir of one label.</summary>
    private sealed class LabelStats
    {
        private readonly object _gate = new();
        private readonly Queue<RollupSample> _reservoir = new();
        // Ordered by minute; at most one entry per minute of BucketRetention.
        private readonly LinkedList<MinuteBucket> _buckets = new();

        public void Record(DateTimeOffset at, int spawns, long gitMs, long wallMs)
        {
            var minute = MinuteOf(at);
            lock (_gate)
            {
                _reservoir.Enqueue(new RollupSample(at, wallMs));
                while (_reservoir.Count > PercentileSampleLimit) _reservoir.Dequeue();

                var bucket = _buckets.Last?.Value is { } last && last.Minute == minute ? last : null;
                if (bucket is null)
                {
                    // An out-of-order clock folds into the newest bucket instead
                    // of breaking the ordering the pruning below relies on.
                    bucket = _buckets.Last?.Value is { } newest && newest.Minute > minute
                        ? newest
                        : _buckets.AddLast(new MinuteBucket(minute)).Value;
                }
                bucket.Count++;
                bucket.Spawns += spawns;
                bucket.GitMs += gitMs;

                var oldest = minute - (long)BucketRetention.TotalMinutes;
                while (_buckets.First is { } first && first.Value.Minute < oldest) _buckets.RemoveFirst();
            }
        }

        public (int Count, int Spawns, long[] WallTimes) Read(DateTimeOffset cutoff)
        {
            var firstMinute = MinuteOf(cutoff);
            lock (_gate)
            {
                var count = 0;
                var spawns = 0;
                foreach (var bucket in _buckets)
                {
                    if (bucket.Minute < firstMinute) continue;
                    count += bucket.Count;
                    spawns += bucket.Spawns;
                }
                var wallTimes = _reservoir.Where(sample => sample.At >= cutoff)
                    .Select(sample => sample.WallMs).ToArray();
                return (count, spawns, wallTimes);
            }
        }

        public int BucketCountForTest
        {
            get { lock (_gate) return _buckets.Count; }
        }

        private static long MinuteOf(DateTimeOffset at) => at.UtcTicks / TimeSpan.TicksPerMinute;

        private sealed class MinuteBucket(long minute)
        {
            public long Minute { get; } = minute;
            public int Count;
            public int Spawns;
            public long GitMs;
        }
    }

    /// <summary>Test hook: how many minute buckets a label currently holds.</summary>
    internal static int BucketCount(string label)
        => _rollups.TryGetValue(label, out var stats) ? stats.BucketCountForTest : 0;

    /// <summary>Test/diagnostic hook: clears all recorded rollup history.</summary>
    internal static void ResetStats() => _rollups.Clear();

    private static double Percentile(double[] sortedAscending, double p)
    {
        if (sortedAscending.Length == 0) return 0;
        if (sortedAscending.Length == 1) return sortedAscending[0];
        var rank = p * (sortedAscending.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedAscending[lower];
        var fraction = rank - lower;
        return sortedAscending[lower] + (sortedAscending[upper] - sortedAscending[lower]) * fraction;
    }

    private sealed class GitRequestScope : IDisposable
    {
        private readonly string _label;
        private readonly ILogger _logger;
        private readonly GitRequestScope? _parent;
        private readonly bool _includeNested;
        private readonly TimeProvider _timeProvider;
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly Dictionary<string, (int Count, long Ms)> _byCommand = new(StringComparer.Ordinal);

        public int Spawns { get; private set; }
        public long GitMs { get; private set; }
        public int FileReads { get; private set; }
        public int Timeouts { get; private set; }

        public GitRequestScope(
            string label,
            ILogger logger,
            GitRequestScope? parent,
            bool includeNested,
            TimeProvider timeProvider)
        {
            _label = label;
            _logger = logger;
            _parent = parent;
            _includeNested = includeNested;
            _timeProvider = timeProvider;
        }

        public (string Command, int Count, long Ms)? SlowestCommand()
        {
            lock (_gate)
            {
                if (_byCommand.Count == 0) return null;
                var top = _byCommand.OrderByDescending(kv => kv.Value.Ms).First();
                return (top.Key, top.Value.Count, top.Value.Ms);
            }
        }

        public void Add(string command, long elapsedMs, bool timedOut)
        {
            AddLocal(command, elapsedMs, timedOut);
            for (var ancestor = _parent; ancestor != null; ancestor = ancestor._parent)
            {
                if (ancestor._includeNested) ancestor.AddLocal(command, elapsedMs, timedOut);
            }
        }

        private void AddLocal(string command, long elapsedMs, bool timedOut)
        {
            lock (_gate)
            {
                Spawns++;
                if (timedOut) Timeouts++;
                GitMs += elapsedMs;
                var prev = _byCommand.TryGetValue(command, out var c) ? c : (0, 0L);
                _byCommand[command] = (prev.Item1 + 1, prev.Item2 + elapsedMs);
            }
        }

        public void AddFileReads(int count)
        {
            AddFileReadsLocal(count);
            for (var ancestor = _parent; ancestor != null; ancestor = ancestor._parent)
            {
                if (ancestor._includeNested) ancestor.AddFileReadsLocal(count);
            }
        }

        private void AddFileReadsLocal(int count)
        {
            lock (_gate) FileReads += count;
        }

        public void Dispose()
        {
            _wall.Stop();
            // Restore the outer scope (if any) so nested requests are balanced.
            _current.Value = _parent;

            string breakdown;
            int spawns;
            long gitMs;
            int fileReads;
            lock (_gate)
            {
                spawns = Spawns;
                gitMs = GitMs;
                fileReads = FileReads;
                breakdown = string.Join(", ", _byCommand
                    .OrderByDescending(kv => kv.Value.Ms)
                    .Select(kv => $"{kv.Key}x{kv.Value.Count}={kv.Value.Ms}ms"));
            }

            // gitMs is the summed subprocess time; when a request fans its reads
            // out in parallel, wallMs is lower than gitMs - that gap is exactly
            // the serial time the parallelism removed. files is the doc-file read
            // count (AGT-2013): a warm wiki cache serves tree/recent/history with
            // files=0 and spawns<=1, which is the whole point of the cache layer.
            _logger.LogInformation(
                "git-info request={Label} spawns={Spawns} gitMs={GitMs} files={FileReads} wallMs={WallMs} breakdown=[{Breakdown}]",
                _label, spawns, gitMs, fileReads, _wall.ElapsedMilliseconds, breakdown);
            RecordRollup(_label, spawns, gitMs, _wall.ElapsedMilliseconds, _timeProvider);
        }
    }
}

/// <summary>
/// One request label's rollup stats over a trailing window, read by the Admin
/// git-telemetry surface. <see cref="P50Ms"/>/<see cref="P95Ms"/> are wall-time
/// percentiles; <see cref="SpawnsPerMinute"/> is the summed spawn count in the
/// window normalized to a per-minute rate. <see cref="SampleCount"/> and
/// <see cref="TotalSpawns"/> count every rollup in the window;
/// <see cref="PercentileSampleCount"/> is the number of rollups the percentiles
/// were computed from, lower than <see cref="SampleCount"/> when the bounded
/// reservoir truncated a busy label.
/// </summary>
public sealed record EndpointStats(
    string Label,
    int SampleCount,
    double P50Ms,
    double P95Ms,
    int TotalSpawns,
    double SpawnsPerMinute,
    int PercentileSampleCount);
