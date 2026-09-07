using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Tasks;

/// <summary>
/// Cache-only Git enrichment for task-list responses. List requests return the
/// latest completed projection immediately and may queue one detached refresh.
/// Git processes run only inside that background refresh, never on the request
/// execution context.
/// </summary>
public sealed class TaskListGitProjectionCache
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<TaskListGitProjectionCache> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IReadOnlyCollection<TaskInfo>, Task<TaskListGitProjection>> _refreshProjection;
    private readonly GitBackgroundExecutor? _executor;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);

    public TaskListGitProjectionCache(
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        ILogger<TaskListGitProjectionCache> logger,
        GitBackgroundExecutor? executor = null)
        : this(
            mergeStatus,
            integrationStatus,
            publishStatus,
            testRuns,
            logger,
            TimeProvider.System,
            executor)
    {
    }

    internal TaskListGitProjectionCache(
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider,
        GitBackgroundExecutor? executor = null)
        : this(
            // Sequential, not fanned out. The parallel build existed to overlap
            // per-repository git spawns; with the background index warming those
            // repositories first (AGT-2726) there is no git left to overlap, and
            // the fan-out only spent thread-pool threads that request paths need.
            tasks => Task.FromResult(BuildProjection(
                tasks,
                mergeStatus.BuildLookup,
                integrationStatus.BuildLookup,
                publishStatus.BuildLookup,
                testRuns.BuildLookup)),
            logger,
            timeProvider,
            executor)
    {
    }

    internal TaskListGitProjectionCache(
        Func<IReadOnlyCollection<TaskInfo>, TaskListGitProjection> refreshProjection,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider,
        GitBackgroundExecutor? executor = null)
        : this(tasks => Task.FromResult(refreshProjection(tasks)), logger, timeProvider, executor)
    {
    }

    internal TaskListGitProjectionCache(
        Func<IReadOnlyCollection<TaskInfo>, Task<TaskListGitProjection>> refreshProjection,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider,
        GitBackgroundExecutor? executor = null)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _refreshProjection = refreshProjection;
        _executor = executor;
    }

    /// <summary>
    /// Returns the most recently completed projection for the requested watch
    /// path set. A cold read returns empty enrichment and queues the initial
    /// refresh. The method performs no Git operation and never waits for an
    /// in-flight refresh.
    /// </summary>
    /// <param name="inputVersion">
    /// Optional snapshot-generation stamp from <see cref="TaskIndexCache"/>. When
    /// supplied it replaces the per-request <see cref="InputSignature"/> hash: the
    /// generation already advances on every task mutation, watcher event, or
    /// safety-TTL rescan, so a warm poll skips the O(N + commits) walk and still
    /// forces a refresh the moment the underlying snapshot changes.
    /// </param>
    public TaskListGitProjection ReadCacheOnly(
        IReadOnlyCollection<TaskInfo> tasks,
        long? inputVersion = null)
    {
        if (tasks.Count == 0) return TaskListGitProjection.Empty;

        var captured = tasks.ToArray();
        var scopeKey = ScopeKey(captured);
        var signature = inputVersion ?? InputSignature(captured);
        var entry = _entries.GetOrAdd(scopeKey, static _ => new CacheEntry());
        TaskListGitProjection snapshot;
        var queueRefresh = false;

        lock (entry.Gate)
        {
            snapshot = entry.Snapshot;
            var now = _timeProvider.GetUtcNow();
            if (TaskListGitRefreshPolicy.ShouldQueue(
                    entry.HasSnapshot,
                    entry.Refreshing,
                    entry.InputSignature != signature,
                    entry.RefreshAfter <= now))
            {
                entry.Refreshing = true;
                queueRefresh = true;
            }
        }

        if (queueRefresh) QueueRefresh(scopeKey, entry, captured, signature);
        return snapshot;
    }

    private void QueueRefresh(
        string scopeKey,
        CacheEntry entry,
        TaskInfo[] tasks,
        long signature)
    {
        try
        {
            // The request's GitProcessTelemetry scope uses AsyncLocal. Suppress
            // ExecutionContext flow so background Git work cannot be charged to
            // the request after ReadCacheOnly has returned.
            if (ExecutionContext.IsFlowSuppressed())
            {
                Dispatch(scopeKey, entry, tasks, signature);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                    Dispatch(scopeKey, entry, tasks, signature);
            }
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Refreshing = false;
                entry.RefreshAfter = _timeProvider.GetUtcNow().Add(FailureRetryInterval);
            }
            _logger.LogWarning(ex, "Task-list Git projection refresh could not be queued for scope {Scope}.", scopeKey);
        }
    }

    /// <summary>
    /// Hands the refresh to the shared background git executor when one is
    /// configured, so it runs on the indexer's bounded, dedicated threads
    /// instead of taking a thread-pool thread that request handling needs
    /// (AGT-2726). Without an executor - unit tests, and hosts that do not run
    /// the index - the original detached <c>Task.Run</c> behaviour stands.
    /// </summary>
    private void Dispatch(
        string scopeKey,
        CacheEntry entry,
        TaskInfo[] tasks,
        long signature)
    {
        if (_executor is null)
        {
            _ = Task.Run(() => Refresh(scopeKey, entry, tasks, signature));
            return;
        }

        var accepted = _executor.Submit(
            "tasks/list-refresh",
            _ => Refresh(scopeKey, entry, tasks, signature).GetAwaiter().GetResult());
        if (accepted) return;

        // Shutdown raced the submission. Release the in-flight flag, otherwise
        // every later read waits for a refresh that will never run.
        lock (entry.Gate)
        {
            entry.Refreshing = false;
            entry.RefreshAfter = _timeProvider.GetUtcNow().Add(FailureRetryInterval);
        }
    }

    private async Task Refresh(
        string scopeKey,
        CacheEntry entry,
        IReadOnlyCollection<TaskInfo> tasks,
        long signature)
    {
        var stopwatch = Stopwatch.StartNew();
        TaskListGitProjection? refreshed = null;
        try
        {
            using var telemetry = GitProcessTelemetry.BeginRequest(
                "tasks/list-refresh",
                _logger,
                includeNested: true);
            refreshed = await _refreshProjection(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task-list Git projection refresh failed for scope {Scope}.", scopeKey);
        }
        finally
        {
            stopwatch.Stop();
            lock (entry.Gate)
            {
                if (refreshed is not null)
                {
                    entry.Snapshot = refreshed;
                    entry.HasSnapshot = true;
                    entry.InputSignature = signature;
                }
                entry.Refreshing = false;
                entry.RefreshAfter = _timeProvider.GetUtcNow().Add(
                    refreshed is null ? FailureRetryInterval : RefreshInterval);
            }
        }

        if (refreshed is not null)
        {
            _logger.LogInformation(
                "task-list-git-refresh-complete scope={Scope} tasks={TaskCount} elapsedMs={ElapsedMs}",
                scopeKey,
                tasks.Count,
                stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Builds all four enrichment lookups on the calling thread. Every lookup
    /// reads a ref-fingerprinted per-repository cache that the background git
    /// index keeps warm, so this is in-memory work; running it sequentially on
    /// one dedicated thread costs less than the four-way <c>Task.Run</c> fan-out
    /// it replaces, which parked four thread-pool threads per refresh.
    /// </summary>
    internal static TaskListGitProjection BuildProjection(
        IReadOnlyCollection<TaskInfo> tasks,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskMergeSignal>> mergeLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskIntegrationStatus>> integrationLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskPublishSignal>> publishLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskTestRunEvidence>> testRunLookup)
        => new(
            mergeLookup(tasks),
            integrationLookup(tasks),
            publishLookup(tasks),
            testRunLookup(tasks));

    internal static async Task<TaskListGitProjection> BuildProjectionAsync(
        IReadOnlyCollection<TaskInfo> tasks,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskMergeSignal>> mergeLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskIntegrationStatus>> integrationLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskPublishSignal>> publishLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskTestRunEvidence>> testRunLookup)
        => await BuildProjectionAsync(
            tasks,
            captured => Task.Run(() => mergeLookup(captured)),
            captured => Task.Run(() => integrationLookup(captured)),
            captured => Task.Run(() => publishLookup(captured)),
            captured => Task.Run(() => testRunLookup(captured)));

    internal static async Task<TaskListGitProjection> BuildProjectionAsync(
        IReadOnlyCollection<TaskInfo> tasks,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskMergeSignal>>> mergeLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskIntegrationStatus>>> integrationLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskPublishSignal>>> publishLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskTestRunEvidence>>> testRunLookup)
    {
        var mergeTask = mergeLookup(tasks);
        var integrationTask = integrationLookup(tasks);
        var publishTask = publishLookup(tasks);
        var testRunTask = testRunLookup(tasks);

        await Task.WhenAll(mergeTask, integrationTask, publishTask, testRunTask);
        return new TaskListGitProjection(
            await mergeTask,
            await integrationTask,
            await publishTask,
            await testRunTask);
    }

    private static string ScopeKey(IEnumerable<TaskInfo> tasks)
        // Distinct the raw watch paths first so the expensive Path.GetFullPath
        // normalization runs once per project rather than once per task.
        => string.Join(
            "|",
            tasks.Select(task => task.WatchPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

    private static int InputSignature(IEnumerable<TaskInfo> tasks)
    {
        var hash = new HashCode();
        foreach (var task in tasks.OrderBy(task => task.TaskKey, StringComparer.Ordinal))
        {
            hash.Add(task.TaskKey, StringComparer.Ordinal);
            hash.Add(task.State, StringComparer.Ordinal);
            hash.Add(task.ProjectName, StringComparer.OrdinalIgnoreCase);
            hash.Add(task.WatchPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(task.IntegrationBranch, StringComparer.Ordinal);
            hash.Add(task.Commit?.Sha, StringComparer.OrdinalIgnoreCase);
            hash.Add(task.Provenance?.Branch, StringComparer.Ordinal);
            hash.Add(task.Provenance?.Merge?.MergeCommit, StringComparer.OrdinalIgnoreCase);
            foreach (var commit in task.Commits)
            {
                hash.Add(commit.Sha, StringComparer.OrdinalIgnoreCase);
                hash.Add(commit.Branch, StringComparer.Ordinal);
                hash.Add(commit.FilesChanged);
            }
        }
        return hash.ToHashCode();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "TaskListGitProjectionCache: invalid watch path");
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }

    private sealed class CacheEntry
    {
        public object Gate { get; } = new();
        public TaskListGitProjection Snapshot { get; set; } = TaskListGitProjection.Empty;
        public bool HasSnapshot { get; set; }
        public bool Refreshing { get; set; }
        public long InputSignature { get; set; }
        public DateTimeOffset RefreshAfter { get; set; } = DateTimeOffset.MinValue;
    }
}

internal static class TaskListGitRefreshPolicy
{
    internal static bool ShouldQueue(
        bool hasSnapshot,
        bool refreshing,
        bool inputChanged,
        bool refreshDue)
        => !refreshing && (!hasSnapshot || inputChanged || refreshDue);
}

public sealed record TaskListGitProjection(
    IReadOnlyDictionary<string, TaskMergeSignal> Merge,
    IReadOnlyDictionary<string, TaskIntegrationStatus> Integration,
    IReadOnlyDictionary<string, TaskPublishSignal> Publish,
    IReadOnlyDictionary<string, TaskTestRunEvidence> TestRuns)
{
    public static TaskListGitProjection Empty { get; } = new(
        new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
        new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal));
}
