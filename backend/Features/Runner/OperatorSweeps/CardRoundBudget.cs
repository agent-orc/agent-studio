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
/// <c>continuation_round_started</c> with <c>automatic=true</c>;</item>
/// <item>review-plan-skew reservations and legacy reopen receipts, counted
/// once per review attempt even when the lane move has not landed yet.</item>
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
        return AgentStudio.Runner.MonolithTransitionAdapter.RoundCounterPolicyCount(
            journal.Select(record => new AgentStudio.OrchestrationDecisions.RoundDecisionFact(
                record.JobId, record.Kind.ToString())),
            timeline.Select(entry => new AgentStudio.OrchestrationDecisions.RoundTimelineFact(
                entry.Kind, entry.Details)), jobId);
    }
}

/// <summary>The target inputs which a skew recovery prepares, independently of target ref movement.</summary>
public sealed record ReviewPlanSkewPlan(string CommitSha, string? BuildProfileFingerprint, string? BriefSha256);

public sealed record ReviewPlanSkewReservation(
    string AttemptId, string SettlementKey, string DeliverySha, string? PlanCommitSha,
    CardRoundBudgetState Budget, CardRoundBudgetAction Action);

/// <summary>
/// Durable reservations survive the gap before Ready and share the card's
/// lifetime allowance. The caller holds the card's skew projection gate.
/// Timeline rows project the charge into the common counter; this small atomic
/// ledger remains authoritative if a timeline write or a lane move is interrupted.
/// </summary>
public static class ReviewPlanSkewAllowance
{
    private sealed record Ledger(ReviewPlanSkewPlan? Plan, IReadOnlyList<ReviewPlanSkewReservation> Reservations);
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private const string FileName = "review-plan-skew-allowance.json";

    private static string PathFor(string folder) => Path.Combine(TaskPaths.LogsDir(folder), FileName);
    private static Ledger Read(string folder)
        => File.Exists(PathFor(folder))
            ? System.Text.Json.JsonSerializer.Deserialize<Ledger>(File.ReadAllText(PathFor(folder)), Json)
                ?? throw new IOException("Review plan skew allowance is unreadable.")
            : new(null, []);

    private static void Write(string folder, Ledger ledger)
        => new AgentStudio.Persistence.AtomicJsonFileWriter().Write(PathFor(folder),
            System.Text.Json.JsonSerializer.Serialize(ledger, Json));

    public static ReviewPlanSkewPlan? ReadPlan(string folder) => Read(folder).Plan;

    public static void ReleasePlan(string folder)
    {
        var ledger = Read(folder);
        if (ledger.Plan is not null) Write(folder, ledger with { Plan = null });
    }

    public static bool ReplacePlan(string folder, ReviewPlanSkewPlan plan, string attemptId, TimelineLog timeline)
    {
        var ledger = Read(folder);
        if (ledger.Plan == plan) return true;
        // Record invalidation before replacing the pin. Failure leaves the old
        // pin in force, and a crash after the row only repeats an explanation.
        if (!timeline.Append(folder, TimelineEventKinds.ReviewAttemptReplanned, TimelineActors.QualityLoop,
            "Review plan skew recovery inputs changed; the pinned plan was explicitly replaced without replenishing its allowance.",
            attemptId, details: new Dictionary<string, string>
            {
                ["cause"] = "review-plan-skew-plan-replaced",
                ["previousPlanCommitSha"] = ledger.Plan?.CommitSha ?? "unavailable",
                ["planCommitSha"] = plan.CommitSha,
                ["reviewAttemptId"] = attemptId,
            })) return false;
        Write(folder, ledger with { Plan = plan });
        return true;
    }

    public static ReviewPlanSkewReservation? Reserve(
        string folder, RemoteReviewSettlementEntry entry, TimelineLog timeline)
    {
        var continuation = entry.Continuation ?? throw new ArgumentException("A continuation is required.", nameof(entry));
        var ledger = Read(folder);
        // Legacy settlements predate the explicit pin. Their workspace proof
        // names the fetched target commit used by that executor.
        var plan = continuation.SkewPlan
            ?? (ReviewSubjectStore.IsValidResultSha(entry.Report.Workspace.IntegrationTipSha)
                ? new ReviewPlanSkewPlan(entry.Report.Workspace.IntegrationTipSha!, null, null) : null);
        var delivery = continuation.Base?.CommitSha ?? entry.Report.Workspace.ExpectedResultSha;
        var existing = ledger.Reservations.SingleOrDefault(item => item.AttemptId == entry.AttemptId);
        if (existing is not null && (existing.SettlementKey != entry.IdempotencyKey
            || existing.DeliverySha != delivery || existing.PlanCommitSha != plan?.CommitSha))
            throw new IOException("Review plan skew reservation identity does not match the settlement.");
        var reservation = existing;
        if (reservation is null)
        {
            var used = Math.Max(continuation.Budget.Used,
                ledger.Reservations.Select(item => item.Budget.Used).DefaultIfEmpty(0).Max());
            var budget = continuation.Budget with { Used = used };
            // The shared decision policy chooses the follow-up disposition for
            // a spent non-product round. Unknown plan identity cannot run a merge.
            var action = CardRoundBudget.DecideForReview(
                plan is null ? budget with { Allowed = 0 } : budget, [], opensConcernRound: true);
            if (action == CardRoundBudgetAction.OpenRound) budget = budget with { Used = used + 1 };
            reservation = new(entry.AttemptId, entry.IdempotencyKey, delivery,
                plan?.CommitSha, budget, action);
            Write(folder, ledger with
            {
                Plan = plan ?? ledger.Plan,
                Reservations = [.. ledger.Reservations, reservation],
            });
        }
        var rows = timeline.ReadAll(folder);
        if (!rows.Any(row => row.Kind == TimelineEventKinds.CardRoundBudgetDisposition
            && row.Details?.GetValueOrDefault("cause") == "review-plan-skew"
            && row.Details.GetValueOrDefault("reviewAttemptId") == entry.AttemptId))
        {
            var details = CardRoundBudgetReceipt.Details(reservation.Budget, [], "review-plan-skew", reservation.Action);
            details["reviewAttemptId"] = entry.AttemptId;
            details["settlementKey"] = entry.IdempotencyKey;
            details["deliverySha"] = delivery;
            details["planCommitSha"] = reservation.PlanCommitSha ?? "unavailable";
            if (!timeline.Append(folder, TimelineEventKinds.CardRoundBudgetDisposition, TimelineActors.QualityLoop,
                $"Review plan skew reservation {reservation.Action}: {reservation.Budget.Used} of {reservation.Budget.Allowed}.",
                entry.AttemptId, details: details)) return null;
        }
        return reservation;
    }
}
