using AgentStudio.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The scheduling contract of the background git index (AGT-2726): a ref change
/// invalidates, concurrent triggers coalesce into one run, a run in progress
/// still serves the previous snapshot, and cross-repository work stays inside
/// the configured process budget.
/// </summary>
public sealed class GitStateIndexTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-09-07T09:00:00Z");

    private static GitStateIndexOptions Options() => new()
    {
        Debounce = TimeSpan.FromMilliseconds(400),
        SweepInterval = TimeSpan.FromMinutes(2),
        MaxConcurrentRepositories = 2,
    };

    private static (GitStateIndex Index, FakeTimeProvider Time, string Root) Registered(
        string name = "repo-a")
    {
        var time = new FakeTimeProvider(Origin);
        var index = new GitStateIndex(Options(), time);
        var root = Path.Combine(Path.GetTempPath(), "git-state-index", name);
        index.Register(root, name);
        return (index, time, root);
    }

    // ---- Admission policy: the whole schedule as a direct matrix -----------

    [Theory]
    // running -> never a second run, whatever else is true
    [InlineData(true, true, true, true, GitIndexAdmission.Coalesce)]
    [InlineData(true, true, false, false, GitIndexAdmission.Coalesce)]
    [InlineData(true, false, true, true, GitIndexAdmission.Coalesce)]
    [InlineData(true, false, false, false, GitIndexAdmission.Idle)]
    // idle + a change -> start once the debounce window has passed
    [InlineData(false, true, true, false, GitIndexAdmission.Start)]
    [InlineData(false, true, true, true, GitIndexAdmission.Start)]
    [InlineData(false, true, false, false, GitIndexAdmission.Wait)]
    [InlineData(false, true, false, true, GitIndexAdmission.Wait)]
    // idle + no change -> only the safety sweep can start a run
    [InlineData(false, false, true, true, GitIndexAdmission.Start)]
    [InlineData(false, false, false, true, GitIndexAdmission.Start)]
    [InlineData(false, false, true, false, GitIndexAdmission.Idle)]
    [InlineData(false, false, false, false, GitIndexAdmission.Idle)]
    internal void Admit_CoversTheSchedule(
        bool running,
        bool dirty,
        bool debounceElapsed,
        bool sweepDue,
        GitIndexAdmission expected)
    {
        Assert.Equal(
            expected,
            GitIndexAdmissionPolicy.Admit(running, dirty, debounceElapsed, sweepDue));
    }

    [Fact]
    public void PendingChange_DoesNotStartBeforeTheDebounceElapses()
    {
        // A fetch rewrites dozens of refs in a burst. Starting on the first
        // event would buy one run per ref, which is the spawn storm this index
        // exists to remove.
        var (index, time, root) = Registered();
        index.Complete(Assert.Single(index.ClaimDue()), 1, 5, null);

        index.Signal(root, "refs");
        Assert.Empty(index.ClaimDue());

        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Single(index.ClaimDue());
    }

    // ---- Invalidation, coalescing, stale-while-revalidate ------------------

    [Fact]
    public void RefChange_MakesTheRepositoryClaimableAgain()
    {
        var (index, time, root) = Registered();
        var startup = Assert.Single(index.ClaimDue());
        index.Complete(startup, spawns: 7, elapsedMs: 120, slowestCommand: "rev-list");

        // Nothing changed: no run is due, and the stamp is fresh.
        Assert.Empty(index.ClaimDue());
        Assert.False(index.Read(root).Stale);

        index.Signal(root, "refs");
        time.Advance(TimeSpan.FromSeconds(1));

        var afterChange = Assert.Single(index.ClaimDue());
        Assert.Equal("refs", afterChange.Trigger);
    }

    [Fact]
    public void ConcurrentTriggers_CoalesceIntoOneRun()
    {
        var (index, time, root) = Registered();
        index.Complete(Assert.Single(index.ClaimDue()), 1, 1, null);

        for (var i = 0; i < 25; i++) index.Signal(root, "refs");
        time.Advance(TimeSpan.FromSeconds(1));

        // 25 triggers, one run. A second poll while that run owns the
        // repository must not produce a duplicate claim.
        var claim = Assert.Single(index.ClaimDue());
        Assert.Empty(index.ClaimDue());
        Assert.Empty(index.ClaimDue());

        index.Complete(claim, 7, 90, null);
        Assert.Empty(index.ClaimDue());
    }

    [Fact]
    public void ChangeDuringARun_IsNotSwallowedByThatRun()
    {
        // The trigger arrived after the run read the refs, so the run's result
        // cannot contain it. Clearing the dirty bit at claim time (not at
        // completion) is what keeps that change scheduled.
        var (index, time, root) = Registered();
        var claim = Assert.Single(index.ClaimDue());

        index.Signal(root, "refs");
        index.Complete(claim, 7, 90, null);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("refs", Assert.Single(index.ClaimDue()).Trigger);
    }

    [Fact]
    public void SnapshotStamp_IsServedDuringARunAndMarkedStale()
    {
        var (index, time, root) = Registered();
        index.Complete(Assert.Single(index.ClaimDue()), 7, 90, null);
        var indexedAt = time.GetUtcNow();

        Assert.Equal(new GitStateStamp(indexedAt, false), index.Read(root));

        index.Signal(root, "refs");
        time.Advance(TimeSpan.FromSeconds(1));
        var running = Assert.Single(index.ClaimDue());

        // Mid-run: the reader still gets the previous completion time, flagged
        // stale. It must never block and never get a null stamp.
        var duringRun = index.Read(root);
        Assert.Equal(indexedAt, duringRun.GitStateAt);
        Assert.True(duringRun.Stale);

        time.Advance(TimeSpan.FromSeconds(2));
        index.Complete(running, 7, 90, null);
        Assert.Equal(new GitStateStamp(time.GetUtcNow(), false), index.Read(root));
    }

    [Fact]
    public void BoardStamp_TakesTheOldestRunAndAnyStaleRepository()
    {
        var time = new FakeTimeProvider(Origin);
        var index = new GitStateIndex(Options(), time);
        index.Register("/tmp/repo-a", "a");
        index.Register("/tmp/repo-b", "b");

        var claims = index.ClaimDue();
        Assert.Equal(2, claims.Count);
        index.Complete(claims[0], 3, 10, null);
        var older = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(30));
        index.Complete(claims[1], 3, 10, null);

        // The board mixes projects, so its honest "as of" is the weakest link.
        var board = index.ReadBoard();
        Assert.Equal(older, board.GitStateAt);
        Assert.False(board.Stale);

        index.Signal("/tmp/repo-b", "refs");
        Assert.True(index.ReadBoard().Stale);
    }

    [Fact]
    public void AbandonedRun_DoesNotStampAFreshnessItNeverEarned()
    {
        // A claim the executor refused, or a run cancelled at shutdown, observed
        // nothing. Completing it would publish an "as of now" for work that did
        // not happen, and the trigger that earned the claim would be lost.
        var (index, time, root) = Registered();
        index.Complete(Assert.Single(index.ClaimDue()), 7, 90, null);
        var indexedAt = time.GetUtcNow();

        index.Signal(root, "refs");
        time.Advance(TimeSpan.FromSeconds(5));
        index.Abandon(Assert.Single(index.ClaimDue()));

        Assert.Equal(indexedAt, index.Read(root).GitStateAt);
        Assert.True(index.Read(root).Stale);
        Assert.Equal("refs", Assert.Single(index.ClaimDue()).Trigger);
    }

    [Fact]
    public void UnregisteredRepository_ReadsAsWarming_NotAsFresh()
    {
        var (index, _, _) = Registered();
        Assert.Equal(GitStateStamp.Warming, index.Read("/tmp/never-registered"));
    }

    [Fact]
    public void Status_ReportsIndexAgeAndTheLastRunFacts()
    {
        var (index, time, root) = Registered();
        index.Complete(Assert.Single(index.ClaimDue()), spawns: 7, elapsedMs: 4_200, slowestCommand: "rev-list");
        time.Advance(TimeSpan.FromSeconds(12));

        var status = Assert.Single(index.Status());
        Assert.Equal(GitStateIndex.Normalize(root), status.Repository);
        Assert.Equal(12, status.IndexAgeSeconds);
        Assert.Equal(7, status.LastSpawns);
        Assert.Equal(4_200, status.LastElapsedMs);
        Assert.Equal("rev-list", status.LastSlowestCommand);
        Assert.Equal("startup", status.LastTrigger);
    }

    // ---- Bounded concurrency ----------------------------------------------

    [Fact]
    public void Executor_RunsNoMoreThanTheConfiguredRepositoriesAtOnce()
    {
        using var executor = new GitBackgroundExecutor(
            degreeOfParallelism: 2,
            NullLogger<GitBackgroundExecutor>.Instance);
        var concurrent = 0;
        var peak = 0;
        var peakGate = new Lock();
        using var release = new ManualResetEventSlim(false);
        using var allStarted = new CountdownEvent(2);
        var finished = new CountdownEvent(8);

        for (var i = 0; i < 8; i++)
        {
            Assert.True(executor.Submit("test", _ =>
            {
                var now = Interlocked.Increment(ref concurrent);
                lock (peakGate) peak = Math.Max(peak, now);
                if (!allStarted.IsSet) allStarted.Signal();
                // Hold the first two slots until both are proven occupied, so
                // the peak measurement cannot be a scheduling artefact.
                release.Wait(TimeSpan.FromSeconds(5));
                Interlocked.Decrement(ref concurrent);
                finished.Signal();
            }));
        }

        Assert.True(allStarted.Wait(TimeSpan.FromSeconds(5)), "no work item started");
        release.Set();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)), "queued work did not drain");
        Assert.Equal(2, executor.DegreeOfParallelism);
        Assert.True(peak <= 2, $"the executor ran {peak} items at once; the budget is 2.");
    }

    [Fact]
    public void Executor_RefusesWorkAfterDisposeInsteadOfSilentlyDroppingIt()
    {
        // The caller marks itself "refreshing" before submitting. A silent drop
        // would leave that flag set forever and freeze every later read.
        var executor = new GitBackgroundExecutor(1, NullLogger<GitBackgroundExecutor>.Instance);
        executor.Dispose();
        Assert.False(executor.Submit("test", _ => { }));
    }

    [Fact]
    public void Executor_SurvivesAFailingWorkItem()
    {
        using var executor = new GitBackgroundExecutor(1, NullLogger<GitBackgroundExecutor>.Instance);
        using var done = new ManualResetEventSlim(false);
        executor.Submit("boom", _ => throw new InvalidOperationException("index run blew up"));
        executor.Submit("next", _ => done.Set());
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "a failed item took the worker thread with it");
    }

    // ---- Load replay -------------------------------------------------------

    /// <summary>
    /// Replays the trigger pattern measured on 2026-09-06: a 55-minute window
    /// in which the board polled about six times a minute against one
    /// repository, with occasional real ref movement (delivery merges and
    /// pushes). That window cost about 3,980 git processes, roughly 72 per
    /// minute, because every poll re-derived git state.
    ///
    /// <para>
    /// The load fixture pins two things at once: a board poll costs no run at
    /// all (it only reads a stamp), and the surviving change-driven plus
    /// sweep-driven runs stay under the acceptance bound of 20 spawns per
    /// minute.
    /// </para>
    /// </summary>
    [Fact]
    public void ReplayedTriggerPattern_StaysInsideTheSpawnBudget()
    {
        const int minutes = 55;
        const int pollsPerMinute = 6;
        const int refChangeEveryMinutes = 5;
        const int spawnsPerRun = 7;
        const int spawnBudgetPerMinute = 20;
        const int measuredBaselinePerMinute = 72;

        var (index, time, root) = Registered();
        var runs = 0;
        var spawns = 0;
        var pollsServed = 0;
        var pollsThatStartedAnIndexRun = 0;

        // The startup run is real work; count it in the budget.
        foreach (var claim in index.ClaimDue())
        {
            runs++;
            spawns += spawnsPerRun;
            index.Complete(claim, spawnsPerRun, 120, null);
        }

        for (var minute = 0; minute < minutes; minute++)
        {
            if (minute % refChangeEveryMinutes == 0) index.Signal(root, "refs");

            for (var poll = 0; poll < pollsPerMinute; poll++)
            {
                // A board poll reads the stamp. That is the whole request path:
                // no signal, no claim, no process.
                var before = runs;
                Assert.NotNull(index.ReadBoard());
                pollsServed++;

                time.Advance(TimeSpan.FromSeconds(60d / pollsPerMinute));
                foreach (var claim in index.ClaimDue())
                {
                    runs++;
                    spawns += spawnsPerRun;
                    index.Complete(claim, spawnsPerRun, 120, null);
                }
                if (runs > before) pollsThatStartedAnIndexRun++;
            }
        }

        var perMinute = (double)spawns / minutes;
        Assert.Equal(minutes * pollsPerMinute, pollsServed);
        Assert.True(
            perMinute < spawnBudgetPerMinute,
            $"the replayed pattern spawned {perMinute:0.0} git processes per minute across {runs} runs; "
            + $"the budget is {spawnBudgetPerMinute} and the pre-AGT-2726 baseline was ~{measuredBaselinePerMinute}.");

        // Runs are driven by ref changes and the safety sweep only. Every run
        // must be attributable to one of them, never to poll volume.
        var refChanges = (int)Math.Ceiling(minutes / (double)refChangeEveryMinutes);
        var sweeps = (int)(minutes / Options().SweepInterval.TotalMinutes) + 1;
        Assert.True(
            runs <= refChanges + sweeps + 1,
            $"{runs} index runs for {refChanges} ref changes and at most {sweeps} sweeps; "
            + $"{pollsThatStartedAnIndexRun} of {pollsServed} polls were followed by a run.");
    }
}
