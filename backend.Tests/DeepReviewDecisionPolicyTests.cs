using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the deep-review policy reads no clock and these facts carry no dates.

/// <summary>
/// AGT-3268: the engine decides per delivery whether a deep review runs, at
/// which model and reasoning level, from Token Economy risk, touched areas,
/// size, producer, history and quota. Repeated deep-review blocks escalate.
/// </summary>
public sealed class DeepReviewDecisionPolicyTests
{
    private static readonly string Sha = new('a', 40);

    private static DeepReviewFacts Facts(
        string mode = DeepReviewModes.Active,
        string risk = DeepReviewRiskClasses.Standard,
        IReadOnlyList<string>? paths = null,
        int? lines = 40,
        string? producerModel = "gpt-6-sol",
        string? producerLevel = "medium",
        IReadOnlyList<DeepReviewRoundRecord>? prior = null,
        int delivered = 0,
        int blocking = 0,
        int redGates = 0,
        string quota = DeepReviewQuotaStates.Comfortable,
        IReadOnlyList<DecisionRuleV1>? rules = null,
        string? resultSha = null)
        => new(mode, "AGT-1", resultSha ?? Sha, risk, risk == DeepReviewRiskClasses.Critical ? "hard floor sol-xhigh" : null,
            paths ?? ["frontend/src/app/list.component.ts"], lines, producerModel, producerLevel,
            prior ?? [], delivered, blocking, redGates, quota, null, "gpt-6-sol", rules);

    [Fact]
    public void Small_standard_delivery_is_skipped_even_when_quota_is_comfortable()
    {
        var decision = DeepReviewDecisionPolicy.Decide(Facts());

        Assert.Equal(DeepReviewActions.Skip, decision.Action);
        Assert.False(decision.Dispatch);
        Assert.StartsWith("no-deep-review-ground", decision.Reason);
        Assert.Contains("quota headroom alone is never a ground", decision.Reason);
        Assert.Contains(decision.Grounds, ground => ground.Id == "quota.state" && ground.Weight == "info");
    }

