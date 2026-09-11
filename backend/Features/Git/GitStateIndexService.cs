using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Git;

/// <summary>
/// One background Git-derived-state index per repository (AGT-2726). Owns the
/// per-repository <see cref="TaskListGitProjection"/> (merge/integration/
/// publish/test-run signals) and keeps <see cref="GitService"/>'s project
/// inventory cache warm, both driven by actual repository change rather than
/// request cadence.
///
/// <para>
/// Before this service, <c>TaskListGitProjectionCache</c> recomputed the
/// whole board's projection whenever a poll landed more than two seconds
/// after the last refresh - so a busy board effectively recomputed every
/// repository every two seconds regardless of whether anything had actually
/// changed. This service instead watches each repository's refs (plus the
/// Task Server's own <see cref="TaskWatcherService.OnJobChanged"/> events -
/// the delivery/integration writes the platform itself produces), debounces
/// bursts, and single-flights concurrent triggers per repository. A slow
/// periodic sweep compares a cheap filesystem <see cref="GitRefSignature"/> as
/// a safety net for changes the watcher missed (network filesystems, git
/// operations that bypass the normal ref-update path). Cross-repository
/// concurrency is bounded so a workspace with many repositories cannot fan out
/// unbounded git processes at once.
/// </para>
///
/// <para>
/// Request paths never call into this service to compute anything: they read
/// <see cref="TaskListGitProjectionCache"/>'s last snapshot and
/// <see cref="GitService.GetProjectInventory"/>'s last snapshot, both of which
/// this service is the sole writer for (inventory by re-using
/// <c>GetProjectInventory</c>'s own signature-gated enqueue - this service
/// only makes that enqueue change-driven instead of request-driven).
/// </para>
/// </summary>
public sealed class GitStateIndexService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskWatcherService _watcher;
    private readonly TaskListGitProjectionCache _projectionCache;
    private readonly Func<IReadOnlyCollection<TaskInfo>, Task<TaskListGitProjection>> _buildProjection;
    private readonly Action<string> _warmInventory;
    private readonly ILogger _logger;
    private readonly GitStateIndexOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _repoSlots;

    private readonly ConcurrentDictionary<string, RepoState> _repos =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a repository's index run completes with a fresh snapshot.</summary>
    public event Action<string, DateTimeOffset>? RepositoryIndexed;

    public GitStateIndexService(
        GitService git,
        TaskScannerService scanner,
        TaskWatcherService watcher,
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        AgentStudio.Review.ReviewProjectionService reviewProjection,
        TaskListGitProjectionCache projectionCache,
        ILogger<GitStateIndexService> logger,
        IConfiguration config)
        : this(
            scanner,
            watcher,
            projectionCache,
            tasks => TaskListGitProjectionCache.BuildProjectionAsync(
                tasks,
                mergeStatus.BuildLookup,
                integrationStatus.BuildLookup,
                publishStatus.BuildLookup,
                testRuns.BuildLookup,
                reviewProjection.BuildLookup),
            projectName => git.GetProjectInventory(projectName),
            logger,
            GitStateIndexOptions.FromConfiguration(config),
            TimeProvider.System)
    {
    }

    internal GitStateIndexService(
        TaskScannerService scanner,
        TaskWatcherService watcher,
        TaskListGitProjectionCache projectionCache,
        Func<IReadOnlyCollection<TaskInfo>, Task<TaskListGitProjection>> buildProjection,
        Action<string> warmInventory,
        ILogger logger,
        GitStateIndexOptions options,
        TimeProvider timeProvider)
    {
        _scanner = scanner;
        _watcher = watcher;
        _projectionCache = projectionCache;
        _buildProjection = buildProjection;
        _warmInventory = warmInventory;
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider;
        _repoSlots = new SemaphoreSlim(options.MaxConcurrentRepos, options.MaxConcurrentRepos);
    }

    /// <summary>Test/diagnostic hook: the set of repositories currently known to the indexer.</summary>
    internal IReadOnlyCollection<string> KnownProjects => _repos.Keys.ToArray();

    /// <summary>Test/diagnostic hook: is a run currently in flight for this project.</summary>
    internal bool IsRunning(string projectName)
        => _repos.TryGetValue(projectName, out var state) && Volatile.Read(ref state.Running) == 1;

    /// <summary>
    /// Per-repository status for the Admin git-telemetry surface: the last
    /// successful index timestamp (null before the first run completes) and
    /// whether a run is currently in flight for it.
    /// </summary>
    public IReadOnlyList<GitStateRepositoryStatus> GetRepositoryStatuses(TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? _timeProvider).GetUtcNow();
        return _repos.Values
            .Select(state => new GitStateRepositoryStatus(
                state.ProjectName,
                state.LastIndexedAt,
                Volatile.Read(ref state.Running) == 1,
                state.LastIndexedAt is { } at ? (now - at).TotalSeconds : null))
            .OrderBy(status => status.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DiscoverRepositories();
        _watcher.OnJobChanged += OnTaskChanged;
        try
        {
            foreach (var state in _repos.Values) RequestRefreshCore(state, "startup");

            using var timer = new PeriodicTimer(_options.SweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Git state indexer stopped");
        }
        finally
        {
            _watcher.OnJobChanged -= OnTaskChanged;
            foreach (var state in _repos.Values) DisposeRepo(state);
        }
    }

    /// <summary>
    /// Enqueues a debounced, single-flight-coalesced refresh for a known
    /// project. Public so API mutation paths that already know they just
    /// changed a repository's ref state (accept/publish/reissue) can prime
    /// the index immediately instead of waiting for the sweep or a watcher
    /// event.
    /// </summary>
    public void RequestRefresh(string projectName, string trigger)
    {
        if (_repos.TryGetValue(projectName, out var state)) RequestRefreshCore(state, trigger);
    }

    private void DiscoverRepositories()
    {
        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (_repos.ContainsKey(entry.Name)) continue;
            var repositoryPath = ResolveRepositoryPath(entry);
            if (string.IsNullOrWhiteSpace(repositoryPath)) continue;

            var state = new RepoState(entry.Name, entry.Path, repositoryPath)
            {
                LastSweepSignature = SafeCapture(repositoryPath),
            };
            if (_repos.TryAdd(entry.Name, state))
            {
                SetupWatcher(state);
            }
        }
    }

    private static string? ResolveRepositoryPath(WatchPathEntry entry)
        => !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath
            : !string.IsNullOrWhiteSpace(entry.RootPath) ? entry.RootPath
            : null;

    private static GitRefSignature SafeCapture(string repositoryPath)
    {
        try { return GitRefSignature.Capture(repositoryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitStateIndexService: initial ref signature capture failed");
            return default;
        }
    }

    private void SetupWatcher(RepoState state)
    {
        var gitDirectory = GitRefSignature.ResolveGitDirectory(state.RepositoryPath);
        if (string.IsNullOrWhiteSpace(gitDirectory) || !Directory.Exists(gitDirectory)) return;

        try
        {
            var fsw = new FileSystemWatcher(gitDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024,
            };
            fsw.Changed += (_, e) => OnGitDirectoryEvent(state, e.FullPath);
            fsw.Created += (_, e) => OnGitDirectoryEvent(state, e.FullPath);
            fsw.Deleted += (_, e) => OnGitDirectoryEvent(state, e.FullPath);
            fsw.Renamed += (_, e) => OnGitDirectoryEvent(state, e.FullPath);
            fsw.Error += (_, e) => _logger.LogDebug(
                e.GetException(), "git-state-watcher-error repository={Repository}", state.ProjectName);
            fsw.EnableRaisingEvents = true;
            state.Watcher = fsw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start git-state watcher for {Project} at {GitDir}", state.ProjectName, gitDirectory);
        }
    }

    private void OnGitDirectoryEvent(RepoState state, string path)
    {
        var name = Path.GetFileName(path);
        var sep = Path.DirectorySeparatorChar;
        var altSep = Path.AltDirectorySeparatorChar;
        var relevant =
            string.Equals(name, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "packed-refs", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}refs{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{altSep}refs{altSep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}worktrees{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{altSep}worktrees{altSep}", StringComparison.OrdinalIgnoreCase);
        if (!relevant) return;
        RequestRefreshCore(state, "fs-watch");
    }

    private void OnTaskChanged(string path)
    {
        foreach (var state in _repos.Values)
        {
            if (IsUnderWatchPath(state.WatchPath, path))
            {
                RequestRefreshCore(state, "task-event");
                return;
            }
        }
    }

    private static bool IsUnderWatchPath(string watchPath, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(watchPath, path);
            return relative != "." && !relative.StartsWith("..") && !Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "GitStateIndexService: watch-path relative-path check failed");
            return false;
        }
    }

    private void Sweep()
    {
        DiscoverRepositories();
        foreach (var state in _repos.Values)
        {
            var signature = SafeCapture(state.RepositoryPath);
            if (signature == state.LastSweepSignature) continue;
            state.LastSweepSignature = signature;
            RequestRefreshCore(state, "sweep");
        }
    }

    /// <summary>
    /// Debounces a trigger for one repository (a burst of ref-file writes or
    /// task-folder events collapses to one run after a quiet window), and
    /// single-flights it against a run already executing: a trigger that
    /// arrives mid-run does not start a second one, it marks a rerun that
    /// fires immediately once the in-flight run finishes.
    /// </summary>
    private void RequestRefreshCore(RepoState state, string trigger)
    {
        lock (state.Gate)
        {
            state.PendingTrigger = trigger;
            state.DebounceTimer ??= new Timer(_ => Dispatch(state), null, Timeout.Infinite, Timeout.Infinite);
            if (state.Disposed) return;
            state.DebounceTimer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Dispatch(RepoState state)
    {
        string trigger;
        lock (state.Gate)
        {
            trigger = state.PendingTrigger ?? "unknown";
            if (state.Disposed) return;
            if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
            {
                state.RerunRequested = true;
                state.RerunTrigger = trigger;
                return;
            }
        }
        _projectionCache.MarkRefreshing(state.WatchPath);
        _ = RunRepoIndexAsync(state, trigger);
    }

    private async Task RunRepoIndexAsync(RepoState state, string trigger)
    {
        await _repoSlots.WaitAsync().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var spawns = 0;
        (string Command, int Count, long Ms)? slowest = null;
        try
        {
            using (GitProcessTelemetry.BeginRequest("git-index-run", _logger, includeNested: true, _timeProvider))
            {
                var tasksForRepo = _scanner.ScanAllJobs()
                    .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, state.WatchPath))
                    .ToArray();
                var projection = await _buildProjection(tasksForRepo).ConfigureAwait(false);
                var gitStateAt = _timeProvider.GetUtcNow();
                _projectionCache.SetSnapshot(state.WatchPath, projection, gitStateAt);
                state.LastIndexedAt = gitStateAt;

                // Re-uses GitService's own signature-gated cache/queue for
                // inventory; this call only makes that enqueue change-driven
                // instead of waiting for the next request to notice staleness.
                _warmInventory(state.ProjectName);

                var tally = GitProcessTelemetry.CurrentTally();
                spawns = tally?.Spawns ?? 0;
                slowest = GitProcessTelemetry.CurrentSlowestCommand();
                RepositoryIndexed?.Invoke(state.ProjectName, gitStateAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git-index-run-failed repository={Repository} trigger={Trigger}", state.ProjectName, trigger);
        }
        finally
        {
            stopwatch.Stop();
            _repoSlots.Release();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation(
                "git-index-run repository={Repository} spawns={Spawns} ms={ElapsedMs} trigger={Trigger}",
                state.ProjectName, spawns, elapsedMs, trigger);
            if (elapsedMs >= _options.SlowRunWarnMs.TotalMilliseconds)
            {
                _logger.LogWarning(
                    "git-index-run-slow repository={Repository} ms={ElapsedMs} slowestCommand={Command} slowestCount={Count} slowestMs={SlowMs}",
                    state.ProjectName, elapsedMs, slowest?.Command ?? "n/a", slowest?.Count ?? 0, slowest?.Ms ?? 0);
            }

            bool rerun;
            string nextTrigger;
            lock (state.Gate)
            {
                rerun = state.RerunRequested;
                nextTrigger = state.RerunTrigger ?? "coalesced";
                state.RerunRequested = false;
                state.RerunTrigger = null;
                if (!rerun) Volatile.Write(ref state.Running, 0);
            }
            if (rerun)
            {
                _projectionCache.MarkRefreshing(state.WatchPath);
                _ = RunRepoIndexAsync(state, nextTrigger);
            }
        }
    }

    private static void DisposeRepo(RepoState state)
    {
        lock (state.Gate)
        {
            state.Disposed = true;
            state.DebounceTimer?.Dispose();
        }
        try { state.Watcher?.Dispose(); }
        catch (Exception ex) { SilentCatch.Note(ex, "GitStateIndexService: watcher dispose"); }
    }

    private sealed class RepoState(string projectName, string watchPath, string repositoryPath)
    {
        public string ProjectName { get; } = projectName;
        public string WatchPath { get; } = watchPath;
        public string RepositoryPath { get; } = repositoryPath;
        public FileSystemWatcher? Watcher;
        public GitRefSignature LastSweepSignature;
        public DateTimeOffset? LastIndexedAt;
        public readonly object Gate = new();
        public int Running;
        public bool RerunRequested;
        public string? RerunTrigger;
        public string? PendingTrigger;
        public Timer? DebounceTimer;
        public bool Disposed;
    }
}

/// <summary>One repository's index freshness, read by the Admin git-telemetry surface.</summary>
public sealed record GitStateRepositoryStatus(
    string ProjectName,
    DateTimeOffset? GitStateAt,
    bool Refreshing,
    double? AgeSeconds);

/// <summary>Tunable knobs for <see cref="GitStateIndexService"/>, all with safe defaults.</summary>
public sealed record GitStateIndexOptions(
    int MaxConcurrentRepos,
    TimeSpan Debounce,
    TimeSpan SweepInterval,
    TimeSpan SlowRunWarnMs)
{
    public static GitStateIndexOptions FromConfiguration(IConfiguration config) => new(
        MaxConcurrentRepos: int.TryParse(config["GitStateIndex:MaxConcurrentRepos"], out var mc) ? Math.Max(1, mc) : 2,
        Debounce: TimeSpan.FromMilliseconds(
            int.TryParse(config["GitStateIndex:DebounceMs"], out var d) ? Math.Max(50, d) : 400),
        SweepInterval: TimeSpan.FromSeconds(
            int.TryParse(config["GitStateIndex:SweepIntervalSeconds"], out var s) ? Math.Max(5, s) : 45),
        SlowRunWarnMs: TimeSpan.FromMilliseconds(
            int.TryParse(config["GitStateIndex:SlowRunWarnMs"], out var w) ? Math.Max(100, w) : 5000));
}
