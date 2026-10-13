using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BoardReadConcurrencyGateTests
{
    [Fact]
    public void Visibility_digest_distinguishes_same_count_boards_and_ignores_order()
    {
        var first = new[] { new TaskInfo { TaskKey = "project-a::one" },
            new TaskInfo { TaskKey = "project-a::two" } };
        var other = new[] { first[0], new TaskInfo { TaskKey = "project-b::two" } };
        Assert.NotEqual(BoardReadSignatureSource.VisibleSetDigest(first),
            BoardReadSignatureSource.VisibleSetDigest(other));
        Assert.Equal(BoardReadSignatureSource.VisibleSetDigest(first),
            BoardReadSignatureSource.VisibleSetDigest(first.Reverse().ToArray()));
    }

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

        var first = cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, CancellationToken.None);
        await started.Task;
        var second = cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, CancellationToken.None);
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

        Assert.Same(await cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, CancellationToken.None),
            await cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, CancellationToken.None));
        Assert.NotSame(await cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, CancellationToken.None),
            await cache.GetOrBuildAsync("generation-2", "tasks/grouped", Build, CancellationToken.None));
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task Failed_build_does_not_poison_waiting_readers()
    {
        var cache = new BoardReadConcurrencyGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrBuildAsync("generation-1", "tasks/grouped", () => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal([1], (await cache.GetOrBuildAsync("generation-1", "tasks/grouped", () => new byte[] { 1 }, CancellationToken.None)).Bytes);
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
        var first = cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, CancellationToken.None);
        await started.Task;
        var waiter = cache.GetOrBuildAsync("generation-1", "tasks/grouped", Build, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();
        Assert.Equal([1], (await first).Bytes);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task A_rebuilt_body_with_the_same_bytes_keeps_its_tag()
    {
        // An index rescan that found nothing moves the signature; the rebuilt
        // body is byte-identical, so the client's copy must still validate.
        var cache = new BoardReadConcurrencyGate();
        var before = await cache.GetOrBuildAsync("generation-1", "tasks/grouped", () => [1, 2, 3], CancellationToken.None);
        var after = await cache.GetOrBuildAsync("generation-2", "tasks/grouped", () => [1, 2, 3], CancellationToken.None);
        var changed = await cache.GetOrBuildAsync("generation-3", "tasks/grouped", () => [1, 2, 4], CancellationToken.None);

        Assert.NotSame(before, after);
        Assert.Equal(before.ETag, after.ETag);
        Assert.NotEqual(before.ETag, changed.ETag);
    }

    [Fact]
    public async Task Variants_with_identical_bytes_never_share_a_tag()
    {
        var cache = new BoardReadConcurrencyGate();
        var standard = await cache.GetOrBuildAsync("a", "tasks/grouped?fixtures=False", () => [1], CancellationToken.None);
        var fixtures = await cache.GetOrBuildAsync("b", "tasks/grouped?fixtures=True", () => [1], CancellationToken.None);
        Assert.NotEqual(standard.ETag, fixtures.ETag);
        Assert.StartsWith("\"", standard.ETag, StringComparison.Ordinal);
    }
}