    [Fact]
    public void Hard_floor_card_runs_at_xhigh_and_dispatches_in_active_mode()
    {
        var decision = DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Critical));

        Assert.Equal(DeepReviewActions.Run, decision.Action);
        Assert.True(decision.Dispatch);
        Assert.Equal("gpt-6-sol", decision.Model);
        Assert.Equal("xhigh", decision.ThinkingLevel);
        Assert.StartsWith("hard-floor: risk.class", decision.Reason);
    }

    [Theory]
    [InlineData("backend/Features/Auth/TokenScopeValidator.cs", "security")]
    [InlineData("backend/Features/Runner/RunLeaseService.cs", "fencing")]
    [InlineData("backend/Migrations/0007_drop_column.sql", "migration")]
    [InlineData("backend/Features/Runner/RemoteReviewSettlementJournal.cs", "concurrency")]
    public void Touched_risk_area_raises_a_standard_card_to_xhigh(string path, string area)
    {
        var decision = DeepReviewDecisionPolicy.Decide(Facts(paths: [path]));

        Assert.Equal(DeepReviewActions.Run, decision.Action);
        Assert.Equal("xhigh", decision.ThinkingLevel);
        Assert.Equal([area], decision.TouchedAreas);
        Assert.Contains(decision.Grounds, ground => ground.Id == "diff.area." + area && ground.Weight == "critical");
    }

    [Fact]
    public void Elevated_grounds_run_at_the_medium_default()
    {
        Assert.Equal("medium", DeepReviewDecisionPolicy.Decide(Facts(lines: 600)).ThinkingLevel);
        Assert.Equal("medium", DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Elevated)).ThinkingLevel);
        Assert.Equal("medium", DeepReviewDecisionPolicy.Decide(Facts(delivered: 2, blocking: 2)).ThinkingLevel);
        Assert.Equal("medium", DeepReviewDecisionPolicy.Decide(Facts(delivered: 1, redGates: 1)).ThinkingLevel);
        var priorBlock = DeepReviewDecisionPolicy.Decide(Facts(
            prior: [new("rev-1", new string('b', 40), "block", ["f1"])]));
        Assert.Equal(DeepReviewActions.Run, priorBlock.Action);
        Assert.Equal("medium", priorBlock.ThinkingLevel);
        Assert.StartsWith("elevated: deep-review.rounds", priorBlock.Reason);
    }

    [Fact]
    public void Producer_below_the_default_route_counts_only_with_a_substantial_diff()
    {
        Assert.Equal(DeepReviewActions.Run,
            DeepReviewDecisionPolicy.Decide(Facts(producerModel: "gpt-6-luna", lines: 200)).Action);
        Assert.Equal(DeepReviewActions.Run,
            DeepReviewDecisionPolicy.Decide(Facts(producerLevel: "low", lines: 200)).Action);
        Assert.Equal(DeepReviewActions.Skip,
            DeepReviewDecisionPolicy.Decide(Facts(producerModel: "gpt-6-luna", lines: 100)).Action);
        Assert.Equal(DeepReviewActions.Skip,
            DeepReviewDecisionPolicy.Decide(Facts(producerModel: "gpt-6-sol", producerLevel: "xhigh", lines: 400)).Action);
    }

    [Fact]
    public void Quota_never_lowers_a_hard_floor_and_only_trims_elevated_work()
    {
        var cappedFloor = DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Critical,
            quota: DeepReviewQuotaStates.Capped));
        Assert.Equal(DeepReviewActions.Ask, cappedFloor.Action);
        Assert.False(cappedFloor.Dispatch);
        Assert.StartsWith("quota-capped-at-hard-floor", cappedFloor.Reason);

        var cappedElevated = DeepReviewDecisionPolicy.Decide(Facts(lines: 600, quota: DeepReviewQuotaStates.Capped));
        Assert.Equal(DeepReviewActions.Skip, cappedElevated.Action);
        Assert.StartsWith("quota-capped:", cappedElevated.Reason);

        Assert.Equal(DeepReviewActions.Skip,
            DeepReviewDecisionPolicy.Decide(Facts(lines: 600, quota: DeepReviewQuotaStates.NearCap)).Action);
        Assert.Equal(DeepReviewActions.Run, DeepReviewDecisionPolicy.Decide(Facts(lines: 600, redGates: 1,
            delivered: 1, quota: DeepReviewQuotaStates.NearCap)).Action);
        var nearFloor = DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Critical,
            quota: DeepReviewQuotaStates.NearCap));
        Assert.Equal(DeepReviewActions.Run, nearFloor.Action);
        Assert.Equal("xhigh", nearFloor.ThinkingLevel);
    }

    [Fact]
    public void One_deep_review_per_delivery_and_the_card_round_budget_bound_it()
    {
        var sameDelivery = DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Critical,
            prior: [new("rev-1", Sha, "pass", [])]));
        Assert.Equal(DeepReviewActions.Skip, sameDelivery.Action);
        Assert.StartsWith("delivery-already-deep-reviewed", sameDelivery.Reason);

        IReadOnlyList<DeepReviewRoundRecord> three =
        [
            new("rev-1", new string('b', 40), "block", ["f1"]),
            new("rev-2", new string('c', 40), "block", ["f2"]),
            new("rev-3", new string('d', 40), "block", ["f3"]),
        ];
        var spent = DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Critical, prior: three));
        Assert.Equal(DeepReviewActions.Skip, spent.Action);
        Assert.Equal("deep-review-round-budget-spent: 3 of 3 deep reviews used on this card.", spent.Reason);
    }

    [Fact]
    public void Shadow_mode_records_the_run_decision_without_dispatching_it()
    {
        var shadow = DeepReviewDecisionPolicy.Decide(Facts(mode: DeepReviewModes.Shadow,
            risk: DeepReviewRiskClasses.Critical));

        Assert.Equal(DeepReviewActions.Run, shadow.Action);
        Assert.Equal(DeepReviewModes.Shadow, shadow.Mode);
        Assert.False(shadow.Dispatch);
        Assert.Equal("xhigh", shadow.ThinkingLevel);

        var off = DeepReviewDecisionPolicy.Decide(Facts(mode: DeepReviewModes.Off, risk: DeepReviewRiskClasses.Critical));
        Assert.Equal(DeepReviewActions.Skip, off.Action);
        Assert.Equal(DeepReviewModes.Shadow, DeepReviewModes.Normalize(null));
        Assert.Equal(DeepReviewModes.Shadow, DeepReviewModes.Normalize("unknown"));
        Assert.Equal(DeepReviewModes.Active, DeepReviewModes.Normalize(" Active "));
    }

    [Fact]
    public void A_deep_review_rule_adds_a_ground_and_may_pin_the_model_but_never_lowers_the_floor()
    {
        var rule = new DecisionRuleV1("deep-review.lines", DecisionRuleTriggers.DeepReview, "diff.changed-lines",
            "120", [DecisionActionKinds.Prepare], "A0", "gpt-6.1-sol", "signal");

        var fired = DeepReviewDecisionPolicy.Decide(Facts(lines: 150, rules: [rule]));
        Assert.Equal(DeepReviewActions.Run, fired.Action);
        Assert.Equal("gpt-6.1-sol", fired.Model);
        Assert.Equal("medium", fired.ThinkingLevel);
        Assert.Contains(fired.Grounds, ground => ground.Id == "rule.deep-review.lines" && ground.Weight == "elevated");

        Assert.Equal(DeepReviewActions.Skip, DeepReviewDecisionPolicy.Decide(Facts(lines: 80, rules: [rule])).Action);
        var floor = DeepReviewDecisionPolicy.Decide(Facts(risk: DeepReviewRiskClasses.Critical, lines: 80, rules: [rule]));
        Assert.Equal("xhigh", floor.ThinkingLevel);
        Assert.Equal("gpt-6-sol", floor.Model);
    }

    [Fact]
    public void Rules_editor_accepts_only_ground_raising_deep_review_rules()
    {
        var catalogue = new DecisionRuleModelCatalogue("codex", [new("gpt-6-sol", "codex", true)]);
        DecisionRuleV1 Rule(string fact = "diff.changed-lines", string? threshold = "200",
            string action = DecisionActionKinds.Prepare, string behavior = "signal", string autonomy = "A0")
            => new("deep-review.size", DecisionRuleTriggers.DeepReview, fact, threshold, [action], autonomy,
                null, behavior);

        Assert.True(DecisionRulePolicy.Validate([Rule()], projectOverlay: true, catalogue).Accepted);
        Assert.True(DecisionRulePolicy.Validate([Rule()], projectOverlay: false, catalogue).Accepted);
        Assert.Contains(DecisionRulePolicy.Validate([Rule(action: DecisionActionKinds.Retry)], false, catalogue).Refusals,
            refusal => refusal.Contains("may only offer prepare", StringComparison.Ordinal));
        Assert.Contains(DecisionRulePolicy.Validate([Rule(behavior: "hold")], false, catalogue).Refusals,
            refusal => refusal.Contains("must use signal", StringComparison.Ordinal));
        Assert.Contains(DecisionRulePolicy.Validate([Rule(threshold: null)], false, catalogue).Refusals,
            refusal => refusal.Contains("numeric warning threshold", StringComparison.Ordinal));
        Assert.Contains(DecisionRulePolicy.Validate([Rule(fact: "run.outcome")], false, catalogue).Refusals,
            refusal => refusal.Contains("not a deep-review fact", StringComparison.Ordinal));
        Assert.Contains(DecisionRulePolicy.Validate([Rule(autonomy: "A2")], false, catalogue).Refusals,
            refusal => refusal.Contains("A0/A1 floor", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_cited_location_is_the_same_finding_whatever_the_wording()
    {
        var first = Block("Unchecked scope lets a viewer write.", "backend/Features/Auth/Scope.cs:42 allows write");
        var reworded = Block("Viewer can still escalate to write.", "backend/Features/Auth/Scope.cs:57 still writes");
        var other = Block("Lease is not fenced.", "backend/Features/Runner/RunLeaseService.cs:10");

        var fingerprint = Assert.Single(DeepReviewDecisionPolicy.BlockFingerprints([first])).Fingerprint;
        Assert.Equal(fingerprint, Assert.Single(DeepReviewDecisionPolicy.BlockFingerprints([reworded])).Fingerprint);
        Assert.NotEqual(fingerprint, Assert.Single(DeepReviewDecisionPolicy.BlockFingerprints([other])).Fingerprint);
        Assert.Empty(DeepReviewDecisionPolicy.BlockFingerprints([first with { Aspect = "code-quality" }]));
        Assert.Empty(DeepReviewDecisionPolicy.BlockFingerprints([first with { Status = "concerns" }]));
        Assert.Empty(DeepReviewDecisionPolicy.BlockFingerprints(
            [first with { Classification = ReviewVerdictCitationPolicy.BlockWithoutCitation }]));
    }

    [Fact]
    public void Repeated_block_after_its_fix_round_is_reported_for_escalation()
    {
        var finding = Block("Unchecked scope lets a viewer write.", "backend/Features/Auth/Scope.cs:42");
        var fingerprint = DeepReviewDecisionPolicy.BlockFingerprints([finding])[0].Fingerprint;
        IReadOnlyList<DeepReviewRoundRecord> prior = [new("rev-1", new string('b', 40), "block", [fingerprint])];

        var repeated = DeepReviewDecisionPolicy.RepeatedBlock([finding], prior, "rev-2");
        Assert.NotNull(repeated);
        Assert.Equal(2, repeated!.ConsecutiveRounds);
        Assert.Equal(fingerprint, repeated.Fingerprint);

        Assert.Null(DeepReviewDecisionPolicy.RepeatedBlock([finding], [], "rev-2"));
        Assert.Null(DeepReviewDecisionPolicy.RepeatedBlock([finding], prior, "rev-1"));
        Assert.Null(DeepReviewDecisionPolicy.RepeatedBlock([finding],
            [new("rev-1", new string('b', 40), "block", ["other"])], "rev-2"));
        Assert.Null(DeepReviewDecisionPolicy.RepeatedBlock([finding],
            [new("rev-0", new string('c', 40), "block", [fingerprint]), new("rev-1", new string('b', 40), "pass", [])],
            "rev-2"));
    }

    [Fact]
    public void Repeated_deep_review_block_escalates_instead_of_another_fix_round()
    {
        var finding = Block("Unchecked scope lets a viewer write.", "backend/Features/Auth/Scope.cs:42");
        var repeated = new DeepReviewRepeatedBlock("fp", "deep-review: Unchecked scope", 2);
        ReviewBudgetFacts Budget(string outcome, DeepReviewRepeatedBlock? repeat) => new(outcome, [finding], 0, 1,
            new CardRoundBudgetState(1, 4), ReviewRoundBudgetLedger.Empty, "rev-2", 4, 2, 0, 2,
            RepeatedDeepReviewBlock: repeat);

        var escalated = ReviewBudgetPolicy.Decide(Budget(ReviewOutcomeKinds.ProductFailure, repeated));
        Assert.True(escalated.Escalate);
        Assert.Same(repeated, escalated.RepeatedDeepReviewBlock);
        Assert.False(ReviewBudgetPolicy.Decide(Budget(ReviewOutcomeKinds.ProductFailure, null)).Escalate);
        Assert.False(ReviewBudgetPolicy.Decide(Budget(ReviewOutcomeKinds.Pass, repeated)).Escalate);

        var transition = TransitionDecisionPolicy.Decide(new TransitionFacts(
            new CardFacts("AGT-1", TaskStates.AutoReview), new AttemptFacts("rev-2", true),
            new ReviewFacts(ReviewOutcomeKinds.ProductFailure, [finding]), new DecisionSettings(),
            new TimelineCounters(1), new GateBatchFacts(), RepeatedDeepReviewBlock: repeated));
        Assert.Equal(TransitionAction.Escalate, transition.Action);
        Assert.Contains("deep-review finding blocked 2 consecutive rounds", transition.Reason);
    }

    [Fact]
    public void Deep_review_block_is_never_degraded_to_a_concern_when_the_budget_is_spent()
    {
        var round = ReviewRoundBudgetPolicy.Decide(
            new ReviewRoundBudgetLedger([new("rev-1", ["code-quality"], []), new("rev-2", ["code-quality"], []),
                new("rev-3", ["code-quality"], [])]),
            "rev-4", [DeepReviewDecisionPolicy.AspectId, "code-quality"], 4, 2);

        Assert.False(round.Degrade);
        Assert.Equal(DeepReviewDecisionPolicy.AspectId, round.SpentBy);
        Assert.Equal(CardRoundBudgetAction.Escalate,
            CardRoundBudgetPolicy.Decide(new CardRoundBudgetState(4, 4), [DeepReviewDecisionPolicy.AspectId]));
        Assert.Equal(CardRoundBudgetAction.DegradeToFollowUp,
            CardRoundBudgetPolicy.Decide(new CardRoundBudgetState(4, 4), ["code-quality"]));
    }

    private static ReviewFollowUpFinding Block(string summary, string finding)
        => new(DeepReviewDecisionPolicy.AspectId, "block", summary, "backend/Features/Auth", finding);
}
