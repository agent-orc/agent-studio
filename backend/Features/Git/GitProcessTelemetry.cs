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
    /// The rolling one-hour view of the same samples, for the Admin SLO panel
    /// (AGT-2726). This is a projection of what is already recorded here, not a
    /// second counter: every entry is written from <see cref="Record"/> and from
    /// the scope rollup below.
    /// </summary>
    internal static readonly GitPerformanceWindow Window = new();

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
        Window.RecordSpawn(DateTimeOffset.UtcNow);
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
    /// The ambient scope's slowest single subcommand so far, or null when
    /// nothing is measuring or nothing has run. The background git index reports
    /// it on a run that exceeds its budget - "this repository was slow" is not
    /// actionable without "and this is the command that made it slow".
    /// </summary>
    internal static (string Command, long Ms)? CurrentSlowest()
        => _current.Value?.Slowest();

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

        public (string Command, long Ms)? Slowest()
        {
            lock (_gate)
            {
                if (_byCommand.Count == 0) return null;
                var slowest = _byCommand.MaxBy(kv => kv.Value.Ms);
                return (slowest.Key, slowest.Value.Ms);
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

            // Same sample, second consumer: the Admin SLO panel reads p50/p95
            // per endpoint from this window instead of re-deriving them by
            // parsing the log line above.
            Window.RecordRequest(_label, DateTimeOffset.UtcNow, _wall.ElapsedMilliseconds, spawns);
        }
    }
}
