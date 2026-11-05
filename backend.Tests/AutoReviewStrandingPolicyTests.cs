using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewStrandingPolicyTests
{
    private static AutoReviewStrandingFacts Stranded() => new(
        TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30), false, false,
        false, false, false, false, true, false, false);

    [Fact]
    public void Missing_review_step_is_started_after_threshold()
        => Assert.Equal(AutoReviewStrandingAction.StartStep,
            AutoReviewStrandingPolicy.Decide(Stranded()));

    [Fact]
    public void Report_only_summary_failure_restarts_post_processing()
        => Assert.Equal(AutoReviewStrandingAction.StartStep,
            AutoReviewStrandingPolicy.Decide(Stranded() with { ReportOnly = true, HasCompletedRun = false }));

    [Fact]
    public void Passed_review_without_batch_entry_reenters_gate()
        => Assert.Equal(AutoReviewStrandingAction.ReenterBatch,
            AutoReviewStrandingPolicy.Decide(Stranded() with { HasReview = true, PassedReview = true }));

    [Fact]
    public void Unrecoverable_card_escalates()
        => Assert.Equal(AutoReviewStrandingAction.Escalate,
            AutoReviewStrandingPolicy.Decide(Stranded() with { HasCompletedRun = false }));

    [Fact]
    public void Terminal_failed_review_without_owner_escalates()
        => Assert.Equal(AutoReviewStrandingAction.Escalate,
            AutoReviewStrandingPolicy.Decide(Stranded() with { HasReview = true }));

    [Theory]
    [InlineData(BatchPhase.Formed)]
    [InlineData(BatchPhase.Assembling)]
    [InlineData(BatchPhase.Assembled)]
    [InlineData(BatchPhase.Running)]
    [InlineData(BatchPhase.Green)]
    [InlineData(BatchPhase.Publishing)]
    public void Active_formed_or_gating_batch_owns_card(BatchPhase phase)
        => Assert.True(AutoReviewStrandingWatchdog.IsActiveBatchPhase(phase));

    [Theory]
    [InlineData(BatchPhase.Published)]
    [InlineData(BatchPhase.Red)]
    [InlineData(BatchPhase.Abandoned)]
    public void Settled_batch_does_not_own_card(BatchPhase phase)
        => Assert.False(AutoReviewStrandingWatchdog.IsActiveBatchPhase(phase));

    [Theory]
    [InlineData("running-step")]
    [InlineData("running-review")]
    [InlineData("pending-batch")]
    [InlineData("formed-batch")]
    [InlineData("gating-batch")]
    [InlineData("queued-round")]
    [InlineData("queued-step")]
    [InlineData("within-threshold")]
    public void Owned_cards_are_not_intervened_on(string owner)
    {
        var facts = owner switch
        {
            "running-step" => Stranded() with { RunningStep = true },
            "running-review" => Stranded() with { ActiveReview = true },
            "pending-batch" or "formed-batch" or "gating-batch" => Stranded() with { BatchMember = true },
            "queued-round" => Stranded() with { QueuedRound = true },
            "queued-step" => Stranded() with { QueuedStep = true },
            _ => Stranded() with { InLane = TimeSpan.FromMinutes(30) },
        };
        Assert.Equal(AutoReviewStrandingAction.Leave, AutoReviewStrandingPolicy.Decide(facts));
    }
}
