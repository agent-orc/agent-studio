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
            var subject = Path.Combine(folder, ReviewSubjectStore.FileName);
            cache.SeedTaskInput(subject);
            cache.SetSnapshot(task.WatchPath, ProjectionFor(task, "task/old") with
            {
                Signatures = new Dictionary<string, string> { [task.TaskKey] = TaskGitSignature.For(task) },
                SubjectVersions = new Dictionary<string, long> { [task.TaskKey] = cache.SubjectVersion(folder) },
            }, DateTimeOffset.UtcNow);
            Assert.Equal("ready", cache.ReadTask(task).State);
            File.WriteAllText(subject, "new review subject");
            Assert.True(cache.MarkTaskInputChanged(subject));
            Assert.False(cache.MarkTaskInputChanged(subject));
            Assert.Equal("stale", cache.ReadTask(task).State);
            Assert.Null(cache.ReadTask(task).Data);
        }
        finally { Directory.Delete(folder, recursive: true); }
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
