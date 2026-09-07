using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The MEASURE half of AGT-2007: <see cref="GitProcessTelemetry"/> is the
/// ambient per-request accounting that lets the git-info endpoints answer "how
/// many git processes ran for this request, and how long did they take". These
/// tests pin the two properties the optimization relies on: spawns accumulate
/// into the active request scope, and that scope flows across the thread-pool
/// boundary so a fanned-out (parallel) request still tallies every spawn.
/// </summary>
public class GitProcessTelemetryTests
{
    [Fact]
    public void Record_OutsideScope_IsNoOp()
    {
        Assert.Null(GitProcessTelemetry.CurrentTally());
        // No ambient scope: recording must be a silent no-op, never throw.
        GitProcessTelemetry.Record("status", 5, 0);
        Assert.Null(GitProcessTelemetry.CurrentTally());
    }

    [Fact]
    public void BeginRequest_AccumulatesSpawns_AndRestoresOnDispose()
    {
        Assert.Null(GitProcessTelemetry.CurrentTally());
        using (GitProcessTelemetry.BeginRequest("test/req", NullLogger.Instance))
        {
            GitProcessTelemetry.Record("status", 10, 0);
            GitProcessTelemetry.Record("diff", 20, 0);

            var tally = GitProcessTelemetry.CurrentTally();
            Assert.NotNull(tally);
            Assert.Equal(2, tally!.Value.Spawns);
            Assert.Equal(30, tally.Value.GitMs);
        }
        // Dispose restores the (absent) outer scope, so nothing leaks.
        Assert.Null(GitProcessTelemetry.CurrentTally());
    }

    [Fact]
    public void BeginRequest_NestedScope_RestoresOuterOnInnerDispose()
    {
        using (GitProcessTelemetry.BeginRequest("outer", NullLogger.Instance))
        {
            GitProcessTelemetry.Record("a", 1, 0);
            using (GitProcessTelemetry.BeginRequest("inner", NullLogger.Instance))
            {
                GitProcessTelemetry.Record("b", 2, 0);
                Assert.Equal(1, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
            }
            // Back in the outer scope, which only saw its own spawn.
            Assert.Equal(1, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }
    }

    [Fact]
    public void BeginRequest_WithNestedAggregation_IncludesChildSpawns()
    {
        using (GitProcessTelemetry.BeginRequest(
                   "outer",
                   NullLogger.Instance,
                   includeNested: true))
        {
            GitProcessTelemetry.Record("a", 1, 0);
            using (GitProcessTelemetry.BeginRequest("inner", NullLogger.Instance))
            {
                GitProcessTelemetry.Record("b", 2, 0);
            }

            var tally = GitProcessTelemetry.CurrentTally();
            Assert.NotNull(tally);
            Assert.Equal(2, tally!.Value.Spawns);
            Assert.Equal(3, tally.Value.GitMs);
        }
    }

    [Fact]
    public async Task BeginRequest_CountsSpawnsRecordedFromParallelTasks()
    {
        using (GitProcessTelemetry.BeginRequest("test/parallel", NullLogger.Instance))
        {
            // The ambient scope must flow into thread-pool work (captured
            // ExecutionContext); otherwise a request that fans its reads out in
            // parallel - the whole point of the optimization - would under-count.
            var tasks = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => GitProcessTelemetry.Record("rev-parse", 3, 0)))
                .ToArray();
            await Task.WhenAll(tasks);

            var tally = GitProcessTelemetry.CurrentTally();
            Assert.NotNull(tally);
            Assert.Equal(16, tally!.Value.Spawns);
            Assert.Equal(48, tally.Value.GitMs);
        }
    }

    [Fact]
    public void CurrentSlowestCommand_ReturnsTheHighestWallTimeCommand()
    {
        using (GitProcessTelemetry.BeginRequest("test/slowest", NullLogger.Instance))
        {
            GitProcessTelemetry.Record("rev-parse", 5, 0);
            GitProcessTelemetry.Record("for-each-ref", 40, 0);
            GitProcessTelemetry.Record("rev-parse", 5, 0);

            var slowest = GitProcessTelemetry.CurrentSlowestCommand();
            Assert.NotNull(slowest);
            Assert.Equal("for-each-ref", slowest!.Value.Command);
            Assert.Equal(1, slowest.Value.Count);
            Assert.Equal(40, slowest.Value.Ms);
        }
    }

    /// <summary>
    /// AGT-2726: <see cref="GitProcessTelemetry.GetStatsSnapshot"/> backs the
    /// Admin git-telemetry surface's p50/p95/spawn-rate. Uses a unique label
    /// per test so concurrently running test classes recording their own real
    /// rollups under other labels cannot affect these assertions.
    /// </summary>
    [Fact]
    public void GetStatsSnapshot_ComputesPercentilesAndSpawnRateForALabel()
    {
        // WallMs is the scope's real elapsed time (Stopwatch), independent of
        // the TimeProvider used only to stamp when each sample was recorded -
        // so this test uses small real sleeps to produce distinct, ordered
        // wall times instead of asserting exact millisecond values (which
        // would be scheduler-timing-flaky).
        var label = "test/stats-" + Guid.NewGuid();
        var clock = new FakeTimeProviderStep(DateTimeOffset.Parse("2026-09-06T20:00:00Z"));

        foreach (var sleepMs in new[] { 1, 2, 4, 8, 16 })
        {
            using (GitProcessTelemetry.BeginRequest(label, NullLogger.Instance, timeProvider: clock))
            {
                Thread.Sleep(sleepMs);
                GitProcessTelemetry.Record("rev-parse", sleepMs, 0);
                GitProcessTelemetry.Record("rev-parse", 0, 0);
            }
        }

        var stats = GitProcessTelemetry.GetStatsSnapshot(TimeSpan.FromHours(1), clock)
            .Single(s => s.Label == label);

        Assert.Equal(5, stats.SampleCount);
        Assert.Equal(10, stats.TotalSpawns); // 5 samples * 2 spawns each
        // p95 must be at least as large as p50, and both must reflect real
        // elapsed time (not the constant zero WallMs a bug would produce).
        Assert.True(stats.P50Ms > 0, $"expected a positive p50, got {stats.P50Ms}");
        Assert.True(stats.P95Ms >= stats.P50Ms, $"expected p95 ({stats.P95Ms}) >= p50 ({stats.P50Ms})");
        Assert.True(stats.SpawnsPerMinute > 0);
    }

    [Fact]
    public void GetStatsSnapshot_ExcludesSamplesOutsideTheWindow()
    {
        var label = "test/stats-window-" + Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-06T20:00:00Z");
        var clock = new FakeTimeProviderStep(now);

        using (GitProcessTelemetry.BeginRequest(label, NullLogger.Instance, timeProvider: clock))
            GitProcessTelemetry.Record("rev-parse", 5, 0);

        clock.Advance(TimeSpan.FromHours(2));
        using (GitProcessTelemetry.BeginRequest(label, NullLogger.Instance, timeProvider: clock))
            GitProcessTelemetry.Record("rev-parse", 5, 0);

        var stats = GitProcessTelemetry.GetStatsSnapshot(TimeSpan.FromHours(1), clock)
            .SingleOrDefault(s => s.Label == label);

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.SampleCount);
    }

    /// <summary>Minimal step-able <see cref="TimeProvider"/> so these tests do not depend on <c>Microsoft.Extensions.Time.Testing</c>.</summary>
    private sealed class FakeTimeProviderStep(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
