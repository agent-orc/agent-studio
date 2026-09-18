using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2869 matrix for the pure decision that decides whether the daemon's poll
/// loop re-drives a finalization the Task Server refused while it was
/// restarting. Everything the daemon observes (durable result, active slot,
/// server reachability, the clock) is an explicit input here, so the branch
/// table can be asserted without a process, a filesystem, or a socket.
/// </summary>
public sealed class FinalizationRetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 5, 17, 53, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(9, 60)]
    public void Backoff_climbs_to_a_steady_minute(int attempts, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            FinalizationRetryPolicy.DelayAfter(attempts));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    [InlineData("running", true, false)]
    [InlineData(FinalizationRetryPolicy.ResultReadyStage, false, false)]
    [InlineData(FinalizationRetryPolicy.ResultReadyStage, true, true)]
    public void Deferral_requires_the_explicit_stage_and_the_durable_result(
        string? stage,
        bool durableResultReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            FinalizationRetryPolicy.CanDefer(stage, durableResultReady));
    }

    [Fact]
    public void First_failure_starts_the_pending_window_and_keeps_the_secured_delivery()
    {
        var teardown = new WorktreeTeardownResult(true, "task/AGT-2869", "abc", null, ResultSha: "def");

        var pending = FinalizationRetryPolicy.Schedule(null, "ResponseEnded", teardown, Now);

        Assert.Equal(1, pending.Attempts);
        Assert.Equal(Now, pending.PendingSinceUtc);
        Assert.Equal(Now.AddSeconds(15), pending.NextAttemptAtUtc);
        Assert.Equal("ResponseEnded", pending.LastReason);
        Assert.Same(teardown, pending.Teardown);
    }

    [Fact]
    public void Later_failures_keep_the_original_pending_instant_and_the_earlier_delivery()
    {
        var teardown = new WorktreeTeardownResult(true, "task/AGT-2869", "abc", null, ResultSha: "def");
        var first = FinalizationRetryPolicy.Schedule(null, "connection refused", teardown, Now);

        var second = FinalizationRetryPolicy.Schedule(
            first,
            "HTTP 503: service restarting",
            teardown: null,
            Now.AddSeconds(20));

        Assert.Equal(2, second.Attempts);
        Assert.Equal(Now, second.PendingSinceUtc);
        Assert.Equal(Now.AddSeconds(50), second.NextAttemptAtUtc);
        Assert.Equal("HTTP 503: service restarting", second.LastReason);
        Assert.Same(teardown, second.Teardown);
    }

    [Fact]
    public void A_slot_without_a_deferred_finalization_is_not_this_loop_s_business()
    {
        Assert.Equal(
            FinalizationRetryAction.Ignore,
            FinalizationRetryPolicy.Decide(
                "running", null, durableResultReady: true, slotIsActive: false, serverAnswered: true, Now));
    }

    [Theory]
    // phase, durable result, active, server answers -> action
    [InlineData("running", true, false, true, FinalizationRetryAction.Ignore)]
    [InlineData("finalizing", false, false, true, FinalizationRetryAction.Ignore)]
    [InlineData("finalizing", true, true, true, FinalizationRetryAction.Ignore)]
    [InlineData("finalizing", true, false, false, FinalizationRetryAction.ServerUnreachable)]
    [InlineData("finalizing", true, false, true, FinalizationRetryAction.Redrive)]
    public void Due_slots_are_re_driven_only_with_a_durable_result_and_a_server_that_answers(
        string phase,
        bool durableResultReady,
        bool slotIsActive,
        bool serverAnswered,
        FinalizationRetryAction expected)
    {
        var pending = FinalizationRetryPolicy.Schedule(null, "ResponseEnded", teardown: null, Now);

        var action = FinalizationRetryPolicy.Decide(
            phase,
            pending,
            durableResultReady,
            slotIsActive,
            serverAnswered,
            pending.NextAttemptAtUtc);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void A_slot_inside_its_backoff_waits_without_probing_the_server()
    {
        var pending = FinalizationRetryPolicy.Schedule(null, "ResponseEnded", teardown: null, Now);

        Assert.Equal(
            FinalizationRetryAction.Wait,
            FinalizationRetryPolicy.Decide(
                FinalizationRetryPolicy.Phase,
                pending,
                durableResultReady: true,
                slotIsActive: false,
                serverAnswered: true,
                pending.NextAttemptAtUtc.AddMilliseconds(-1)));
    }

    [Fact]
    public void Holding_an_in_flight_redrive_pushes_the_next_attempt_without_burning_one()
    {
        var pending = FinalizationRetryPolicy.Schedule(null, "ResponseEnded", teardown: null, Now);

        var held = FinalizationRetryPolicy.HoldFor(pending, Now.AddSeconds(15));

        Assert.Equal(pending.Attempts, held.Attempts);
        Assert.Equal(Now.AddSeconds(45), held.NextAttemptAtUtc);
    }

    [Fact]
    public void The_run_timeout_marks_a_late_delivery_but_never_ends_the_retries()
    {
        var pending = FinalizationRetryPolicy.Schedule(null, "ResponseEnded", teardown: null, Now);
        var timeout = TimeSpan.FromMinutes(90);

        Assert.False(FinalizationRetryPolicy.BeyondRunTimeout(pending, timeout, Now.AddMinutes(89)));
        Assert.True(FinalizationRetryPolicy.BeyondRunTimeout(pending, timeout, Now.AddMinutes(91)));
        Assert.Equal(
            FinalizationRetryAction.Redrive,
            FinalizationRetryPolicy.Decide(
                FinalizationRetryPolicy.Phase,
                pending,
                durableResultReady: true,
                slotIsActive: false,
                serverAnswered: true,
                Now.AddHours(4)));
    }
}
