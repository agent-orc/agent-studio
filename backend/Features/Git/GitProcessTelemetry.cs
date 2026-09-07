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
        bool includeNested = false)
    {
        var scope = new GitRequestScope(label, logger, _current.Value, includeNested);
        _current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Records one completed git spawn against the ambient request scope (a
    /// no-op when nothing is measuring) and warns on a pathologically slow
    /// individual spawn.
    /// </summary>
    internal static void Record(string command, long elapsedMs, int exitCode)
    {
        _current.Value?.Add(command, elapsedMs);
        // Every spawn also lands in the process-wide window, including the ones
        // no request scope is measuring. The Admin spawn budget has to count
        // them all, and this stays the single source: no second counter.
        RecordSpawnInWindow();
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

    /// <summary>
    /// The subcommand that consumed the most wall time in the ambient scope, or
    /// null when nothing is measuring or nothing has spawned. The git index logs
    /// it for any run over its slow threshold, which is the first question an
    /// operator asks about a five-second capture.
    /// </summary>
    internal static (string Command, int Count, long Ms)? CurrentSlowestCommand()
        => _current.Value?.SlowestCommand();

    // ----- Process-wide rolling window (AGT-2726) -----

    /// <summary>Reporting window for the Admin performance panel.</summary>
    internal static readonly TimeSpan WindowDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Hard ceilings so a runaway hour cannot grow the window without bound.
    /// Both are far above the measured load (about 4,000 spawns and 1,700
    /// request scopes an hour) and only ever drop the oldest samples.
    /// </summary>
    private const int RequestWindowCapacity = 20_000;
    private const int SpawnWindowCapacity = 100_000;

    private static readonly object _windowGate = new();
    private static readonly Queue<GitTelemetrySample> _requestWindow = new();
    private static readonly Queue<DateTimeOffset> _spawnWindow = new();

    /// <summary>Test seam: the clock the rolling window stamps samples with.</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Test seam: drops every sample so one test cannot see another's load.</summary>
    internal static void ResetWindow()
    {
        lock (_windowGate)
        {
            _requestWindow.Clear();
            _spawnWindow.Clear();
        }
    }

    private static void RecordSpawnInWindow()
    {
        var now = Clock.GetUtcNow();
        lock (_windowGate)
        {
            _spawnWindow.Enqueue(now);
            Trim(now);
        }
    }

    private static void RecordRequestInWindow(GitTelemetrySample sample)
    {
        lock (_windowGate)
        {
            _requestWindow.Enqueue(sample);
            Trim(sample.CompletedAtUtc);
        }
    }

    private static void Trim(DateTimeOffset now)
    {
        var cutoff = now - WindowDuration;
        while (_requestWindow.Count > 0
               && (_requestWindow.Peek().CompletedAtUtc < cutoff || _requestWindow.Count > RequestWindowCapacity))
            _requestWindow.Dequeue();
        while (_spawnWindow.Count > 0
               && (_spawnWindow.Peek() < cutoff || _spawnWindow.Count > SpawnWindowCapacity))
            _spawnWindow.Dequeue();
    }

    /// <summary>
    /// Per-label latency percentiles and the spawn rate over the last
    /// <see cref="WindowDuration"/>. This is the source the Admin page reads;
    /// it is the same accounting the log rollup uses, not a parallel counter.
    /// </summary>
    internal static GitTelemetrySummary Summarize()
    {
        var now = Clock.GetUtcNow();
        var cutoff = now - WindowDuration;
        GitTelemetrySample[] samples;
        int spawns;
        lock (_windowGate)
        {
            Trim(now);
            samples = _requestWindow.ToArray();
            spawns = _spawnWindow.Count;
        }

        var endpoints = samples
            .GroupBy(sample => sample.Label, StringComparer.Ordinal)
            .Select(group =>
            {
                var durations = group.Select(sample => sample.WallMs).OrderBy(value => value).ToArray();
                return new GitTelemetryEndpointSummary(
                    group.Key,
                    durations.Length,
                    Percentile(durations, 50),
                    Percentile(durations, 95),
                    group.Sum(sample => sample.Spawns),
                    group.Sum(sample => sample.GitMs));
            })
            .OrderByDescending(summary => summary.P95Ms)
            .ToList();

        var minutes = WindowDuration.TotalMinutes;
        return new GitTelemetrySummary(
            cutoff,
            now,
            endpoints,
            spawns,
            minutes <= 0 ? 0 : spawns / minutes);
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending sample set. Pure so the edge
    /// cases (empty, single sample, exact boundary) are a direct matrix test.
    /// </summary>
    internal static double Percentile(IReadOnlyList<long> ascending, double percentile)
    {
        if (ascending.Count == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100d * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    private sealed class GitRequestScope : IDisposable
    {
        private readonly string _label;
        private readonly ILogger _logger;
        private readonly GitRequestScope? _parent;
        private readonly bool _includeNested;
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly Dictionary<string, (int Count, long Ms)> _byCommand = new(StringComparer.Ordinal);

        public int Spawns { get; private set; }
        public long GitMs { get; private set; }
        public int FileReads { get; private set; }

        public GitRequestScope(
            string label,
            ILogger logger,
            GitRequestScope? parent,
            bool includeNested)
        {
            _label = label;
            _logger = logger;
            _parent = parent;
            _includeNested = includeNested;
        }

        public void Add(string command, long elapsedMs)
        {
            AddLocal(command, elapsedMs);
            for (var ancestor = _parent; ancestor != null; ancestor = ancestor._parent)
            {
                if (ancestor._includeNested) ancestor.AddLocal(command, elapsedMs);
            }
        }

        private void AddLocal(string command, long elapsedMs)
        {
            lock (_gate)
            {
                Spawns++;
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

        public (string Command, int Count, long Ms)? SlowestCommand()
        {
            lock (_gate)
            {
                if (_byCommand.Count == 0) return null;
                var slowest = _byCommand.MaxBy(entry => entry.Value.Ms);
                return (slowest.Key, slowest.Value.Count, slowest.Value.Ms);
            }
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

            RecordRequestInWindow(new GitTelemetrySample(
                _label,
                _wall.ElapsedMilliseconds,
                spawns,
                gitMs,
                Clock.GetUtcNow()));
        }
    }
}

/// <summary>One completed request scope, retained for the rolling window.</summary>
internal sealed record GitTelemetrySample(
    string Label,
    long WallMs,
    int Spawns,
    long GitMs,
    DateTimeOffset CompletedAtUtc);

/// <summary>Latency and spawn cost of one telemetry label over the window.</summary>
public sealed record GitTelemetryEndpointSummary(
    string Label,
    int Calls,
    double P50Ms,
    double P95Ms,
    int Spawns,
    long GitMs);

/// <summary>The rolling-window rollup the Admin performance panel renders.</summary>
public sealed record GitTelemetrySummary(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<GitTelemetryEndpointSummary> Endpoints,
    int Spawns,
    double SpawnsPerMinute);
