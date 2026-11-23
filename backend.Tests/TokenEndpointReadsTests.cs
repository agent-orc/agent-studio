using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TokenEndpointReadsTests
{
    [Theory]
    [InlineData("summary:Studio")]
    [InlineData("unpriced:Studio")]
    public async Task SlowRead_ReturnsJsonProblem_ThenReusesTheCompletedFlight(string key)
    {
        var reads = new TokenEndpointReads(new FakeTimeProvider());
        var pending = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<object> Load() { calls++; return pending.Task; }
        var waiting = Assert.IsType<ProblemHttpResult>(await reads.ReadAsync(key, Load, wait: TimeSpan.Zero));
        Assert.Equal(503, waiting.StatusCode);
        Assert.NotEmpty(waiting.ProblemDetails.Detail!);
        var payload = new { hasData = false };
        pending.SetResult(payload);
        var ready = Assert.IsAssignableFrom<IValueHttpResult>(await reads.ReadAsync(key, Load));
        Assert.Same(payload, ready.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PermissionScopes_AreIsolated_AndFailuresAreRetryable()
    {
        var reads = new TokenEndpointReads(new FakeTimeProvider());
        var failed = await reads.ReadAsync("unpriced:private", () => Task.FromException<object>(new IOException()));
        Assert.IsType<ProblemHttpResult>(failed);
        var first = Assert.IsAssignableFrom<IValueHttpResult>(await reads.ReadAsync("unpriced:private", () => Task.FromResult<object>("private")));
        var other = Assert.IsAssignableFrom<IValueHttpResult>(await reads.ReadAsync("unpriced:public", () => Task.FromResult<object>("public")));
        Assert.Equal("private", first.Value);
        Assert.Equal("public", other.Value);
    }
    [Fact]
    public async Task PendingReads_AreBoundedAndAnExistingScopeCanStillRetry()
    {
        var reads = new TokenEndpointReads(new FakeTimeProvider());
        var pending = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<object> Load() { calls++; return pending.Task; }
        for (var index = 0; index < 64; index++)
            await reads.ReadAsync("scope:" + index, Load, wait: TimeSpan.Zero);
        var busy = Assert.IsType<ProblemHttpResult>(await reads.ReadAsync("scope:extra", Load, wait: TimeSpan.Zero));
        Assert.Contains("busy", busy.ProblemDetails.Detail!);
        await reads.ReadAsync("scope:0", Load, wait: TimeSpan.Zero);
        Assert.Equal(64, calls);
        pending.SetResult("ready");
    }

    [Fact]
    public async Task PendingRead_SurvivesTtl_AndCompletedResultsExpire()
    {
        var clock = new FakeTimeProvider();
        var reads = new TokenEndpointReads(clock);
        var pending = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<object> Load() { calls++; return pending.Task; }
        await reads.ReadAsync("summary", Load, wait: TimeSpan.Zero);
        clock.Advance(TimeSpan.FromMinutes(1));
        await reads.ReadAsync("summary", Load, wait: TimeSpan.Zero);
        Assert.Equal(1, calls);
        pending.SetResult("ready");
        await reads.ReadAsync("summary", Load);
        clock.Advance(TimeSpan.FromSeconds(31));
        await reads.ReadAsync("summary", Load);
        Assert.Equal(2, calls);
    }
}
