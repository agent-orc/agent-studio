using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestration.Decisions;

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
    AgentStudio.Runner.ReviewRoundBudgetLedger? DeliveredRounds = null);

public sealed record StagePolicyDecision(OrchestrationAction Action, TransitionDecision? Transition = null);

/// <summary>Stage protocol projection. The stage handlers only read facts and apply effects.</summary>
public static class StageDecisionPolicy
{
    public static TransitionFacts Facts(string taskId, int reissues, StagePayload payload)
    {
        var findings = (payload.Verdicts ?? []).Select(verdict => new ReviewFollowUpFinding(
            verdict.Aspect, verdict.Status, verdict.Summary, verdict.EvidenceChecked,
            verdict.Missing, verdict.Classification)).Concat((payload.ReviewFindings ?? []).Select(item =>
                new ReviewFollowUpFinding("review-finding",
                    item.Severity is "blocker" or "critical" ? "block" : "concerns",
                    item.Summary, Finding: item.Summary))).ToArray();
        return new(new(taskId, "4-auto-review"),
            new(payload.ReviewAttemptId ?? payload.RunAttemptId ?? string.Empty,
                !string.IsNullOrWhiteSpace(payload.ResultSha),
                payload.ResultRef is { Length: > 0 } resultRef && payload.ResultSha is { Length: > 0 } sha
                    ? new(resultRef, sha, payload.ReviewAttemptId ?? string.Empty) : null),
            new(payload.ReviewOutcome ?? "Pass", findings, payload.ReviewPlan, payload.Verdicts,
                payload.FailureClassification),
            new(payload.MaximumCardRounds, payload.MaximumConcernRounds, MaximumReissues: payload.MaximumReissues),
            new(payload.AutomaticRoundsUsed ?? reissues, payload.ConcernRoundsUsed, reissues),
            new(payload.ReviewPlan?.BuildTestDeferredToBatch == true, payload.InfrastructureRetryAvailable), payload.DeliveredRounds);
    }

    public static AgentStudio.Runner.ReviewRoundBudgetLedger DeliveredHistory(IEnumerable<StagePayload> priorOldestFirst)
        => new(priorOldestFirst.Where(payload => payload.ReviewAttemptId is not null
                && payload.ReviewOutcome is "Pass" or "ProductFailure" or "IntegrationBranchDefect")
            .GroupBy(payload => payload.ReviewAttemptId, StringComparer.Ordinal).Select(group => group.Last())
            .Select(payload => new AgentStudio.Runner.DeliveredReviewRound(payload.ReviewAttemptId!,
                (payload.Verdicts ?? []).Where(verdict => ReviewGradingPolicy.IsBlockingToken(verdict.Status))
                    .Select(verdict => verdict.Aspect).ToArray(), [])).ToArray());

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
        return new(ReviewOutcome(payload.ReviewOutcome, payload.AgentOutcome, payload.Terminal));
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
