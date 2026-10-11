using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BoardReadConcurrencyGateTests
{
    [Fact]
    public async Task Concurrent_readers_share_one_serialized_body()
    {
        var cache = new BoardReadConcurrencyGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        byte[] Build()
        {
            Interlocked.Increment(ref builds);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return [1, 2, 3];
        }

        var first = cache.GetOrBuildAsync("generation-1", Build, CancellationToken.None);
        await started.Task;
        var second = cache.GetOrBuildAsync("generation-1", Build, CancellationToken.None);
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, builds);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task Changed_generation_builds_new_body()
    {
        var cache = new BoardReadConcurrencyGate();
        var builds = 0;
        byte[] Build() => [(byte)Interlocked.Increment(ref builds)];

        Assert.Same(await cache.GetOrBuildAsync("generation-1", Build, CancellationToken.None),
            await cache.GetOrBuildAsync("generation-1", Build, CancellationToken.None));
        Assert.NotSame(await cache.GetOrBuildAsync("generation-1", Build, CancellationToken.None),
            await cache.GetOrBuildAsync("generation-2", Build, CancellationToken.None));
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task Failed_build_does_not_poison_waiting_readers()
    {
        var cache = new BoardReadConcurrencyGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrBuildAsync("generation-1", () => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal([1], await cache.GetOrBuildAsync("generation-1", () => new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Canceled_waiter_does_not_cancel_the_shared_build()
    {
        var cache = new BoardReadConcurrencyGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        byte[] Build()
        {
            Interlocked.Increment(ref builds);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return [1];
        }
        using var canceled = new CancellationTokenSource();
        var first = cache.GetOrBuildAsync("generation-1", Build, CancellationToken.None);
        await started.Task;
        var waiter = cache.GetOrBuildAsync("generation-1", Build, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();
        Assert.Equal([1], await first);
        Assert.Equal(1, builds);
    }
}
