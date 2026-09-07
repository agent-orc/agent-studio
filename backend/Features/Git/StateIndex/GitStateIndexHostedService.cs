namespace AgentStudio.Git;

/// <summary>
/// Drives <see cref="GitStateIndex"/>: discovers the workspace's repositories,
/// watches their git metadata for ref movement, folds the Task Server's own
/// task events in as triggers, and ticks the index so due captures start.
///
/// <para>
/// This is the only component that decides <em>when</em> git-derived state is
/// recomputed. Nothing on a request path schedules work here; the endpoints
/// read the last capture and return.
/// </para>
/// </summary>
public sealed class GitStateIndexHostedService : BackgroundService
{
    /// <summary>
    /// Scheduling resolution. The tick only inspects in-memory bookkeeping, so
    /// it is cheap; the debounce inside the index decides what actually runs.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often newly registered projects are picked up. Resolving a git
    /// toplevel is memoized for the process lifetime, so after the first sweep
    /// this costs nothing and a freshly onboarded project's Git view fills in
    /// seconds rather than after a minute of empty state.
    /// </summary>
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(15);

    private readonly GitStateIndex _index;
    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly TaskChangeNotifier _tasks;
    private readonly ILogger<GitStateIndexHostedService> _logger;
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watcherGate = new();
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public GitStateIndexHostedService(
        GitStateIndex index,
        GitService git,
        TaskScannerService scanner,
        TaskChangeNotifier tasks,
        ILogger<GitStateIndexHostedService> logger)
    {
        _index = index;
        _git = git;
        _scanner = scanner;
        _tasks = tasks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never make StartAsync await repository discovery: resolving a git
        // toplevel forks git, and startup must not block on the filesystem.
        await Task.Yield();

        _tasks.TaskMoved += OnTaskMoved;
        _tasks.JobsBulkChanged += OnJobsBulkChanged;

        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Discover(DateTimeOffset.UtcNow);
                    Sweep(DateTimeOffset.UtcNow);
                    await _index.RunDueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "git-index tick failed; the next tick retries.");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
        }
        catch (OperationCanceledException ex)
        {
            SilentCatch.Note(ex, "GitStateIndexHostedService: shutdown");
        }
        finally
        {
            _tasks.TaskMoved -= OnTaskMoved;
            _tasks.JobsBulkChanged -= OnJobsBulkChanged;
            DisposeWatchers();
        }
    }

    private void Discover(DateTimeOffset now)
    {
        if (now - _lastDiscovery < DiscoveryInterval) return;
        _lastDiscovery = now;

        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            string? root;
            try
            {
                root = _git.ResolveRepoRootForProject(entry.Name);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "GitStateIndexHostedService: repository resolution");
                continue;
            }
            if (string.IsNullOrWhiteSpace(root)) continue;

            _index.Register(root!, entry.Name);
            EnsureWatching(root!);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < GitStateIndex.SweepInterval) return;
        _lastSweep = now;
        _index.RequestSweep();
    }

    /// <summary>
    /// Watches one repository's shared git metadata directory. Linked worktrees
    /// keep their HEAD under <c>worktrees/&lt;name&gt;/HEAD</c> inside that same
    /// directory, so a single recursive watcher covers the primary checkout and
    /// every task worktree.
    /// </summary>
    private void EnsureWatching(string repositoryRoot)
    {
        lock (_watcherGate)
        {
            if (_watchers.ContainsKey(repositoryRoot)) return;

            var metadata = ReadOnlyGitRefFingerprint.ResolveCommonDirectory(repositoryRoot);
            if (string.IsNullOrWhiteSpace(metadata) || !Directory.Exists(metadata))
            {
                // No readable metadata directory: the periodic sweep remains the
                // only trigger for this repository, which is correct but slower.
                _logger.LogInformation(
                    "git-index-watch repository={Repository} watcher=absent reason=no-metadata-directory",
                    repositoryRoot);
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(metadata!)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size,
                };
                void Changed(object _, FileSystemEventArgs args) => OnMetadataChanged(repositoryRoot, metadata!, args.FullPath);
                watcher.Created += Changed;
                watcher.Changed += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (_, args) => OnMetadataChanged(repositoryRoot, metadata!, args.FullPath);
                watcher.Error += (_, args) =>
                {
                    // A dropped buffer means unknown missed events. Force a full
                    // capture rather than trusting the last snapshot.
                    SilentCatch.Note(args.GetException(), "GitStateIndexHostedService: watcher error");
                    _index.Request(repositoryRoot, GitIndexTriggers.Sweep);
                };
                watcher.EnableRaisingEvents = true;
                _watchers[repositoryRoot] = watcher;
                _logger.LogInformation(
                    "git-index-watch repository={Repository} metadata={Metadata}",
                    repositoryRoot,
                    metadata);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "git-index-watch could not start for repository {Repository}; the periodic sweep still covers it.",
                    repositoryRoot);
            }
        }
    }

    private void OnMetadataChanged(string repositoryRoot, string metadataDirectory, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(metadataDirectory, fullPath);
            var trigger = GitIndexPathClassifier.Classify(relative);
            if (trigger is null) return;
            _index.Request(repositoryRoot, trigger);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitStateIndexHostedService: metadata change classification");
        }
    }

    private void OnTaskMoved(TaskMoveEvent moved) => RequestForWatchPath(moved.WatchPath);

    private void OnJobsBulkChanged() => _index.RequestAll(GitIndexTriggers.TaskEvent);

    private void RequestForWatchPath(string? watchPath)
    {
        try
        {
            var root = _git.ResolveRepoRootForWatchPath(watchPath);
            if (!string.IsNullOrWhiteSpace(root)) _index.Request(root!, GitIndexTriggers.TaskEvent);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitStateIndexHostedService: task-event trigger");
        }
    }

    private void DisposeWatchers()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers.Values)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    SilentCatch.Note(ex, "GitStateIndexHostedService: watcher dispose");
                }
            }
            _watchers.Clear();
        }
    }
}
