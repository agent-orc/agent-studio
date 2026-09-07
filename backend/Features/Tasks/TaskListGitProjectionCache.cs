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
    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);

    public TaskListGitProjectionCache(
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        AgentStudio.Review.ReviewProjectionService reviewProjection,
        ILogger<TaskListGitProjectionCache> logger)
        : this(
            mergeStatus,
            integrationStatus,
            publishStatus,
            testRuns,
            reviewProjection,
            logger,
            TimeProvider.System)
    {
    }

    internal TaskListGitProjectionCache(
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        AgentStudio.Review.ReviewProjectionService reviewProjection,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider)
        : this(
            tasks => BuildProjectionAsync(
                tasks,
                mergeStatus.BuildLookup,
                integrationStatus.BuildLookup,
                publishStatus.BuildLookup,
                testRuns.BuildLookup,
                reviewProjection.BuildLookup),
            logger,
            timeProvider)
    {
    }

    internal TaskListGitProjectionCache(
        Func<IReadOnlyCollection<TaskInfo>, TaskListGitProjection> refreshProjection,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider)
        : this(tasks => Task.FromResult(refreshProjection(tasks)), logger, timeProvider)
    {
    }

    internal TaskListGitProjectionCache(
        Func<IReadOnlyCollection<TaskInfo>, Task<TaskListGitProjection>> refreshProjection,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _refreshProjection = refreshProjection;
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
                _ = Task.Run(() => Refresh(scopeKey, entry, tasks, signature));
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(() => Refresh(scopeKey, entry, tasks, signature));
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

    internal static async Task<TaskListGitProjection> BuildProjectionAsync(
        IReadOnlyCollection<TaskInfo> tasks,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskMergeSignal>> mergeLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskIntegrationStatus>> integrationLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskPublishSignal>> publishLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, TaskTestRunEvidence>> testRunLookup,
        Func<IReadOnlyCollection<TaskInfo>, Dictionary<string, AgentStudio.Review.ReviewProjectionView>> reviewProjectionLookup)
        => await BuildProjectionAsync(
            tasks,
            captured => Task.Run(() => mergeLookup(captured)),
            captured => Task.Run(() => integrationLookup(captured)),
            captured => Task.Run(() => publishLookup(captured)),
            captured => Task.Run(() => testRunLookup(captured)),
            captured => Task.Run(() => reviewProjectionLookup(captured)));

    internal static async Task<TaskListGitProjection> BuildProjectionAsync(
        IReadOnlyCollection<TaskInfo> tasks,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskMergeSignal>>> mergeLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskIntegrationStatus>>> integrationLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskPublishSignal>>> publishLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, TaskTestRunEvidence>>> testRunLookup,
        Func<IReadOnlyCollection<TaskInfo>, Task<Dictionary<string, AgentStudio.Review.ReviewProjectionView>>> reviewProjectionLookup)
    {
        var mergeTask = mergeLookup(tasks);
        var integrationTask = integrationLookup(tasks);
        var publishTask = publishLookup(tasks);
        var testRunTask = testRunLookup(tasks);
        var reviewProjectionTask = reviewProjectionLookup(tasks);

        await Task.WhenAll(mergeTask, integrationTask, publishTask, testRunTask, reviewProjectionTask);
        return new TaskListGitProjection(
            await mergeTask,
            await integrationTask,
            await publishTask,
            await testRunTask,
            await reviewProjectionTask);
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
    IReadOnlyDictionary<string, TaskTestRunEvidence> TestRuns,
    IReadOnlyDictionary<string, AgentStudio.Review.ReviewProjectionView> ReviewProjection)
{
    public static TaskListGitProjection Empty { get; } = new(
        new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
        new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal),
        new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal));
}
