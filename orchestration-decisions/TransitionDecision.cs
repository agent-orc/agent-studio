using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public enum TransitionAction
{
    CreateReview, FindingRound, ConcernRound, AcceptAndIntegrate, DeferToBatch,
    HumanReview, Escalate, ReviewInfrastructureRetry, DegradeWithFollowUp,
    SweepIgnore, SweepHold, SweepRound, SweepWaitForPerson, SalvageContinuation,
    ReturnToReady, Continue, Halt,
}

public enum DecisionUnion { Continue, Retry, Halt, Ask, Prepare, Defer, Adapt }
public enum DecisionAuthority { Automatic, Operator, VerifiedDelivery, LinkedFollowUp }

public sealed record CardFacts(string Key, string State);
public sealed record ContinuationBase(string ResultRef, string ResultSha, string AttemptId);
public sealed record AttemptFacts(string Id, bool HasSettledEnvelope, ContinuationBase? ReviewedDelivery = null);
public sealed record ReviewFacts(string Outcome, IReadOnlyList<ReviewFollowUpFinding> Findings,
    ReviewPlanDto? Plan = null, IReadOnlyList<ReviewVerdictDto>? Verdicts = null,
    string? FailureClassification = null);
public sealed record DecisionSettings(int MaximumRounds = 4, int MaximumConcernRounds = 0,
    int MaximumDeliveredRounds = 4, int ConsecutiveBlockRounds = 2, int MaximumReissues = 2);
public sealed record TimelineCounters(int AutomaticRounds = 0, int ConcernRounds = 0, int Reissues = 0);
public sealed record GateBatchFacts(bool DeferredToBatch = false, bool InfrastructureRetryAvailable = false,
    bool AspectRetryExhausted = false, bool InterventionActive = false, bool ProbeFailed = false);
public sealed record TransitionFacts(CardFacts Card, AttemptFacts Attempt, ReviewFacts Review,
    DecisionSettings Settings, TimelineCounters Counters, GateBatchFacts Gate,
    ReviewRoundBudgetLedger? DeliveredRounds = null, bool AcceptedReplay = false);
public sealed record DecisionReceipt(string Rule, string CardKey, string AttemptId,
    int RoundsUsed, int RoundsAllowed, IReadOnlyList<string> Aspects);
public sealed record CandidateAction(TransitionAction Action, DecisionUnion Union, DecisionAuthority AuthorityFloor);
public sealed record TransitionDecision(TransitionAction Action, string Reason, DecisionReceipt Receipt,
    IReadOnlyList<CandidateAction> Candidates, IReadOnlyList<ReviewFollowUpFinding> Findings,
    ContinuationBase? ContinuationBase = null, bool RequiresLinkedFollowUp = false,
    string? LinkedFollowUpLane = null);

/// <summary>Deterministic fallback and candidate constraints for AGT-W73. No boundary access.</summary>
public static class TransitionDecisionPolicy
{
    public const string LinkedFollowUpLane = TaskStates.Ready;

