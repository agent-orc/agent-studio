namespace AgentStudio.OrchestrationDecisions;

public sealed record RoundDecisionFact(string JobId, string Kind);
public sealed record RoundTimelineFact(string Kind, Dictionary<string, string>? Details);

public static class RoundCounterPolicy
{
    public static int Count(IEnumerable<RoundDecisionFact> journal, IEnumerable<RoundTimelineFact> timeline, string jobId)
    {
        var rows = timeline.ToArray();
        // Reservation precedes Ready. Its later completion is the same round,
        // including legacy skew rounds which only wrote a completion receipt.
        var skew = rows.Where(entry => entry.Details?.GetValueOrDefault("cause") == "review-plan-skew"
                && (entry.Kind == TimelineEventKinds.QualityLoopReopened
                    || entry.Kind == TimelineEventKinds.CardRoundBudgetDisposition
                        && entry.Details.GetValueOrDefault("disposition") == "OpenRound"))
            .Select(entry => entry.Details!.GetValueOrDefault("reviewAttemptId"))
            .Where(attempt => !string.IsNullOrWhiteSpace(attempt)).Distinct(StringComparer.Ordinal).Count();
        return journal.Count(record => record.Kind == nameof(AgentStudio.Runner.ReviewDecisionKind.Reissue) && record.JobId == jobId)
            + rows.Count(IsTimelineOnlyRound) + skew;
    }

    private static bool IsTimelineOnlyRound(RoundTimelineFact entry)
    {
        if (entry.Details is not { } details) return false;
        if (entry.Kind == TimelineEventKinds.QualityLoopReopened)
            return details.TryGetValue("reviewAttemptId", out var attempt) && !string.IsNullOrWhiteSpace(attempt)
                && details.TryGetValue("cause", out var cause) && cause is "multi-aspect-block" or "multi-aspect-concern";
        return entry.Kind == TimelineEventKinds.ContinuationRoundStarted && details.GetValueOrDefault("automatic") == "true";
    }
}
