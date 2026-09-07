using AgentStudio.Git;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the contract AGT-2726 rests on: git-derived state is computed by one
/// background index, never on a request path, and a request reads the last
/// completed capture whatever the index is doing at that moment.
///
/// <para>
/// Everything here is deterministic. The capture step is an injected delegate
/// and the clock is a <see cref="FakeTimeProvider"/>, so the debounce,
/// single-flight and concurrency rules are asserted directly rather than by
/// waiting on a real repository.
/// </para>
/// </summary>
public sealed class GitStateIndexTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-09-07T09:00:00Z");

    // ----- Pure policy matrices -----

    [Theory]
    [InlineData("HEAD", GitIndexTriggers.HeadChange)]
    [InlineData("packed-refs", GitIndexTriggers.PackedRefs)]
    [InlineData("refs/heads/develop", GitIndexTriggers.RefChange)]
    [InlineData("refs\\heads\\develop", GitIndexTriggers.RefChange)]
    [InlineData("refs/remotes/origin/main", GitIndexTriggers.RefChange)]
    [InlineData("reftable/tables.list", GitIndexTriggers.RefChange)]
    [InlineData("worktrees", GitIndexTriggers.Worktree)]
    [InlineData("worktrees/agt-2726", GitIndexTriggers.Worktree)]
    [InlineData("worktrees/agt-2726/HEAD", GitIndexTriggers.Worktree)]
    [InlineData("worktrees/agt-2726/gitdir", GitIndexTriggers.Worktree)]
    // Lock files are the other edge of a ref write; reacting to them would
    // double every run for no new state.
    [InlineData("refs/heads/develop.lock", null)]
    [InlineData("HEAD.lock", null)]
    // Churn that moves nothing the index projects.
    [InlineData("index", null)]
    [InlineData("COMMIT_EDITMSG", null)]
    [InlineData("logs/HEAD", null)]
    [InlineData("objects/ab/cdef", null)]
    [InlineData("worktrees/agt-2726/ORIG_HEAD", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Classify_MapsOnlyStateBearingPathsToATrigger(string? relativePath, string? expected)
        => Assert.Equal(expected, GitIndexPathClassifier.Classify(relativePath));

    [Theory]
    [InlineData(true, false, true, true, true)]
    // Nothing pending: an idle repository never runs.
    [InlineData(false, false, true, true, false)]
    // Single flight: a repository already capturing is not started again.
    [InlineData(true, true, true, true, false)]
    // Debounce: a burst of triggers coalesces into the run after the window.
    [InlineData(true, false, false, true, false)]
    // Spawn budget: over the cross-repository cap the trigger stays pending.
    [InlineData(true, false, true, false, false)]
    public void ShouldStartRun_RequiresPendingWorkIdleRepositoryElapsedDebounceAndASlot(
        bool hasPendingTrigger,
        bool running,
        bool debounceElapsed,
        bool slotAvailable,
        bool expected)
        => Assert.Equal(
            expected,
            GitIndexRunPolicy.ShouldStartRun(hasPendingTrigger, running, debounceElapsed, slotAvailable));

    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(0, 299, false)]
    [InlineData(0, 300, true)]
    [InlineData(0, 900, true)]
    public void IsSweepDue_OnlyAfterTheSweepIntervalHasElapsed(
        int? lastRunSecondsAgo,
        int nowSeconds,
        bool expected)
    {
        var lastRun = lastRunSecondsAgo is null ? (DateTimeOffset?)null : Origin.AddSeconds(lastRunSecondsAgo.Value);
        Assert.Equal(
            expected,
            GitIndexRunPolicy.IsSweepDue(lastRun, Origin.AddSeconds(nowSeconds), TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(null, 5, 0)]
    [InlineData(250d, 5, 0)]
    [InlineData(1200d, 5, 1)]
    [InlineData(250d, 45, 1)]
    [InlineData(1200d, 45, 2)]
    public void SloPolicy_WarnsOnlyOnABreachedBudget(
        double? groupedP95Ms,
        double spawnsPerMinute,
        int expectedWarnings)
        => Assert.Equal(
            expectedWarnings,
            GitStateSloPolicy.Evaluate(groupedP95Ms, spawnsPerMinute).Count);

    [Theory]
    [InlineData(new long[0], 95, 0)]
    [InlineData(new long[] { 42 }, 50, 42)]
    [InlineData(new long[] { 42 }, 95, 42)]
    [InlineData(new long[] { 1, 2, 3, 4 }, 50, 2)]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 95, 10)]
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 100, 10)]
    public void Percentile_UsesNearestRankAndNeverIndexesOutsideTheSample(
        long[] ascending,
        double percentile,
        double expected)
        => Assert.Equal(expected, GitProcessTelemetry.Percentile(ascending, percentile));

    // ----- Index behaviour -----

    [Fact]
    public async Task RunDueAsync_AfterTheDebounce_CapturesAndPublishesTheSnapshot()
    {
        var time = new FakeTimeProvider(Origin);
        var index = Build(time, repository => State(repository, "sha-1"));
        index.Register("/repos/alpha", "alpha");

        // Inside the debounce window nothing runs yet.
        await index.RunDueAsync(CancellationToken.None);
        Assert.Null(index.Snapshot("/repos/alpha"));

        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);

        var snapshot = index.Snapshot("/repos/alpha");
        Assert.NotNull(snapshot);
        Assert.Equal("sha-1", snapshot!.Head);
        Assert.Equal(Origin.Add(GitStateIndex.Debounce), snapshot.CapturedAtUtc);
        Assert.NotNull(index.InventoryFor("alpha"));
    }

    [Fact]
    public async Task Request_TriggersDuringARun_CoalesceIntoOneFollowUpCapture()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var time = new FakeTimeProvider(Origin);
        var captures = 0;
        var index = Build(time, repository =>
        {
            if (Interlocked.Increment(ref captures) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            return State(repository, $"sha-{captures}");
        });
        index.Register("/repos/alpha", "alpha");
        time.Advance(GitStateIndex.Debounce);

        var first = index.RunDueAsync(CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        // Five ref moves land while the capture is in flight. Single flight:
        // none of them starts a second concurrent run.
        for (var i = 0; i < 5; i++) index.Request("/repos/alpha", GitIndexTriggers.RefChange);
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref captures));

        release.Set();
        await first;

        // They coalesced into exactly one follow-up run.
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, Volatile.Read(ref captures));
    }

    [Fact]
    public async Task Snapshot_WhileARunIsInFlight_KeepsServingThePreviousCapture()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var time = new FakeTimeProvider(Origin);
        var captures = 0;
        var index = Build(time, repository =>
        {
            var attempt = Interlocked.Increment(ref captures);
            if (attempt == 2)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            return State(repository, $"sha-{attempt}");
        });
        index.Register("/repos/alpha", "alpha");
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.Equal("sha-1", index.Snapshot("/repos/alpha")!.Head);

        index.Request("/repos/alpha", GitIndexTriggers.RefChange);
        time.Advance(GitStateIndex.Debounce);
        var second = index.RunDueAsync(CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        // Stale while revalidate: the read returns immediately with the old
        // capture and the stamp says a change is already known.
        Assert.Equal("sha-1", index.Snapshot("/repos/alpha")!.Head);
        Assert.True(index.Stamp().Stale);
        Assert.Equal(Origin.Add(GitStateIndex.Debounce), index.Stamp().GitStateAt);

        release.Set();
        await second;
        Assert.Equal("sha-2", index.Snapshot("/repos/alpha")!.Head);
        Assert.False(index.Stamp().Stale);
    }

    [Fact]
    public async Task RunDueAsync_OverManyRepositories_NeverExceedsTheCrossRepositoryBudget()
    {
        using var release = new ManualResetEventSlim();
        var time = new FakeTimeProvider(Origin);
        var concurrent = 0;
        var peak = 0;
        var index = Build(time, repository =>
        {
            var running = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, running);
            release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref concurrent);
            return State(repository, "sha-1");
        });
        for (var i = 0; i < 6; i++) index.Register($"/repos/repo-{i}", $"repo-{i}");
        time.Advance(GitStateIndex.Debounce);

        var run = index.RunDueAsync(CancellationToken.None);
        // Give the started captures a moment to pile up before releasing them.
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref concurrent) >= GitStateIndex.MaxConcurrentRepositories,
            TimeSpan.FromSeconds(5)));
        release.Set();
        await run;

        Assert.True(
            Volatile.Read(ref peak) <= GitStateIndex.MaxConcurrentRepositories,
            $"The index ran {peak} repositories at once; the budget is {GitStateIndex.MaxConcurrentRepositories}.");

        // The repositories that did not get a slot stayed pending rather than
        // being dropped, so a following tick clears them.
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.All(
            Enumerable.Range(0, GitStateIndex.MaxConcurrentRepositories),
            i => Assert.NotNull(index.Snapshot($"/repos/repo-{i}")));
    }

    [Fact]
    public async Task Generation_AdvancesOnChangedStateAndStandsStillOnAnIdenticalCapture()
    {
        var time = new FakeTimeProvider(Origin);
        var head = "sha-1";
        var index = Build(time, repository => State(repository, head));
        var indexed = 0;
        index.RepositoryIndexed += _ => Interlocked.Increment(ref indexed);
        index.Register("/repos/alpha", "alpha");

        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        var afterFirst = index.Generation;
        Assert.Equal(1, Volatile.Read(ref indexed));

        // The safety sweep re-captures an unchanged repository. That must not
        // invalidate every downstream projection.
        index.Request("/repos/alpha", GitIndexTriggers.Sweep);
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.Equal(afterFirst, index.Generation);
        Assert.Equal(1, Volatile.Read(ref indexed));

        head = "sha-2";
        index.Request("/repos/alpha", GitIndexTriggers.RefChange);
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.True(index.Generation > afterFirst);
        Assert.Equal(2, Volatile.Read(ref indexed));
    }

    [Fact]
    public async Task RunDueAsync_WhenACaptureThrows_KeepsTheOldSnapshotAndRecoversOnTheNextTrigger()
    {
        var time = new FakeTimeProvider(Origin);
        var fail = false;
        var index = Build(time, repository =>
            fail ? throw new InvalidOperationException("git exploded") : State(repository, "sha-1"));
        index.Register("/repos/alpha", "alpha");
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);

        fail = true;
        index.Request("/repos/alpha", GitIndexTriggers.RefChange);
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.Equal("sha-1", index.Snapshot("/repos/alpha")!.Head);

        fail = false;
        index.Request("/repos/alpha", GitIndexTriggers.RefChange);
        time.Advance(GitStateIndex.Debounce);
        await index.RunDueAsync(CancellationToken.None);
        Assert.Equal("sha-1", index.Snapshot("/repos/alpha")!.Head);
        Assert.Equal(3, index.RecentRuns().Count);
        Assert.False(index.RecentRuns()[1].Succeeded);
    }

    [Fact]
    public async Task MaxDebounceWait_UnderContinuousChurn_StillCaptures()
    {
        var time = new FakeTimeProvider(Origin);
        var index = Build(time, repository => State(repository, "sha-1"));
        index.Register("/repos/alpha", "alpha");

        // A trigger every 100 ms keeps resetting the debounce. Without the upper
        // bound the repository would never settle and the index would never meet
        // its freshness budget.
        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            index.Request("/repos/alpha", GitIndexTriggers.RefChange);
            await index.RunDueAsync(CancellationToken.None);
        }

        Assert.NotNull(index.Snapshot("/repos/alpha"));
    }

    [Fact]
    public void Stamp_WithNoRegisteredRepositories_IsUnknownAndStale()
    {
        var index = Build(new FakeTimeProvider(Origin), repository => State(repository, "sha-1"));
        Assert.Equal(GitStateStamp.Unknown, index.Stamp());
    }

    private static GitStateIndex Build(
        FakeTimeProvider time,
        Func<GitIndexedRepository, GitRepositoryState> capture)
        => new(capture, NullLogger.Instance, time);

    private static GitRepositoryState State(GitIndexedRepository repository, string head)
    {
        var inventories = repository.ProjectNames.ToDictionary(
            project => project,
            project => new GitProjectInventory(
                project,
                repository.RepositoryRoot,
                true,
                "develop",
                [],
                [],
                [],
                null),
            StringComparer.OrdinalIgnoreCase);

        return new GitRepositoryState(
            repository.RepositoryRoot,
            DateTimeOffset.MinValue,
            head,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["develop"] = head },
            [],
            inventories);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
