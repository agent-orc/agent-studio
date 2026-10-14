namespace AgentStudio.OrchestrationDecisions;

public enum RemoteReviewContinuationStep { Move, Finish, Completed, Unapplicable }

public static class ContinuationReplayPolicy
{
    public static RemoteReviewContinuationStep Decide(IEnumerable<RoundTimelineFact> ledger,
        string attemptId, string cause, string moveKey, string currentState)
    {
        var rows = ledger.ToArray();
        if (rows.Any(item => (item.Kind is TimelineEventKinds.QualityLoopReopened
                or TimelineEventKinds.OrchestratorEscalated)
            && item.Details?.GetValueOrDefault("reviewAttemptId") == attemptId
            && item.Details?.GetValueOrDefault("cause") == cause))
            return RemoteReviewContinuationStep.Completed;
        if (rows.Any(item => item.Kind == TimelineEventKinds.LaneChanged
            && item.Details?.GetValueOrDefault("idempotencyKey") == moveKey))
            return RemoteReviewContinuationStep.Finish;
        return currentState == TaskStates.AutoReview
            ? RemoteReviewContinuationStep.Move : RemoteReviewContinuationStep.Unapplicable;
    }
}
