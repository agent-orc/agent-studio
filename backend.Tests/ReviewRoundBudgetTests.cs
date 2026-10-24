using AgentStudio.Review;
using AgentStudio.Runner;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewRoundBudgetTests
{
    [Fact]
    public void Corrupt_sidecar_is_empty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "review-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, ReviewRoundBudgetStore.FileName), "{broken");
            Assert.Empty(ReviewRoundBudgetStore.Read(folder).Rounds);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Accepted_engine_review_updates_existing_ledger_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), "engine-review-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var seed = new ReviewRoundBudgetLedger([
                new DeliveredReviewRound("round-one", ["code-quality"], []),
                new DeliveredReviewRound("round-two", ["code-quality"], []),
            ]);
            ReviewRoundBudgetStore.Record(folder, ReviewRoundBudgetLedger.Empty, seed.Rounds[0]);
            ReviewRoundBudgetStore.Record(folder, seed, seed.Rounds[1]);
            var payload = new StagePayload(ReviewOutcome: ReviewOutcomeKinds.ProductFailure,
                ReviewAttemptId: "round-three",
                Verdicts: [new AgentStudio.TaskServer.Contracts.ReviewVerdictDto(
                    "code-quality", "block", "quality:concerns", "A repeat finding.")],
                DeliveredRounds: seed, MaximumDeliveredRounds: 4);

            ReviewRoundBudgetStore.RecordAcceptedEngineReview(folder, payload);
            ReviewRoundBudgetStore.RecordAcceptedEngineReview(folder, payload);

            var ledger = ReviewRoundBudgetStore.Read(folder);
            Assert.Equal(3, ledger.Delivered);
            Assert.Equal("round-three", ledger.Rounds.Last().AttemptId);
            Assert.Equal("code-quality", ledger.SpentBy);
            Assert.Equal(4, ReviewRoundBudgetPolicy.Decide(ledger, "round-four",
                ["code-quality"], 4, 2).RoundNumber);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("productfailure")]
    [InlineData("PRODUCTFAILURE")]
    [InlineData("pass")]
    [InlineData("integrationbranchdefect")]
    public void Accepted_engine_review_with_any_outcome_casing_is_recorded(string outcome)
    {
        // The report endpoint accepts outcome tokens case-insensitively (AGT-3138).
        var folder = Path.Combine(Path.GetTempPath(), "engine-review-casing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var payload = new StagePayload(ReviewOutcome: outcome, ReviewAttemptId: "round-one",
                Verdicts: [new ReviewVerdictDto("code-quality", "block", "quality:concerns", "A finding.")]);

            ReviewRoundBudgetStore.RecordAcceptedEngineReview(folder, payload);

            var round = Assert.Single(ReviewRoundBudgetStore.Read(folder).Rounds);
            Assert.Equal("round-one", round.AttemptId);
            Assert.Equal(["code-quality"], round.BlockingAspects);
            Assert.Equal("round-one", Assert.Single(StageDecisionPolicy.DeliveredHistory([payload]).Rounds).AttemptId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Engine_review_ledger_counts_blocks_exactly_as_the_transition_decision()
    {
        var folder = Path.Combine(Path.GetTempPath(), "engine-review-uncited-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var seed = new ReviewRoundBudgetLedger([
                new DeliveredReviewRound("round-one", ["code-quality"], []),
                new DeliveredReviewRound("round-two", ["code-quality"], []),
            ]);
            ReviewVerdictDto[] verdicts =
            [
                new("code-quality", "block", ReviewVerdictCitationPolicy.BlockWithoutCitation, "Uncited block."),
                new("requirement-fit", "block", "requirement:gap", "Cited block.", "src/a.cs", "The missing test."),
            ];
            var payload = new StagePayload(ReviewOutcome: ReviewOutcomeKinds.ProductFailure,
                ReviewAttemptId: "round-three", Verdicts: verdicts, DeliveredRounds: seed,
                MaximumDeliveredRounds: 4);
            var decision = StageDecisionPolicy.Council("card", 0, payload).Transition!;

            ReviewRoundBudgetStore.RecordAcceptedEngineReview(folder, payload);

            var recorded = ReviewRoundBudgetStore.Read(folder).Rounds.Single(round => round.AttemptId == "round-three");
            Assert.Equal(["requirement-fit"], recorded.BlockingAspects);
            Assert.Equal(["requirement-fit"], ReviewRoundBudgetPolicy.CountedBlockingAspects(verdicts));
            Assert.Equal(["requirement-fit"], decision.Findings
                .Where(finding => ReviewGradingPolicy.IsBlockingToken(finding.Status)).Select(finding => finding.Aspect));
            // The uncited code-quality block neither degrades this round nor starts a block streak.
            Assert.Empty(recorded.DegradedAspects);
            Assert.Null(recorded.SpentBy);
            Assert.NotEqual(TransitionAction.DegradeWithFollowUp, decision.Action);
            Assert.Equal(["requirement-fit"], Assert.Single(
                StageDecisionPolicy.DeliveredHistory([payload]).Rounds).BlockingAspects);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("block", "requirement:gap", true)]
    [InlineData("BLOCK", "requirement:gap", true)]
    [InlineData("block", "block-without-citation", false)]
    [InlineData("block", "BLOCK-WITHOUT-CITATION", false)]
    [InlineData("concerns", "requirement:gap", false)]
    [InlineData("pass", "", false)]
    public void Counted_blocking_aspects_exclude_uncited_and_non_blocking_verdicts(
        string status, string classification, bool counted)
    {
        var aspects = ReviewRoundBudgetPolicy.CountedBlockingAspects(
            [new ReviewVerdictDto("code-quality", status, classification, "A verdict.")]);
        Assert.Equal(counted, aspects.Count == 1);
    }

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
    public void ReviewVerificationBlock_IsNeverDegraded()
    {
        var history = new ReviewRoundBudgetLedger([
            new DeliveredReviewRound("r1", ["review-verification"], []),
            new DeliveredReviewRound("r2", ["review-verification"], []),
        ]);

        var decision = ReviewRoundBudgetPolicy.Decide(history, "r3",
            ["review-verification"], 3, 2);

        Assert.False(decision.Degrade);
        Assert.Equal("review-verification", decision.SpentBy);
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
        var folder = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "review-budget-" + Guid.NewGuid().ToString("N"))).FullName;
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
    public void Record_OnAMovedCardFolder_FailsInsteadOfRecreatingTheOldLane()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "review-budget-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var staleFolder = Path.Combine(root, "4-auto-review", "AGT-1");
            var round = new DeliveredReviewRound("r1", ["code-quality"], []);

            Assert.Throws<DirectoryNotFoundException>(() =>
                ReviewRoundBudgetStore.Record(staleFolder, ReviewRoundBudgetLedger.Empty, round));
            Assert.Throws<DirectoryNotFoundException>(() =>
                ReviewRoundBudgetStore.MarkFollowUp(staleFolder, "r1", "AGT-2"));
            Assert.False(Directory.Exists(Path.Combine(root, "4-auto-review")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
        var followUp = AgentStudio.OrchestrationDecisions.ReviewFollowUpPolicy.Decide(findings, 0, 1);
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
        var followUp = AgentStudio.OrchestrationDecisions.ReviewFollowUpPolicy.Decide(
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
