using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// One background Git-derived-state index per repository (AGT-2726). Owns the
/// per-repository <see cref="TaskListGitProjection"/> (merge/integration/
/// publish/test-run/review signals and reconstructed progress commits) and keeps <see cref="GitService"/>'s project
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
/// periodic sweep compares <see cref="GitRefSignature"/> and Git's effective
/// configuration as a safety net for changes the watcher missed (network
/// filesystems, included config, or ref updates outside the watch path). Cross-repository
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
    private readonly Func<string, string> _settingsVersion;
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
        TaskSessionLog sessions,
        ProjectSettingsService settings,
        TaskListGitProjectionCache projectionCache,
        ILogger<GitStateIndexService> logger,
        IConfiguration config)
        : this(
            scanner,
            watcher,
            projectionCache,
            async tasks =>
            {
                var projection = await TaskListGitProjectionCache.BuildProjectionAsync(
                    tasks, mergeStatus.BuildLookup, integrationStatus.BuildLookup,
                    publishStatus.BuildLookup, testRuns.BuildLookup,
                    reviewProjection.BuildLookup).ConfigureAwait(false);
                var commits = new Dictionary<string, IReadOnlyList<TaskCommitInfo>>(StringComparer.Ordinal);
                foreach (var task in tasks)
                {
                    if (task.State != TaskStates.Progress || task.Commits.Count > 1
                        || !JobCommitsAggregation.HasPriorPostProcessingTransition(task)) continue;
                    var enriched = JobCommitsAggregation.WithReconstructedInProgressCommits(
                        new TaskDetail { Info = task }, sessions, task.WatchPath, git).Info;
                    if (!ReferenceEquals(enriched, task)) commits[task.TaskKey] = enriched.Commits.ToArray();
                }
                return projection with
                {
                    Commits = commits,
                    Signatures = tasks.ToDictionary(task => task.TaskKey, TaskGitSignature.For, StringComparer.Ordinal),
                    SubjectVersions = tasks.ToDictionary(task => task.TaskKey,
                        task => projectionCache.SubjectVersion(task.FolderPath), StringComparer.Ordinal),
                };
            },
            projectName => git.GetProjectInventory(projectName),
            logger,
            GitStateIndexOptions.FromConfiguration(config),
            TimeProvider.System,
            projectName =>
            {
                var project = settings.Get(projectName);
                var entries = project.PublishAutomation is null ? "" : string.Join(";",
                    project.PublishAutomation.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
                return $"{project.IntegrationBranch}\0{entries}";
            })
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
        TimeProvider timeProvider,
        Func<string, string>? settingsVersion = null)
    {
        _scanner = scanner;
        _watcher = watcher;
        _projectionCache = projectionCache;
        _buildProjection = buildProjection;
        _warmInventory = warmInventory;
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider;
        _settingsVersion = settingsVersion ?? (_ => "");
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
                state.LastIndexedAt is { } at ? (now - at).TotalSeconds : null,
                state.LastErrorReason))
            .OrderBy(status => status.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DiscoverRepositories();
        _watcher.OnJobChanged += OnTaskChanged;
        _watcher.OnPathChanged += OnTaskSidecarChanged;
        try
        {
            foreach (var state in _repos.Values) RequestRefreshCore(state, "startup");

            using var timer = new PeriodicTimer(_options.SweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Git state indexer stopped");
        }
        finally
        {
            _watcher.OnJobChanged -= OnTaskChanged;
            _watcher.OnPathChanged -= OnTaskSidecarChanged;
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
        var configured = _scanner.GetWatchPaths();
        foreach (var existing in _repos.Values)
        {
            var current = configured.FirstOrDefault(entry =>
                string.Equals(entry.Name, existing.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (current is not null
                && WatchPathComparison.PathsEqual(existing.WatchPath, current.Path)
                && WatchPathComparison.PathsEqual(existing.RepositoryPath, ResolveRepositoryPath(current))) continue;
            if (_repos.TryRemove(existing.ProjectName, out var removed))
            {
                DisposeRepo(removed);
                _projectionCache.ResetRepository(removed.WatchPath, "repository-changed");
            }
        }
        foreach (var entry in configured)
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
            var paths = new[] { gitDirectory, GitRefSignature.ResolveCommonGitDirectory(gitDirectory) }
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var fsw = new FileSystemWatcher(path)
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
                state.Watchers.Add(fsw);
            }
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
            || string.Equals(name, "config", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "config.worktree", StringComparison.OrdinalIgnoreCase)
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

    private void OnTaskSidecarChanged(string path)
    {
        var name = Path.GetFileName(path);
        if (!string.Equals(name, ReviewSubjectStore.FileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, PipelineExecutionLog.FileName, StringComparison.OrdinalIgnoreCase)) return;
        if (!_projectionCache.MarkTaskInputChanged(path)) return;
        OnTaskChanged(path);
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

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        DiscoverRepositories();
        foreach (var state in _repos.Values)
        {
            await _repoSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                var refs = SafeCapture(state.RepositoryPath);
                string config;
                try { config = GitConfigSignature.Capture(state.RepositoryPath).Signature; }
                catch { config = "unavailable"; }
                if (refs == state.LastSweepSignature && config == state.LastSweepConfig
                    && _settingsVersion(state.ProjectName) == state.LastSettingsVersion) continue;
                state.LastSweepSignature = refs;
                state.LastSweepConfig = config;
                RequestRefreshCore(state, "sweep");
            }
            finally { _repoSlots.Release(); }
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
            if (state.Disposed) return;
            if (trigger != "task-event") state.InvalidationVersion++;
            if (Volatile.Read(ref state.Running) == 1)
            {
                state.RerunRequested = true;
                state.RerunTrigger = trigger;
                return;
            }
            state.PendingTrigger = trigger;
            state.DebounceTimer ??= new Timer(_ => Dispatch(state), null, Timeout.Infinite, Timeout.Infinite);
            state.DebounceTimer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Dispatch(RepoState state)
    {
        string trigger;
        lock (state.Gate)
        {
            trigger = state.PendingTrigger ?? "unknown";
            state.PendingTrigger = null;
            if (state.Disposed) return;
            if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
            {
                state.RerunRequested = true;
                state.RerunTrigger = trigger;
                return;
            }
        }
        if (trigger is not ("task-event" or "fs-watch" or "sweep"))
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
                using var deadline = new CancellationTokenSource(_options.RunDeadline);
                lock (state.Gate)
                {
                    if (state.Disposed) return;
                    state.ActiveDeadline = deadline;
                }
                using var budget = GitProcessBudget.Begin(deadline.Token);
                var tasksForRepo = ReadTasks(state);
                var effectiveConfig = GitConfigSignature.Capture(state.RepositoryPath);
                using var configScope = GitConfigScope.Begin(state.RepositoryPath, effectiveConfig.OriginUrl);
                var input = CaptureInput(state, tasksForRepo, effectiveConfig.Signature);
                if ((trigger is "task-event" or "fs-watch" or "sweep")
                    && input == state.LastPublishedInput)
                    return;
                if (trigger is "task-event" or "fs-watch" or "sweep")
                    _projectionCache.MarkRefreshing(state.WatchPath);
                long invalidation;
                lock (state.Gate) invalidation = state.InvalidationVersion;
                var projection = await _buildProjection(tasksForRepo)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                var after = CaptureInput(state, ReadTasks(state), effectiveConfig.Signature);
                bool superseded;
                DateTimeOffset gitStateAt;
                lock (state.Gate)
                {
                    superseded = state.Disposed || after != input
                        || !GitConfigSignature.FilesUnchanged(effectiveConfig)
                        || invalidation != state.InvalidationVersion;
                    gitStateAt = _timeProvider.GetUtcNow();
                    if (!superseded)
                    {
                        _projectionCache.SetSnapshot(state.WatchPath, projection, gitStateAt, input.Version);
                        state.LastIndexedAt = gitStateAt;
                        state.LastPublishedInput = input;
                        state.LastSweepSignature = input.Refs;
                        state.LastSweepConfig = input.Config;
                        state.LastSettingsVersion = input.SettingsVersion;
                        state.Failures = 0;
                        state.LastErrorReason = null;
                    }
                }
                if (superseded)
                {
                    lock (state.Gate)
                    {
                        if (state.Disposed) return;
                        _projectionCache.MarkFailed(state.WatchPath, "superseded");
                        state.RerunRequested = true;
                        state.RerunTrigger = "superseded";
                    }
                    return;
                }

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
            int retry = 0;
            lock (state.Gate)
            {
                if (!state.Disposed)
                {
                    var reason = ex is OperationCanceledException ? "timeout"
                        : ex is DirectoryNotFoundException ? "repository-unavailable"
                        : "refresh-failed";
                    state.LastErrorReason = reason;
                    _projectionCache.MarkFailed(state.WatchPath, reason);
                    if (++state.Failures <= _options.MaxRetries) retry = state.Failures;
                }
            }
            if (retry > 0) _ = RetryAfterBackoffAsync(state, retry);
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
                state.ActiveDeadline = null;
                rerun = state.RerunRequested && !state.Disposed;
                nextTrigger = state.RerunTrigger ?? "coalesced";
                state.RerunRequested = false;
                state.RerunTrigger = null;
                if (!rerun) Volatile.Write(ref state.Running, 0);
            }
            if (rerun)
            {
                _ = RunRepoIndexAsync(state, nextTrigger);
            }
        }
    }

    private TaskInfo[] ReadTasks(RepoState state) => _scanner.ScanAllJobsRaw()
        .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, state.WatchPath))
        .ToArray();

    private RepoInput CaptureInput(RepoState state, TaskInfo[] tasks, string configSignature)
    {
        var parts = new StringBuilder("schema=2;");
        foreach (var task in tasks.OrderBy(task => task.TaskKey, StringComparer.Ordinal))
        {
            parts.Append(task.TaskKey).Append(':').Append(TaskGitSignature.For(task)).Append(':');
            var subject = Path.Combine(task.FolderPath, ReviewSubjectStore.FileName);
            var pipeline = Path.Combine(task.FolderPath, PipelineExecutionLog.FileName);
            _projectionCache.SeedTaskInput(subject);
            _projectionCache.SeedTaskInput(pipeline);
            AppendFileFact(parts, subject);
            AppendFileFact(parts, pipeline);
        }
        var taskSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())));
        return new RepoInput(state.RepositoryPath, SafeCapture(state.RepositoryPath),
            configSignature, _settingsVersion(state.ProjectName), taskSignature);
    }

    private static void AppendFileFact(StringBuilder parts, string path)
    {
        var file = new FileInfo(path);
        parts.Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)
            .Append('/').Append(file.Exists ? file.Length : 0).Append(';');
    }

    private async Task RetryAfterBackoffAsync(RepoState state, int failureCount)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30000, 500 * (1 << (failureCount - 1)))))
            .ConfigureAwait(false);
        if (!state.Disposed) RequestRefreshCore(state, "retry");
    }

    private static void DisposeRepo(RepoState state)
    {
        lock (state.Gate)
        {
            state.Disposed = true;
            state.DebounceTimer?.Dispose();
            try { state.ActiveDeadline?.Cancel(); }
            catch (ObjectDisposedException) { /* run already finished */ }
        }
        foreach (var watcher in state.Watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception ex) { SilentCatch.Note(ex, "GitStateIndexService: watcher dispose"); }
        }
    }

    private sealed class RepoState(string projectName, string watchPath, string repositoryPath)
    {
        public string ProjectName { get; } = projectName;
        public string WatchPath { get; } = watchPath;
        public string RepositoryPath { get; } = repositoryPath;
        public readonly List<FileSystemWatcher> Watchers = [];
        public GitRefSignature LastSweepSignature;
        public string? LastSweepConfig;
        public string? LastSettingsVersion;
        public DateTimeOffset? LastIndexedAt;
        public readonly object Gate = new();
        public int Running;
        public bool RerunRequested;
        public string? RerunTrigger;
        public string? PendingTrigger;
        public Timer? DebounceTimer;
        public bool Disposed;
        public RepoInput? LastPublishedInput;
        public long InvalidationVersion;
        public int Failures;
        public string? LastErrorReason;
        public CancellationTokenSource? ActiveDeadline;
    }

    private sealed record RepoInput(string RepositoryPath, GitRefSignature Refs,
        string Config, string SettingsVersion, string Tasks)
    {
        public string Version => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"schema=2\0{RepositoryPath}\0{Refs}\0{Config}\0{SettingsVersion}\0{Tasks}")));
    }
}

