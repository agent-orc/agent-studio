namespace AgentStudio.OrchestrationDecisions;

public sealed record RoundDecisionFact(string JobId, string Kind);
public sealed record RoundTimelineFact(string Kind, Dictionary<string, string>? Details);

public static class RoundCounterPolicy
{
    public static int Count(IEnumerable<RoundDecisionFact> journal, IEnumerable<RoundTimelineFact> timeline, string jobId)
        => journal.Count(record => record.Kind == nameof(AgentStudio.Runner.ReviewDecisionKind.Reissue) && record.JobId == jobId)
           + timeline.Count(IsTimelineOnlyRound);

    private static bool IsTimelineOnlyRound(RoundTimelineFact entry)
    {
        if (entry.Details is not { } details) return false;
        if (entry.Kind == TimelineEventKinds.QualityLoopReopened)
            return details.TryGetValue("reviewAttemptId", out var attempt) && !string.IsNullOrWhiteSpace(attempt)
                && details.TryGetValue("cause", out var cause) && cause is "multi-aspect-block" or "multi-aspect-concern";
        return entry.Kind == TimelineEventKinds.ContinuationRoundStarted && details.GetValueOrDefault("automatic") == "true";
    }
}
