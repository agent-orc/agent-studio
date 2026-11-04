using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the policy receives every timestamp as a pure input and never reads now.

/// <summary>Security model SM-2: the pure decisions of the kill switch, as a matrix.</summary>
public sealed class OperatorKillSwitchPolicyTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(61);

    public static TheoryData<TimeSpan?, string, TimeSpan?, bool, string?> Drills => new()
    {
        { Fast, OperatorKillPropagationStates.Revoked, Fast, true, null },
        { Fast, OperatorKillPropagationStates.NotConfigured, null, true, null },
        { null, OperatorKillPropagationStates.Revoked, Fast, true, "studio-not-refused" },
        { Slow, OperatorKillPropagationStates.Revoked, Fast, true, "studio-too-slow" },
        { Fast, OperatorKillPropagationStates.Pending, Fast, true, "operations-server-unreachable" },
        { Fast, OperatorKillPropagationStates.Revoked, Slow, true, "operations-server-too-slow" },
        { Fast, OperatorKillPropagationStates.Revoked, Fast, false, "other-sessions-affected" },
        { TimeSpan.FromSeconds(60), OperatorKillPropagationStates.NotConfigured, null, true, null },
    };

    [Theory]
    [MemberData(nameof(Drills))]
    public void A_drill_passes_only_when_both_refusals_land_within_the_budget_and_other_sessions_stay(
        TimeSpan? studio, string operationsState, TimeSpan? operations, bool kept, string? failure)
        => Assert.Equal(failure, OperatorKillSwitchPolicy.DrillFailure(studio, operationsState, operations, kept));

    [Theory]
    [InlineData(null, 720, false)]
    [InlineData(-1, 720, true)]
    [InlineData(0, 720, true)]
    [InlineData(1, 720, false)]
    [InlineData(-1, 0, false)]
    public void A_drill_is_due_once_its_time_passed_and_never_with_the_schedule_off(int? minutesUntilDue, int intervalHours, bool due)
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        DateTime? next = minutesUntilDue is { } minutes ? now.AddMinutes(minutes) : null;
        Assert.Equal(due, OperatorKillSwitchPolicy.DrillDue(next, now, TimeSpan.FromHours(intervalHours)));
    }

    [Fact]
    public void The_first_drill_is_scheduled_one_interval_after_start_and_an_existing_schedule_is_kept()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var interval = TimeSpan.FromDays(30);
        Assert.Equal(now + interval, OperatorKillSwitchPolicy.NextDrillAt(null, now, interval));
        Assert.Equal(now.AddDays(2), OperatorKillSwitchPolicy.NextDrillAt(now.AddDays(2), now, interval));
        Assert.Null(OperatorKillSwitchPolicy.NextDrillAt(now.AddDays(2), now, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(40, 30)]
    public void A_failed_revocation_is_retried_with_capped_backoff(int attempts, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), OperatorKillSwitchPolicy.RetryDelay(attempts));

    [Fact]
    public void Only_a_pending_revocation_past_its_backoff_is_retried()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(OperatorKillSwitchPolicy.RetryDue(new OperatorKillPropagation(OperatorKillPropagationStates.Pending), now));
        Assert.False(OperatorKillSwitchPolicy.RetryDue(new OperatorKillPropagation(OperatorKillPropagationStates.Pending, 2, now.AddSeconds(-3)), now));
        Assert.True(OperatorKillSwitchPolicy.RetryDue(new OperatorKillPropagation(OperatorKillPropagationStates.Pending, 2, now.AddSeconds(-4)), now));
        Assert.False(OperatorKillSwitchPolicy.RetryDue(new OperatorKillPropagation(OperatorKillPropagationStates.Revoked, 1, now.AddHours(-1)), now));
        Assert.False(OperatorKillSwitchPolicy.RetryDue(new OperatorKillPropagation(OperatorKillPropagationStates.NotConfigured), now));
    }
}
