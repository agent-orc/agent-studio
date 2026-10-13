using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestration.Decisions;

public enum TransitionAction
{
    CreateReview, FindingRound, ConcernRound, AcceptAndIntegrate, DeferToBatch,
    HumanReview, Escalate, ReviewInfrastructureRetry, DegradeWithFollowUp,
    Continue, ReturnToReady, SweepFixRound, SweepGateRound, SalvageContinuation,
    Hold, Ignore,
}

public enum DecisionIntent { Continue, Retry, Halt, Ask, Prepare, Defer, Adapt }
public enum AuthorityFloor { Automatic, FencedAttempt, Operator }

public sealed record ContinuationBaseFact(string Ref, string Sha, string ReviewAttemptId);
public sealed record CardFacts(string Id, string Lane);
public sealed record AttemptFacts(string Id, bool HasSettledResultEnvelope, ContinuationBaseFact? ReviewedDelivery = null);
public sealed record ProjectDecisionSettings(int MaximumConcernRounds = 1, int MaximumDeliveredRounds = 4,
    int ConsecutiveBlockRounds = 2, int MaximumAutomaticReissues = 2);
public sealed record ReviewFacts(string Outcome, IReadOnlyList<ReviewVerdictDto> Verdicts,
    ReviewPlanDto? Plan = null, string? FailureClassification = null, string? Summary = null);
public sealed record RoundFacts(CardRoundBudgetState CardBudget, ReviewRoundBudgetLedger Delivered,
    int ConcernRoundsUsed = 0, int AutomaticReissuesUsed = 0);
public sealed record InfrastructureFacts(bool AspectRetryExhausted = false, bool HasIntervention = false,
    bool ProbeFailed = false, bool HasRetryBudget = false);
public sealed record TransitionFacts(CardFacts Card, AttemptFacts Attempt, ReviewFacts Review,
    RoundFacts Rounds, ProjectDecisionSettings Settings, InfrastructureFacts Infrastructure);
public sealed record DecisionReceipt(string CardId, string AttemptId, string Rule,
    int RoundsUsed, int RoundsAllowed, IReadOnlyList<string> Aspects, bool RequiresLinkedFollowUp);
public sealed record ActionCandidate(TransitionAction Action, DecisionIntent Intent, AuthorityFloor AuthorityFloor);
public sealed record TransitionDecision(TransitionAction Action, string Reason, DecisionReceipt Receipt,
    IReadOnlyList<ReviewFollowUpFinding> Findings, ContinuationBaseFact? ContinuationBase,
    IReadOnlyList<ActionCandidate> Candidates);

