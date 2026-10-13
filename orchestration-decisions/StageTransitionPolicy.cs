using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestration.Decisions;

public static class StageTransitionPolicy
{
    public static OrchestrationAction Review(string? reviewOutcome, string? executionOutcome, string? terminal)
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
            "blocked" or "needsinput" or "unknown" => OrchestrationAction.Escalate,
            _ => OrchestrationAction.Continue,
        };
    }

    public static OrchestrationAction FromDecision(TransitionDecision decision) => decision.Action switch
    {
        TransitionAction.FindingRound or TransitionAction.ConcernRound => OrchestrationAction.Reissue,
        TransitionAction.Escalate => OrchestrationAction.Escalate,
        TransitionAction.DegradeWithFollowUp => OrchestrationAction.Complete,
        _ => OrchestrationAction.Continue,
    };

    public static int CriticalFindingCount(IEnumerable<string> severities)
        => severities.Count(severity => severity is "blocker" or "critical");

    public static OrchestrationAction Council(IReadOnlyList<ReviewFollowUpFinding> findings,
        int criticalFindings, int concernRoundsUsed, int maximumConcernRounds, bool reviewAlreadyDecided = false)
    {
        if (reviewAlreadyDecided) return OrchestrationAction.Continue;
        var followUp = ReviewFollowUpPolicy.Decide(findings.Where(finding => !string.Equals(
            finding.Classification, ReviewVerdictCitationPolicy.BlockWithoutCitation,
            StringComparison.OrdinalIgnoreCase)), concernRoundsUsed, maximumConcernRounds);
        if (followUp.Action == ReviewFollowUpAction.RetryAspect) return OrchestrationAction.Escalate;
        return criticalFindings > 0 || followUp.StartsCodingRound
            ? OrchestrationAction.Reissue : OrchestrationAction.Continue;
    }

    public static OrchestrationAction Gate(int failed) => failed > 0
        ? OrchestrationAction.Reissue : OrchestrationAction.Continue;

    public static OrchestrationAction GateTerminal(string phase) => phase switch
    {
        GateStates.Passed => OrchestrationAction.Continue,
        GateStates.ProductFailed => OrchestrationAction.Reissue,
        _ => OrchestrationAction.Escalate,
    };

    public static OrchestrationAction Completion(bool priorFailure) => priorFailure
        ? OrchestrationAction.Escalate : OrchestrationAction.Complete;

    private static string Normalize(string? value)
        => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
