using AgentStudio.Review;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewRoundBudgetTests
{
    [Fact]
    public void EpochAndOperatorContinuation_DoNotResetDeliveredRoundBudget()
    {
        var history = new ReviewRoundBudgetLedger([
            new DeliveredReviewRound("review-epoch-0", ["code-quality"], []),
            new DeliveredReviewRound("review-epoch-1", ["documentation-impact"], []),
            new DeliveredReviewRound("review-epoch-2", ["requirement-fit"], []),
        ]);

        var decision = ReviewRoundBudgetPolicy.Decide(
            history, "review-epoch-3", ["code-quality"], maximumRounds: 4,
            consecutiveBlockRounds: 2);

        Assert.Equal(4, decision.RoundNumber);
        Assert.True(decision.Degrade);
        Assert.Equal("code-quality", decision.SpentBy);
    }

    [Fact]
    public void DifferentFindingsFromSameAspect_TriggerOneDegradedDelivery()
    {
        // Summaries live in report evidence; the counter deliberately keys on
        // the aspect id, so three different code locations still form a streak.
        var history = new ReviewRoundBudgetLedger([
            new DeliveredReviewRound("r1", ["code-quality"], []),
            new DeliveredReviewRound("r2", ["code-quality"], []),
        ]);

        var decision = ReviewRoundBudgetPolicy.Decide(
            history, "r3", ["code-quality", "documentation-impact"], 4, 2);

        Assert.Equal(3, decision.RoundNumber);
        Assert.Equal("code-quality", decision.SpentBy);
        Assert.Equal(["code-quality"], decision.DegradedAspects);
    }

    [Fact]
    public void DegradedReport_PassesWithConcernsAndRetainsFindingSummary()
    {
        var request = new AgentStudio.TaskServer.Contracts.ReviewReportRequest(
            "executor", "instance", "lease", 1, "key", "ProductFailure", null,
            "Original overall summary.", null!, null!, [], [],
            [new AgentStudio.TaskServer.Contracts.ReviewVerdictDto(
                "code-quality", "block", "quality:concerns", "Issue in another method.")]);
        var decision = new ReviewRoundBudgetDecision(3, 4, ["code-quality"], "code-quality");

        var applied = ReviewRoundBudgetPolicy.ApplyRemoteReport(request, decision);

        Assert.Equal("Pass", applied.Outcome);
        Assert.Equal("concerns", Assert.Single(applied.Verdicts).Status);
        Assert.Equal("Issue in another method.", applied.Verdicts[0].Summary);
        Assert.Contains("Original overall summary.", applied.Summary);
    }

    [Fact]
    public void RecurringAspect_DoesNotEraseAnUnrelatedNewBlock()
    {
        var history = new ReviewRoundBudgetLedger([
            new DeliveredReviewRound("r1", ["code-quality"], []),
            new DeliveredReviewRound("r2", ["code-quality"], []),
        ]);
        var decision = ReviewRoundBudgetPolicy.Decide(
            history, "r3", ["code-quality", "requirement-fit"], 4, 2);
        var request = new AgentStudio.TaskServer.Contracts.ReviewReportRequest(
            "executor", "instance", "lease", 1, "key", "ProductFailure", null,
            "Two findings.", null!, null!, [], [],
            [new AgentStudio.TaskServer.Contracts.ReviewVerdictDto(
                "code-quality", "block", "quality:concerns", "Recurring finding."),
             new AgentStudio.TaskServer.Contracts.ReviewVerdictDto(
                "requirement-fit", "block", "requirement:missing", "New finding.")]);

        var applied = ReviewRoundBudgetPolicy.ApplyRemoteReport(request, decision);

        Assert.Equal(["code-quality"], decision.DegradedAspects);
        Assert.Equal("ProductFailure", applied.Outcome);
        Assert.Equal("concerns", applied.Verdicts[0].Status);
        Assert.Equal("block", applied.Verdicts[1].Status);
    }

    [Fact]
    public void BuildTestBlock_IsNeverDegradedEvenWhenLifetimeBudgetIsSpent()
    {
        var history = new ReviewRoundBudgetLedger([
            new DeliveredReviewRound("r1", ["code-quality"], []),
            new DeliveredReviewRound("r2", ["code-quality"], []),
            new DeliveredReviewRound("r3", ["code-quality"], []),
        ]);

        var decision = ReviewRoundBudgetPolicy.Decide(
            history, "r4", ["build-tests", "code-quality"], 4, 2);

        Assert.False(decision.Degrade);
        Assert.Equal("build-tests", decision.SpentBy);
    }

    [Fact]
    public void ZeroAutomaticReissueAllowance_KeepsFirstBlockForEscalation()
    {
        var decision = ReviewRoundBudgetPolicy.Decide(
            ReviewRoundBudgetLedger.Empty, "r1", ["requirement-fit"],
            maximumRounds: 4, consecutiveBlockRounds: 2,
            priorAutomaticReissues: 0, maximumAutomaticReissues: 0);

        Assert.False(decision.Degrade);
        Assert.Equal(1, decision.RoundNumber);
    }

    [Fact]
    public void AcceptedAttempt_IsChargedOnceAndFollowUpLinkIsStableAcrossReplay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "review-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seed = ReviewRoundBudgetLedger.Empty;
            var round = new DeliveredReviewRound("r4", ["code-quality"], ["code-quality"],
                SpentBy: "code-quality");
            ReviewRoundBudgetStore.Record(folder, seed, round);
            ReviewRoundBudgetStore.MarkFollowUp(folder, "r4", "AGT-9999");
            ReviewRoundBudgetStore.Record(folder, seed, round);

            var saved = ReviewRoundBudgetStore.Read(folder);
            Assert.Equal(1, saved.Delivered);
            Assert.Equal("AGT-9999", Assert.Single(saved.Rounds).FollowUpTaskKey);
            Assert.Equal("code-quality", saved.SpentBy);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void LegacySeed_CountsDeliveredReviewsButNotInfrastructureRetries()
    {
        var attempts = new ReviewAttempt[]
        {
            new() { Plane = ReviewPlane.Remote, AttemptId = "pass", Outcome = "Pass",
                BuildTestsResult = "passed", ReportRef = "pass.md" },
            new() { Plane = ReviewPlane.Remote, AttemptId = "infra", Outcome = "ReviewInfra",
                BuildTestsResult = "not-proven", ReportRef = "infra.md" },
        };

        var ledger = ReviewRoundBudgetStore.Read(
            Path.Combine(Path.GetTempPath(), "missing-review-budget-" + Guid.NewGuid().ToString("N")), attempts);

        Assert.Equal(1, ledger.Delivered);
        Assert.Equal("pass", ledger.Rounds[0].AttemptId);
    }

    [Fact]
    public void LegacyProjection_SeedsDeliveredRoundsWithoutReset()
    {
        var folder = Path.Combine(Path.GetTempPath(), "review-budget-" + Guid.NewGuid().ToString("N"));
        var old = new ReviewAttempt
        {
            Plane = ReviewPlane.Remote,
            AttemptId = "old-round",
            BuildTestsResult = "passed",
            ReportRef = "remote-review-grade-old-round.md",
            Aspects = [new ReviewAspectVerdict { Aspect = "code-quality", Status = "block" }],
        };

        var ledger = ReviewRoundBudgetStore.Read(folder, [old]);

        Assert.Equal(1, ledger.Delivered);
        Assert.Equal("code-quality", Assert.Single(ledger.Rounds).BlockingAspects[0]);
    }
    [Fact]
    public void DegradedBlock_DoesNotStartAnAutomaticConcernRound()
    {
        // Local review follows the same composition as Remote Review: the
        // follow-up policy sees an actionable finding, the spent budget moves it
        // to the linked follow-up card, and the delivery is accepted.
        var findings = new AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding[]
        {
            new("code-quality", "concerns", "Dead branch in backend/Feature.cs.",
                "backend/Feature.cs", "Remove the dead branch."),
        };
        var followUp = AgentStudio.TaskServer.Contracts.ReviewFollowUpPolicy.Decide(findings, 0, 1);
        Assert.Equal(AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.ReviewConcernRound, followUp.Action);

        var degraded = new ReviewRoundBudgetDecision(4, 4, ["code-quality"], "code-quality");
        var applied = ReviewRoundBudgetPolicy.ApplyFollowUp(followUp, degraded);

        Assert.Equal(AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.Accept, applied.Action);
        Assert.False(applied.StartsCodingRound);
        Assert.Contains("linked follow-up", applied.Reason);
    }

    [Fact]
    public void UnspentBudget_KeepsTheOrdinaryConcernRound()
    {
        var followUp = AgentStudio.TaskServer.Contracts.ReviewFollowUpPolicy.Decide(
            [new("code-quality", "concerns", "Dead branch in backend/Feature.cs.",
                "backend/Feature.cs", "Remove the dead branch.")], 0, 1);
        var unspent = new ReviewRoundBudgetDecision(2, 4, [], null);

        Assert.Same(followUp, ReviewRoundBudgetPolicy.ApplyFollowUp(followUp, unspent));
    }

    [Fact]
    public void DegradedBlock_LeavesAspectRetryUntouched()
    {
        var retry = new AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision(
            AgentStudio.TaskServer.Contracts.ReviewGrade.ProductFailure,
            AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.RetryAspect, [], "infra");
        var degraded = new ReviewRoundBudgetDecision(4, 4, ["code-quality"], "code-quality");

        Assert.Same(retry, ReviewRoundBudgetPolicy.ApplyFollowUp(retry, degraded));
    }
}
