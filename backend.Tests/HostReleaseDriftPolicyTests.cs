using AgentStudio.Runner;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for the pure host-versus-Stable comparison (AGT-2826). The
/// incident it closes: a runner host kept serving work on a three-week-old
/// agent-host release while Stable ran v0.3.0, and no surface said so.
/// </summary>
public sealed class HostReleaseDriftPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static StableReleaseIdentity Stable(
        string version = "0.3.0",
        string? commit = "aaaaaaa1111",
        DateTime? builtAtOverride = null)
        => new(version, commit, builtAtOverride ?? new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc));

    private static Contract.RunnerReleaseIdentityDto Host(
        string releaseId = "agt-host-20260823T060000Z-bbbbbbb",
        string version = "0.2.7",
        string? commit = "bbbbbbb2222",
        DateTime? builtAtOverride = null)
        => new(releaseId, version, commit, builtAtOverride ?? new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_host_three_weeks_older_than_stable_is_behind_and_alarms()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(Host(), Stable(), Now);

        Assert.Equal(HostReleaseDriftStates.Behind, verdict.State);
        Assert.Equal(19, verdict.BehindBy!.Value.Days);
        Assert.True(verdict.AlarmDue);
    }

    [Fact]
    public void The_same_commit_is_current_even_when_the_labels_differ()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(
            Host(releaseId: "agt-host-20260823T060000Z-aaaaaaa", version: "0.2.7", commit: "aaaaaaa1111"),
            Stable(),
            Now);

        Assert.Equal(HostReleaseDriftStates.Current, verdict.State);
        Assert.False(verdict.AlarmDue);
    }

    [Theory]
    [InlineData(7, true)]   // prefixes agree on the shortest length
    [InlineData(6, false)]  // too short to be evidence of the same code
    public void Commit_prefixes_only_count_from_seven_characters(int length, bool matches)
        => Assert.Equal(matches, HostReleaseDriftPolicy.CommitsMatch("aaaaaaa1111"[..length], "aaaaaaa1111"));

    [Fact]
    public void A_host_newer_than_stable_is_not_reported_as_drift()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(
            Host(version: "0.3.1", builtAtOverride: new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc)),
            Stable(),
            Now);

        Assert.Equal(HostReleaseDriftStates.Current, verdict.State);
        Assert.Null(verdict.BehindBy);
    }

    /// <summary>A rolling update legitimately leaves a host briefly behind.</summary>
    [Fact]
    public void A_host_behind_by_less_than_the_grace_window_is_marked_but_not_alarmed()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(
            Host(builtAtOverride: new DateTime(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc)),
            Stable(),
            Now);

        Assert.Equal(HostReleaseDriftStates.Behind, verdict.State);
        Assert.Equal(12, verdict.BehindBy!.Value.TotalHours);
        Assert.False(verdict.AlarmDue);
    }

    /// <summary>
    /// Without build stamps the version ordering still says which side is older,
    /// but not by how much, so the alarm falls back to how long this process has
    /// observed the drift.
    /// </summary>
    [Fact]
    public void Without_build_stamps_the_version_ordering_decides_and_the_alarm_waits()
    {
        var host = new Contract.RunnerReleaseIdentityDto("agt-host-local", "0.2.7");
        var stable = new StableReleaseIdentity("0.3.0", null, null);

        var fresh = HostReleaseDriftPolicy.Evaluate(host, stable, Now, Now);
        Assert.Equal(HostReleaseDriftStates.Behind, fresh.State);
        Assert.Null(fresh.BehindBy);
        Assert.False(fresh.AlarmDue);

        var persisted = HostReleaseDriftPolicy.Evaluate(host, stable, Now, Now.AddHours(-25));
        Assert.True(persisted.AlarmDue);
        Assert.Equal(25, persisted.BehindFor!.Value.TotalHours);
    }

    [Fact]
    public void A_host_that_reported_no_release_is_unknown_rather_than_behind()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(null, Stable(), Now, Now.AddDays(-30));

        Assert.Equal(HostReleaseDriftStates.Unknown, verdict.State);
        Assert.False(verdict.AlarmDue);
    }

    [Fact]
    public void An_unorderable_pair_is_unknown_rather_than_a_guess()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(
            new Contract.RunnerReleaseIdentityDto("agt-host-nightly", "nightly"),
            new StableReleaseIdentity("0.3.0", null, null),
            Now,
            Now.AddDays(-30));

        Assert.Equal(HostReleaseDriftStates.Unknown, verdict.State);
        Assert.False(verdict.AlarmDue);
    }

    [Fact]
    public void A_server_without_a_stable_identity_never_accuses_a_host()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(Host(), new StableReleaseIdentity("", null, null), Now);

        Assert.Equal(HostReleaseDriftStates.Unknown, verdict.State);
        Assert.False(verdict.AlarmDue);
    }

    [Fact]
    public void The_grace_window_is_configurable()
    {
        var verdict = HostReleaseDriftPolicy.Evaluate(
            Host(builtAtOverride: new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc)),
            Stable(),
            Now,
            grace: TimeSpan.FromHours(4));

        Assert.Equal(8, verdict.BehindBy!.Value.TotalHours);
        Assert.True(verdict.AlarmDue);
    }
}