    public static TransitionDecision Decide(TransitionFacts facts)
    {
        // An uncited block is not an actionable finding. The former Council loop
        // skipped it before deciding whether to reissue the reviewed delivery.
        var findings = facts.Review.Findings.Where(item =>
            !string.Equals(item.Classification, ReviewVerdictCitationPolicy.BlockWithoutCitation,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var verdicts = (facts.Review.Verdicts ?? []).Where(item =>
            !string.Equals(item.Classification, ReviewVerdictCitationPolicy.BlockWithoutCitation,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var review = ReviewBudgetPolicy.Decide(new ReviewBudgetFacts(
            facts.Review.Outcome, findings, facts.Counters.ConcernRounds,
            facts.Settings.MaximumConcernRounds,
            new(facts.Counters.AutomaticRounds, facts.Settings.MaximumRounds),
            facts.DeliveredRounds ?? ReviewRoundBudgetLedger.Empty, facts.Attempt.Id,
            facts.Settings.MaximumDeliveredRounds, facts.Settings.ConsecutiveBlockRounds,
            facts.Counters.Reissues, facts.Settings.MaximumReissues, facts.AcceptedReplay));
        TransitionAction action;
        string reason;
        if (facts.AcceptedReplay || facts.Card.State != TaskStates.AutoReview)
        {
            action = TransitionAction.Halt;
            reason = "The review is already applied or the card has left Auto Review.";
        }
        else if (string.Equals(facts.Review.Outcome, ReviewOutcomeKinds.ReviewInfra, StringComparison.OrdinalIgnoreCase))
        {
            action = RetryInfrastructure(facts.Gate.InfrastructureRetryAvailable,
                facts.Gate.AspectRetryExhausted, facts.Gate.InterventionActive, facts.Gate.ProbeFailed,
                ReviewReportDiagnosisPolicy.RetriesOnUnchangedSubject(facts.Review.FailureClassification))
                ? TransitionAction.ReviewInfrastructureRetry : TransitionAction.Escalate;
            reason = "Review infrastructure requires a bounded retry or operator intervention.";
        }
        else if (IsDeferredAcceptance(facts.Gate.DeferredToBatch, facts.Review.Outcome))
        {
            action = TransitionAction.DeferToBatch;
            reason = "The batch gate owns full verification and integration.";
        }
        else if (review.Escalate)
        {
            action = TransitionAction.Escalate;
            reason = "The card round budget is spent with an open test block.";
        }
        else if (review.ShouldDegrade)
        {
            action = TransitionAction.DegradeWithFollowUp;
            reason = "Move the budget-spent findings to a linked follow-up before acceptance.";
        }
        else if (OpensFindingRound(facts.Review.Outcome, review.FollowUp.Action))
        {
            action = TransitionAction.FindingRound;
            reason = review.FollowUp.Reason;
        }
        else if (review.FollowUp.Action == ReviewFollowUpAction.ReviewConcernRound
                 && string.Equals(facts.Review.Outcome, ReviewOutcomeKinds.Pass, StringComparison.OrdinalIgnoreCase))
        {
            action = TransitionAction.ConcernRound;
            reason = review.FollowUp.Reason;
        }
        else
        {
            var integration = RemoteDeliveryIntegrationPolicy.Decide(facts.Attempt.HasSettledEnvelope,
                facts.Review.Outcome, facts.Review.Plan, verdicts);
            action = integration.ShouldIntegrate ? TransitionAction.AcceptAndIntegrate : TransitionAction.HumanReview;
            reason = integration.Reason;
        }
        return Create(action, reason, facts.Card.Key, facts.Attempt.Id,
            facts.Counters.AutomaticRounds, facts.Settings.MaximumRounds,
            action == TransitionAction.DegradeWithFollowUp
                ? findings.Where(item => review.RoundBudget.DegradedAspects.Contains(
                    item.Aspect, StringComparer.OrdinalIgnoreCase)).ToArray()
                : review.FollowUp.Findings, facts.Attempt.ReviewedDelivery);
    }

    public static TransitionDecision Create(TransitionAction action, string reason, string cardKey,
        string attemptId, int roundsUsed, int roundsAllowed,
        IReadOnlyList<ReviewFollowUpFinding>? findings = null, ContinuationBase? continuationBase = null)
    {
        var items = findings ?? [];
        return new(action, reason,
            new(action.ToString(), cardKey, attemptId, roundsUsed, roundsAllowed,
                items.Select(item => item.Aspect).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            [new(action, Union(action), Floor(action))], items,
            action is TransitionAction.FindingRound or TransitionAction.ConcernRound
                or TransitionAction.SalvageContinuation ? continuationBase : null,
            action == TransitionAction.DegradeWithFollowUp,
            action == TransitionAction.DegradeWithFollowUp ? LinkedFollowUpLane : null);
    }

    public static DecisionUnion Union(TransitionAction action) => action switch
    {
        TransitionAction.CreateReview => DecisionUnion.Prepare,
        TransitionAction.FindingRound or TransitionAction.ConcernRound or TransitionAction.SweepRound
            or TransitionAction.SalvageContinuation => DecisionUnion.Adapt,
        TransitionAction.ReviewInfrastructureRetry or TransitionAction.ReturnToReady => DecisionUnion.Retry,
        TransitionAction.DeferToBatch or TransitionAction.SweepHold => DecisionUnion.Defer,
        TransitionAction.HumanReview or TransitionAction.Escalate or TransitionAction.SweepWaitForPerson => DecisionUnion.Ask,
        TransitionAction.Halt or TransitionAction.SweepIgnore => DecisionUnion.Halt,
        _ => DecisionUnion.Continue,
    };

    public static DecisionAuthority Floor(TransitionAction action) => action switch
    {
        TransitionAction.AcceptAndIntegrate => DecisionAuthority.VerifiedDelivery,
        TransitionAction.DegradeWithFollowUp => DecisionAuthority.LinkedFollowUp,
        TransitionAction.Escalate or TransitionAction.HumanReview or TransitionAction.SweepWaitForPerson
            => DecisionAuthority.Operator,
        _ => DecisionAuthority.Automatic,
    };

    /// <summary>Lane effect selected by the engine's typed action for one occurrence.</summary>
    public static string? TargetLane(TransitionAction? action, string? sourcePayloadTarget,
        OrchestrationAction stageAction) => action switch
    {
        TransitionAction.FindingRound or TransitionAction.ConcernRound
            or TransitionAction.ReturnToReady => TaskStates.Ready,
        TransitionAction.Escalate => TaskStates.Escalated,
        TransitionAction.HumanReview or TransitionAction.AcceptAndIntegrate
            or TransitionAction.DegradeWithFollowUp => TaskStates.HumanReview,
        TransitionAction.Halt or TransitionAction.SweepIgnore or TransitionAction.SweepHold
            or TransitionAction.DeferToBatch or TransitionAction.ReviewInfrastructureRetry => null,
        _ => sourcePayloadTarget ?? (stageAction is OrchestrationAction.Escalate or OrchestrationAction.Fail
            ? TaskStates.Escalated : null),
    };

    public static bool IsDeferredAcceptance(bool deferred, string outcome)
        => deferred && (string.Equals(outcome, ReviewOutcomeKinds.Pass, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, ReviewOutcomeKinds.IntegrationBranchDefect, StringComparison.OrdinalIgnoreCase));

    public static bool RetryInfrastructure(bool budgetAvailable, bool aspectRetryExhausted,
        bool interventionActive, bool probeFailed, bool diagnosisSupported)
        => budgetAvailable && !aspectRetryExhausted && !interventionActive && !probeFailed && diagnosisSupported;

    public static bool OpensFindingRound(string outcome, ReviewFollowUpAction action)
        => outcome is ReviewOutcomeKinds.Pass or ReviewOutcomeKinds.ProductFailure or ReviewOutcomeKinds.IntegrationBranchDefect
           && action == ReviewFollowUpAction.ReviewFindingRound;

    public static string CompletionLane(string outcome, bool providerRejected) => providerRejected
        ? TaskStates.Escalated : outcome.Trim().ToLowerInvariant() switch
        {
            CompletionOutcomeKinds.Done or CompletionOutcomeKinds.NoOp => TaskStates.AutoReview,
            CompletionOutcomeKinds.Blocked or CompletionOutcomeKinds.NeedsInput or CompletionOutcomeKinds.Unknown => TaskStates.Escalated,
            CompletionOutcomeKinds.EnvironmentFailure or CompletionOutcomeKinds.Stopped or CompletionOutcomeKinds.MechanicalFallback => TaskStates.Ready,
            _ => string.Empty,
        };

    public static bool CreateReview(bool epicPlanning, string outcome, bool verifiedDelivery, bool reportOnly)
        => !epicPlanning && outcome is CompletionOutcomeKinds.Done or CompletionOutcomeKinds.NoOp && verifiedDelivery && !reportOnly;

    public static TransitionAction Salvage(string? outcome, bool hasCommit, int roundsUsed)
        => !string.Equals(outcome?.Trim(), CompletionOutcomeKinds.Unknown, StringComparison.OrdinalIgnoreCase)
            ? TransitionAction.Continue : hasCommit && Math.Max(0, roundsUsed) < 1
                ? TransitionAction.SalvageContinuation : TransitionAction.Escalate;
}
