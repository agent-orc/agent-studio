using AgentStudio.Pipeline;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateCancellationPolicyTests
{
    [Theory]
    [InlineData("same", "same", "attempt", "attempt", "review", "review", false, true)]
    [InlineData("old", "new", "attempt", "attempt", "review", "review", false, false)]
    [InlineData("same", "same", "old", "new", "review", "review", false, false)]
    [InlineData("same", "same", "attempt", "attempt", "review", "progress", false, false)]
    [InlineData("same", "same", "attempt", "attempt", "review", "review", true, false)]
    public void Completed_gate_result_requires_current_delivery_and_attempt(
        string expectedSha, string liveSha, string expectedAttempt, string liveAttempt,
        string expectedLane, string liveLane, bool pending, bool owns)
        => Assert.Equal(owns, GateCancellationPolicy.OwnsResult(
            expectedSha, liveSha, expectedAttempt, liveAttempt,
            expectedLane, liveLane, pending));

    [Theory]
    [InlineData("old", "new", "review", "review", false, false, null, "superseded")]
    [InlineData("same", "same", "review", "ready", false, false, null, "superseded")]
    [InlineData("same", "same", "review", "review", true, false, null, "superseded")]
    [InlineData("old", "new", "review", "review", false, true, "operator", "superseded")]
    [InlineData("same", "same", "review", "review", false, true, null, "shutdown")]
    [InlineData("same", "same", "review", "review", false, false, "shutdown", "shutdown")]
    [InlineData("same", "same", "review", "review", false, false, null, "operator")]
    [InlineData("same", "same", "review", "review", false, true, "operator", "operator")]
    [InlineData("same", "same", "review", "review", false, false, "superseded", "superseded")]
    [InlineData("same", "same", "review", "review", false, true, "superseded", "superseded")]
    public void Ownership_precedes_cancellation_origin(string expected, string current,
        string lane, string currentLane, bool pending, bool shutdown, string? origin, string reason)
        => Assert.Equal(reason, GateCancellationPolicy.Decide(expected, current, lane, currentLane, pending, shutdown, origin));

    [Theory]
    // The service's own stop refunds the start the gate charged when it began.
    [InlineData(0, "shutdown", true, 0)]
    [InlineData(1, "shutdown", true, 1)]
    // A shutdown reported by a remote gate host while this service runs is charged.
    [InlineData(0, "shutdown", false, 1)]
    [InlineData(1, "shutdown", false, 2)]
    // An operator cancellation is charged even while the service stops.
    [InlineData(0, "operator", true, 1)]
    [InlineData(1, "operator", false, 2)]
    [InlineData(0, null, false, 1)]
    public void Only_the_services_own_stop_leaves_the_gate_start_budget_untouched(
        int startsBefore, string? reason, bool serviceStopping, int charged)
        => Assert.Equal(charged, GateCancellationPolicy.ChargedStartsAfterCancellation(
            startsBefore, reason, serviceStopping));

    [Fact]
    public void Repeated_service_stops_never_reach_the_start_budget()
    {
        var starts = 0;
        for (var restart = 0; restart < 10; restart++)
            starts = GateCancellationPolicy.ChargedStartsAfterCancellation(
                starts, GateCancellationReasons.Shutdown, serviceStopping: true);

        Assert.Equal(0, starts);
    }

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
