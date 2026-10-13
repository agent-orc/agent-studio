namespace AgentStudio.Orchestration.Decisions;

public sealed record RoundJournalFact(string JobId, string Kind);
public sealed record RoundTimelineFact(string Kind, IReadOnlyDictionary<string, string>? Details);

public static class AutomaticRoundPolicy
{
    public static int Count(IEnumerable<RoundJournalFact> journal, IEnumerable<RoundTimelineFact> timeline, string jobId)
        => journal.Count(row => row.Kind == "Reissue" && row.JobId == jobId)
           + timeline.Count(IsTimelineRound);

    private static bool IsTimelineRound(RoundTimelineFact row)
    {
        if (row.Details is not { } details) return false;
        if (row.Kind == "quality_loop_reopened")
            return !string.IsNullOrWhiteSpace(details.GetValueOrDefault("reviewAttemptId"))
                   && details.GetValueOrDefault("cause") is "multi-aspect-block" or "multi-aspect-concern";
        return row.Kind == "continuation_round_started" && details.GetValueOrDefault("automatic") == "true";
    }
}
