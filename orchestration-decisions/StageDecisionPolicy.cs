using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public sealed record LegacyReviewFinding(string? Severity, string? Summary);
public sealed record StagePayload(
    string? ReviewOutcome = null, string? AgentOutcome = null, string? Terminal = null,
    string? RunAttemptId = null, string? ReviewSubjectId = null, string? ReviewAttemptId = null,
    string? ResultSha = null, string? ResultRef = null,
    IReadOnlyList<ReviewVerdictDto>? Verdicts = null,
    IReadOnlyList<LegacyReviewFinding>? ReviewFindings = null,
    IReadOnlyList<ReviewOrchestrationGateDto>? Gates = null,
    ReviewPlanDto? ReviewPlan = null, string? FailureClassification = null,
    int? AutomaticRoundsUsed = null, int MaximumCardRounds = 4,
    int ConcernRoundsUsed = 0, int MaximumConcernRounds = 0,
    bool InfrastructureRetryAvailable = false, int MaximumReissues = 2,
    int? ReissuesUsed = null,
    ReviewRoundBudgetLedger? DeliveredRounds = null,
    int MaximumDeliveredRounds = 4);

public sealed record StagePolicyDecision(OrchestrationAction Action, TransitionDecision? Transition = null);

/// <summary>Stage protocol projection. The stage handlers only read facts and apply effects.</summary>
public static class StageDecisionPolicy
{
    public static TransitionFacts Facts(string taskId, int reissues, StagePayload payload)
    {
        var reissuesUsed = payload.ReissuesUsed ?? reissues;
        var findings = (payload.Verdicts ?? []).Where(verdict =>
            !ReviewRoundBudgetPolicy.IsUncitedBlock(verdict.Classification)).Select(verdict => new ReviewFollowUpFinding(
            verdict.Aspect, verdict.Status, verdict.Summary, verdict.EvidenceChecked,
            verdict.Missing, verdict.Classification)).Concat((payload.ReviewFindings ?? []).Select(item =>
                new ReviewFollowUpFinding("review-finding",
                    item.Severity is "blocker" or "critical" ? "block" : "concerns",
                    item.Summary, Finding: item.Summary))).ToArray();
        return new(new(taskId, TaskStates.AutoReview),
            new(payload.ReviewAttemptId ?? payload.RunAttemptId ?? string.Empty,
                !string.IsNullOrWhiteSpace(payload.ResultSha),
                payload.ResultRef is { Length: > 0 } resultRef && payload.ResultSha is { Length: > 0 } sha
                    ? new(resultRef, sha, payload.ReviewAttemptId ?? string.Empty) : null),
            new(payload.ReviewOutcome ?? ReviewOutcomeKinds.Pass, findings, payload.ReviewPlan, payload.Verdicts,
                payload.FailureClassification),
            new(payload.MaximumCardRounds, payload.MaximumConcernRounds,
                payload.MaximumDeliveredRounds, MaximumReissues: payload.MaximumReissues),
            new(payload.AutomaticRoundsUsed ?? reissuesUsed, payload.ConcernRoundsUsed, reissuesUsed),
            new(payload.ReviewPlan?.BuildTestDeferredToBatch == true, payload.InfrastructureRetryAvailable), payload.DeliveredRounds);
    }

    public static ReviewRoundBudgetLedger DeliveredHistory(IEnumerable<StagePayload> priorOldestFirst)
        => new(priorOldestFirst.Where(payload => payload.ReviewAttemptId is not null
                && ReviewRoundBudgetPolicy.IsDeliveredOutcome(payload.ReviewOutcome))
            .GroupBy(payload => payload.ReviewAttemptId, StringComparer.Ordinal).Select(group => group.Last())
            .Select(payload => new DeliveredReviewRound(payload.ReviewAttemptId!,
                ReviewRoundBudgetPolicy.CountedBlockingAspects(ReviewFindingMergePolicy.Merge((payload.Verdicts ?? [])
                    .Select(item => new ReviewFollowUpFinding(item.Aspect, item.Status, item.Summary,
                        item.EvidenceChecked, item.Missing, item.Classification)))), [],
                BlockingFindingKeys: ReviewFindingMergePolicy.BlockingFingerprints((payload.Verdicts ?? [])
                    .Select(item => new ReviewFollowUpFinding(item.Aspect, item.Status, item.Summary,
                        item.EvidenceChecked, item.Missing, item.Classification)))))
            .ToArray());

    public static int ConcernRoundsUsed(IEnumerable<StagePayload> priorReports)
        => priorReports.Count(payload => TransitionDecisionPolicy.Decide(Facts(
            string.Empty, payload.AutomaticRoundsUsed ?? 0, payload)).Action == TransitionAction.ConcernRound);

