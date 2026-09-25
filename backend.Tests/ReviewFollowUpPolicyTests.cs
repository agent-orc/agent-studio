using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public class ReviewFollowUpPolicyTests
{
    [Fact]
    public void Concern_then_pass_policy_consumes_one_round()
    {
        var folder = Path.Combine(Path.GetTempPath(), "review-concern-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var reviewer = new Queue<IReadOnlyList<ReviewFollowUpFinding>>([
                [new("code-quality", "concerns", "Dead assertion in backend.Tests/FooTests.cs.", Finding: "Remove the dead assertion.")],
                [new("code-quality", "pass", "The dead assertion is gone.")],
            ]);

            var first = ReviewFollowUpPolicy.Decide(reviewer.Dequeue(), 0, 1);
            Assert.Equal(ReviewFollowUpAction.ReviewConcernRound, first.Action);
            Assert.True(ReviewConcernRoundStore.TryConsume(
                folder,
                1,
                "review-first",
                first.Findings.Select(finding => finding.Aspect),
                out var round));
            Assert.Equal(1, round.Used);

            var second = ReviewFollowUpPolicy.Decide(reviewer.Dequeue(), round.Used, round.Maximum);
            Assert.Equal(ReviewFollowUpAction.Accept, second.Action);
            ReviewConcernRoundStore.MarkReviewed(folder, fixRunIndex: 2, stillOpen: false);
            var settled = Assert.IsType<ReviewConcernRoundLedger>(ReviewConcernRoundStore.Read(folder));
            Assert.Equal(2, settled.FixRunIndex);
            Assert.False(settled.StillOpen);
            Assert.Empty(reviewer);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Actionable_concern_starts_exactly_one_default_round()
    {
        var finding = new ReviewFollowUpFinding(
            "code-quality", "concerns", "Dead assertion in backend.Tests/FooTests.cs.",
            "backend.Tests/FooTests.cs", "Remove the no-op assertion.");

        Assert.Equal(
            ReviewFollowUpAction.ReviewConcernRound,
            ReviewFollowUpPolicy.Decide([finding], concernRoundsUsed: 0, maxConcernRounds: 1).Action);
        Assert.Equal(
            ReviewFollowUpAction.Accept,
            ReviewFollowUpPolicy.Decide([finding], concernRoundsUsed: 1, maxConcernRounds: 1).Action);
    }

    [Fact]
    public void Setting_zero_disables_concern_round()
        => Assert.Equal(
            ReviewFollowUpAction.Accept,
            ReviewFollowUpPolicy.Decide(
                [new("code-quality", "concerns", "Finding: dead code.", Finding: "Dead code remains.")],
                concernRoundsUsed: 0,
                maxConcernRounds: 0).Action);

    [Fact]
    public void Concern_without_a_file_or_named_finding_does_not_start_a_round()
        => Assert.Equal(
            ReviewFollowUpAction.Accept,
            ReviewFollowUpPolicy.Decide(
                [new("code-quality", "concerns", "The change could be cleaner.")],
                concernRoundsUsed: 0,
                maxConcernRounds: 1).Action);

    [Fact]
    public void Unparseable_verdict_is_infrastructure_not_a_concern_round()
        => Assert.Equal(
            ReviewFollowUpAction.RetryAspect,
            ReviewFollowUpPolicy.Decide(
                [new("code-quality", "concerns", "Aspect runner produced no parseable verdict.", Classification: "review:unparseable")],
                concernRoundsUsed: 0,
                maxConcernRounds: 1).Action);

    [Fact]
    public void Local_consumer_marks_retry_aspect_exhaustion_as_infrastructure()
    {
        var verdict = new AspectVerdict(
            "code-quality",
            AspectStatus.Concerns,
            "Aspect runner produced no parseable verdict.",
            "body",
            "review:unparseable");
        var decision = ReviewFollowUpPolicy.Decide(
            [new("code-quality", "concerns", verdict.Summary, Classification: verdict.ConcernTagId)],
            0,
            1);

        var exhausted = ReviewDecisionOrchestrator.MarkRetryAspectAsInfrastructure(
            AspectRunReport.From([verdict]),
            decision);

        Assert.True(exhausted.HasInfraFailure);
        Assert.True(Assert.Single(exhausted.Verdicts).IsInfraFailure);
    }

    [Fact]
    public void Remote_consumer_classifies_retry_aspect_exhaustion_as_review_infrastructure()
    {
        var request = new ReviewReportRequest(
            "reviewer",
            "instance",
            "lease",
            1,
            "report",
            "Pass",
            null,
            "concern",
            null!,
            null!,
            [],
            [],
            []);
        var decision = ReviewFollowUpPolicy.Decide(
            [new("code-quality", "concerns", "No parseable verdict.", Classification: "review:unparseable")],
            0,
            1);

        var classified = V1ReviewPlaneEndpoints.ClassifyRetryAspectExhaustion(request, decision);

        Assert.Equal("ReviewInfra", classified.Outcome);
        Assert.Equal("AspectVerdictUnparseable", classified.FailureClassification);
        Assert.Equal(decision.Reason, classified.Summary);
    }

    [Fact]
    public void Block_path_remains_a_finding_round()
        => Assert.Equal(
            ReviewFollowUpAction.ReviewFindingRound,
            ReviewFollowUpPolicy.Decide(
                [new("tests-and-evidence", "block", "Required test fails.", Finding: "FooTests fails.")],
                concernRoundsUsed: 99,
                maxConcernRounds: 0).Action);

    [Fact]
    public void Finding_round_context_does_not_consume_the_concern_budget()
    {
        var folder = Path.Combine(Path.GetTempPath(), "review-finding-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var ledger = ReviewConcernRoundStore.RecordFinding(
                folder,
                maximumConcernRounds: 1,
                reviewAttemptId: "review-blocked",
                aspectIds: ["code-quality"],
                reviewedResultSha: "result-a",
                reviewedIntegrationTipSha: "integration-a",
                previousVerdicts: [new ReviewVerdictDto("code-quality", "block", "quality:concerns", "Dead code", "backend/Foo.cs", "Remove it")]);

            Assert.Equal(RunTriggers.ReviewFinding, ledger.RoundKind);
            Assert.Equal(0, ledger.Used);
            Assert.True(ReviewConcernRoundStore.TryConsume(
                folder,
                1,
                "review-concern",
                ["requirement-fit"],
                out var concern));
            Assert.Equal(1, concern.Used);
            Assert.Equal(RunTriggers.ReviewConcern, concern.RoundKind);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Scoped_review_carries_unaffected_aspects_and_reruns_required_cases()
    {
        var decisions = ScopedReviewPolicy.Plan(new ScopedReviewFacts(
            Enabled: true,
            ReviewedTreeIdentityMatches: true,
            DeltaFileCount: 2,
            MaximumDeltaFiles: 20,
            ChangedFiles: ["backend/Foo.cs", "contracts/api.schema.json"],
            RemovedFiles: ["frontend/src/app/foo.spec.ts"],
            PreviousAspects:
            [
                new("code-quality", "concerns", ["backend/Foo.cs"], RaisedFinding: true),
                new("requirement-fit", "pass", ["README.md"], RaisedFinding: false),
                new("documentation-impact", "pass", ["docs/start/README.md"], RaisedFinding: false),
                new("tests-and-evidence", "pass", ["backend.Tests/Bar.cs"], RaisedFinding: false),
            ]));

        Assert.True(decisions.Single(item => item.Aspect == "code-quality").Run);
        Assert.False(decisions.Single(item => item.Aspect == "requirement-fit").Run);
        Assert.True(decisions.Single(item => item.Aspect == "documentation-impact").Run);
        Assert.True(decisions.Single(item => item.Aspect == "tests-and-evidence").Run);
    }

    [Theory]
    [InlineData(false, true, 1, 20)]
    [InlineData(true, false, 1, 20)]
    [InlineData(true, true, 21, 20)]
    public void Scoped_review_falls_back_to_full_review(
        bool enabled, bool identityMatches, int delta, int bound)
    {
        var decisions = ScopedReviewPolicy.Plan(new ScopedReviewFacts(
            enabled, identityMatches, delta, bound,
            ["backend/Foo.cs"], [],
            [new("requirement-fit", "pass", ["README.md"], false)]));

        Assert.True(Assert.Single(decisions).Run);
    }
}
