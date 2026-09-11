using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// Read-only, per-repository snapshot store for task-list Git enrichment
/// (merge signal, integration status, publish signal, test-run evidence).
///
/// <para>
/// AGT-2726: this class used to be the thing that decided when to recompute -
/// every list/grouped request that landed past a 2-second TTL (or after any
/// task mutation changed the combined input signature for the whole board)
/// queued a detached recompute across every task in every project. Under a
/// busy board that meant a full re-derivation roughly every two seconds,
/// regardless of whether any repository's ref state had actually moved. That
/// is now <see cref="AgentStudio.Git.GitStateIndexService"/>'s job: it watches
/// each repository's refs (plus the Task Server's own change events) and
/// recomputes one repository's projection only when that repository actually
/// changed, on a debounce, with a slow periodic sweep as a safety net.
/// </para>
///
/// <para>
/// This class is now a pure snapshot store keyed by repository (watch path):
/// <see cref="ReadCacheOnly"/> merges the last completed per-repository
/// snapshots for the repositories the requested tasks touch, doing no Git
/// work and starting no background work itself. Only the indexer calls
/// <see cref="SetSnapshot"/> and <see cref="MarkRefreshing"/>.
/// </para>
/// </summary>
public sealed class TaskListGitProjectionCache
{
    private readonly ConcurrentDictionary<string, RepoEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the merged, most recently completed projection for every
    /// repository the requested tasks belong to. A repository the indexer has
    /// never computed contributes nothing (not an error - the merged result
    /// simply omits its tasks until the first index run completes). Never
    /// blocks and never starts a Git operation.
    /// </summary>
    public TaskListGitProjection ReadCacheOnly(IReadOnlyCollection<TaskInfo> tasks)
    {
        if (tasks.Count == 0) return TaskListGitProjection.Empty;

        var repoKeys = tasks
            .Select(t => NormalizePath(t.WatchPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (repoKeys.Length == 1)
            return _entries.TryGetValue(repoKeys[0], out var only) ? only.Snapshot : TaskListGitProjection.Empty;

        var merge = new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal);
        var integration = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        var publish = new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal);
        var testRuns = new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal);
        var reviewProjection = new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal);
        foreach (var key in repoKeys)
        {
            if (!_entries.TryGetValue(key, out var entry)) continue;
            foreach (var (k, v) in entry.Snapshot.Merge) merge[k] = v;
            foreach (var (k, v) in entry.Snapshot.Integration) integration[k] = v;
            foreach (var (k, v) in entry.Snapshot.Publish) publish[k] = v;
            foreach (var (k, v) in entry.Snapshot.TestRuns) testRuns[k] = v;
            foreach (var (k, v) in entry.Snapshot.ReviewProjection) reviewProjection[k] = v;
        }
        return new TaskListGitProjection(merge, integration, publish, testRuns, reviewProjection);
    }

    /// <summary>
    /// Freshness stamp for the repositories the requested tasks belong to:
    /// the oldest <c>gitStateAt</c> among them (null when at least one
    /// repository has never been indexed), and whether any of them is
    /// currently mid-refresh. The board surfaces this quietly next to the
    /// list/grouped response; it never gates the response on it.
    /// </summary>
    public GitProjectionFreshness ReadFreshness(IReadOnlyCollection<TaskInfo> tasks)
    {
        if (tasks.Count == 0) return new GitProjectionFreshness(null, false);

        DateTimeOffset? oldest = null;
        var stale = false;
        var any = false;
        foreach (var key in tasks.Select(t => NormalizePath(t.WatchPath)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            any = true;
            if (!_entries.TryGetValue(key, out var entry) || entry.GitStateAt is null)
            {
                stale = true;
                continue;
            }
            if (entry.Refreshing) stale = true;
            if (oldest is null || entry.GitStateAt < oldest) oldest = entry.GitStateAt;
        }
        return new GitProjectionFreshness(oldest, stale || !any);
    }

    /// <summary>
    /// Written only by <see cref="AgentStudio.Git.GitStateIndexService"/> when
    /// a repository's index run completes.
    /// </summary>
    internal void SetSnapshot(string watchPath, TaskListGitProjection projection, DateTimeOffset gitStateAt)
    {
        var key = NormalizePath(watchPath);
        _entries.AddOrUpdate(
            key,
            _ => new RepoEntry { Snapshot = projection, GitStateAt = gitStateAt, Refreshing = false },
            (_, existing) =>
            {
                existing.Snapshot = projection;
                existing.GitStateAt = gitStateAt;
                existing.Refreshing = false;
                return existing;
            });
    }

    /// <summary>
    /// Marks a repository as mid-refresh so concurrent readers can report
    /// <see cref="GitProjectionFreshness.Stale"/> while the indexer's run for
    /// it is in flight. The previous snapshot (if any) keeps serving reads.
    /// </summary>
    internal void MarkRefreshing(string watchPath)
    {
        var key = NormalizePath(watchPath);
        _entries.AddOrUpdate(
            key,
            _ => new RepoEntry { Snapshot = TaskListGitProjection.Empty, GitStateAt = null, Refreshing = true },
            (_, existing) =>
            {
                existing.Refreshing = true;
                return existing;
            });
    }

    internal static string NormalizePath(string path)
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

    /// <summary>
    /// Fans the four independent Git-derived lookups for one repository's
    /// task set out concurrently. Shared by <see cref="AgentStudio.Git.GitStateIndexService"/>
    /// (the sole caller in production) and tests.
    /// </summary>
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

    private sealed class RepoEntry
    {
        public TaskListGitProjection Snapshot = TaskListGitProjection.Empty;
        public DateTimeOffset? GitStateAt;
        public bool Refreshing;
    }
}

/// <summary>
/// Freshness stamp returned alongside a <see cref="TaskListGitProjection"/>
/// read: the oldest <c>gitStateAt</c> among the touched repositories, and
/// whether any of them is missing a snapshot or currently mid-refresh.
/// </summary>
public sealed record GitProjectionFreshness(DateTimeOffset? GitStateAt, bool Stale);

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