/// <summary>
/// Deterministic authority for transition candidates. Callers gather facts and apply the selected
/// action under their own fencing transaction. No clock, filesystem, process, or transport is consulted.
/// </summary>
public static class TransitionDecisionPolicy
{
    public static TransitionDecision Review(TransitionFacts facts)
    {
        var findings = facts.Review.Verdicts.Select(v => new ReviewFollowUpFinding(
            v.Aspect, v.Status, v.Summary, v.EvidenceChecked, v.Missing, v.Classification)).ToArray();
        var followUp = ReviewFollowUpPolicy.Decide(findings, facts.Rounds.ConcernRoundsUsed,
            facts.Settings.MaximumConcernRounds);
        TransitionDecision Select(TransitionAction action, string reason, string rule,
            IReadOnlyList<ReviewFollowUpFinding>? selected = null)
            => new(action, reason, new(facts.Card.Id, facts.Attempt.Id, rule,
                    facts.Rounds.CardBudget.Used, facts.Rounds.CardBudget.Allowed,
                    (selected ?? findings).Select(f => f.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    action == TransitionAction.DegradeWithFollowUp),
                selected ?? findings,
                action is TransitionAction.FindingRound or TransitionAction.ConcernRound
                    ? facts.Attempt.ReviewedDelivery : null,
                [Candidate(action)]);

        if (IsInfrastructure(facts.Review.Outcome) || followUp.Action == ReviewFollowUpAction.RetryAspect)
            return Select(CanRetryInfrastructure(facts.Review.Outcome,
                    facts.Infrastructure.AspectRetryExhausted || followUp.Action == ReviewFollowUpAction.RetryAspect,
                    facts.Infrastructure.HasIntervention, facts.Infrastructure.ProbeFailed,
                    facts.Review.FailureClassification, facts.Infrastructure.HasRetryBudget)
                    ? TransitionAction.ReviewInfrastructureRetry : TransitionAction.Escalate,
                "Review infrastructure must resolve before product findings are acted on.", "review-infrastructure");
        if (facts.Infrastructure.ProbeFailed)
            return Select(TransitionAction.Hold, "The failed cause probe retains its intervention.", "cause-probe");
        if (DeferToBatch(facts.Review.Plan?.BuildTestDeferredToBatch == true, facts.Review.Outcome))
            return Select(TransitionAction.DeferToBatch, "The frozen plan defers the full build/test gate to its batch.", "batch-gate");
        if (!IsDeliveredOutcome(facts.Review.Outcome))
            return Select(TransitionAction.HumanReview, "The review did not deliver an accepting verdict.", "review-outcome");

        var blocks = findings.Where(f => ReviewGradingPolicy.IsBlockingToken(f.Status)).Select(f => f.Aspect).ToArray();
        var card = CardRoundBudgetPolicy.DecideForReview(facts.Rounds.CardBudget, blocks,
            followUp.Action == ReviewFollowUpAction.ReviewConcernRound);
        if (card == CardRoundBudgetAction.Escalate)
            return Select(TransitionAction.Escalate, "The card round budget is spent with open test findings.", "card-round-budget");
        var lifetime = ReviewRoundBudgetPolicy.Decide(facts.Rounds.Delivered, facts.Attempt.Id, blocks,
            facts.Settings.MaximumDeliveredRounds, facts.Settings.ConsecutiveBlockRounds,
            facts.Rounds.AutomaticReissuesUsed, facts.Settings.MaximumAutomaticReissues);
        if (card == CardRoundBudgetAction.DegradeToFollowUp || lifetime.Degrade)
            return Select(TransitionAction.DegradeWithFollowUp, "Open findings move to one linked follow-up before delivery proceeds.", "review-round-budget");
        if (followUp.Action == ReviewFollowUpAction.ReviewFindingRound)
            return Select(TransitionAction.FindingRound, followUp.Reason, "review-findings", followUp.Findings);
        if (followUp.Action == ReviewFollowUpAction.ReviewConcernRound)
            return Select(TransitionAction.ConcernRound, followUp.Reason, "review-concerns", followUp.Findings);
        var integration = RemoteDeliveryIntegrationPolicy.Decide(facts.Attempt.HasSettledResultEnvelope,
            facts.Review.Outcome, facts.Review.Plan, facts.Review.Verdicts);
        return Select(integration.ShouldIntegrate ? TransitionAction.AcceptAndIntegrate : TransitionAction.HumanReview,
            integration.Reason, "delivery-gate");
    }

    public static ActionCandidate Candidate(TransitionAction action) => new(action, action switch
    {
        TransitionAction.CreateReview => DecisionIntent.Prepare,
        TransitionAction.FindingRound or TransitionAction.ConcernRound or TransitionAction.DegradeWithFollowUp
            or TransitionAction.SweepFixRound or TransitionAction.SweepGateRound => DecisionIntent.Adapt,
        TransitionAction.ReviewInfrastructureRetry or TransitionAction.SalvageContinuation => DecisionIntent.Retry,
        TransitionAction.DeferToBatch or TransitionAction.Hold => DecisionIntent.Defer,
        TransitionAction.Escalate or TransitionAction.HumanReview => DecisionIntent.Ask,
        TransitionAction.Ignore => DecisionIntent.Halt,
        _ => DecisionIntent.Continue,
    }, action is TransitionAction.Escalate or TransitionAction.HumanReview
        ? AuthorityFloor.Operator : action is TransitionAction.Hold or TransitionAction.Ignore
            ? AuthorityFloor.Automatic : AuthorityFloor.FencedAttempt);

    public static bool IsDeliveredOutcome(string? outcome)
        => outcome?.ToLowerInvariant() is "pass" or "productfailure" or "integrationbranchdefect";

    public static bool DeferToBatch(bool deferred, string? outcome)
        => deferred && outcome?.ToLowerInvariant() is "pass" or "integrationbranchdefect";

    public static bool IsInfrastructure(string? outcome)
        => string.Equals(outcome, "ReviewInfra", StringComparison.OrdinalIgnoreCase)
           || string.Equals(outcome, "InfrastructureFailure", StringComparison.OrdinalIgnoreCase);

    public static bool CanRetryInfrastructure(string? outcome, bool aspectRetryExhausted,
        bool hasIntervention, bool probeFailed, string? classification, bool hasRetryBudget)
        => IsInfrastructure(outcome) && !aspectRetryExhausted && !hasIntervention && !probeFailed
           && ReviewReportDiagnosisPolicy.RetriesOnUnchangedSubject(classification) && hasRetryBudget;

    public static string CodingLane(string outcome, bool providerRejected)
        => providerRejected ? "5e-escalated" : outcome switch
        {
            "done" or "noop" => "4-auto-review",
            "blocked" or "needsinput" or "unknown" => "5e-escalated",
            "environmentfailure" or "stopped" or "mechanicalfallback" => "2-ready",
            _ => string.Empty,
        };

    public static bool CreatesReview(bool epicPlanning, string outcome, bool unverifiedDelivery, bool reportOnly)
        => !epicPlanning && outcome is "done" or "noop" && !unverifiedDelivery && !reportOnly;

    public static bool OpensFindingRound(string outcome, ReviewFollowUpAction followUp)
        => IsDeliveredOutcome(outcome) && followUp == ReviewFollowUpAction.ReviewFindingRound;
}
