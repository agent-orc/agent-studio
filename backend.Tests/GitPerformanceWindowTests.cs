using AgentStudio.Admin;
using AgentStudio.Git;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The Admin SLO panel's inputs (AGT-2726 requirement 4): percentiles over the
/// retained hour, the spawn rate, and the warnings an operator is meant to act
/// on. All of it is derived from the samples <see cref="GitProcessTelemetry"/>
/// already records - these tests pin that derivation, not a second counter.
/// </summary>
public sealed class GitPerformanceWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T10:00:00Z");

    [Fact]
    public void Percentiles_AreValuesThatActuallyHappened()
    {
        var window = new GitPerformanceWindow();
        // 1..100 ms, one sample each.
        for (var ms = 1; ms <= 100; ms++)
            window.RecordRequest("tasks/grouped", Now.AddSeconds(-ms), ms, spawns: 0);

        var endpoint = Assert.Single(window.Snapshot(Now).Endpoints);
        Assert.Equal("tasks/grouped", endpoint.Endpoint);
        Assert.Equal(100, endpoint.Calls);
        Assert.Equal(50, endpoint.P50Ms);
        Assert.Equal(95, endpoint.P95Ms);
        Assert.Equal(100, endpoint.MaxMs);
        Assert.Equal(0, endpoint.Spawns);
    }

    [Fact]
    public void Percentile_OfASingleSample_IsThatSample()
    {
        Assert.Equal(42, GitPerformanceWindow.Percentile([42], 0.95));
        Assert.Equal(0, GitPerformanceWindow.Percentile([], 0.95));
    }

    [Fact]
    public void SamplesOlderThanTheRetentionWindow_AreDropped()
    {
        var window = new GitPerformanceWindow();
        window.RecordRequest("tasks/grouped", Now - GitPerformanceWindow.Retention - TimeSpan.FromMinutes(1), 9_000, 0);
        window.RecordRequest("tasks/grouped", Now - TimeSpan.FromMinutes(5), 40, 0);

        // A 9 s outlier from ninety minutes ago must not keep alarming the
        // panel; the window is explicitly "the last hour".
        var endpoint = Assert.Single(window.Snapshot(Now).Endpoints);
        Assert.Equal(1, endpoint.Calls);
        Assert.Equal(40, endpoint.P95Ms);
    }

    [Fact]
    public void SpawnRate_ReportsTheRecentMinuteAndTheWindowAverage()
    {
        var window = new GitPerformanceWindow();
        for (var i = 0; i < 24; i++) window.RecordSpawn(Now - TimeSpan.FromSeconds(30));
        for (var i = 0; i < 6; i++) window.RecordSpawn(Now - TimeSpan.FromMinutes(20));

        var snapshot = window.Snapshot(Now);
        Assert.Equal(30, snapshot.SpawnsInWindow);
        // The alarm reads the recent rate; a burst must not disappear into an
        // hour average.
        Assert.Equal(24, snapshot.SpawnsLastMinute);
        Assert.True(snapshot.SpawnsPerMinute is > 0 and < 24);
    }

    // ---- SLO warnings ------------------------------------------------------

    private static GitStateIndexOptions Budgets() => new()
    {
        GroupedP95Budget = TimeSpan.FromSeconds(1),
        SpawnsPerMinuteBudget = 20,
    };

    [Fact]
    public void NoWarnings_WhenTheBoardIsInsideEveryBudget()
    {
        var snapshot = new GitPerformanceSnapshot(
            [new GitEndpointPerformance("tasks/grouped", 300, 40, 180, 260, 0)],
            SpawnsInWindow: 200,
            SpawnsPerMinute: 3.6,
            SpawnsLastMinute: 5);

        Assert.Empty(GitPerformanceEndpoints.BuildWarnings(snapshot, [], Budgets()));
    }

    [Fact]
    public void BoardP95_OverBudget_Warns()
    {
        var snapshot = new GitPerformanceSnapshot(
            [new GitEndpointPerformance("tasks/grouped", 300, 400, 1_400, 95_800, 0)],
            0, 0, 0);

        var warning = Assert.Single(GitPerformanceEndpoints.BuildWarnings(snapshot, [], Budgets()));
        Assert.Contains("tasks/grouped p95 is 1400 ms", warning);
    }

    [Fact]
    public void RequestPathThatSpawnedGit_IsNamedAsTheDefect_NotAsSlowness()
    {
        // The invariant, stated directly: a request path with a non-zero spawn
        // count is broken even when it happens to be fast today.
        var snapshot = new GitPerformanceSnapshot(
            [new GitEndpointPerformance("tasks/list", 20, 10, 20, 30, Spawns: 4)],
            0, 0, 0);

        var warning = Assert.Single(GitPerformanceEndpoints.BuildWarnings(snapshot, [], Budgets()));
        Assert.Contains("tasks/list spawned 4 git process(es) on the request path", warning);
    }

    [Fact]
    public void BackgroundLabelsMaySpawn_WithoutWarning()
    {
        var snapshot = new GitPerformanceSnapshot(
            [new GitEndpointPerformance("git/index-run", 30, 120, 900, 1_200, Spawns: 210)],
            SpawnsInWindow: 210,
            SpawnsPerMinute: 3.5,
            SpawnsLastMinute: 7);

        Assert.Empty(GitPerformanceEndpoints.BuildWarnings(snapshot, [], Budgets()));
    }

    [Fact]
    public void SpawnBurst_OverBudget_Warns()
    {
        var snapshot = new GitPerformanceSnapshot([], 4_000, 72, SpawnsLastMinute: 72);
        var warning = Assert.Single(GitPerformanceEndpoints.BuildWarnings(snapshot, [], Budgets()));
        Assert.Contains("Git spawns reached 72 in the last minute", warning);
    }

    [Fact]
    public void StaleIndex_Warns_ButARunningOneDoesNot()
    {
        var stale = new GitIndexRepositoryStatus(
            "/repos/studio", "Studio", Now.AddMinutes(-5), 300, true, Running: false, "refs", 7, 120, null, 4);
        var indexing = stale with { Running = true };
        var snapshot = new GitPerformanceSnapshot([], 0, 0, 0);

        Assert.Contains(
            "Git index for Studio is 300 s old.",
            GitPerformanceEndpoints.BuildWarnings(snapshot, [stale], Budgets()));
        // A repository that is being indexed right now is not a problem to
        // report; it is the fix in progress.
        Assert.Empty(GitPerformanceEndpoints.BuildWarnings(snapshot, [indexing], Budgets()));
    }
}
