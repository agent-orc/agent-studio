using AgentStudio.Pipeline;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateCancellationPolicyTests
{
    [Theory]
    [InlineData("old", "new", "review", "review", false, false, null, "superseded")]
    [InlineData("same", "same", "review", "ready", false, false, null, "superseded")]
    [InlineData("same", "same", "review", "review", true, false, null, "superseded")]
    [InlineData("old", "new", "review", "review", false, true, "operator", "superseded")]
    [InlineData("same", "same", "review", "review", false, true, null, "shutdown")]
    [InlineData("same", "same", "review", "review", false, false, "shutdown", "shutdown")]
    [InlineData("same", "same", "review", "review", false, false, null, "operator")]
    [InlineData("same", "same", "review", "review", false, true, "operator", "operator")]
    public void Ownership_precedes_cancellation_origin(string expected, string current,
        string lane, string currentLane, bool pending, bool shutdown, string? origin, string reason)
        => Assert.Equal(reason, GateCancellationPolicy.Decide(expected, current, lane, currentLane, pending, shutdown, origin));

    [Fact]
    public void Shutdown_retry_uses_one_post_step_budget_and_preserves_subject_identity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gate-cancel-" + Guid.NewGuid().ToString("N"));
        var clock = new TestClock();
        try
        {
            Assert.True(GateCancellationRetry.Schedule(folder, "sha", "attempt", 0,
                clock.GetUtcNow(), PostProcessingOutcomeTaxonomy.RetryBackoff(1)));
            var receipt = Assert.IsType<GateCancellationRetry>(GateCancellationRetry.Read(folder, "sha", "attempt"));
            Assert.Equal(1, receipt.RetryNumber);
            Assert.Null(GateCancellationRetry.Read(folder, "new", "attempt"));
            Assert.Null(GateCancellationRetry.Read(folder, "sha", "new"));
            Assert.False(GateCancellationRetry.Schedule(folder, "sha", "attempt", receipt.RetryNumber,
                clock.GetUtcNow(), TimeSpan.Zero));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
