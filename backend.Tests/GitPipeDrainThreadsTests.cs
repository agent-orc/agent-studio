using AgentStudio.Git;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3567: the pipe drains of synchronous Git children reuse dedicated
/// threads instead of starting two per child. They must stay off the
/// ThreadPool, deliver results and failures, and never make one blocked drain
/// wait for another.
/// </summary>
public sealed class GitPipeDrainThreadsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Drain_runs_off_the_thread_pool_and_returns_its_result()
    {
        var (value, onPool, named) = await GitPipeDrainThreads
            .Run(() => (42, Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name))
            .WaitAsync(Deadline);

        Assert.Equal(42, value);
        Assert.False(onPool);
        Assert.Equal("git-pipe-drain", named);
    }

    [Fact]
    public async Task Drain_failure_reaches_the_waiter_and_the_thread_keeps_serving()
    {
        var failed = GitPipeDrainThreads.Run<int>(() => throw new IOException("pipe broke"));

        var error = await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(Deadline));
        Assert.Equal("pipe broke", error.Message);
        Assert.Equal(7, await GitPipeDrainThreads.Run(() => 7).WaitAsync(Deadline));
    }

    [Fact]
    public async Task Sequential_drains_reuse_an_idle_thread()
    {
        var threads = new HashSet<int>();
        for (var index = 0; index < 20; index++)
        {
            var id = await GitPipeDrainThreads.Run(() => Environment.CurrentManagedThreadId).WaitAsync(Deadline);
            threads.Add(id);
        }

        // Other tests may take the idle thread in between; at least one drain
        // still finds a thread that served an earlier one.
        Assert.True(threads.Count < 20, "every sequential drain started a new thread");
    }

    [Fact]
    public async Task Blocked_drains_never_wait_for_each_other()
    {
        const int count = 24;
        using var allStarted = new CountdownEvent(count);
        using var release = new ManualResetEventSlim();
        var drains = Enumerable.Range(0, count).Select(index => GitPipeDrainThreads.Run(() =>
        {
            allStarted.Signal();
            release.Wait();
            return index;
        })).ToArray();

        // Each drain blocks until all of them run: a bounded or shared thread
        // would never get there.
        Assert.True(allStarted.Wait(Deadline), $"{allStarted.CurrentCount} drains never started");
        release.Set();
        Assert.Equal(Enumerable.Range(0, count), await Task.WhenAll(drains).WaitAsync(Deadline));
    }
}
