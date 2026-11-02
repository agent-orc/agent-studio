using AgentStudio.Readiness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The readiness monitor is the fact producer behind the readiness reads
/// (AGT-3412): reads return its latest report, a recheck collects at once, and
/// the background loop collects after start and then on its interval.
/// </summary>
public sealed class ServerReadinessMonitorTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private ServerReadinessFacts Facts() => new(
        _clock.GetUtcNow(),
        new GitIdentityFact(null, null, null, null),
        new CommitProbeFact(true, false, "Committer identity unknown"),
        false, [], [], [],
        new GateWorkerFact([], null, "unverified", null, null),
        new ExecutionHostsFact([], 0),
        new GitHubAppFact(false, null, null, new Dictionary<string, GitHubAppCoverageFact>()),
        []);

    [Fact]
    public async Task Reads_are_pending_until_the_first_collection_and_a_recheck_collects_at_once()
    {
        var collections = 0;
        var monitor = new ServerReadinessMonitor(_ => { collections++; return Task.FromResult(Facts()); },
            _clock, NullLogger<ServerReadinessMonitor>.Instance);

        Assert.Same(ServerReadinessReport.Pending, monitor.Latest);

        var report = await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, collections);
        Assert.Same(report, monitor.Latest);
        Assert.Equal(ReadinessStates.Fail, report.State);
        Assert.Equal(2, report.Failing);
        Assert.Equal(_clock.GetUtcNow(), report.ComputedAt);
    }

    [Fact]
    public async Task The_background_loop_collects_after_start_and_then_every_interval()
    {
        var collected = new SemaphoreSlim(0);
        var monitor = new ServerReadinessMonitor(_ => { collected.Release(); return Task.FromResult(Facts()); },
            _clock, NullLogger<ServerReadinessMonitor>.Instance);
        using var stop = new CancellationTokenSource();

        await monitor.StartAsync(stop.Token);
        Assert.False(await collected.WaitAsync(TimeSpan.FromMilliseconds(50)));
        await AdvanceUntilCollected(collected, ServerReadinessMonitor.StartupDelay);
        Assert.Equal(_clock.GetUtcNow(), (await WaitForReport(monitor)).ComputedAt);
        await AdvanceUntilCollected(collected, ServerReadinessMonitor.Interval);
        await monitor.StopAsync(CancellationToken.None);
    }

    /// <summary>Advances the clock in steps until the loop collected; the loop may not have armed its delay yet.</summary>
    private async Task AdvanceUntilCollected(SemaphoreSlim collected, TimeSpan step)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            _clock.Advance(step);
            if (await collected.WaitAsync(TimeSpan.FromMilliseconds(50))) return;
        }
        Assert.Fail("the monitor did not collect");
    }

    private static async Task<ServerReadinessReport> WaitForReport(ServerReadinessMonitor monitor)
    {
        for (var attempt = 0; attempt < 200 && monitor.Latest.ComputedAt is null; attempt++) await Task.Delay(10);
        Assert.NotNull(monitor.Latest.ComputedAt);
        return monitor.Latest;
    }
}
