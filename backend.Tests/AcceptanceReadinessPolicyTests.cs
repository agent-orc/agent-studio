using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3564 - one row per readiness class of a Human Review card. The facts
/// are the admission preview the completion checks produce, plus the park.
/// </summary>
public sealed class AcceptanceReadinessPolicyTests
{
    private const string Brief = "brief-current";

    [Fact]
    public void Admitted_card_is_ready()
    {
        var verdict = Classify(CompletionAdmissionPreview.Admitted(
            "Content review passed for the current brief.", Content("pass"), null));

        Assert.Equal(AcceptanceReadinessClasses.Ready, verdict.Readiness);
        Assert.Equal("Ready to accept", verdict.Headline);
        Assert.False(AcceptanceReadinessClasses.OffersRound(verdict.Readiness));
    }

    [Fact]
    public void Concern_verdict_carries_the_aspect_and_concern_text()
    {
        var content = Content("concerns", summary: "The filter does not reach the lane count.",
            missing: "frontend/src/app/lane.ts");
        var verdict = Classify(Refused(CompletionAdmissionStages.Content,
            CompletionContentPolicy.Decide(content).Message, content));

        Assert.Equal(AcceptanceReadinessClasses.ReviewConcerns, verdict.Readiness);
        Assert.Equal("requirement-fit", verdict.Aspect);
        Assert.Equal("concerns", verdict.ContentStatus);
        Assert.Equal("The filter does not reach the lane count.", verdict.Concern);
        Assert.Equal("frontend/src/app/lane.ts", verdict.Missing);
        Assert.Equal("The requirement-fit review returned concerns: The filter does not reach the lane count.",
            verdict.Reason);
    }

    [Fact]
    public void Verdict_for_an_older_brief_is_stale()
    {
        var content = Content("pass", reviewBrief: "brief-older");
        var verdict = Classify(Refused(CompletionAdmissionStages.Content,
            CompletionContentPolicy.Decide(content).Message, content));

        Assert.Equal(AcceptanceReadinessClasses.VerdictStale, verdict.Readiness);
        Assert.Contains("brief-older", verdict.Reason);
    }

    [Fact]
    public void Review_of_another_run_is_stale()
    {
        var content = Content(null) with
        {
            Settlement = new CompletionSettlementProblem("review-7", CompletionSettlementProblem.ReviewForOtherRun, null),
        };
        var verdict = Classify(Refused(CompletionAdmissionStages.Content,
            CompletionContentPolicy.Decide(content).Message, content));

        Assert.Equal(AcceptanceReadinessClasses.VerdictStale, verdict.Readiness);
        Assert.Contains("review-7", verdict.Reason);
    }

    [Fact]
    public void Research_card_without_content_verdict_says_so()
    {
        var content = Content(null) with { Mode = TaskModes.Research };
        var verdict = AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(
            Refused(CompletionAdmissionStages.Content, CompletionContentPolicy.Decide(content).Message, content),
            TaskModes.Research, null));

