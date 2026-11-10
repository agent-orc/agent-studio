using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3562, AGT-3710: first in, first out start order and pre-lease deadline of batch flights.</summary>
public sealed class BatchGateStartPolicyTests
{
    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(false, false, true, false, false)]
    public void Only_an_unfinished_red_parent_without_a_live_flight_ends_isolation(
        bool red, bool active, bool pending, bool allFound, bool expected)
        => Assert.Equal(expected, BatchGateStartPolicy.EndInterruptedIsolation(
            red, active, pending, allFound));

    // clock-independent: the pure policy receives an explicit now; fixture instants meet only each other.
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 59, 0, TimeSpan.Zero);

    private static BatchGateStartCandidate Batch(string id, DateTimeOffset queuedSince,
        long firstSequence = 1, bool stacked = false, bool child = false, bool deferred = false)
        => new(id, queuedSince, firstSequence, stacked, child, deferred);

    [Fact]
    public void Formed_batches_start_in_formation_order_regardless_of_size_or_input_order()
    {
        // 10 November 2026: four batches formed 03:47 to 04:40 UTC waited
        // while batches formed at 04:50 and 05:11 started within seconds.
        var batches = new[]
        {
            Batch("3735f957", At.AddMinutes(132), firstSequence: 90),
            Batch("0d8df544", At.AddMinutes(9), firstSequence: 40),
            Batch("17d2ad28", At.AddMinutes(111), firstSequence: 80),
            Batch("b80b7541", At.AddMinutes(-12), firstSequence: 20),
            Batch("64bda9c0", At.AddMinutes(41), firstSequence: 50),
            Batch("fa98470e", At.AddMinutes(2), firstSequence: 10),
        };

        Assert.Equal(["b80b7541", "fa98470e", "0d8df544", "64bda9c0", "17d2ad28", "3735f957"],
            BatchGateStartPolicy.Order(batches));
        Assert.Equal(BatchGateStartPolicy.Order(batches),
            BatchGateStartPolicy.Order(batches.Reverse()));
    }

    [Fact]
    public void Equal_formation_times_break_on_the_lowest_enqueue_sequence_then_the_batch_id()
    {
        var later = Batch("aaaa", At, firstSequence: 7);
        var earlier = Batch("zzzz", At, firstSequence: 3);
        var sameSequence = Batch("bbbb", At, firstSequence: 7);

        Assert.Equal(["zzzz", "aaaa", "bbbb"],
            BatchGateStartPolicy.Order([sameSequence, later, earlier]));
    }

    [Fact]
    public void Stacked_successor_and_child_start_first_and_a_deferred_batch_last()
    {
        var ordinary = Batch("ordinary", At);
        var stacked = Batch("stacked", At.AddMinutes(30), stacked: true);
        var child = Batch("child", At.AddMinutes(20), child: true);
        var deferred = Batch("deferred", At.AddMinutes(-120), deferred: true);

        Assert.Equal(["stacked", "child", "ordinary", "deferred"],
            BatchGateStartPolicy.Order([deferred, ordinary, child, stacked]));
    }

    [Fact]
    public void Older_batch_starts_although_larger_batches_keep_forming()
    {
        // 5 November 2026: batch 24991efe (12:59 UTC) stayed in phase 0 for
        // hours while later, larger batches ran. One flight per project starts
        // per pass; a younger batch forms before each pass.
        var waiting = new List<BatchGateStartCandidate> { Batch("small", At) };
        for (var minute = 10; minute <= 30; minute += 10)
            waiting.Add(Batch($"large-{minute}", At.AddMinutes(minute - 1), firstSequence: minute));

        Assert.Equal("small", BatchGateStartPolicy.Order(waiting)[0]);
    }

    [Theory]
    [InlineData(false, false, false, true, true)]   // published, red or paused after its gate
    [InlineData(false, false, true, true, true)]    // abandoned during its gate
    [InlineData(false, false, false, false, true)]  // paused before its gate by an error
    [InlineData(false, false, true, false, false)]  // retired before replay, e.g. base moved
    [InlineData(false, true, false, false, false)]  // no lease: still formed
    [InlineData(true, true, false, false, false)]   // cancelled at the lease deadline
    public void Only_a_completed_or_failed_flight_starts_the_next_batch_at_once(
        bool cancelled, bool stillFormed, bool retired, bool gateRan, bool expected)
        => Assert.Equal(expected,
            BatchGateStartPolicy.StartsNextAfterFlight(cancelled, stillFormed, retired, gateRan));

    [Theory]
    [InlineData(true, true, true)]    // started by a completed flight, re-formed on its publication
    [InlineData(true, false, false)]  // started by a tick: the successor waits for the next tick
    [InlineData(false, true, false)]  // retired without a successor
    public void Re_formed_successor_continues_a_chain_that_a_completed_flight_started(
        bool reformed, bool followsCompleted, bool expected)
        => Assert.Equal(expected, BatchGateStartPolicy.StartsNextAfterFlight(false, false,
            batchRetired: true, gateRan: false, reformed, followsCompleted));

    [Fact]
    public void Lease_deadline_applies_only_to_a_flight_that_has_not_claimed_its_lease()
    {
        Assert.False(BatchGateStartPolicy.LeaseDeadlinePassed(At, false, At.AddMinutes(39), 40));
        Assert.True(BatchGateStartPolicy.LeaseDeadlinePassed(At, false, At.AddMinutes(40), 40));
        Assert.False(BatchGateStartPolicy.LeaseDeadlinePassed(At, true, At.AddHours(5), 40));
        Assert.Contains("40 minutes", BatchGateStartPolicy.LeaseDeadlineReason(40));
    }
}
