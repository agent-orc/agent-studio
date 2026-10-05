using AgentStudio.Tasks;

using Microsoft.AspNetCore.Http;

using Xunit;

namespace AgentStudio.Tests;

public sealed class BoardReadConcurrencyGateTests
{
    [Fact]
    public async Task Concurrent_reads_wait_until_the_previous_body_is_serialized()
    {
        var gate = new BoardReadConcurrencyGate();
        var finishBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var built = 0;
        var first = gate.Wrap(() =>
        {
            built++;
            return new Response(_ => finishBody.Task);
        }).ExecuteAsync(new DefaultHttpContext());
        var second = gate.Wrap(() =>
        {
            built++;
            return new Response(_ => Task.CompletedTask);
        }).ExecuteAsync(new DefaultHttpContext());

        try
        {
            // The first factory has returned, but its response is still being
            // serialized. A lock around only the factory leaves this race open.
            Assert.Equal(1, built);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            finishBody.TrySetResult();
            await Task.WhenAll(first, second);
        }

        Assert.Equal(2, built);
    }

    [Fact]
    public async Task Disconnected_waiter_never_builds_a_board_snapshot()
    {
        var gate = new BoardReadConcurrencyGate();
        var finishBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.Wrap(() => new Response(_ => finishBody.Task))
            .ExecuteAsync(new DefaultHttpContext());
        using var abort = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = abort.Token };
        var built = false;
        var waiting = gate.Wrap(() =>
        {
            built = true;
            return new Response(_ => Task.CompletedTask);
        }).ExecuteAsync(context);

        try
        {
            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(built);
        }
        finally
        {
            finishBody.TrySetResult();
            await first;
        }
    }

    [Fact]
    public async Task Already_disconnected_request_does_not_build_a_response()
    {
        var gate = new BoardReadConcurrencyGate();
        using var abort = new CancellationTokenSource();
        abort.Cancel();
        var context = new DefaultHttpContext { RequestAborted = abort.Token };
        var built = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Wrap(() =>
        {
            built = true;
            return new Response(_ => Task.CompletedTask);
        }).ExecuteAsync(context));

        Assert.False(built);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_projection_or_serialization_releases_the_next_reader(bool failDuringBuild)
    {
        var gate = new BoardReadConcurrencyGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.Wrap(() =>
        {
            if (failDuringBuild) throw new InvalidOperationException("projection failed");
            return new Response(_ => Task.FromException(new InvalidOperationException("serialization failed")));
        }).ExecuteAsync(new DefaultHttpContext()));

        var completed = false;
        await gate.Wrap(() => new Response(_ =>
        {
            completed = true;
            return Task.CompletedTask;
        })).ExecuteAsync(new DefaultHttpContext());
        Assert.True(completed);
    }

    [Fact]
    public async Task Cancelled_serialization_releases_the_next_reader()
    {
        var gate = new BoardReadConcurrencyGate();
        using var abort = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = abort.Token };
        var first = gate.Wrap(() => new Response(http => Task.Delay(Timeout.Infinite, http.RequestAborted)))
            .ExecuteAsync(context);
        var next = gate.Wrap(() => new Response(_ => Task.CompletedTask))
            .ExecuteAsync(new DefaultHttpContext());

        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await next;
    }

    private sealed class Response(Func<HttpContext, Task> execute) : IResult
    {
        public Task ExecuteAsync(HttpContext context) => execute(context);
    }
}
