using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.Tasks;

namespace AgentStudio.Git;

/// <summary>
/// Owns every git process the board's derived state costs (AGT-2726): the ref
/// watchers that trigger a run, the debounce and safety sweep that pace it, and
/// the submission of runs onto the shared bounded executor.
///
/// <para>
/// The scheduler itself does no git work and holds no lock while deciding.
/// It ticks, asks <see cref="GitStateIndex.ClaimDue"/> which repositories are
/// admitted, and hands each claim to <see cref="GitBackgroundExecutor"/>. That
/// separation is what makes the invariant checkable: nothing on a request path
/// can reach a git spawn, because the only code that spawns runs on the
/// executor's own threads.
/// </para>
/// </summary>
public sealed class GitStateIndexHostedService(
    GitStateIndex index,
    IGitRepositoryStateRefresher refresher,
    GitBackgroundExecutor executor,
    TaskScannerService scanner,
    GitService git,
    TaskChangeNotifier changes,
    ILogger<GitStateIndexHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, GitRepositoryRefWatcher?> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly GitStateIndexOptions _options = index.Options;
    private volatile bool _discoveryPending;

    /// <summary>
    /// Runs discovery on the executor rather than on the scheduler's thread.
    /// Resolving a repository root spawns git, and the scheduler runs on the
    /// thread pool; keeping every git spawn on the executor is what makes the
    /// process budget a real ceiling instead of an approximation.
    /// </summary>
    private void QueueDiscovery()
    {
        _discoveryPending = true;
        var accepted = executor.Submit("git-index:discover", _ =>
        {
            try { DiscoverRepositories(); }
            finally { _discoveryPending = false; }
        });
        if (!accepted) _discoveryPending = false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync only returns once ExecuteAsync reaches
        // its first await, so everything before this yield would run inside host
        // startup. Discovery resolves repository roots with `rev-parse
        // --show-toplevel`; doing that synchronously here would make the backend
        // boot wait on git, which is the exact coupling this service removes.
        await Task.Yield();

        var lastDiscovery = DateTimeOffset.MinValue;
        // A ref watcher sees a merge land; it cannot see the Task Server decide
        // that a card is delivered. Lane moves and bulk changes are the
        // integration and delivery events this process produces itself, so they
        // trigger the index directly rather than waiting for the sweep.
        void OnMoved(TaskMoveEvent moved) => index.SignalAll("task-move");
        void OnBulk() => index.SignalAll("task-bulk");
        changes.TaskMoved += OnMoved;
        changes.JobsBulkChanged += OnBulk;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastDiscovery >= _options.SweepInterval && !_discoveryPending)
                {
                    lastDiscovery = now;
                    QueueDiscovery();
                }

                Dispatch();
                await Task.Delay(_options.TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("git-index scheduler stopped");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "git-index scheduler stopped unexpectedly");
        }
        finally
        {
            changes.TaskMoved -= OnMoved;
            changes.JobsBulkChanged -= OnBulk;
            foreach (var watcher in _watchers.Values) watcher?.Dispose();
            _watchers.Clear();
        }
    }

    /// <summary>
    /// Hands every admitted repository to the executor. A claim the executor
    /// refuses (shutdown) is abandoned immediately, otherwise the repository
    /// would stay marked "running" and never be indexed again.
    /// </summary>
    private void Dispatch()
    {
        foreach (var claim in index.ClaimDue())
        {
            var accepted = executor.Submit(
                $"git-index:{claim.ProjectName}",
                token => RunOnce(claim, token));
            if (!accepted) index.Abandon(claim);
        }
    }

    /// <summary>
    /// One repository's index run. Always releases the claim - a repository
    /// whose run threw must become claimable again, or its stamp would report
    /// "indexing" forever. A run that failed still completed (it observed the
    /// repository as best it could); a run that was cancelled mid-flight is
    /// abandoned instead, because it never observed anything and must not stamp
    /// a freshness it did not earn.
    /// </summary>
    internal void RunOnce(GitIndexClaim claim, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var spawns = 0;
        string? slowest = null;
        long slowestMs = 0;
        var cancelled = false;

        try
        {
            using (GitProcessTelemetry.BeginRequest("git/index-run", logger, includeNested: true))
            using (GitBackgroundPriority.Enter(_options.LowProcessPriority))
            {
                try
                {
                    refresher.Refresh(claim.RepositoryRoot, claim.ProjectName, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "git-index run failed for repository {Repository}",
                        claim.RepositoryRoot);
                }

                // Read the tally inside the scope: disposing it restores the
                // outer scope and the counts are no longer reachable.
                spawns = GitProcessTelemetry.CurrentTally()?.Spawns ?? 0;
                if (GitProcessTelemetry.CurrentSlowest() is { } worst)
                    (slowest, slowestMs) = (worst.Command, worst.Ms);
            }
        }
        finally
        {
            stopwatch.Stop();
            if (cancelled) index.Abandon(claim);
            else index.Complete(claim, spawns, stopwatch.ElapsedMilliseconds, slowest);
        }

        if (cancelled) return;

        logger.LogInformation(
            "git-index-run repository={Repository} spawns={Spawns} ms={ElapsedMs} trigger={Trigger}",
            claim.RepositoryRoot,
            spawns,
            stopwatch.ElapsedMilliseconds,
            claim.Trigger);

        if (stopwatch.Elapsed >= _options.SlowRunThreshold)
        {
            logger.LogWarning(
                "git-index-run-slow repository={Repository} ms={ElapsedMs} spawns={Spawns} trigger={Trigger} slowest={SlowestCommand} slowestMs={SlowestMs}",
                claim.RepositoryRoot,
                stopwatch.ElapsedMilliseconds,
                spawns,
                claim.Trigger,
                slowest ?? "none",
                slowestMs);
        }
    }

    /// <summary>
    /// Registers every configured repository and attaches its ref watcher.
    /// Resolving a repository root can itself spawn git (<c>rev-parse
    /// --show-toplevel</c>), which is why this runs on the sweep cadence from
    /// the scheduler and never from a request.
    /// </summary>
    internal void DiscoverRepositories()
    {
        List<WatchPathEntry> entries;
        try
        {
            entries = scanner.GetWatchPaths();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "git-index repository discovery failed");
            return;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            string? root;
            try
            {
                root = git.ResolveRepositoryRoot(entry);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "GitStateIndexHostedService: repository root resolution is best-effort");
                continue;
            }

            var normalized = GitStateIndex.Normalize(root);
            if (normalized is null) continue;

            index.Register(normalized, entry.Name);
            _watchers.GetOrAdd(normalized, key => GitRepositoryRefWatcher.TryCreate(
                key,
                trigger => index.Signal(key, trigger),
                logger));
        }

        // The safety net for everything the watchers cannot see: a network
        // share with no change notifications, a repository registered while the
        // backend was down, a dropped watcher buffer. It only marks
        // repositories dirty; admission still decides whether a run happens.
        index.SignalAll("sweep");
    }
}
