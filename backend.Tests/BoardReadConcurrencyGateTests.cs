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

        var first = Task.Run(() => cache.GetOrBuild("generation-1", Build));
        await started.Task;
        var second = Task.Run(() => cache.GetOrBuild("generation-1", Build));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, builds);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public void Changed_generation_builds_new_body()
    {
        var cache = new BoardReadConcurrencyGate();
        var builds = 0;
        byte[] Build() => [(byte)Interlocked.Increment(ref builds)];

        Assert.Same(cache.GetOrBuild("generation-1", Build), cache.GetOrBuild("generation-1", Build));
        Assert.NotSame(cache.GetOrBuild("generation-1", Build), cache.GetOrBuild("generation-2", Build));
        Assert.Equal(2, builds);
    }

    [Fact]
    public void Failed_build_does_not_poison_waiting_readers()
    {
        var cache = new BoardReadConcurrencyGate();
        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrBuild("generation-1", () => throw new InvalidOperationException()));
        Assert.Equal([1], cache.GetOrBuild("generation-1", () => new byte[] { 1 }));
    }

    [Fact]
    public void Card_summary_is_reused_within_generation_and_rebuilt_after_invalidation()
    {
        var cache = new BoardReadConcurrencyGate();
        var builds = 0;
        byte[] Build() => [(byte)Interlocked.Increment(ref builds)];

        Assert.Same(cache.GetOrBuildCard("generation-1", "AGT-3043", Build),
            cache.GetOrBuildCard("generation-1", "AGT-3043", Build));
        Assert.NotSame(cache.GetOrBuildCard("generation-1", "AGT-3043", Build),
            cache.GetOrBuildCard("generation-2", "AGT-3043", Build));
        Assert.Equal(2, builds);
    }
}
