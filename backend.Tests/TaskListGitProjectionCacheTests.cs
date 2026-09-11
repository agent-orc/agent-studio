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
