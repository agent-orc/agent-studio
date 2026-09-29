using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2726: <see cref="TaskListGitProjectionCache"/> is now a pure
/// per-repository snapshot store. It never spawns Git and never starts a
/// background refresh itself - that is <c>GitStateIndexService</c>'s job.
/// These tests pin the store's read/merge/freshness contract in isolation.
/// </summary>
public sealed class TaskListGitProjectionCacheTests
{
    [Fact]
    public void ReadCacheOnly_BeforeAnySnapshot_ReturnsEmptyAndSpawnsNoGit()
    {
        var cache = new TaskListGitProjectionCache();
        using var telemetry = GitProcessTelemetry.BeginRequest(
            "tasks/list", NullLogger.Instance, includeNested: true);

        var result = cache.ReadCacheOnly([Job("task-1", "watch-a")]);

        Assert.Same(TaskListGitProjection.Empty, result);
        Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
    }

    [Fact]
    public void ReadCacheOnly_AfterSetSnapshot_ReturnsWrittenProjectionWithoutGitWork()
    {
        var cache = new TaskListGitProjectionCache();
        var task = Job("task-1", "watch-a");
        var signal = new TaskMergeSignal { Branch = "task/task-1" };
        var projection = new TaskListGitProjection(
            new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal) { [task.TaskKey] = signal },
            new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
            new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
            new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal),
            new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal));
        var stateAt = DateTimeOffset.Parse("2026-09-06T12:00:00Z");

        cache.SetSnapshot("watch-a", projection, stateAt);

        using var telemetry = GitProcessTelemetry.BeginRequest(
            "tasks/list", NullLogger.Instance, includeNested: true);
        var result = cache.ReadCacheOnly([task]);

        Assert.Same(signal, result.Merge[task.TaskKey]);
        Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);

        var freshness = cache.ReadFreshness([task]);
        Assert.Equal(stateAt, freshness.GitStateAt);
        Assert.False(freshness.Stale);
    }

    [Fact]
    public void ReadCacheOnly_AcrossMultipleRepositories_MergesEachRepositorysSnapshot()
    {
        var cache = new TaskListGitProjectionCache();
        var taskA = Job("task-a", "watch-a");
        var taskB = Job("task-b", "watch-b");
        cache.SetSnapshot("watch-a", ProjectionFor(taskA, "task/a"), DateTimeOffset.UtcNow);
        cache.SetSnapshot("watch-b", ProjectionFor(taskB, "task/b"), DateTimeOffset.UtcNow);

        var merged = cache.ReadCacheOnly([taskA, taskB]);

        Assert.Equal("task/a", merged.Merge[taskA.TaskKey].Branch);
        Assert.Equal("task/b", merged.Merge[taskB.TaskKey].Branch);
    }

    [Fact]
    public void ReadCacheOnly_DuplicateTaskKeys_UsesOneRepositorysVersionWithoutThrowing()
    {
        var cache = new TaskListGitProjectionCache();
        var taskA = Job("same", "watch-a") with { TaskKey = "same" };
        var taskB = Job("same", "watch-b") with { TaskKey = "same" };
        cache.SetSnapshot(taskA.WatchPath, ProjectionFor(taskA, "task/a") with
        {
            Signatures = new Dictionary<string, string> { ["same"] = TaskGitSignature.For(taskA) },
            SubjectVersions = new Dictionary<string, long> { ["same"] = 0 },
        }, DateTimeOffset.UtcNow);
        cache.SetSnapshot(taskB.WatchPath, ProjectionFor(taskB, "task/b") with
        {
            Signatures = new Dictionary<string, string> { ["same"] = TaskGitSignature.For(taskB) },
            SubjectVersions = new Dictionary<string, long> { ["same"] = 0 },
        }, DateTimeOffset.UtcNow);

        var merged = cache.ReadCacheOnly([taskA, taskB]);

        Assert.Equal("task/b", merged.Merge["same"].Branch);
        Assert.Equal(TaskGitSignature.For(taskB), merged.TaskSignatures["same"]);
        Assert.Equal(0, merged.TaskSubjectVersions["same"]);

        var changedB = taskB with { Commits = [] };
        var stale = cache.ReadCacheOnly([taskA, changedB]);
        Assert.Empty(stale.Merge);
        Assert.Empty(stale.TaskSignatures);
    }

    [Fact]
    public void ReadCacheOnly_ProjectAcrossRepositories_ServesEachTaskFromItsOwnRepositorySnapshot()
    {
        (bool Exists, string? Hash) stamp = (true, "first");
        var cache = new TaskListGitProjectionCache(_ => stamp);
        var taskA = Job("task-a", "watch-a") with { FolderPath = Path.Combine("watch-a", "tasks", "task-a") };
        var taskB = Job("task-b", "watch-b") with { FolderPath = Path.Combine("watch-b", "tasks", "task-b") };
        var subjectB = ReviewSubjectStore.PathFor(taskB.FolderPath);
        cache.SeedTaskInput(subjectB);
        stamp = (true, "second");
        Assert.True(cache.MarkTaskInputChanged(subjectB));

        // watch-a holds an older-version snapshot without per-task signatures
        // that still carries a fact under task B's key; watch-b holds the
        // current version, bound to B's signature and subject generation 1.
        var older = ProjectionFor(taskA, "task/a-older");
        ((Dictionary<string, TaskMergeSignal>)older.Merge)[taskB.TaskKey] =
            new TaskMergeSignal { Branch = "task/b-from-watch-a" };
        cache.SetSnapshot(taskA.WatchPath, older, DateTimeOffset.UtcNow);
        cache.SetSnapshot(taskB.WatchPath, ProjectionFor(taskB, "task/b-current") with
        {
            Signatures = new Dictionary<string, string> { [taskB.TaskKey] = TaskGitSignature.For(taskB) },
            SubjectVersions = new Dictionary<string, long> { [taskB.TaskKey] = 1 },
        }, DateTimeOffset.UtcNow);

        foreach (var order in new[] { new[] { taskA, taskB }, new[] { taskB, taskA } })
        {
            var merged = cache.ReadCacheOnly(order);

            Assert.Equal("task/a-older", merged.Merge[taskA.TaskKey].Branch);
            Assert.Equal("task/b-current", merged.Merge[taskB.TaskKey].Branch);
            Assert.Equal(TaskGitSignature.For(taskB), merged.TaskSignatures[taskB.TaskKey]);
            Assert.Equal(1, merged.TaskSubjectVersions[taskB.TaskKey]);
            Assert.False(merged.TaskSignatures.ContainsKey(taskA.TaskKey));
            Assert.Equal(cache.ReadTask(taskB).Data?.Merge?.Branch, merged.Merge[taskB.TaskKey].Branch);
        }
        Assert.DoesNotContain(taskB.TaskKey, cache.ReadCacheOnly([taskA]).Merge.Keys);
    }

    [Fact]
    public void ReadFreshness_WhenOneOfSeveralRepositoriesNeverIndexed_IsStale()
    {
        var cache = new TaskListGitProjectionCache();
        var taskA = Job("task-a", "watch-a");
        var taskB = Job("task-b", "watch-b");
        cache.SetSnapshot("watch-a", ProjectionFor(taskA, "task/a"), DateTimeOffset.UtcNow);
        // watch-b never indexed.

        var freshness = cache.ReadFreshness([taskA, taskB]);

        Assert.True(freshness.Stale);
    }

    [Fact]
    public void ReadFreshness_WhileMarkedRefreshing_IsStaleButKeepsServingThePriorSnapshot()
    {
        var cache = new TaskListGitProjectionCache();
        var task = Job("task-1", "watch-a");
        var signal = new TaskMergeSignal { Branch = "task/task-1" };
        cache.SetSnapshot("watch-a", ProjectionFor(task, "task/task-1"), DateTimeOffset.UtcNow);

        cache.MarkRefreshing("watch-a");

        var freshness = cache.ReadFreshness([task]);
        Assert.True(freshness.Stale);
        var stillServed = cache.ReadCacheOnly([task]);
        Assert.Equal("task/task-1", stillServed.Merge[task.TaskKey].Branch);
    }

    [Fact]
    public void TaskResource_UsesCompletedGenerationAndSuppressesSupersededAttempt()
    {
        var cache = new TaskListGitProjectionCache();
        var task = Job("task-1", "watch-a");
        var projection = ProjectionFor(task, "task/first") with
        {
            Signatures = new Dictionary<string, string>
            {
                [task.TaskKey] = TaskGitSignature.For(task),
            },
        };
        var computedAt = DateTimeOffset.UtcNow;
        cache.SetSnapshot(task.WatchPath, projection, computedAt);

        using var telemetry = GitProcessTelemetry.BeginRequest("task/detail/git", NullLogger.Instance);
        var ready = cache.ReadTask(task);
        Assert.Equal("ready", ready.State);
        Assert.Equal(computedAt, ready.ComputedAt);
        Assert.Equal("task/first", ready.Data!.Merge!.Branch);

        var nextAttempt = task with { Commits = [new TaskCommitInfo
        {
            Sha = "abcdef0123456789abcdef0123456789abcdef01",
            FilesChanged = 1,
        }] };
        var stale = cache.ReadTask(nextAttempt);
        Assert.Equal("stale", stale.State);
        Assert.Equal("task-changed", stale.ReasonCode);
        Assert.Null(stale.Data);
        Assert.Empty(cache.ReadCacheOnly([nextAttempt]).Merge);
        Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);

        cache.MarkFailed(task.WatchPath, "timeout");
        var failed = cache.ReadTask(task);
        Assert.Equal("stale", failed.State);
        Assert.Equal("timeout", failed.ReasonCode);
        Assert.Equal("task/first", failed.Data!.Merge!.Branch);
        Assert.True(cache.ReadFreshness([task]).Stale);
    }

    [Fact]
    public void TaskResource_ColdFailureIsUnavailableAndAReadNeverSchedulesWork()
    {
        var cache = new TaskListGitProjectionCache();
        var task = Job("task-1", "watch-a");
        Assert.Equal("warming", cache.ReadTask(task).State);
        cache.MarkFailed(task.WatchPath, "repository-unavailable");
        var missing = cache.ReadTask(task);
        Assert.Equal("unavailable", missing.State);
        Assert.Null(missing.Data);
        Assert.Equal("repository-unavailable", missing.ReasonCode);
    }

    [Fact]
    public void PublishedSnapshot_DoesNotFollowBuilderDictionaryMutation()
    {
        var cache = new TaskListGitProjectionCache();
        var task = Job("task-1", "watch-a");
        var source = new Dictionary<string, TaskMergeSignal>
        {
            [task.TaskKey] = new() { Branch = "task/first" },
        };
        cache.SetSnapshot(task.WatchPath, ProjectionFor(task, "unused") with { Merge = source },
            DateTimeOffset.UtcNow);
        source[task.TaskKey] = new TaskMergeSignal { Branch = "task/second" };
        Assert.Equal("task/first", cache.ReadCacheOnly([task]).Merge[task.TaskKey].Branch);
    }

    [Fact]
    public void ReviewSubjectChange_SuppressesPriorPositiveUntilNextSnapshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "git-subject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var task = Job("task-1", "watch-a") with { FolderPath = folder };
            var cache = new TaskListGitProjectionCache();
            var subject = ReviewSubjectStore.PathFor(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(subject)!);
            File.WriteAllText(subject, "old");
            var originalTime = File.GetLastWriteTimeUtc(subject);
            cache.SeedTaskInput(subject);
            cache.SetSnapshot(task.WatchPath, ProjectionFor(task, "task/old") with
            {
                Signatures = new Dictionary<string, string> { [task.TaskKey] = TaskGitSignature.For(task) },
                SubjectVersions = new Dictionary<string, long> { [task.TaskKey] = cache.SubjectVersion(folder) },
            }, DateTimeOffset.UtcNow);
            Assert.Equal("ready", cache.ReadTask(task).State);
            File.WriteAllText(subject, "new");
            File.SetLastWriteTimeUtc(subject, originalTime);
            Assert.True(cache.MarkTaskInputChanged(subject));
            Assert.False(cache.MarkTaskInputChanged(subject));
            Assert.Equal("stale", cache.ReadTask(task).State);
            Assert.Null(cache.ReadTask(task).Data);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewSubjectWatcherReadFailure_InvalidatesFactAndRetainsStampForRetry(bool denied)
    {
        var folder = Path.Combine(Path.GetTempPath(), "git-subject-" + Guid.NewGuid().ToString("N"));
        var task = Job("task-1", "watch-a") with { FolderPath = folder };
        var subject = ReviewSubjectStore.PathFor(task.FolderPath);
        (bool Exists, string? Hash) stamp = (true, "first");
        Exception? readFailure = null;
        var cache = new TaskListGitProjectionCache(_ =>
        {
            if (readFailure is not null) throw readFailure;
            return stamp;
        });
        cache.SeedTaskInput(subject);
        cache.SetSnapshot(task.WatchPath, ProjectionFor(task, "task/first") with
        {
            Signatures = new Dictionary<string, string> { [task.TaskKey] = TaskGitSignature.For(task) },
            SubjectVersions = new Dictionary<string, long> { [task.TaskKey] = 0 },
        }, DateTimeOffset.UtcNow);
        Assert.Equal("ready", cache.ReadTask(task).State);

        readFailure = denied ? new UnauthorizedAccessException() : new IOException();
        Assert.True(cache.MarkTaskInputChanged(subject));
        Assert.Equal(1, cache.SubjectVersion(task.FolderPath));
        Assert.Equal("stale", cache.ReadTask(task).State);
        Assert.Null(cache.ReadTask(task).Data);

        readFailure = null;
        Assert.False(cache.MarkTaskInputChanged(subject));
        stamp = (true, "second");
        Assert.True(cache.MarkTaskInputChanged(subject));
        Assert.Equal(2, cache.SubjectVersion(task.FolderPath));
    }

    [Fact]
    public void CaseDistinctRepositoryPaths_OrdinalComparer_KeepSeparateSnapshotsAndSubjectGenerations()
    {
        var (taskA, taskB) = CaseDistinctTasks();
        (bool Exists, string? Hash) stamp = (true, "first");
        var cache = new TaskListGitProjectionCache(_ => stamp, StringComparer.Ordinal);
        var subjectA = ReviewSubjectStore.PathFor(taskA.FolderPath);
        var subjectB = ReviewSubjectStore.PathFor(taskB.FolderPath);
        cache.SeedTaskInput(subjectA);
        cache.SeedTaskInput(subjectB);
        SetCaseDistinctSnapshots(cache, taskA, taskB);

        Assert.Equal("task/a", cache.ReadTask(taskA).Data?.Merge?.Branch);
        Assert.Equal("task/b", cache.ReadTask(taskB).Data?.Merge?.Branch);
        var merged = cache.ReadCacheOnly([taskA, taskB]);
        Assert.Equal("task/a", merged.Merge[taskA.TaskKey].Branch);
        Assert.Equal("task/b", merged.Merge[taskB.TaskKey].Branch);
        Assert.False(cache.ReadFreshness([taskA, taskB]).Stale);

        stamp = (true, "second");
        Assert.True(cache.MarkTaskInputChanged(subjectA));
        Assert.Equal(1, cache.SubjectVersion(taskA.FolderPath));
        Assert.Equal(0, cache.SubjectVersion(taskB.FolderPath));
        Assert.Equal("stale", cache.ReadTask(taskA).State);
        Assert.Equal("ready", cache.ReadTask(taskB).State);
        // B keeps its own sidecar stamp, so its change is detected separately.
        Assert.True(cache.MarkTaskInputChanged(subjectB));
        Assert.Equal(1, cache.SubjectVersion(taskB.FolderPath));
    }

    [Fact]
    public void CaseDistinctRepositoryPaths_CaseInsensitiveComparer_ShareSnapshotsAndSubjectGenerations()
    {
        var (taskA, taskB) = CaseDistinctTasks();
        (bool Exists, string? Hash) stamp = (true, "first");
        var cache = new TaskListGitProjectionCache(_ => stamp, StringComparer.OrdinalIgnoreCase);
        var subjectA = ReviewSubjectStore.PathFor(taskA.FolderPath);
        var subjectB = ReviewSubjectStore.PathFor(taskB.FolderPath);
        cache.SeedTaskInput(subjectA);
        cache.SeedTaskInput(subjectB);
        SetCaseDistinctSnapshots(cache, taskA, taskB);

        // One repository entry: B's snapshot replaced A's.
        Assert.Null(cache.ReadTask(taskA).Data);
        Assert.Equal("task/b", cache.ReadTask(taskB).Data?.Merge?.Branch);
        var merged = cache.ReadCacheOnly([taskA, taskB]);
        Assert.False(merged.Merge.ContainsKey(taskA.TaskKey));
        Assert.Equal("task/b", merged.Merge[taskB.TaskKey].Branch);

        stamp = (true, "second");
        Assert.True(cache.MarkTaskInputChanged(subjectA));
        Assert.Equal(1, cache.SubjectVersion(taskA.FolderPath));
        Assert.Equal(1, cache.SubjectVersion(taskB.FolderPath));
        Assert.Equal("stale", cache.ReadTask(taskB).State);
        // The shared sidecar stamp already holds "second".
        Assert.False(cache.MarkTaskInputChanged(subjectB));
    }

    [Fact]
    public void DefaultPathComparer_IsTheSharedFileSystemComparer()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Assert.Same(expected, FileSystemPathComparer.Instance);
        var (taskA, taskB) = CaseDistinctTasks();
        var cache = new TaskListGitProjectionCache();
        SetCaseDistinctSnapshots(cache, taskA, taskB);
        var separate = FileSystemPathComparer.Instance == StringComparer.Ordinal;
        Assert.Equal(separate ? "task/a" : null, cache.ReadTask(taskA).Data?.Merge?.Branch);
    }

    [Fact]
    public async Task BuildProjectionAsync_StartsAllLookupsBeforeWaitingForCompletion()
    {
        var task = Job("task-1", "watch-a");
        var started = 0;
        var merge = new TaskCompletionSource<Dictionary<string, TaskMergeSignal>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var integration = new TaskCompletionSource<Dictionary<string, TaskIntegrationStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource<Dictionary<string, TaskPublishSignal>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var testRuns = new TaskCompletionSource<Dictionary<string, TaskTestRunEvidence>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewProjection = new TaskCompletionSource<Dictionary<string, AgentStudio.Review.ReviewProjectionView>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<T> Start<T>(TaskCompletionSource<T> completion)
        {
            started++;
            return completion.Task;
        }

        var projectionTask = TaskListGitProjectionCache.BuildProjectionAsync(
            [task],
            _ => Start(merge),
            _ => Start(integration),
            _ => Start(publish),
            _ => Start(testRuns),
            _ => Start(reviewProjection));

        Assert.Equal(5, started);
        Assert.False(projectionTask.IsCompleted);
        merge.SetResult(new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal));
        integration.SetResult(new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal));
        publish.SetResult(new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal));
        testRuns.SetResult(new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal));
        reviewProjection.SetResult(new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal));
        var projection = await projectionTask;

        Assert.Empty(projection.Merge);
        Assert.Empty(projection.Integration);
        Assert.Empty(projection.Publish);
        Assert.Empty(projection.TestRuns);
        Assert.Empty(projection.ReviewProjection);
    }

    private static (TaskInfo A, TaskInfo B) CaseDistinctTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "case-" + Guid.NewGuid().ToString("N"));
        var repoA = Path.Combine(root, "repo");
        var repoB = Path.Combine(root, "REPO");
        return (
            Job("task-1", repoA) with { FolderPath = Path.Combine(repoA, "tasks", "task-1") },
            Job("task-1", repoB) with { FolderPath = Path.Combine(repoB, "tasks", "task-1") });
    }

    private static void SetCaseDistinctSnapshots(TaskListGitProjectionCache cache, TaskInfo taskA, TaskInfo taskB)
    {
        foreach (var (task, branch) in new[] { (taskA, "task/a"), (taskB, "task/b") })
        {
            cache.SetSnapshot(task.WatchPath, ProjectionFor(task, branch) with
            {
                Signatures = new Dictionary<string, string> { [task.TaskKey] = TaskGitSignature.For(task) },
                SubjectVersions = new Dictionary<string, long> { [task.TaskKey] = 0 },
            }, DateTimeOffset.UtcNow);
        }
    }

    private static TaskListGitProjection ProjectionFor(TaskInfo task, string branch)
        => new(
            new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal)
            {
                [task.TaskKey] = new TaskMergeSignal { Branch = branch },
            },
            new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
            new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
            new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal),
            new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal));

    private static TaskInfo Job(string id, string watchPath)
        => new()
        {
            Id = id,
            TaskKey = $"{watchPath}::{id}",
            Title = id,
            State = TaskStates.Completed,
            ProjectName = "project",
            WatchPath = watchPath,
            Commits =
            [
                new TaskCommitInfo
                {
                    Sha = "0123456789abcdef0123456789abcdef01234567",
                    FilesChanged = 1,
                },
            ],
        };
}
