using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2869 matrix for the pure verdict that separates a live remote run from a
/// phantom one. It is the only place the board's remote-running / disconnected /
/// stale distinction is decided, so the branch table is asserted directly
/// rather than through the surrounding execution-location projection.
/// </summary>
public sealed class RemoteRunStalenessPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 5, 27, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("active", 0, RemoteRunLiveness.Running)]
    [InlineData("active", 74, RemoteRunLiveness.Running)]
    [InlineData("active", 76, RemoteRunLiveness.Disconnected)]
    // An expired or released lease means the heartbeat stopped before the lease
    // it was renewing: nobody is driving the run any more.
    [InlineData("expired", 0, RemoteRunLiveness.Stale)]
    [InlineData("expired", 600, RemoteRunLiveness.Stale)]
    [InlineData("released", 5, RemoteRunLiveness.Stale)]
    [InlineData("none", 5, RemoteRunLiveness.Stale)]
    public void A_leased_run_is_stale_once_no_lease_covers_its_heartbeat(
        string leaseState,
        int heartbeatAgeSeconds,
        RemoteRunLiveness expected)
    {
        Assert.Equal(
            expected,
            RemoteRunStalenessPolicy.ForLeasedRun(
                leaseState,
                Now.AddSeconds(-heartbeatAgeSeconds),
                Now));
    }

    [Theory]
    [InlineData(0, RemoteRunLiveness.Running)]
    [InlineData(179, RemoteRunLiveness.Running)]
    [InlineData(181, RemoteRunLiveness.Stale)]
    public void An_ownerless_run_is_live_only_while_its_replay_keeps_arriving(
        int activityAgeSeconds,
        RemoteRunLiveness expected)
    {
        Assert.Equal(
            expected,
            RemoteRunStalenessPolicy.ForOwnerlessRun(Now.AddSeconds(-activityAgeSeconds), Now));
    }

    [Fact]
    public void An_ownerless_run_with_no_activity_at_all_is_stale()
    {
        Assert.Equal(
            RemoteRunLiveness.Stale,
            RemoteRunStalenessPolicy.ForOwnerlessRun(null, Now));
    }

    [Fact]
    public void A_live_run_needs_no_explanation()
    {
        Assert.Null(RemoteRunStalenessPolicy.DescribeLastRunnerEvent(
            RemoteRunLiveness.Running, Now, Now));
    }

    [Fact]
    public void A_disconnected_run_names_its_last_heartbeat_and_its_still_valid_lease()
    {
        var described = RemoteRunStalenessPolicy.DescribeLastRunnerEvent(
            RemoteRunLiveness.Disconnected, Now.AddMinutes(-2), Now.AddMinutes(-9));

        Assert.Contains("Last runner heartbeat", described);
        Assert.Contains("lease is still valid", described);
    }

    [Fact]
    public void A_stale_run_names_the_freshest_evidence_and_says_nobody_owns_it()
    {
        var heartbeat = Now.AddMinutes(-9);
        var activity = Now.AddMinutes(-11);

        var described = RemoteRunStalenessPolicy.DescribeLastRunnerEvent(
            RemoteRunLiveness.Stale, heartbeat, activity);

        Assert.Contains(heartbeat.ToString("u"), described);
        Assert.Contains("no fenced authority is driving this run", described);
    }

    [Fact]
    public void A_stale_run_with_no_recorded_evidence_says_so_instead_of_inventing_a_time()
    {
        var described = RemoteRunStalenessPolicy.DescribeLastRunnerEvent(
            RemoteRunLiveness.Stale, null, null);

        Assert.Equal(
            "No runner event has been recorded; no fenced authority is driving this run.",
            described);
    }
}
