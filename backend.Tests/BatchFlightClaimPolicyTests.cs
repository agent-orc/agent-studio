using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3569: the flight claim rule as a direct matrix.</summary>
public sealed class BatchFlightClaimPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static BatchFlightClaimFacts Claim(int deadlineInMinutes = 10, int heartbeatInMinutes = 2,
        bool ended = false, bool settled = false, bool ownerGone = false,
        BatchFlightStage stage = BatchFlightStage.Reserving)
        => new(stage, Now.AddMinutes(deadlineInMinutes), Now.AddMinutes(heartbeatInMinutes),
            ended, settled, Now, ownerGone);

    [Fact]
    public void A_scope_without_a_claim_or_with_a_settled_end_is_granted()
    {
        Assert.True(BatchFlightClaimPolicy.Decide(null).Granted);
        Assert.True(BatchFlightClaimPolicy.Decide(Claim(ended: true, settled: true)).Granted);
    }

    [Fact]
    public void A_live_claim_holds_the_scope()
        => Assert.Equal(new BatchFlightClaimDecision(false, BatchFlightClaimPolicy.Held),
            BatchFlightClaimPolicy.Decide(Claim()));

    [Theory]
    [InlineData(10, -1, false, false)] // heartbeat lapsed, not yet expired
    [InlineData(-1, 2, false, false)]  // deadline passed, not yet expired
    [InlineData(10, 2, true, false)]   // expired, outcome not applied
    public void A_claim_that_is_not_live_keeps_the_scope_closed_until_its_outcome_is_settled(
        int deadline, int heartbeat, bool ended, bool settled)
        => Assert.Equal(new BatchFlightClaimDecision(false, BatchFlightClaimPolicy.AwaitingSettlement),
            BatchFlightClaimPolicy.Decide(Claim(deadline, heartbeat, ended, settled)));

    [Theory]
    [InlineData(10, 2, false, null)]
    [InlineData(10, 0, false, BatchFlightClaimExpiry.HeartbeatLost)]
    [InlineData(10, 2, true, BatchFlightClaimExpiry.HeartbeatLost)]
    [InlineData(0, 2, false, BatchFlightClaimExpiry.StageDeadlinePassed)]
    // A step that hung past its deadline counts as a deadline even when the heartbeat lapsed too.
    [InlineData(-5, -1, false, BatchFlightClaimExpiry.StageDeadlinePassed)]
    public void Expiry_names_the_deadline_first_then_the_heartbeat(int deadline, int heartbeat,
        bool ownerGone, BatchFlightClaimExpiry? expected)
        => Assert.Equal(expected, BatchFlightClaimPolicy.Expiry(Claim(deadline, heartbeat, ownerGone: ownerGone)));

    [Fact]
    public void An_ended_claim_never_expires_again_and_is_not_live()
    {
        var ended = Claim(-5, -5, ended: true);
        Assert.Null(BatchFlightClaimPolicy.Expiry(ended));
        Assert.False(BatchFlightClaimPolicy.IsLive(ended));
    }

    [Fact]
    public void A_heartbeat_does_not_extend_a_step_past_its_deadline()
    {
        Assert.True(BatchFlightClaimPolicy.Renews(Claim()));
        Assert.False(BatchFlightClaimPolicy.Renews(Claim(deadlineInMinutes: 0)));
        Assert.False(BatchFlightClaimPolicy.Renews(Claim(heartbeatInMinutes: 0)));
    }

    [Theory]
    [InlineData(BatchFlightStage.Reserving, BatchFlightClaimExpiry.StageDeadlinePassed, 0, BatchFlightExpiryOutcome.Retry)]
    [InlineData(BatchFlightStage.Reserving, BatchFlightClaimExpiry.StageDeadlinePassed, 1, BatchFlightExpiryOutcome.VisibleGate)]
    [InlineData(BatchFlightStage.Assembling, BatchFlightClaimExpiry.StageDeadlinePassed, 0, BatchFlightExpiryOutcome.Retry)]
    [InlineData(BatchFlightStage.Gating, BatchFlightClaimExpiry.StageDeadlinePassed, 0, BatchFlightExpiryOutcome.Retry)]
    [InlineData(BatchFlightStage.Gating, BatchFlightClaimExpiry.StageDeadlinePassed, 3, BatchFlightExpiryOutcome.VisibleGate)]
    [InlineData(BatchFlightStage.Publishing, BatchFlightClaimExpiry.StageDeadlinePassed, 0, BatchFlightExpiryOutcome.Recover)]
    [InlineData(BatchFlightStage.Reserving, BatchFlightClaimExpiry.HeartbeatLost, 0, BatchFlightExpiryOutcome.Recover)]
    [InlineData(BatchFlightStage.Gating, BatchFlightClaimExpiry.HeartbeatLost, 5, BatchFlightExpiryOutcome.Recover)]
    public void Outcome_retries_within_the_budget_then_shows_a_gate_and_leaves_publication_to_recovery(
        BatchFlightStage stage, BatchFlightClaimExpiry expiry, int prior, BatchFlightExpiryOutcome expected)
        => Assert.Equal(expected, BatchFlightClaimPolicy.Outcome(stage, expiry, prior));

    [Theory]
    [InlineData(BatchFlightStage.Reserving, 40)]
    [InlineData(BatchFlightStage.Assembling, BatchFlightClaimPolicy.AssemblingDeadlineMinutes)]
    [InlineData(BatchFlightStage.Gating, 150 + BatchFlightClaimPolicy.GatingGraceMinutes)]
    [InlineData(BatchFlightStage.Publishing, BatchFlightClaimPolicy.PublishingDeadlineMinutes)]
    public void Each_step_has_its_own_deadline(BatchFlightStage stage, int minutes)
    {
        Assert.Equal(minutes, BatchFlightClaimPolicy.DeadlineMinutes(stage, 40, 150));
        Assert.Equal(Now.AddMinutes(minutes), BatchFlightClaimPolicy.StageDeadline(stage, Now, 40, 150));
    }

    [Fact]
    public void A_retried_reservation_keeps_the_lease_deadline_wording_and_other_expiries_name_their_step()
    {
        Assert.Equal(BatchGateStartPolicy.LeaseDeadlineReason(5), BatchFlightClaimPolicy.ExpiryReason(
            BatchFlightStage.Reserving, BatchFlightClaimExpiry.StageDeadlinePassed, BatchFlightExpiryOutcome.Retry, 5));
        Assert.Contains("gate step within 165 minutes", BatchFlightClaimPolicy.ExpiryReason(
            BatchFlightStage.Gating, BatchFlightClaimExpiry.StageDeadlinePassed, BatchFlightExpiryOutcome.Retry, 165));
        Assert.Contains("per-task gate", BatchFlightClaimPolicy.ExpiryReason(
            BatchFlightStage.Reserving, BatchFlightClaimExpiry.StageDeadlinePassed,
            BatchFlightExpiryOutcome.VisibleGate, 5));
        Assert.Contains("restart recovery", BatchFlightClaimPolicy.ExpiryReason(
            BatchFlightStage.Gating, BatchFlightClaimExpiry.HeartbeatLost, BatchFlightExpiryOutcome.Recover, 165));
    }
}