/// <summary>One repository's index freshness, read by the Admin git-telemetry surface.</summary>
public sealed record GitStateRepositoryStatus(
    string ProjectName,
    DateTimeOffset? GitStateAt,
    bool Refreshing,
    double? AgeSeconds,
    string? ReasonCode = null);

/// <summary>Tunable knobs for <see cref="GitStateIndexService"/>, all with safe defaults.</summary>
public sealed record GitStateIndexOptions(
    int MaxConcurrentRepos,
    TimeSpan Debounce,
    TimeSpan SweepInterval,
    TimeSpan SlowRunWarnMs)
{
    public TimeSpan RunDeadline { get; init; } = TimeSpan.FromSeconds(90);
    public int MaxRetries { get; init; } = 5;
    public static GitStateIndexOptions FromConfiguration(IConfiguration config) => new(
        MaxConcurrentRepos: int.TryParse(config["GitStateIndex:MaxConcurrentRepos"], out var mc) ? Math.Clamp(mc, 1, 2) : 2,
        Debounce: TimeSpan.FromMilliseconds(
            int.TryParse(config["GitStateIndex:DebounceMs"], out var d) ? Math.Max(50, d) : 400),
        SweepInterval: TimeSpan.FromSeconds(
            int.TryParse(config["GitStateIndex:SweepIntervalSeconds"], out var s) ? Math.Max(5, s) : 45),
        SlowRunWarnMs: TimeSpan.FromMilliseconds(
            int.TryParse(config["GitStateIndex:SlowRunWarnMs"], out var w) ? Math.Max(100, w) : 5000))
    {
        MaxRetries = int.TryParse(config["GitStateIndex:MaxRetries"], out var retries)
            ? Math.Clamp(retries, 0, 5) : 5,
    };
}
