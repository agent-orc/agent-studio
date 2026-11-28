using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public class CodeQualityReviewPolicyTests
{
    private const string Runbook = "docs/operations/setup/linux-runner-host.md";
    private const string Parser = "runner/ReviewTestFailureAttribution.cs";
    private const string Prompt = CodeQualityReviewPolicy.ProtocolHeading;

    private static readonly ReviewVerdictDto Migration = new(
        "code-quality", "block", CodeQualityReviewPolicy.Confirmed,
        "The migration can enable the launcher after release verification fails.",
        Runbook + " migration step 3",
        "The migration shell block lacks a failure gate between release verification and hardening.");

    private static readonly ReviewVerdictDto SiblingClaim = new(
        "code-quality", "block", CodeQualityReviewPolicy.Confirmed,
        "A passing sibling's output can invalidate a matching assertion fingerprint.",
        Parser + " AssertionFingerprint",
        "The Passed and Skipped heading branch can consume sibling output.");

    [Fact]
    public void Runbook_block_remains_actionable_even_with_a_redundant_argument_suggestion()
    {
        var suggestion = new ReviewVerdictDto("code-quality", "concerns",
            CodeQualityReviewPolicy.Advisory,
            "deploy/agent-host/agent-runner-deploy checks the same release path twice.",
            "deploy/agent-host/agent-runner-deploy guard invocation",
            "Remove the redundant argument when convenient.");

        var advisory = Settle(suggestion, Prompt, ["deploy/agent-host/agent-runner-deploy"]);
        Assert.False(Decide(advisory).StartsCodingRound);
        var decision = Decide(Settle(Migration, Prompt, [Runbook]), advisory);
        Assert.Equal(ReviewFollowUpAction.ReviewFindingRound, decision.Action);
        Assert.Equal(Migration.Summary, Assert.Single(decision.Findings).Summary);
    }

    [Theory]
    [InlineData("block", CodeQualityReviewPolicy.Confirmed)]
    [InlineData("concerns", CodeQualityReviewPolicy.Uncorroborated)]
    public void Dropped_or_unconfirmed_parser_claim_stays_visible_without_another_coding_round(
        string status, string classification)
    {
        var prompt = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, "agt-4326-round-3", SiblingClaim);
        var candidate = SiblingClaim with { Status = status, Classification = classification };
        var settled = Settle(candidate, prompt, [Parser]);
        Assert.Equal("concerns", settled.Status);
        Assert.Equal(CodeQualityReviewPolicy.Uncorroborated, settled.Classification);
        Assert.Equal(SiblingClaim.EvidenceChecked, settled.EvidenceChecked);
        Assert.Equal(SiblingClaim.Missing, settled.Missing);
        Assert.False(Decide(settled).StartsCodingRound);
        Assert.Contains(SiblingClaim.Missing!, prompt);
    }

    [Theory]
    [InlineData("pass", "RemoteAspectVerdict")]
    [InlineData("pass", CodeQualityReviewPolicy.Advisory)]
    [InlineData("concerns", CodeQualityReviewPolicy.Uncorroborated)]
    [InlineData("block", CodeQualityReviewPolicy.Rechecked)]
    public void Omitted_parser_claim_retains_the_prior_attempt_finding_and_evidence(
        string status, string classification)
    {
        const string attempt = "agt-4326-round-3";
        var prompt = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, attempt, SiblingClaim);
        var candidate = new ReviewVerdictDto("code-quality", status, classification,
            "The remaining changes look sound.", "none", "none");

        var settled = Settle(candidate, prompt, [Parser]);

        Assert.Equal("concerns", settled.Status);
        Assert.Contains(candidate.Summary, settled.Summary);
        Assert.Contains(attempt, settled.Summary);
        Assert.Contains(SiblingClaim.Summary, settled.Summary);
        Assert.Equal(SiblingClaim.EvidenceChecked, settled.EvidenceChecked);
        Assert.Equal(SiblingClaim.Missing, settled.Missing);
        Assert.False(Decide(settled).StartsCodingRound);
        Assert.Equal(settled, Settle(settled, prompt, [Parser]));
    }

    [Theory]
    [InlineData("block", Runbook + " migration step 3")]
    [InlineData("pass", Runbook + " migration step 3")]
    [InlineData("block", Parser + " UnrelatedMethod")]
    [InlineData("pass", Parser + " UnrelatedMethod")]
    public void An_unrelated_citation_cannot_clear_or_reopen_the_previous_claim(string status, string evidence)
    {
        var prompt = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, "agt-4326-round-3", SiblingClaim);
        var candidate = Migration with
        {
            Status = status,
            Classification = CodeQualityReviewPolicy.Rechecked,
            EvidenceChecked = evidence,
        };
        var settled = Settle(candidate, prompt, [Runbook, Parser]);

        Assert.Equal("concerns", settled.Status);
        Assert.Equal(CodeQualityReviewPolicy.Uncorroborated, settled.Classification);
        Assert.Equal(candidate.EvidenceChecked, settled.EvidenceChecked);
        Assert.Contains(SiblingClaim.EvidenceChecked!, settled.Summary);
        Assert.Contains(SiblingClaim.Missing!, settled.Summary);
        Assert.False(Decide(settled).StartsCodingRound);
    }

    [Theory]
    [InlineData("block", ReviewFollowUpAction.ReviewFindingRound)]
    [InlineData("pass", ReviewFollowUpAction.Accept)]
    public void Same_contract_recheck_records_the_citation_and_disposition(
        string status, ReviewFollowUpAction expectedAction)
    {
        var prompt = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, "previous-review", Migration);
        var candidate = Migration with
        {
            Status = status,
            Classification = CodeQualityReviewPolicy.Rechecked,
            Summary = status == "pass"
                ? "Forced verification failure exits before the launcher is enabled."
                : Migration.Summary,
            Missing = status == "pass" ? "none" : Migration.Missing,
        };

        var settled = Settle(candidate, prompt, [Runbook]);

        Assert.Equal(status, settled.Status);
        Assert.Equal(CodeQualityReviewPolicy.Rechecked, settled.Classification);
        Assert.Equal(Migration.EvidenceChecked, settled.EvidenceChecked);
        Assert.Equal(expectedAction, Decide(settled).Action);
    }

    [Fact]
    public void Independently_confirmed_regression_can_block_when_the_previous_claim_remains_unresolved()
    {
        var prompt = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, "previous-review", SiblingClaim);
        var settled = Settle(Migration with { Classification = CodeQualityReviewPolicy.Independent }, prompt, [Runbook]);

        Assert.Equal(ReviewFollowUpAction.ReviewFindingRound, Decide(settled).Action);
        Assert.Equal(CodeQualityReviewPolicy.Independent, settled.Classification);
        Assert.Contains(SiblingClaim.EvidenceChecked!, settled.Summary);
        Assert.Equal(Migration.EvidenceChecked, settled.EvidenceChecked);
    }

    [Fact]
    public void Next_recheck_uses_the_independent_blocker_citation_after_an_older_claim_was_preserved()
    {
        var first = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, "parser-review", SiblingClaim);
        var independent = Settle(Migration with { Classification = CodeQualityReviewPolicy.Independent }, first, [Runbook]);
        var second = CodeQualityReviewPolicy.WithPreviousFinding(Prompt, "migration-review", independent);

        var rechecked = Settle(Migration with { Classification = CodeQualityReviewPolicy.Rechecked }, second, [Runbook]);

        Assert.Equal(CodeQualityReviewPolicy.Rechecked, rechecked.Classification);
        Assert.True(Decide(rechecked).StartsCodingRound);
    }

    [Theory]
    [InlineData(null, "Exact contract", "The launch proceeds after verification fails.", ReviewVerdictCitationPolicy.BlockWithoutCitation)]
    [InlineData(Runbook, "none", "The launch proceeds after verification fails.", ReviewVerdictCitationPolicy.BlockWithoutCitation)]
    [InlineData(Runbook, "Exact contract", "none", CodeQualityReviewPolicy.Uncorroborated)]
    [InlineData("other.md", "Exact contract", "The launch proceeds after verification fails.", ReviewDiffEvidencePolicy.ConcernClassification)]
    public void Evidence_gaps_are_advisory_even_with_a_behavioral_claim(
        string? evidence, string gap, string consequence, string expectedClassification)
    {
        var settled = Settle(Migration with
        {
            EvidenceChecked = evidence,
            Missing = gap,
            Summary = consequence,
        }, Prompt, [Runbook]);

        Assert.Equal(expectedClassification, settled.Classification);
        Assert.False(Decide(settled).StartsCodingRound);
        Assert.Equal(evidence, settled.EvidenceChecked);
    }

    [Theory]
    [InlineData("A retry in src/delivery.cs marks an unpersisted receipt complete.", "The retry must remain pending on failure.")]
    [InlineData("Callers of src/delivery.cs still enter the old path and skip the new central behavior.", "The new implementation has no caller.")]
    [InlineData("Existing receipts in src/delivery.cs cannot load after restart.", "The reader rejects the mandatory legacy receipt format.")]
    public void A_confirmed_consequence_on_the_shipped_path_still_blocks(string summary, string gap)
    {
        var candidate = Migration with { Summary = summary, EvidenceChecked = "src/delivery.cs", Missing = gap };
        Assert.Equal(ReviewFollowUpAction.ReviewFindingRound, Decide(Settle(candidate, Prompt, ["src/delivery.cs"])).Action);
    }

    [Fact]
    public void Current_report_admission_downgrades_an_implicit_block_and_is_stable_on_retry()
    {
        var verdict = Migration with { Classification = "RemoteAspectVerdict" };
        var request = new ReviewReportRequest(
            "executor", "instance", "lease", 1, "report", "ProductFailure", "ReviewFinding",
            verdict.Summary, null!, null!, [], [], [verdict]);
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto("aspect-code-quality", verdict.Aspect, "claude", [],
                ExecutionKind: ReviewCommandKinds.AgentAspect, Prompt: Prompt)],
            [verdict.Aspect]);

        var normalized = ReviewVerdictCitationPolicy.NormalizeReport(request, plan.RequiredAspects, plan);
        Assert.Equal("Pass", normalized.Outcome);
        Assert.Equal(CodeQualityReviewPolicy.Uncorroborated, normalized.FailureClassification);
        Assert.False(Decide(Assert.Single(normalized.Verdicts)).StartsCodingRound);
        var retry = ReviewVerdictCitationPolicy.NormalizeReport(normalized, plan.RequiredAspects, plan);
        Assert.Equal(normalized.Outcome, retry.Outcome);
        Assert.Equal(normalized.FailureClassification, retry.FailureClassification);
        Assert.Equal(Assert.Single(normalized.Verdicts), Assert.Single(retry.Verdicts));

        var legacy = ReviewVerdictCitationPolicy.NormalizeReport(request, plan.RequiredAspects);
        Assert.Equal("ProductFailure", legacy.Outcome);
        Assert.Equal(verdict, Assert.Single(legacy.Verdicts));
    }

    [Fact]
    public void Evidence_downgrade_replay_reduces_two_eligible_concern_rounds_to_zero()
    {
        var uncited = Settle(Migration with { EvidenceChecked = null }, Prompt, [Runbook]);
        var unconfirmed = Settle(SiblingClaim with
        {
            Status = "concerns",
            Classification = CodeQualityReviewPolicy.Uncorroborated,
            Summary = $"The parser in {Parser} fails when passing sibling output follows the assertion.",
        }, Prompt, [Parser]);

        // The previous classifier used the finding text alone for these concerns.
        ReviewVerdictDto[] concerns = [
            uncited with { Missing = $"The shell block in {Runbook} fails to stop before hardening." },
            unconfirmed,
        ];
        Assert.Equal(2, concerns.Count(concern =>
            Decide(concern with { Classification = "RemoteAspectVerdict" }).Action == ReviewFollowUpAction.ReviewConcernRound));
        Assert.Equal(0, concerns.Count(concern => Decide(concern).StartsCodingRound));
    }

    private static ReviewVerdictDto Settle(ReviewVerdictDto verdict, string prompt, string[] paths)
    {
        verdict = CodeQualityReviewPolicy.Normalize(verdict, prompt);
        return ReviewDiffEvidencePolicy.Normalize(verdict, paths);
    }

    private static ReviewFollowUpDecision Decide(params ReviewVerdictDto[] verdicts)
        => ReviewFollowUpPolicy.Decide(verdicts.Select(verdict =>
            new ReviewFollowUpFinding(verdict.Aspect, verdict.Status, verdict.Summary,
                verdict.EvidenceChecked, verdict.Missing, verdict.Classification)), 0, 1);
}
