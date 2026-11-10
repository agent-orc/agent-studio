using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3562: start order and pre-lease deadline of batch flights.</summary>
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

    private static BatchGateStartCandidate Batch(string id, int size, DateTimeOffset closed,
        bool stacked = false, bool deferred = false, int maximumWait = 45)
        => new(id, size, closed, stacked, deferred, maximumWait);

    [Fact]
    public void Larger_batch_starts_before_an_older_smaller_one_within_the_maximum_wait()
    {
        var older = Batch("older", 2, At);
        var larger = Batch("larger", 5, At.AddMinutes(10));

        Assert.Equal(["larger", "older"],
            BatchGateStartPolicy.Order([older, larger], At.AddMinutes(44)));
    }

    [Fact]
    public void Batch_past_its_maximum_wait_starts_before_younger_ones_regardless_of_size()
    {
        var oldest = Batch("oldest", 1, At);
        var older = Batch("older", 2, At.AddMinutes(5));
        var larger = Batch("larger", 8, At.AddMinutes(40));

        Assert.Equal(["oldest", "older", "larger"],
            BatchGateStartPolicy.Order([larger, older, oldest], At.AddMinutes(50)));
        Assert.Equal(["oldest", "larger", "older"],
            BatchGateStartPolicy.Order([larger, older, oldest], At.AddMinutes(45)));
    }

    [Fact]
    public void Stacked_successor_starts_first_and_a_deferred_batch_last()
    {
        var ordinary = Batch("ordinary", 5, At);
        var stacked = Batch("stacked", 2, At, stacked: true);
        var deferred = Batch("deferred", 8, At.AddMinutes(-120), deferred: true);

        Assert.Equal(["stacked", "ordinary", "deferred"],
            BatchGateStartPolicy.Order([deferred, ordinary, stacked], At.AddMinutes(60)));
    }

    [Theory]
    [InlineData(45, 50)]
    [InlineData(20, 20)]
    public void Older_smaller_batch_starts_within_the_maximum_wait_although_larger_batches_keep_forming(
        int maximumWait, int expectedStartMinute)
    {
        // 5 November 2026: batch 24991efe (12:59 UTC) stayed in phase 0 for
        // hours while later, larger batches ran. One flight per project starts
        // per pass; a larger batch forms before each pass.
        var waiting = new List<BatchGateStartCandidate> { Batch("small", 2, At, maximumWait: maximumWait) };
        int? startedAt = null;
        for (var minute = 10; minute <= 240 && startedAt is null; minute += 10)
        {
            waiting.Add(Batch($"large-{minute}", 6, At.AddMinutes(minute - 1), maximumWait: maximumWait));
            var next = BatchGateStartPolicy.Order(waiting, At.AddMinutes(minute))[0];
            waiting.RemoveAll(batch => batch.BatchId == next);
            if (next == "small") startedAt = minute;
        }

        Assert.Equal(expectedStartMinute, startedAt);
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