        Assert.Equal(AcceptanceReadinessClasses.NoContentVerdict, verdict.Readiness);
        Assert.Equal("This research card has no requirement-fit verdict: No review aspect ran.", verdict.Reason);
    }

    [Fact]
    public void Merged_tree_without_gate_is_integrated_unverified()
    {
        var verification = new TaskIntegrationVerification
        {
            State = IntegrationVerificationStates.Unverified,
            Sha = new string('b', 40),
            Reason = "No gate ran on this tree.",
        };
        var verdict = Classify(Refused(CompletionAdmissionStages.IntegrationUnverified,
            "Acceptance refused: the delivery is on the integration branch but integrated-unverified.",
            Content("pass"), verification));

        Assert.Equal(AcceptanceReadinessClasses.IntegratedUnverified, verdict.Readiness);
        Assert.Equal("The delivery is on the integration branch, but no gate passed on the merged tree bbbbbbbbbbbb. "
            + "No gate ran on this tree.", verdict.Reason);
    }

    [Fact]
    public void Batch_gate_without_evidence_is_integrated_unverified()
    {
        var verdict = Classify(Refused(CompletionAdmissionStages.BatchGate, "batch-gate-evidence-missing"));

        Assert.Equal(AcceptanceReadinessClasses.IntegratedUnverified, verdict.Readiness);
        Assert.Contains("batch-gate-evidence-missing", verdict.Reason);
    }

    [Fact]
    public void Delivery_missing_from_the_branch_is_not_integrated()
    {
        var verdict = Classify(Refused(CompletionAdmissionStages.IntegrationMissing,
            "Acceptance does not integrate deliveries. The task remains in Human Review because its current delivery is 'pending' on 'develop'."));

        Assert.Equal(AcceptanceReadinessClasses.NotIntegrated, verdict.Readiness);
        Assert.StartsWith("Acceptance does not integrate deliveries.", verdict.Reason);
    }

    [Theory]
    [InlineData("Live measurement needs the next deploy before acceptance.")]
    [InlineData("Wait until this is deployed to stage.")]
    public void Park_waiting_for_a_deploy_outranks_admission(string reason)
    {
        var verdict = AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(
            CompletionAdmissionPreview.Admitted("Content review passed for the current brief.", Content("pass"), null),
            TaskModes.Coding, Park("operator-decision", reason)));

        Assert.Equal(AcceptanceReadinessClasses.WaitingDeploy, verdict.Readiness);
        Assert.Contains(reason.TrimEnd('.'), verdict.Reason);
        Assert.False(AcceptanceReadinessClasses.OffersRound(verdict.Readiness));
    }

    [Fact]
    public void Park_for_a_person_is_an_owner_decision()
    {
        var verdict = AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(
            Refused(CompletionAdmissionStages.Content, "Completion refused: x.", Content(null)),
            TaskModes.Coding, Park("agent-needs-input", "Pick the primary column.")));

        Assert.Equal(AcceptanceReadinessClasses.OwnerDecision, verdict.Readiness);
        Assert.Equal("Parked for an owner decision: Pick the primary column.", verdict.Reason);
    }

    [Fact]
    public void Failure_park_keeps_the_admission_class()
    {
        var verdict = AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(
            CompletionAdmissionPreview.Admitted("Content review passed for the current brief.", Content("pass"), null),
            TaskModes.Coding, Park("watchdog-kill", "The run was killed by the watchdog.")));

        Assert.Equal(AcceptanceReadinessClasses.Ready, verdict.Readiness);
    }

    [Fact]
    public void Pending_decision_card_is_an_owner_decision()
    {
        var verdict = Classify(Refused(CompletionAdmissionStages.Decision, "A pending decision cannot be completed or archived."));

        Assert.Equal(AcceptanceReadinessClasses.OwnerDecision, verdict.Readiness);
    }

    [Theory]
    [InlineData(CompletionAdmissionStages.Lineage)]
    [InlineData(CompletionAdmissionStages.Contract)]
    [InlineData(CompletionAdmissionStages.Binding)]
    public void Other_refusals_are_blocked_with_the_completion_wording(string stage)
    {
        var verdict = Classify(Refused(stage, "Completion has no delivery claim."));

        Assert.Equal(AcceptanceReadinessClasses.Blocked, verdict.Readiness);
        Assert.Equal("Completion has no delivery claim.", verdict.Reason);
        Assert.True(AcceptanceReadinessClasses.OffersRound(verdict.Readiness));
    }

    [Fact]
    public void Round_prompt_carries_the_refusal_concern_and_result_ref()
    {
        var verdict = new AcceptanceReadinessVerdict(AcceptanceReadinessClasses.ReviewConcerns, "Review concerns",
            "reason", "requirement-fit", "concerns", "The reason is only in a settlement file.", "lane.ts");

        var prompt = AcceptanceReadinessPolicy.RoundPrompt(verdict,
            "Completion refused: requirement-fit verdict was concerns.", "refs/heads/runner/r1/AGT-1");

        Assert.Contains("Completion refused: requirement-fit verdict was concerns.", prompt);
        Assert.Contains("requirement-fit concern: The reason is only in a settlement file.", prompt);
        Assert.Contains("Missing: lane.ts", prompt);
        Assert.Contains("Start from the latest result refs/heads/runner/r1/AGT-1", prompt);
    }

    [Fact]
    public void Every_class_has_a_display_position()
    {
        Assert.Equal(9, AcceptanceReadinessClasses.All.Distinct().Count());
    }

    private static AcceptanceReadinessVerdict Classify(CompletionAdmissionPreview admission)
        => AcceptanceReadinessPolicy.Classify(new AcceptanceReadinessFacts(admission, TaskModes.Coding, null));

    private static CompletionAdmissionPreview Refused(string stage, string message,
        CompletionContentFacts? content = null, TaskIntegrationVerification? verification = null)
        => CompletionAdmissionPreview.Refused(stage, message, content, verification);

    private static CompletionContentFacts Content(string? status, string? summary = null, string? missing = null,
        string reviewBrief = Brief)
        => new(TaskModes.Coding, Brief, Brief, status is null ? null : reviewBrief, "requirement-fit", status,
            status is null ? [] : ["requirement-fit"], [], ContentSummary: summary, Missing: missing);

    private static ParkedBlockerStatus Park(string blockerType, string reason)
        => new(blockerType, ParkedBlockerConditionKinds.Manual, "A person clears this park.",
            DateTime.UnixEpoch, 60, reason, ParkedBlockerStatuses.Undeterminable, null, "");
}
