using System.Collections.Concurrent;
using System.Data.Common;
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
///
/// <para>
/// AGT-3322 (Git facts S2): this service is also the only writer of the Git
/// fact store (<see cref="GitFactStore"/>). Each repository generation is
/// committed in one transaction before the in-memory pointer is swapped. On
/// start the last stored generation is served at once as <c>restored</c> and
/// confirmed from files alone (ref fingerprint, configuration sources,
/// settings, task inputs) before any Git runs; only a moved input starts a
/// normal run. <c>git config</c> runs only after a configuration watcher event
/// or a changed configuration file, so a task event that changes no card input
/// starts no Git process.
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
    private readonly GitOriginStore? _originStore;
    private readonly Func<string, string?> _originRoot;
    private readonly GitFactStore? _factStore;
    private readonly Func<string, IReadOnlyList<string>> _fingerprintBranches;
    private readonly string _producerId;
    private readonly TaskCompletionSource _restoration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const string SweepConfigUnavailable = "unavailable";
    private const string SweepConfigSourcesChanged = "sources-changed";

    private readonly ConcurrentDictionary<string, RepoState> _repos =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a repository's index run completes with a fresh snapshot.</summary>
    public event Action<string, DateTimeOffset>? RepositoryIndexed;

    /// <summary>Test/diagnostic hook: one report per finished index run.</summary>
    internal event Action<GitIndexRunReport>? RunCompleted;

    /// <summary>Completes once the stored generations were loaded on start.</summary>
    internal Task Restoration => _restoration.Task;

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
        GitOriginStore originStore,
        GitFactStore factStore,
        ILogger<GitStateIndexService> logger,
        IConfiguration config)
        : this(
            scanner,
            watcher,
            projectionCache,
            async tasks =>
            {
                tasks = TaskProjectionCards.Select(tasks, logger);
                var projection = await TaskListGitProjectionCache.BuildProjectionAsync(
                    tasks, mergeStatus.BuildLookup, integrationStatus.BuildLookup,
                    publishStatus.BuildLookup, testRuns.BuildLookup,
                    reviewProjection.BuildLookup, logger).ConfigureAwait(false);
                var commits = new Dictionary<string, IReadOnlyList<TaskCommitInfo>>(StringComparer.Ordinal);
                var unavailable = projection.UnavailableTasks.ToHashSet(StringComparer.Ordinal);
                foreach (var task in tasks)
                {
                    try
                    {
                        if (task.State != TaskStates.Progress || task.Commits.Count > 1
                            || !JobCommitsAggregation.HasPriorPostProcessingTransition(task)) continue;
                        var enriched = JobCommitsAggregation.WithReconstructedInProgressCommits(
                            new TaskDetail { Info = task }, sessions, task.WatchPath, git).Info;
                        if (!ReferenceEquals(enriched, task)) commits[task.TaskKey] = enriched.Commits.ToArray();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        unavailable.Add(task.TaskKey);
                        logger.LogWarning(ex, "git-index-card-unavailable taskKey={TaskKey}", task.TaskKey);
                    }
                }
                return projection with
                {
                    Commits = commits,
                    Unavailable = unavailable,
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
            },
            originStore,
            // Publish under the same root TaskIntegrationStatusService asks for.
            git.ResolveRepoRootForWatchPath,
            factStore,
            projectName => settings.Get(projectName).IntegrationBranch is { Length: > 0 } branch ? [branch] : [])
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
        Func<string, string>? settingsVersion = null,
        GitOriginStore? originStore = null,
        Func<string, string?>? originRoot = null,
        GitFactStore? factStore = null,
        Func<string, IReadOnlyList<string>>? fingerprintBranches = null,
        string? producerId = null)
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
        _originStore = originStore;
        _originRoot = originRoot ?? (_ => null);
        _factStore = factStore;
        _fingerprintBranches = fingerprintBranches ?? (_ => []);
        _producerId = producerId ?? $"backend:{Environment.MachineName}:{Environment.ProcessId}";
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
        // Read each repository's failure fields under its gate: a failed run
        // sets the reason before it counts the failure, so an unlocked read
        // could report "refresh-failed" with zero consecutive failures.
        return _repos.Values
            .Select(state =>
            {
                lock (state.Gate)
                {
                    return new GitStateRepositoryStatus(
                        state.ProjectName,
                        state.LastIndexedAt,
                        Volatile.Read(ref state.Running) == 1,
                        state.LastIndexedAt is { } at ? (now - at).TotalSeconds : null,
                        state.LastErrorReason,
                        state.Failures,
                        state.LastError,
                        state.FailureSince,
                        HealthState(state),
                        state.StoreGeneration,
                        state.ConfigGeneration,
                        state.ProducerEpoch,
                        SpawnsPerMinute(state, now));
                }
            })
            .OrderBy(status => status.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            OpenFactStore();
            DiscoverRepositories();
        }
        finally { _restoration.TrySetResult(); }
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

    private void OpenFactStore()
    {
        if (_factStore is null) return;
        var outcome = _factStore.Open();
        _logger.LogInformation("git-facts-store-open outcome={Outcome} path={Path}",
            outcome, _factStore.DatabasePath ?? "none");
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
                Restore(state);
                SetupWatcher(state);
            }
        }
    }

    /// <summary>
    /// Cold start: takes the producer lease and serves the last stored
    /// generation as <c>restored</c> before any Git runs. A relocated checkout
    /// or workspace is not served. The first run then confirms it from files
    /// (<see cref="TryConfirmRestored"/>) or recomputes.
    /// </summary>
    private void Restore(RepoState state)
    {
        if (_factStore is not { IsAvailable: true }) return;
        try
        {
            var epoch = _factStore.AcquireProducerEpoch(state.ProjectName, _producerId);
            lock (state.Gate) state.ProducerEpoch = epoch;
            var snapshot = _factStore.LoadCurrent(state.ProjectName);
            if (snapshot is null) return;
            if (!GitFactRestorePolicy.CanServe(snapshot.Repository.CanonicalRoot, snapshot.Repository.WorkspaceRoot,
                    state.RepositoryPath, state.WatchPath, WatchPathComparison.PathsEqual))
            {
                _logger.LogInformation("git-facts-restore-skipped repository={Repository} reason=relocated",
                    state.ProjectName);
                return;
            }
            var projection = CardGitFactsCodec.ToProjection(snapshot.Cards, out var unreadable);
            var generation = snapshot.Generation;
            if (!_projectionCache.SetRestored(state.WatchPath, projection, generation.PublishedAt,
                    generation.InputVersion)) return;
            lock (state.Gate)
            {
                state.Config = snapshot.Repository.Config;
                state.ConfigGeneration = snapshot.Repository.ConfigGeneration;
                state.StoreGeneration = generation.Generation;
                state.LastIndexedAt = generation.PublishedAt;
                state.RestoredServing = true;
                state.PendingRestore = new PendingRestore(new GitFactGenerationInput(generation.RefFingerprint,
                    generation.ConfigGeneration, generation.SettingsVersion, generation.TaskInputSignature),
                    unreadable == 0);
            }
            if (snapshot.Repository.Config is { } config && GitConfigSignature.SourcesUnchanged(config))
                PublishOrigin(state, config);
            _logger.LogInformation(
                "git-facts-restored repository={Repository} generation={Generation} cards={Cards} unreadable={Unreadable} ageSeconds={AgeSeconds}",
                state.ProjectName, generation.Generation, snapshot.Cards.Count, unreadable,
                (_timeProvider.GetUtcNow() - generation.PublishedAt).TotalSeconds);
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "git-facts-restore-failed repository={Repository}", state.ProjectName);
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
                .Distinct(FileSystemPathComparer.Instance);
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
        var paths = FileSystemPathComparer.Instance;
        var relevant =
            paths.Equals(name, "HEAD")
            || paths.Equals(name, "packed-refs")
            || paths.Equals(name, "config")
            || paths.Equals(name, "config.worktree")
            || path.Contains($"{sep}refs{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{altSep}refs{altSep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}worktrees{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{altSep}worktrees{altSep}", StringComparison.OrdinalIgnoreCase);
        if (!relevant) return;
        // AGT-3322: only a configuration event (or HEAD under an onbranch
        // include) makes the next run resolve Git's configuration again.
        var configEvent = paths.Equals(name, "config") || paths.Equals(name, "config.worktree");
        lock (state.Gate)
        {
            if (configEvent || paths.Equals(name, "HEAD") && state.Config?.DependsOnHead == true)
                state.ConfigDirty = true;
        }
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
        if (!IsGitRelevantSidecar(path)) return;
        if (!_projectionCache.MarkTaskInputChanged(path)) return;
        OnTaskChanged(path);
    }

    internal static bool IsGitRelevantSidecar(string path)
        => FileSystemPathComparer.Instance.Equals(Path.GetFileName(path), ReviewSubjectStore.FileName);

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
        // The safety sweep observes every repository, but the task tree only
        // needs one disk scan for the whole pass.
        var allTasks = _scanner.ScanAllJobsRaw();
        foreach (var state in _repos.Values)
        {
            await _repoSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                var refs = SafeCapture(state.RepositoryPath);
                string config;
                try
                {
                    // Files only (AGT-3322): a changed or newly created
                    // configuration source queues a run that resolves Git's
                    // configuration again; an unchanged one starts no Git.
                    GitConfigSignature.EnsureRepository(state.RepositoryPath);
                    GitEffectiveConfig? known;
                    lock (state.Gate) known = state.Config;
                    if (known is not null && GitConfigSignature.SourcesUnchanged(known))
                    {
                        config = known.Signature;
                    }
                    else
                    {
                        lock (state.Gate) state.ConfigDirty = true;
                        config = SweepConfigSourcesChanged;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A missing checkout or unreadable config is a typed sweep
                    // outcome: the changed value queues one refresh, whose run
                    // publishes the bounded reason code and keeps the last snapshot.
                    _logger.LogDebug(ex, "git-state-sweep-config-unavailable repository={Repository}",
                        state.ProjectName);
                    config = SweepConfigUnavailable;
                }
                string taskSignature;
                try
                {
                    var repoTasks = allTasks.Where(task =>
                        WatchPathComparison.PathsEqual(task.WatchPath, state.WatchPath)).ToArray();
                    taskSignature = CaptureTaskInputSignature(repoTasks, _projectionCache);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "git-state-sweep-task-input-unavailable repository={Repository}",
                        state.ProjectName);
                    RequestRefreshCore(state, "sweep");
                    continue;
                }
                if (Volatile.Read(ref state.LastErrorReason) is null
                    && refs == state.LastSweepSignature && config == state.LastSweepConfig
                    && _settingsVersion(state.ProjectName) == state.LastSettingsVersion
                    && taskSignature == state.LastPublishedInput?.Tasks) continue;
                state.LastSweepSignature = refs;
                state.LastSweepConfig = config;
                RequestRefreshCore(state, "sweep");
            }
            finally { _repoSlots.Release(); }
        }
    }

    /// <summary>
    /// AGT-3083: every capture the index pays for also refreshes the shared
    /// origin store, so integration lookups need no <c>git config</c> of their
    /// own. A config source created after an earlier capture is one of the
    /// capture's absent sources, so the store resolves it again once it
    /// appears (AGT-3322).
    /// </summary>
    private void PublishOrigin(RepoState state, GitEffectiveConfig config)
    {
        if (_originStore is null) return;
        string? root = null;
        try { root = _originRoot(state.WatchPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SilentCatch.Note(ex, "GitStateIndexService: origin root resolution failed");
        }
        _originStore.Publish(string.IsNullOrWhiteSpace(root) ? state.RepositoryPath : root, config);
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
            // A restored generation is confirmed or replaced by this run; it
            // is not marked refreshing before the run knows which.
            if (trigger is not ("task-event" or "fs-watch" or "sweep") && state.PendingRestore is null)
                _projectionCache.MarkRefreshing(state.WatchPath);
        }
        _ = RunRepoIndexAsync(state, trigger);
    }

    private async Task RunRepoIndexAsync(RepoState state, string trigger)
    {
        await _repoSlots.WaitAsync().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var spawns = 0;
        (string Command, int Count, long Ms)? slowest = null;
        var run = new RunContext();
        try
        {
            using (GitProcessTelemetry.BeginRequest("git-index-run", _logger, includeNested: true, _timeProvider))
            {
                try
                {
                    run.Outcome = await IndexAsync(state, trigger, run).ConfigureAwait(false);
                }
                finally
                {
                    spawns = GitProcessTelemetry.CurrentTally()?.Spawns ?? 0;
                    slowest = GitProcessTelemetry.CurrentSlowestCommand();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git-index-run-failed repository={Repository} trigger={Trigger}", state.ProjectName, trigger);
            run.Outcome = "failed";
            var reason = ex is OperationCanceledException ? "timeout"
                : ex is DirectoryNotFoundException ? "repository-unavailable"
                : "refresh-failed";
            var retry = RecordFailure(state, reason, ex.ToString(), run.Attempted);
            if (retry > 0) _ = RetryAfterBackoffAsync(state, retry);
        }
        finally
        {
            stopwatch.Stop();
            _repoSlots.Release();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation(
                "git-index-run repository={Repository} spawns={Spawns} ms={ElapsedMs} trigger={Trigger} outcome={Outcome}",
                state.ProjectName, spawns, elapsedMs, trigger, run.Outcome);
            if (elapsedMs >= _options.SlowRunWarnMs.TotalMilliseconds)
            {
                _logger.LogWarning(
                    "git-index-run-slow repository={Repository} ms={ElapsedMs} slowestCommand={Command} slowestCount={Count} slowestMs={SlowMs}",
                    state.ProjectName, elapsedMs, slowest?.Command ?? "n/a", slowest?.Count ?? 0, slowest?.Ms ?? 0);
            }
            RecordRunHealth(state, run.Outcome, spawns, slowest);
            try
            {
                RunCompleted?.Invoke(new GitIndexRunReport(state.ProjectName, trigger, run.Outcome, spawns,
                    elapsedMs, run.ConfigCaptured));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SilentCatch.Note(ex, "GitStateIndexService: run report handler failed");
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

    /// <summary>
    /// One index run: observe the inputs from files, confirm a restored
    /// generation or skip an unchanged one, else compute, publish to the store
    /// in one transaction and swap the in-memory pointer. Returns the outcome.
    /// </summary>
    private async Task<string> IndexAsync(RepoState state, string trigger, RunContext run)
    {
        using var deadline = new CancellationTokenSource(_options.RunDeadline);
        lock (state.Gate)
        {
            if (state.Disposed) return "disposed";
            state.ActiveDeadline = deadline;
        }
        using var budget = GitProcessBudget.Begin(deadline.Token);
        var tasksForRepo = TaskProjectionCards.Select(ReadTasks(state), _logger);
        var effectiveConfig = ResolveConfig(state, run);
        PublishOrigin(state, effectiveConfig);
        using var configScope = GitConfigScope.Begin(state.RepositoryPath, effectiveConfig.OriginUrl);
        var input = CaptureInput(state, tasksForRepo, effectiveConfig.Signature);
        run.Attempted = input;
        var restoreChecked = TryConfirmRestored(state, input, out var confirmed);
        if (confirmed) return "confirmed";
        var changeDriven = trigger is "task-event" or "fs-watch" or "sweep";
        if (changeDriven && !restoreChecked
            && (Volatile.Read(ref state.LastErrorReason) is null && input == state.LastPublishedInput
                || trigger == "task-event" && input == state.LastFailedInput))
            return "unchanged";
        if (changeDriven || restoreChecked)
            _projectionCache.MarkRefreshing(state.WatchPath);
        long invalidation;
        lock (state.Gate) invalidation = state.InvalidationVersion;
        var projection = await _buildProjection(tasksForRepo)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        // Compare like with like: the input was captured from the
        // de-duplicated cards, so a raw re-read would differ whenever a
        // duplicate id exists and mark every run superseded forever.
        var after = CaptureInput(state, TaskProjectionCards.Select(ReadTasks(state)), effectiveConfig.Signature);
        lock (state.Gate)
        {
            var superseded = state.Disposed || after != input
                || !GitConfigSignature.SourcesUnchanged(effectiveConfig)
                || invalidation != state.InvalidationVersion;
            if (superseded)
            {
                if (state.Disposed) return "disposed";
                _projectionCache.MarkFailed(state.WatchPath, "superseded");
                state.RerunRequested = true;
                state.RerunTrigger = "superseded";
                return "superseded";
            }
        }
        if (projection.RepositoryFailure is { } failure)
        {
            // AGT-3322: a lookup that fails for the whole repository is a
            // repository health state. The last good generation keeps serving
            // (memory and store), nothing is published, and retries stay bounded.
            _logger.LogWarning(
                "git-index-repository-degraded repository={Repository} lookup={Lookup} reason={Reason} detail={Detail}",
                state.ProjectName, failure.Lookup, failure.ReasonCode, failure.Detail);
            var retry = RecordFailure(state, "repository-degraded",
                $"{failure.Lookup}: {failure.ReasonCode}: {failure.Detail}", input);
            if (retry > 0) _ = RetryAfterBackoffAsync(state, retry);
            return "degraded";
        }

        var gitStateAt = _timeProvider.GetUtcNow();
        PublishToStore(state, input, effectiveConfig, projection, tasksForRepo, gitStateAt);
        lock (state.Gate)
        {
            if (state.Disposed) return "disposed";
            _projectionCache.SetSnapshot(state.WatchPath, projection, gitStateAt, input.Version);
            state.LastIndexedAt = gitStateAt;
            state.LastPublishedInput = input;
            state.LastSweepSignature = input.Refs;
            state.LastSweepConfig = input.Config;
            state.LastSettingsVersion = input.SettingsVersion;
            state.RestoredServing = false;
            ClearFailure(state);
        }

        // Re-uses GitService's own signature-gated cache/queue for
        // inventory; this call only makes that enqueue change-driven
        // instead of waiting for the next request to notice staleness.
        _warmInventory(state.ProjectName);
        RepositoryIndexed?.Invoke(state.ProjectName, gitStateAt);
        return "published";
    }

    /// <summary>
    /// Resolves the configuration for a run. Git runs only when
    /// <see cref="GitConfigCapturePolicy"/> asks for it; a changed resolved
    /// signature advances <c>config_generation</c>.
    /// </summary>
    private GitEffectiveConfig ResolveConfig(RepoState state, RunContext run)
    {
        GitConfigSignature.EnsureRepository(state.RepositoryPath);
        GitEffectiveConfig? known;
        bool dirty;
        lock (state.Gate)
        {
            known = state.Config;
            dirty = state.ConfigDirty;
        }
        var reason = GitConfigCapturePolicy.Decide(known is not null, dirty,
            known is not null && GitConfigSignature.SourcesUnchanged(known));
        if (reason == GitConfigCaptureReason.None) return known!;

        // Cleared before the capture, so an event during it marks the next run.
        lock (state.Gate) state.ConfigDirty = false;
        GitEffectiveConfig captured;
        try
        {
            captured = GitConfigSignature.Capture(state.RepositoryPath);
        }
        catch
        {
            lock (state.Gate) state.ConfigDirty = true;
            throw;
        }
        run.ConfigCaptured = true;
        long generation;
        lock (state.Gate)
        {
            generation = state.ConfigGeneration = GitConfigCapturePolicy.NextGeneration(
                state.ConfigGeneration, state.Config?.Signature, captured.Signature);
            state.Config = captured;
        }
        _logger.LogDebug("git-config-captured repository={Repository} reason={Reason} configGeneration={Generation}",
            state.ProjectName, reason, generation);
        return captured;
    }

    /// <summary>
    /// Confirms a restored generation without Git when every input it was
    /// computed from is unchanged. Returns whether a restored generation was
    /// pending; <paramref name="confirmed"/> tells whether it became ready.
    /// </summary>
    private bool TryConfirmRestored(RepoState state, RepoInput input, out bool confirmed)
    {
        confirmed = false;
        PendingRestore? restored;
        long configGeneration;
        lock (state.Gate)
        {
            restored = state.PendingRestore;
            state.PendingRestore = null;
            configGeneration = state.ConfigGeneration;
        }
        if (restored is null) return false;
        var verdict = GitFactRestorePolicy.Confirm(restored.Input, new GitFactGenerationInput(
            input.RefFingerprint, configGeneration, input.SettingsVersion, input.DurableTasks),
            restored.CardsReadable);
        if (verdict != GitFactRestoreVerdict.Ready)
        {
            _logger.LogInformation("git-facts-restore-delta repository={Repository} verdict={Verdict}",
                state.ProjectName, verdict);
            return true;
        }
        var now = _timeProvider.GetUtcNow();
        lock (state.Gate)
        {
            if (state.Disposed) return true;
            _projectionCache.ConfirmRestored(state.WatchPath, now, input.Version);
            state.LastIndexedAt = now;
            state.LastPublishedInput = input;
            state.LastSweepSignature = input.Refs;
            state.LastSweepConfig = input.Config;
            state.LastSettingsVersion = input.SettingsVersion;
            state.RestoredServing = false;
            ClearFailure(state);
        }
        confirmed = true;
        _logger.LogInformation("git-facts-restore-confirmed repository={Repository}", state.ProjectName);
        _warmInventory(state.ProjectName);
        RepositoryIndexed?.Invoke(state.ProjectName, now);
        return true;
    }

    /// <summary>
    /// Commits the generation before the in-memory swap. A store failure or a
    /// fenced producer never withholds fresh facts from this process's readers;
    /// it costs restart persistence only and is logged and recorded.
    /// </summary>
    private void PublishToStore(RepoState state, RepoInput input, GitEffectiveConfig config,
        TaskListGitProjection projection, TaskInfo[] tasks, DateTimeOffset publishedAt)
    {
        if (_factStore is not { IsAvailable: true }) return;
        long epoch;
        long configGeneration;
        lock (state.Gate)
        {
            if (state.Fenced) return;
            epoch = state.ProducerEpoch;
            configGeneration = state.ConfigGeneration;
        }
        try
        {
            if (epoch == 0)
            {
                epoch = _factStore.AcquireProducerEpoch(state.ProjectName, _producerId);
                lock (state.Gate) state.ProducerEpoch = epoch;
            }
            var result = _factStore.Publish(new GitFactPublication(
                new GitRepositoryFacts(state.ProjectName, state.RepositoryPath, state.WatchPath, config,
                    configGeneration),
                new GitRefGenerationDraft(GitRefObjectIds.Read(state.RepositoryPath), input.RefFingerprint,
                    configGeneration, input.SettingsVersion, input.DurableTasks, input.Version, _producerId, epoch,
                    publishedAt),
                CardGitFactsCodec.ToRows(projection, tasks)));
            if (result.Outcome == GitFactPublishOutcome.Published)
            {
                lock (state.Gate) state.StoreGeneration = result.Generation;
            }
            else if (result.Outcome == GitFactPublishOutcome.RejectedStaleEpoch)
            {
                lock (state.Gate) state.Fenced = true;
                _logger.LogWarning(
                    "git-facts-publish-rejected repository={Repository} epoch={Epoch} storedEpoch={StoredEpoch}",
                    state.ProjectName, epoch, result.StoredEpoch);
            }
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            _logger.LogWarning(ex, "git-facts-publish-failed repository={Repository}", state.ProjectName);
        }
    }

    /// <summary>Records a failed or degraded run; returns the retry ordinal, or 0 for no retry.</summary>
    private int RecordFailure(RepoState state, string reason, string error, RepoInput? attemptedInput)
    {
        lock (state.Gate)
        {
            if (state.Disposed) return 0;
            state.LastErrorReason = reason;
            state.LastError = error;
            state.FailureSince ??= _timeProvider.GetUtcNow();
            state.LastFailedInput = attemptedInput;
            _projectionCache.MarkFailed(state.WatchPath, reason);
            return ++state.Failures <= _options.MaxRetries ? state.Failures : 0;
        }
    }

    private static void ClearFailure(RepoState state)
    {
        state.Failures = 0;
        state.LastErrorReason = null;
        state.LastError = null;
        state.FailureSince = null;
        state.LastFailedInput = null;
    }

    private void RecordRunHealth(RepoState state, string outcome, int spawns, (string Command, int Count, long Ms)? slowest)
    {
        var now = _timeProvider.GetUtcNow();
        GitProducerHealth health;
        lock (state.Gate)
        {
            state.RecentSpawns.Enqueue((now, spawns));
            // A fenced producer no longer writes the store, health included.
            if (outcome is "unchanged" or "disposed" || state.Fenced || _factStore is not { IsAvailable: true }) return;
            health = new GitProducerHealth(state.ProjectName, HealthState(state),
                state.StoreGeneration == 0 ? null : state.StoreGeneration, state.LastIndexedAt, state.Failures,
                state.LastErrorReason, state.RerunRequested ? 1 : 0, SpawnsPerMinute(state, now),
                slowest?.Command, slowest?.Ms, now);
        }
        try { _factStore.RecordHealth(health); }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "git-facts-health-write-failed repository={Repository}", state.ProjectName);
        }
    }

    private static string HealthState(RepoState state)
        => GitRepositoryHealthPolicy.State(state.LastIndexedAt is not null, state.RestoredServing,
            state.LastErrorReason, state.Fenced);

    /// <summary>Spawns of this repository's runs in the last minute. Caller holds the gate.</summary>
    private static double SpawnsPerMinute(RepoState state, DateTimeOffset now)
    {
        while (state.RecentSpawns.TryPeek(out var oldest) && now - oldest.At > TimeSpan.FromMinutes(1))
            state.RecentSpawns.Dequeue();
        return state.RecentSpawns.Sum(entry => entry.Spawns);
    }

    private TaskInfo[] ReadTasks(RepoState state) => _scanner.ScanAllJobsRaw()
        .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, state.WatchPath))
        .ToArray();

    private RepoInput CaptureInput(RepoState state, TaskInfo[] tasks, string configSignature)
    {
        var (taskSignature, durableTasks) = CaptureTaskInputSignatures(tasks, _projectionCache);
        var refs = SafeCapture(state.RepositoryPath);
        return new RepoInput(state.RepositoryPath, refs, RefFingerprint(state, refs),
            configSignature, _settingsVersion(state.ProjectName), taskSignature, durableTasks);
    }

    /// <summary>
    /// The stored ref fingerprint: the watcher-facing <see cref="GitRefSignature"/>
    /// plus <see cref="ReadOnlyGitRefFingerprint"/> over HEAD, origin/HEAD and
    /// the integration branch. Both read files only, so a restart can compare
    /// it without starting Git.
    /// </summary>
    private string RefFingerprint(RepoState state, GitRefSignature refs)
    {
        IReadOnlyList<string> branches;
        try { branches = _fingerprintBranches(state.ProjectName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or KeyNotFoundException)
        {
            SilentCatch.Note(ex, "GitStateIndexService: integration branch for the ref fingerprint unavailable");
            branches = [];
        }
        var detailed = ReadOnlyGitRefFingerprint.CaptureDetailed(state.RepositoryPath, branches);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"refs=1\0{refs}\0{detailed.Value}\0{string.Join(",", branches)}")));
    }

    internal static string CaptureTaskInputSignature(TaskInfo[] tasks, TaskListGitProjectionCache cache)
        => CaptureTaskInputSignatures(tasks, cache).Tasks;

    /// <summary>
    /// The task half of a run's input in one pass. <c>Tasks</c> also carries
    /// this process's review-subject versions; <c>Durable</c> carries only
    /// file facts, so it survives a restart and confirms a restored generation.
    /// </summary>
    internal static (string Tasks, string Durable) CaptureTaskInputSignatures(TaskInfo[] tasks,
        TaskListGitProjectionCache cache)
    {
        var parts = new StringBuilder("schema=4;");
        var durable = new StringBuilder("durable=1;");
        foreach (var task in tasks.OrderBy(task => task.TaskKey, StringComparer.Ordinal))
        {
            var start = parts.Length;
            parts.Append(task.TaskKey).Append(':').Append(TaskGitSignature.For(task)).Append(':');
            var subject = ReviewSubjectStore.PathFor(task.FolderPath);
            cache.SeedTaskInput(subject);
            AppendFileFact(parts, subject);
            durable.Append(parts, start, parts.Length - start);
            parts.Append(cache.SubjectVersion(task.FolderPath)).Append(';');
        }
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString()))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(durable.ToString()))));
    }

    private static void AppendFileFact(StringBuilder parts, string path)
    {
        var (exists, hash) = TaskListGitProjectionCache.SidecarStamp(path);
        parts.Append(exists ? '1' : '0').Append('/').Append(hash).Append(';');
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
            catch (ObjectDisposedException ex)
            {
                SilentCatch.Note(ex, "GitStateIndexService: run deadline already disposed");
            }
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
        public string? LastError;
        public DateTimeOffset? FailureSince;
        public RepoInput? LastFailedInput;
        public CancellationTokenSource? ActiveDeadline;
        // AGT-3322: configuration generation and fact-store state.
        public GitEffectiveConfig? Config;
        public long ConfigGeneration;
        public bool ConfigDirty;
        public PendingRestore? PendingRestore;
        public bool RestoredServing;
        public long StoreGeneration;
        public long ProducerEpoch;
        public bool Fenced;
        public readonly Queue<(DateTimeOffset At, int Spawns)> RecentSpawns = new();
    }

    private sealed record PendingRestore(GitFactGenerationInput Input, bool CardsReadable);

    private sealed class RunContext
    {
        public RepoInput? Attempted;
        public bool ConfigCaptured;
        public string Outcome = "failed";
    }

    private sealed record RepoInput(string RepositoryPath, GitRefSignature Refs, string RefFingerprint,
        string Config, string SettingsVersion, string Tasks, string DurableTasks)
    {
        public string Version => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"schema=3\0{RepositoryPath}\0{Refs}\0{Config}\0{SettingsVersion}\0{Tasks}")));
    }
}

/// <summary>One repository's index freshness, read by the Admin git-telemetry surface.</summary>
public sealed record GitStateRepositoryStatus(
    string ProjectName,
    DateTimeOffset? GitStateAt,
    bool Refreshing,
    double? AgeSeconds,
    string? ReasonCode = null,
    int ConsecutiveFailures = 0,
    string? LastError = null,
    DateTimeOffset? FailureSince = null,
    string State = GitRepositoryHealthPolicy.Warming,
    long FactGeneration = 0,
    long ConfigGeneration = 0,
    long ProducerEpoch = 0,
    double SpawnsPerMinute = 0);

/// <summary>One finished index run, for tests and diagnostics.</summary>
internal sealed record GitIndexRunReport(string ProjectName, string Trigger, string Outcome, int Spawns,
    long ElapsedMs, bool ConfigCaptured);

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
