using AgentStudio.Quota;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkspaceQuotaPolicyTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 18, 23, 59, 0, TimeSpan.Zero));

    [Fact]
    public void Week_rolls_over_on_monday_utc()
    {
        var before = WorkspaceQuotaPolicy.Week(_clock);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var after = WorkspaceQuotaPolicy.Week(_clock);
        Assert.Equal(before.End, after.Start);
        Assert.Equal(TimeSpan.FromDays(7), after.End - after.Start);
    }

    [Theory]
    [InlineData(79, "ok", true)]
    [InlineData(80, "warning", true)]
    [InlineData(100, "blocked", false)]
    public void Token_admission_warns_then_refuses_new_work(long tokens, string status, bool admits)
    {
        var limits = new QuotaLimits(100, 10, 2);
        var usage = new QuotaUsage(tokens, 1, 0, 0);
        Assert.Equal(status, WorkspaceQuotaPolicy.Status(limits, usage));
        Assert.Equal(admits, WorkspaceQuotaPolicy.Allows(limits, usage, hosting: false));
    }

    [Fact]
    public void Compute_and_hosting_limits_are_independent()
    {
        var limits = new QuotaLimits(100, 2, 1);
        Assert.False(WorkspaceQuotaPolicy.Allows(limits, new QuotaUsage(0, 0, 2, 0), hosting: false));
        Assert.True(WorkspaceQuotaPolicy.Allows(limits, new QuotaUsage(0, 0, 0, 1), hosting: false));
        Assert.False(WorkspaceQuotaPolicy.Allows(limits, new QuotaUsage(0, 0, 0, 1), hosting: true));
    }

    [Fact]
    public void Meter_clips_running_job_to_week_and_multiplies_granted_cores()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero));
        var (start, end) = WorkspaceQuotaPolicy.Week(clock);
        var hours = WorkspaceQuotaPolicy.CoreHours(start.AddHours(-1), null, 4, start, end, clock);
        Assert.Equal(8m, hours);
    }

    [Fact]
    public void Refusing_a_new_job_does_not_stop_metering_an_active_job()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero));
        var (start, end) = WorkspaceQuotaPolicy.Week(clock);
        var activeStart = start.AddHours(1);
        var before = WorkspaceQuotaPolicy.CoreHours(activeStart, null, 2, start, end, clock);
        Assert.False(WorkspaceQuotaPolicy.Allows(new QuotaLimits(100, 2, 1),
            new QuotaUsage(0, 0, before, 0), hosting: false));
        clock.Advance(TimeSpan.FromHours(1));
        var after = WorkspaceQuotaPolicy.CoreHours(activeStart, null, 2, start, end, clock);
        Assert.Equal(before + 2, after);
    }

    [Fact]
    public void Full_hosting_slots_are_their_own_state_and_never_block_coding()
    {
        var limits = new QuotaLimits(100, 10, 2);
        var full = new QuotaUsage(10, 0, 1, 2);
        Assert.Equal("hosting-full", WorkspaceQuotaPolicy.Status(limits, full));
        Assert.Equal("All 2 hosting slots are in use; new deploys wait until an environment stops.",
            WorkspaceQuotaPolicy.Message("hosting-full", limits));
        Assert.True(WorkspaceQuotaPolicy.Allows(limits, full, hosting: false));
        Assert.Equal("blocked", WorkspaceQuotaPolicy.Status(limits, full with { ModelTokens = 100 }));
        Assert.Equal("warning", WorkspaceQuotaPolicy.Status(limits, new QuotaUsage(85, 0, 0, 1)));
    }

    [Fact]
    public void Every_token_column_counts_once()
        => Assert.Equal(120, WorkspaceQuotaPolicy.ModelTokens(30, 20, 70, 0));

    [Fact]
    public void Claimed_deploy_without_report_counts_until_its_lease_ends()
    {
        var started = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(started.AddMinutes(2), WorkspaceQuotaPolicy.DeployEnd(started, null, _clock));
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(started.AddMinutes(5), WorkspaceQuotaPolicy.DeployEnd(started, null, _clock));
        Assert.Equal(started.AddMinutes(1), WorkspaceQuotaPolicy.DeployEnd(started, started.AddMinutes(1), _clock));
    }

    [Fact]
    public void Override_audit_uses_injected_clock_and_actor()
    {
        var expiry = WorkspaceQuotaPolicy.Week(_clock).End;
        var entry = WorkspaceQuotaPolicy.OverrideEntry(_clock, "ws", "project", "owner-1", "Incident response", expiry);
        Assert.Equal(_clock.GetUtcNow(), entry.At);
        Assert.Equal("owner-1", entry.Actor);
        Assert.Equal("Incident response", entry.Reason);
        Assert.Equal(expiry, entry.ExpiresAt);
    }
}
