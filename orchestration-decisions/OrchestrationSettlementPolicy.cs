using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public sealed record OrchestrationSettlementDecision(
    string RunStatus,
    OrchestrationStage NextStage,
    int ReissueAttempts,
    string? TaskState,
    bool IsTerminal,
    string? SupersededReason = null,
    string? SettlementReason = null,
    TransitionDecision? Transition = null);

public static class OrchestrationSettlementPolicy
{
    public const string IntegratingStatus = "integrating";

    /// <summary>
    /// Settles one stage and, when the run accepted the delivery for
    /// integration (AGT-3061), holds the card in Auto Review until the
    /// integration of the accepted delivery reports: a run that would hand the
    /// card to Human Review settles as <see cref="IntegratingStatus"/> instead.
    /// <paramref name="acceptedAction"/> is the engine transition the run
    /// accepted, from this stage or an earlier one; <paramref name="reviewedPublication"/>
    /// is the project's delivery policy.
    /// </summary>
    public static OrchestrationSettlementDecision Decide(
        OrchestrationAction action,
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts,
        string currentTaskState,
        long expectedTaskVersion,
        long currentTaskVersion,
        RepeatedReviewBlockDiagnosis? repeatedBlock,
        TransitionDecision? transition,
        TransitionAction? acceptedAction,
        bool reviewedPublication)
    {
        var decision = Decide(action, stages, currentStage, currentReissueAttempts, maxReissueAttempts,
            currentTaskState, expectedTaskVersion, currentTaskVersion, repeatedBlock, transition);
        return acceptedAction == TransitionAction.AcceptAndIntegrate && reviewedPublication
            && decision is { RunStatus: "completed", TaskState: TaskStates.HumanReview }
            ? decision with
            {
                RunStatus = IntegratingStatus,
                TaskState = null,
                SettlementReason = "The accepted delivery waits in Auto Review for its integration.",
            }
            : decision;
    }

    public static OrchestrationSettlementDecision Decide(
        OrchestrationAction action,
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts,
        string currentTaskState,
        long expectedTaskVersion,
        long currentTaskVersion,
        RepeatedReviewBlockDiagnosis? repeatedBlock = null,
        TransitionDecision? transition = null)
    {
        if (!string.Equals(currentTaskState, TaskStates.AutoReview, StringComparison.Ordinal))
        {
            return new OrchestrationSettlementDecision(
                "superseded",
                currentStage,
                currentReissueAttempts,
                null,
                true,
                $"Task moved to '{currentTaskState}' before settlement.");
        }

        if (expectedTaskVersion > 0 && currentTaskVersion != expectedTaskVersion)
        {
            return new OrchestrationSettlementDecision(
                "superseded",
                currentStage,
                currentReissueAttempts,
                null,
                true,
                $"Task version changed from {expectedTaskVersion} to {currentTaskVersion} before settlement.");
        }

        if (transition is not null && (action == OrchestrationAction.Reissue
            || transition.Action is TransitionAction.DegradeWithFollowUp or TransitionAction.Escalate))
        {
            return transition.Action switch
            {
                TransitionAction.FindingRound or TransitionAction.ConcernRound
                    or TransitionAction.ReviewInfrastructureRetry => Terminal(
                        "reissued", currentStage, currentReissueAttempts + 1, TaskStates.Ready, transition.Reason)
                        with { Transition = transition },
                TransitionAction.DegradeWithFollowUp => Terminal(
                    "completed", currentStage, currentReissueAttempts, TaskStates.HumanReview, transition.Reason)
                    with { Transition = transition },
                TransitionAction.Escalate => Terminal(
                    "escalated", currentStage, currentReissueAttempts, TaskStates.Escalated, transition.Reason)
                    with { Transition = transition },
                _ => Continue(stages, currentStage, currentReissueAttempts) with { Transition = transition },
            };
        }

        return action switch
        {
            OrchestrationAction.Continue => Continue(stages, currentStage, currentReissueAttempts),
            OrchestrationAction.Reissue => Reissue(
                currentStage,
                currentReissueAttempts,
                maxReissueAttempts,
                repeatedBlock),
            OrchestrationAction.Escalate => Terminal(
                "escalated", currentStage, currentReissueAttempts, TaskStates.Escalated),
            OrchestrationAction.Complete => Terminal(
                "completed", currentStage, currentReissueAttempts, TaskStates.HumanReview),
            OrchestrationAction.Fail => Terminal(
                "failed", currentStage, currentReissueAttempts, TaskStates.Escalated),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private static OrchestrationSettlementDecision Continue(
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int reissueAttempts)
    {
        var index = stages.IndexOf(currentStage);
        if (index >= 0 && index + 1 < stages.Count)
        {
            return new OrchestrationSettlementDecision(
                "pending",
                stages[index + 1],
                reissueAttempts,
                null,
                false);
        }

        return Terminal("completed", currentStage, reissueAttempts, TaskStates.HumanReview);
    }

    private static OrchestrationSettlementDecision Reissue(
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts,
        RepeatedReviewBlockDiagnosis? repeatedBlock)
    {
        var reissueAttempts = currentReissueAttempts + 1;
        if (repeatedBlock?.MustEscalate == true)
        {
            return Terminal(
                "escalated",
                currentStage,
                reissueAttempts,
                TaskStates.Escalated,
                $"The same aspect block repeated for {repeatedBlock.ConsecutiveRounds} consecutive review rounds " +
                $"(limit {repeatedBlock.MaximumRounds}): {repeatedBlock.Finding}. " +
                "Escalated for a human scope decision instead of reissuing the same work again.");
        }
        return reissueAttempts <= maxReissueAttempts
            ? Terminal("reissued", currentStage, reissueAttempts, TaskStates.Ready)
            : Terminal(
                "escalated",
                currentStage,
                reissueAttempts,
                TaskStates.Escalated,
                $"The task-wide review reissue budget is exhausted ({reissueAttempts - 1}/{maxReissueAttempts} retries used).");
    }

    private static OrchestrationSettlementDecision Terminal(
        string status,
        OrchestrationStage currentStage,
        int reissueAttempts,
        string taskState,
        string? settlementReason = null)
        => new(status, currentStage, reissueAttempts, taskState, true, SettlementReason: settlementReason);
}

internal static class OrchestrationStageListExtensions
{
    internal static int IndexOf(
        this IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage stage)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            if (stages[index] == stage) return index;
        }

        return -1;
    }
}
