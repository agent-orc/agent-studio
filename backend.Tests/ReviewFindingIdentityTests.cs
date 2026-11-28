using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewFindingIdentityTests
{
    private const string Parser = "runner/ReviewTestFailureAttribution.cs";

    [Fact]
    public void Delivered_rounds_keep_exact_recurrences_and_unchecked_claims_independent()
    {
        var confirmation = new ReviewFindingClaim("backend/OperatorBatchService.cs ConfirmAsync",
            "A persisted confirmation reaches finalization.", "Audit failure skips finalization.",
            "backend/OperatorBatchService.cs:77");
        var omitted = new ReviewFindingClaim(Parser + " ReadAssertion",
            "Preserve failed test identity.", "Passing sibling output contaminates the fingerprint.", Parser + ":30");
        var related = omitted with
        {
            Violation = "A real sibling boundary truncates the selected diagnostic.",
            Evidence = Parser + ":41",
        };
        var first = Settle(null, "ProductFailure", [confirmation, omitted, confirmation]);
        var original = Assert.IsType<ReviewFindingRound>(first.FindingRound);
        Assert.Empty(original.Rechecks);
        Assert.Equal(2, original.OpenFindings.Count);
        var confirmationId = original.OpenFindings[0].Id!;
        var omittedId = original.OpenFindings[1].Id!;

        var recurrence = confirmation with { Evidence = "backend/OperatorBatchService.cs:102" };
        var next = Settle(original, "ProductFailure", [recurrence, related]);
        var round = next.FindingRound!;
        Assert.Equal(
            [new(confirmationId, "still-present", recurrence.Evidence), new(omittedId, "not-rechecked")],
            round.Rechecks);
        var newId = Assert.Single(round.OpenFindings, item => item.Violation == related.Violation).Id!;
        Assert.Equal(3, round.OpenFindings.Select(item => item.Id).Distinct().Count());

        var settled = Settle(round, "Pass", [], [new(confirmationId, "addressed", "Finalization ran after audit failure."),
            new(newId, "withdrawn", "The full diagnostic was captured before the sibling heading.")]);
        Assert.Equal(omittedId, Assert.Single(settled.FindingRound!.OpenFindings).Id);
        Assert.Equal(["addressed", "not-rechecked", "withdrawn"],
            settled.FindingRound.Rechecks.Select(item => item.Disposition));

        // The engine reissues the two current blockers, leaving the omitted claim open.
        var facts = StageDecisionPolicy.Facts("card", 0,
            new StagePayload(ReviewOutcome: "ProductFailure", ReviewAttemptId: "review-2", Verdicts: next.Verdicts));
        var followUp = AgentStudio.OrchestrationDecisions.ReviewFollowUpPolicy.Decide(facts.Review.Findings, 0, 0);
        var prompt = ReviewFindingRoundPrompt.Build(followUp.Findings, "review-2", true);
        Assert.Equal([confirmationId, newId], ReviewFindingRoundPrompt.RoutedFindingIds(prompt));
        Assert.DoesNotContain(omittedId, prompt);
    }

    [Fact]
    public void Carried_over_verdict_and_infra_report_cannot_close_an_open_finding()
    {
        var claim = new ReviewFindingClaim(Parser, "Preserve failed test identity.", "Wrong test selected.", Parser + ":30");
        var first = Settle(null, "ProductFailure", [claim]).FindingRound!;
        var id = Assert.Single(first.OpenFindings).Id!;
        var report = Report("Pass", [], [new(id, "addressed", "Parser now selects the right test.")]);
        var carried = report with { Verdicts = [report.Verdicts[0] with { CarriedOverFrom = "review-1" }] };

        var settled = ReviewFindingIdentity.Record(carried, "card", first, "Pass");
        Assert.Equal(new ReviewFindingRecheck(id, "not-rechecked"), Assert.Single(settled.FindingRound!.Rechecks));
        Assert.Null(ReviewFindingIdentity.Record(report, "card", first, "ReviewInfra").FindingRound);
    }

    private static ReviewReportRequest Settle(ReviewFindingRound? previous, string outcome,
        IReadOnlyList<ReviewFindingClaim> claims, IReadOnlyList<ReviewFindingRecheck>? rechecks = null)
        => ReviewFindingIdentity.Record(Report(outcome, claims, rechecks), "card", previous, outcome);

    private static ReviewReportRequest Report(string outcome, IReadOnlyList<ReviewFindingClaim> claims,
        IReadOnlyList<ReviewFindingRecheck>? rechecks)
        => new("reviewer", "instance", "lease", 1, "report", outcome, null, null,
            new("repo", "head", "head", "tree", false, false, "folder", "namespace",
                ChangedPaths: [Parser, "backend/OperatorBatchService.cs"]),
            new("host", "reviewer", "instance", "os", "arch", "runtime",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [new ReviewVerdictDto("code-quality", outcome == "Pass" ? "pass" : "block",
                "RemoteAspectVerdict", "Inspected delivery.", Parser, "Preserve assertion boundaries.")
            {
                FindingClaims = claims,
                FindingRechecks = rechecks,
            }]);
}
