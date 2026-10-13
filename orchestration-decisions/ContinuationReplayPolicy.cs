using AgentStudio.Runner;

namespace AgentStudio.Orchestration.Decisions
{
    public static class ContinuationReplayPolicy
    {
        public static RemoteReviewContinuationStep Decide(IEnumerable<RoundTimelineFact> ledger,
            string attemptId, string cause, string moveKey, string currentState)
        {
            var rows = ledger.ToArray();
            if (rows.Any(item => (item.Kind is "quality_loop_reopened" or "orchestrator_escalated")
                && item.Details?.GetValueOrDefault("reviewAttemptId") == attemptId
                && item.Details?.GetValueOrDefault("cause") == cause)) return RemoteReviewContinuationStep.Completed;
            if (rows.Any(item => item.Kind == "lane_changed"
                && item.Details?.GetValueOrDefault("idempotencyKey") == moveKey)) return RemoteReviewContinuationStep.Finish;
            return currentState == "4-auto-review" ? RemoteReviewContinuationStep.Move : RemoteReviewContinuationStep.Unapplicable;
        }
    }
}

namespace AgentStudio.Runner
{
    public enum RemoteReviewContinuationStep { Move, Finish, Completed, Unapplicable }
}
