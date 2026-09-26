using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;

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
    private readonly ConcurrentDictionary<string, long> _subjectVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (bool Exists, string? Hash)> _sidecarStamps =
        new(StringComparer.OrdinalIgnoreCase);

    private long _generation;

    /// <summary>
    /// Monotonic version of the merged snapshot store. Every indexer write -
    /// a completed run (<see cref="SetSnapshot"/>) and the mid-refresh marker
    /// (<see cref="MarkRefreshing"/>, which moves the published
    /// <see cref="GitProjectionFreshness.Stale"/> flag) - advances it. The
    /// board read (AGT-2703) folds this into its ETag, so a client holding a
    /// validator provably holds the Git-derived half of the board too. It is
    /// deliberately store-wide rather than per repository: the board response
    /// merges every repository anyway, and one counter keeps the validator a
    /// single cheap read.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

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

        var repoGroups = tasks
            .GroupBy(task => NormalizePath(task.WatchPath), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (repoGroups.Length == 1)
            return _entries.TryGetValue(repoGroups[0].Key, out var only)
                ? FilterForTasks(only.Snapshot, tasks) : TaskListGitProjection.Empty;

        var merge = new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal);
        var integration = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        var publish = new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal);
        var testRuns = new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal);
        var reviewProjection = new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal);
        var commits = new Dictionary<string, IReadOnlyList<TaskCommitInfo>>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        var subjectVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var repoGroup in repoGroups)
        {
            var repoTasks = repoGroup.ToArray();
            // A task key can occur in more than one project. Keep every fact
            // aligned with the last repository's version, including when its
            // snapshot is missing or its task facts have become stale.
            foreach (var task in repoTasks)
            {
                merge.Remove(task.TaskKey);
                integration.Remove(task.TaskKey);
                publish.Remove(task.TaskKey);
                testRuns.Remove(task.TaskKey);
                reviewProjection.Remove(task.TaskKey);
                commits.Remove(task.TaskKey);
                signatures.Remove(task.TaskKey);
                subjectVersions.Remove(task.TaskKey);
            }
            if (!_entries.TryGetValue(repoGroup.Key, out var entry)) continue;
            var snapshot = FilterForTasks(entry.Snapshot, repoTasks);
            foreach (var (k, v) in snapshot.Merge) merge[k] = v;
            foreach (var (k, v) in snapshot.Integration) integration[k] = v;
            foreach (var (k, v) in snapshot.Publish) publish[k] = v;
            foreach (var (k, v) in snapshot.TestRuns) testRuns[k] = v;
            foreach (var (k, v) in snapshot.ReviewProjection) reviewProjection[k] = v;
            foreach (var (k, v) in snapshot.ReconstructedCommits) commits[k] = v;
            foreach (var (k, v) in snapshot.TaskSignatures) signatures[k] = v;
            foreach (var (k, v) in snapshot.TaskSubjectVersions) subjectVersions[k] = v;
        }
        return new TaskListGitProjection(merge, integration, publish,
            testRuns, reviewProjection, commits, signatures, subjectVersions);
    }

    private TaskListGitProjection FilterForTasks(
        TaskListGitProjection projection, IReadOnlyCollection<TaskInfo> tasks)
    {
        if (projection.TaskSignatures.Count == 0) return projection;
        var valid = tasks.Where(task => projection.TaskSignatures.TryGetValue(task.TaskKey, out var signature)
                && signature == TaskGitSignature.For(task)
                && (!projection.TaskSubjectVersions.TryGetValue(task.TaskKey, out var expected)
                    || expected == SubjectVersion(task.FolderPath)))
            .Select(task => task.TaskKey).ToHashSet(StringComparer.Ordinal);
        return projection with
        {
            Merge = projection.Merge.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Integration = projection.Integration.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Publish = projection.Publish.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            TestRuns = projection.TestRuns.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            ReviewProjection = projection.ReviewProjection.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Commits = projection.ReconstructedCommits.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Signatures = projection.TaskSignatures.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            SubjectVersions = projection.TaskSubjectVersions.Where(pair => valid.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
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
            if (entry.Refreshing || entry.Reason is not null) stale = true;
            if (oldest is null || entry.GitStateAt < oldest) oldest = entry.GitStateAt;
        }
        return new GitProjectionFreshness(oldest, stale || !any);
    }

    /// <summary>
    /// Written only by <see cref="AgentStudio.Git.GitStateIndexService"/> when
    /// a repository's index run completes.
    /// </summary>
    internal void SetSnapshot(string watchPath, TaskListGitProjection projection,
        DateTimeOffset gitStateAt, string? inputKey = null)
    {
        var key = NormalizePath(watchPath);
        var frozen = projection.Freeze();
        _entries.AddOrUpdate(
            key,
            _ => new RepoEntry(frozen, gitStateAt, false, null, Interlocked.Increment(ref _generation), inputKey),
            (_, existing) => new RepoEntry(frozen, gitStateAt, false, null, Interlocked.Increment(ref _generation), inputKey));
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
            _ => new RepoEntry(TaskListGitProjection.Empty, null, true, null, Interlocked.Increment(ref _generation), null),
            (_, existing) => existing with { Refreshing = true, Reason = null, Generation = Interlocked.Increment(ref _generation) });
    }

    internal void MarkFailed(string watchPath, string reason)
    {
        var key = NormalizePath(watchPath);
        _entries.AddOrUpdate(key,
            _ => new RepoEntry(TaskListGitProjection.Empty, null, false, reason, Interlocked.Increment(ref _generation), null),
            (_, existing) => existing with { Refreshing = false, Reason = reason, Generation = Interlocked.Increment(ref _generation) });
    }

    internal void ResetRepository(string watchPath, string reason)
    {
        _entries[NormalizePath(watchPath)] = new RepoEntry(TaskListGitProjection.Empty,
            null, false, reason, Interlocked.Increment(ref _generation), null);
    }

    internal long SubjectVersion(string folderPath)
        => _subjectVersions.TryGetValue(NormalizePath(folderPath), out var version) ? version : 0;

    internal void SeedTaskInput(string path)
    {
        _sidecarStamps.GetOrAdd(NormalizePath(path), static (_, source) => SidecarStamp(source), path);
    }

    internal bool MarkTaskInputChanged(string path)
    {
        var key = NormalizePath(path);
        var next = SidecarStamp(path);
        if (_sidecarStamps.TryGetValue(key, out var previous) && previous == next) return false;
        _sidecarStamps[key] = next;
        // ReviewSubjectStore writes under <task>/logs/. The version belongs to
        // the task folder used by ReadTask and the indexer's input signature.
        var taskFolder = Path.GetDirectoryName(Path.GetDirectoryName(path) ?? path) ?? path;
        _subjectVersions.AddOrUpdate(NormalizePath(taskFolder),
            1, (_, version) => version + 1);
        Interlocked.Increment(ref _generation);
        return true;
    }

    private static (bool Exists, string? Hash) SidecarStamp(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return default;
        using var stream = file.OpenRead();
        return (true, Convert.ToHexString(SHA256.HashData(stream)));
    }

    /// <summary>One atomic, cache-only task Git read. Incompatible task facts are withheld.</summary>
    public TaskGitSnapshot ReadTask(TaskInfo task)
    {
        if (!_entries.TryGetValue(NormalizePath(task.WatchPath), out var entry))
            return new TaskGitSnapshot(task.TaskKey, task.ProjectName, "warming", null, 0,
                TaskGitSignature.For(task), null, null, null);
        var projection = entry.Snapshot;
        var compatible = projection.TaskSignatures.TryGetValue(task.TaskKey, out var signature)
            && signature == TaskGitSignature.For(task)
            && (!projection.TaskSubjectVersions.TryGetValue(task.TaskKey, out var expected)
                || expected == SubjectVersion(task.FolderPath));
        var state = entry.GitStateAt is null
            ? entry.Reason is null ? "warming" : "unavailable"
            : entry.Refreshing || entry.Reason is not null || !compatible ? "stale" : "ready";
        return new TaskGitSnapshot(task.TaskKey, task.ProjectName, state, entry.GitStateAt, entry.Generation,
            TaskGitSignature.For(task),
            entry.Reason ?? (!compatible && entry.GitStateAt is not null ? "task-changed" : null),
            compatible ? projection.ForTask(task.TaskKey) : null, entry.InputKey);
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

    private sealed record RepoEntry(TaskListGitProjection Snapshot, DateTimeOffset? GitStateAt,
        bool Refreshing, string? Reason, long Generation, string? InputKey);
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
    IReadOnlyDictionary<string, AgentStudio.Review.ReviewProjectionView> ReviewProjection,
    IReadOnlyDictionary<string, IReadOnlyList<TaskCommitInfo>>? Commits = null,
    IReadOnlyDictionary<string, string>? Signatures = null,
    IReadOnlyDictionary<string, long>? SubjectVersions = null)
{
    public IReadOnlyDictionary<string, IReadOnlyList<TaskCommitInfo>> ReconstructedCommits => Commits ?? ImmutableDictionary<string, IReadOnlyList<TaskCommitInfo>>.Empty;
    public IReadOnlyDictionary<string, string> TaskSignatures => Signatures ?? ImmutableDictionary<string, string>.Empty;
    public IReadOnlyDictionary<string, long> TaskSubjectVersions => SubjectVersions ?? ImmutableDictionary<string, long>.Empty;

    internal TaskListGitProjection Freeze() => this with
    {
        Merge = Merge.ToImmutableDictionary(StringComparer.Ordinal),
        Integration = Integration.ToImmutableDictionary(StringComparer.Ordinal),
        Publish = Publish.ToImmutableDictionary(StringComparer.Ordinal),
        TestRuns = TestRuns.ToImmutableDictionary(StringComparer.Ordinal),
        ReviewProjection = ReviewProjection.ToImmutableDictionary(StringComparer.Ordinal),
        Commits = ReconstructedCommits.ToImmutableDictionary(pair => pair.Key,
            pair => (IReadOnlyList<TaskCommitInfo>)pair.Value.ToArray(), StringComparer.Ordinal),
        Signatures = TaskSignatures.ToImmutableDictionary(StringComparer.Ordinal),
        SubjectVersions = TaskSubjectVersions.ToImmutableDictionary(StringComparer.Ordinal),
    };

    internal TaskGitFacts ForTask(string key) => new(
        Merge.GetValueOrDefault(key), Integration.GetValueOrDefault(key),
        Publish.GetValueOrDefault(key), TestRuns.GetValueOrDefault(key),
        ReviewProjection.GetValueOrDefault(key), ReconstructedCommits.GetValueOrDefault(key));

    public static TaskListGitProjection Empty { get; } = new(
        new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
        new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal),
        new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal));
}

public sealed record TaskGitFacts(TaskMergeSignal? Merge, TaskIntegrationStatus? Integration,
    TaskPublishSignal? Publish, TaskTestRunEvidence? TestEvidence,
    AgentStudio.Review.ReviewProjectionView? ReviewProjection,
    IReadOnlyList<TaskCommitInfo>? ReconstructedCommits);

public sealed record TaskGitSnapshot(string TaskKey, string ProjectName, string State,
    DateTimeOffset? ComputedAt, long Generation, string InputVersion,
    string? ReasonCode, TaskGitFacts? Data, string? ResourceVersion)
{
    public int SchemaVersion => 2;
}
