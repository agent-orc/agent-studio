namespace AgentStudio.Runner;

/// <param name="Used">Automatic rounds this card has already spent, across every review-attempt epoch.</param>
/// <param name="Allowed">Rounds the card may spend in its lifetime.</param>
public sealed record CardRoundBudgetState(int Used, int Allowed)
{
    public int Remaining => Math.Max(0, Allowed - Used);
    public bool Exhausted => Used >= Allowed;
}

/// <summary>
/// One round budget per card (AGT-3011), shared by the orchestrator and the
/// operator sweeps.
/// <para>
/// The orchestrator's own reissue cap
/// (<see cref="ReviewDecisionOrchestrator.CountReissuesInCurrentChain"/>) is
/// scoped to the current review-attempt epoch, and every operator requeue opens
/// a new epoch. That is how cards reached 13 rounds while the night-shift
/// scripts drove them: each <c>/continue</c> plus requeue replenished the
/// budget. This count deliberately ignores epochs. A round is spent by whoever
/// opens it, and nothing a sweep, a requeue, or a <c>/continue</c> does can
/// give it back.
/// </para>
/// <para>
/// Counted rounds, each exactly once:
/// <list type="bullet">
/// <item><see cref="ReviewDecisionKind.Reissue"/> journal records: orchestrator
/// reissues and every sweep round (the sweeps append one per round);</item>
/// <item>Remote Review finding and concern rounds, which reopen the card
/// without a journal record: <c>quality_loop_reopened</c> with a
/// <c>reviewAttemptId</c> and a <c>multi-aspect-*</c> cause;</item>
/// <item>automatic timeout or lost-worker continuations:
/// <c>continuation_round_started</c> with <c>automatic=true</c>.</item>
/// </list>
/// </para>
/// </summary>
public static class CardRoundBudget
{
    /// <summary>The cap the night-shift <c>auto-fix-rounds.mjs</c> script used.</summary>
    public const int DefaultRoundsPerCard = 4;

    public const string ConfigurationKey = "OperatorSweeps:MaxRoundsPerCard";

    public static int ResolveAllowed(IConfiguration? configuration)
        => Math.Max(0, configuration?.GetValue(ConfigurationKey, DefaultRoundsPerCard) ?? DefaultRoundsPerCard);

    public static CardRoundBudgetState Evaluate(
        IEnumerable<ReviewDecisionRecord> journal,
        IEnumerable<TimelineEvent> timeline,
        string jobId,
        int allowed)
        => new(CountRoundsUsed(journal, timeline, jobId), Math.Max(0, allowed));

    public static int CountRoundsUsed(
        IEnumerable<ReviewDecisionRecord> journal,
        IEnumerable<TimelineEvent> timeline,
        string jobId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(timeline);
        var reissues = journal.Count(record =>
            record.Kind == ReviewDecisionKind.Reissue
            && string.Equals(record.JobId, jobId, StringComparison.Ordinal));
        var timelineRounds = timeline.Count(IsTimelineOnlyRound);
        return reissues + timelineRounds;
    }

    private static bool IsTimelineOnlyRound(TimelineEvent entry)
    {
        var details = entry.Details;
        if (details is null) return false;
        if (entry.Kind == TimelineEventKinds.QualityLoopReopened)
        {
            return details.TryGetValue("reviewAttemptId", out var attempt)
                   && !string.IsNullOrWhiteSpace(attempt)
                   && details.TryGetValue("cause", out var cause)
                   && cause is "multi-aspect-block" or "multi-aspect-concern";
        }
        return entry.Kind == TimelineEventKinds.ContinuationRoundStarted
               && details.GetValueOrDefault("automatic") == "true";
    }
}