    public static StagePolicyDecision Review(string taskId, int reissues, StagePayload payload)
    {
        if (payload.Verdicts is not null || payload.ReviewPlan is not null)
        {
            var decision = TransitionDecisionPolicy.Decide(Facts(taskId, reissues, payload));
            return new(ToStageAction(decision.Action), decision);
        }
        var stageAction = ReviewOutcome(payload.ReviewOutcome, payload.AgentOutcome, payload.Terminal);
        if (payload.ReviewOutcome is null) return new(stageAction);
        var transitionAction = stageAction switch
        {
            OrchestrationAction.Reissue => TransitionAction.FindingRound,
            OrchestrationAction.Escalate => TransitionAction.Escalate,
            _ => TransitionAction.Continue,
        };
        return new(stageAction, TransitionDecisionPolicy.Create(transitionAction,
            "The remote review report selected the next stage.", taskId,
            payload.ReviewAttemptId ?? payload.RunAttemptId ?? string.Empty,
            payload.AutomaticRoundsUsed ?? reissues, payload.MaximumCardRounds));
    }

    public static TransitionDecision Coding(string taskId, string attemptId, string? targetState)
    {
        var action = targetState switch
        {
            TaskStates.AutoReview => TransitionAction.CreateReview,
            TaskStates.Ready => TransitionAction.ReturnToReady,
            TaskStates.Escalated => TransitionAction.Escalate,
            TaskStates.HumanReview => TransitionAction.HumanReview,
            _ => TransitionAction.Continue,
        };
        return TransitionDecisionPolicy.Create(action,
            $"The settled coding attempt continues to {targetState ?? "its current lane"}.",
            taskId, attemptId, 0, 0);
    }

    public static StagePolicyDecision Council(string taskId, int reissues, StagePayload payload)
    {
        var decision = TransitionDecisionPolicy.Decide(Facts(taskId, reissues, payload));
        return new(ToStageAction(decision.Action), decision);
    }

    public static OrchestrationAction ReviewOutcome(string? reviewOutcome, string? executionOutcome, string? terminal)
    {
        if (!string.IsNullOrWhiteSpace(reviewOutcome))
            return Normalize(reviewOutcome) switch
            {
                "pass" or "integrationbranchdefect" => OrchestrationAction.Continue,
                "productfailure" => OrchestrationAction.Reissue,
                _ => OrchestrationAction.Escalate,
            };
        return Normalize(executionOutcome ?? terminal) switch
        {
            "blocked" or "needsinput" => OrchestrationAction.Escalate,
            _ => OrchestrationAction.Continue,
        };
    }

    public static OrchestrationAction ToStageAction(TransitionAction action) => action switch
    {
        TransitionAction.FindingRound or TransitionAction.ConcernRound or TransitionAction.ReturnToReady
            or TransitionAction.ReviewInfrastructureRetry => OrchestrationAction.Reissue,
        TransitionAction.Escalate or TransitionAction.SweepWaitForPerson => OrchestrationAction.Escalate,
        _ => OrchestrationAction.Continue,
    };

    public static OrchestrationAction Gate(int failed) => failed > 0
        ? OrchestrationAction.Reissue : OrchestrationAction.Continue;

    public static StagePolicyDecision GateTransition(string taskId, string attemptId,
        int failed, int roundsUsed, int roundsAllowed,
        bool infrastructureFailure = false, bool interventionRequired = false)
    {
        var action = infrastructureFailure || interventionRequired ? TransitionAction.Escalate
            : failed == 0 ? TransitionAction.Continue
            : roundsUsed >= roundsAllowed ? TransitionAction.Escalate
            : TransitionAction.ReturnToReady;
        var reason = interventionRequired ? "The failed build and test gate requires operator intervention."
            : infrastructureFailure ? "The build and test gate failed in infrastructure."
            : failed == 0 ? "The build and test gate passed."
            : action == TransitionAction.Escalate
                ? "The card round budget is spent with a build and test failure."
                : "The build and test gate failed; reopen a bounded fix round.";
        return new(ToStageAction(action), TransitionDecisionPolicy.Create(action, reason,
            taskId, attemptId, roundsUsed, roundsAllowed));
    }

    public static OrchestrationAction RemoteGate(string phase) => phase switch
    {
        GateStates.Passed => OrchestrationAction.Continue,
        GateStates.ProductFailed => OrchestrationAction.Reissue,
        _ => OrchestrationAction.Escalate,
    };

    public static OrchestrationAction Completion(bool priorFailure) => priorFailure
        ? OrchestrationAction.Escalate : OrchestrationAction.Complete;

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
