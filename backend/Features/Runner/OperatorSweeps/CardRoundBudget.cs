namespace AgentStudio.Runner;

/// <param name="Used">Automatic rounds this card has already spent, across every review-attempt epoch.</param>
/// <param name="Allowed">Rounds the card may spend in its lifetime.</param>
public sealed record CardRoundBudgetState(int Used, int Allowed)
{
    public int Remaining => Math.Max(0, Allowed - Used);
    public bool Exhausted => Used >= Allowed;
}

public enum CardRoundBudgetAction { OpenRound, DegradeToFollowUp, Escalate }

/// <summary>The same receipt fields are written by every automatic-round owner.</summary>
public static class CardRoundBudgetReceipt
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates =
        new(StringComparer.Ordinal);

    public static Dictionary<string, string> Details(
        CardRoundBudgetState budget, IEnumerable<string> blockingAspects, string cause,
        CardRoundBudgetAction action = CardRoundBudgetAction.OpenRound)
        => new(StringComparer.Ordinal)
        {
            ["cause"] = cause,
            ["roundsUsed"] = budget.Used.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["roundsAllowed"] = budget.Allowed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["blockingAspects"] = string.Join(",", blockingAspects.Distinct(StringComparer.OrdinalIgnoreCase)),
            ["disposition"] = action.ToString(),
        };

    public static void RecordDispositionOnce(
        TimelineLog timeline, string folder, string attemptId,
        CardRoundBudgetState budget, IEnumerable<string> blockingAspects,
        CardRoundBudgetAction action)
    {
        lock (Gates.GetOrAdd(attemptId, _ => new object()))
        {
            if (timeline.ReadAll(folder).Any(entry =>
                    entry.Kind == TimelineEventKinds.CardRoundBudgetDisposition
                    && entry.Details?.GetValueOrDefault("reviewAttemptId") == attemptId)) return;
            var details = Details(budget, blockingAspects, "card-round-budget", action);
            details["reviewAttemptId"] = attemptId;
            timeline.Append(folder, TimelineEventKinds.CardRoundBudgetDisposition,
                TimelineActors.QualityLoop,
                $"Automatic round budget {action}: {budget.Used} of {budget.Allowed}; "
                + $"open aspects: {details["blockingAspects"]}.",
                runId: attemptId, details: details);
        }
    }
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
/// without a <see cref="ReviewDecisionKind.Reissue"/> decision record:
/// <c>quality_loop_reopened</c> with a
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

    public static CardRoundBudgetAction Decide(
        CardRoundBudgetState budget, IEnumerable<string> blockingAspects)
    {
        if (!budget.Exhausted) return CardRoundBudgetAction.OpenRound;
        var aspects = blockingAspects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return aspects.Overlaps(["build-tests", "tests-and-evidence", "review-verification"])
            ? CardRoundBudgetAction.Escalate
            : CardRoundBudgetAction.DegradeToFollowUp;
    }

    /// <summary>
    /// The review-plane question, shared by Remote Review and the local
    /// orchestrator: a spent budget only intervenes when the review would open
    /// another round, a block (finding round) or a concern round. A passing
    /// report whose concerns open no round keeps its Pass without a follow-up.
    /// </summary>
    public static CardRoundBudgetAction DecideForReview(
        CardRoundBudgetState budget, IReadOnlyCollection<string> blockingAspects, bool opensConcernRound)
        => blockingAspects.Count > 0 || opensConcernRound
            ? Decide(budget, blockingAspects)
            : CardRoundBudgetAction.OpenRound;

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
