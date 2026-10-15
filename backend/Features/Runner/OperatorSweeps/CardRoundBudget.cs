namespace AgentStudio.Runner;

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

    public static CardRoundBudgetAction Decide(CardRoundBudgetState budget, IEnumerable<string> blockingAspects)
        => MonolithTransitionAdapter.CardRoundBudget(budget, blockingAspects);

    public static CardRoundBudgetAction DecideForReview(
        CardRoundBudgetState budget, IReadOnlyCollection<string> blockingAspects, bool opensConcernRound)
        => MonolithTransitionAdapter.CardRoundBudgetForReview(budget, blockingAspects, opensConcernRound);

    public static int CountRoundsUsed(
        IEnumerable<ReviewDecisionRecord> journal,
        IEnumerable<TimelineEvent> timeline,
        string jobId)
    {
        return AgentStudio.OrchestrationDecisions.RoundCounterPolicy.Count(
            journal.Select(record => new AgentStudio.OrchestrationDecisions.RoundDecisionFact(
                record.JobId, record.Kind.ToString())),
            timeline.Select(entry => new AgentStudio.OrchestrationDecisions.RoundTimelineFact(
                entry.Kind, entry.Details)), jobId);
    }
}
