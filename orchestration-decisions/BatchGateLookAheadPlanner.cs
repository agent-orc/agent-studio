using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Pure batch membership and wait decision. Card risk is a relative scale.</summary>
public static class BatchGateLookAheadPlanner
{
    /// <summary>Reason of a batch closed instead of a per-task gate because the gate queue is backlogged.</summary>
    public const string BacklogReason = "gate backlog: batch preferred over a per-task gate";

    /// <summary>
    /// Reason of a hold for queued cards that wait for their batch pre-check
    /// instead of a per-task gate (AGT-3466).
    /// </summary>
    public const string QueuedPartnersReason =
        "queued cards wait for their batch pre-check: the next batch takes this card instead of a per-task gate";

    /// <summary>Deadlines a card may wait behind a gate backlog before it takes its own gate.</summary>
    public const int BacklogAgeOutDeadlines = 2;

    /// <summary>
    /// True once a card has waited <see cref="BacklogAgeOutDeadlines"/> deadlines.
    /// The gate backlog then no longer defers its per-task gate, so a card that
    /// no batch takes cannot starve while other batches keep closing.
    /// </summary>
    public static bool AgedOut(DateTimeOffset readyAtUtc, DateTimeOffset nowUtc, int deadlineMinutes)
        => nowUtc - readyAtUtc >= TimeSpan.FromMinutes((double)BacklogAgeOutDeadlines * Math.Max(0, deadlineMinutes));

    /// <summary>
    /// AGT-3407: the gate host is idle only when the load gate is clear, no
    /// batch and no per-task gate runs or waits for a slot, and a gate slot is
    /// free. Only then does a lone card take its own gate before its deadline;
    /// behind busy slots it waits for a batch partner, because a per-task gate
    /// there only joins the slot queue.
    /// </summary>
    public static bool GateHostIdle(bool loadClear, bool batchRunning, int perTaskGates,
        int slotsBusy, int slotsTotal)
        => loadClear && !batchRunning && perTaskGates == 0 && slotsBusy < slotsTotal;

    /// <summary>
    /// AGT-3466: per-task gates share the gate host with the batch. At most
    /// one slot fewer than the host has runs a per-task gate at once, so one
    /// slot stays free for the batch, the rule the batch pre-check follows. A
    /// one-slot host still runs one per-task gate; without a known slot count
    /// there is no limit.
    /// </summary>
    public static int PerTaskGateLimit(int slotsTotal)
        => slotsTotal <= 0 ? int.MaxValue : Math.Max(1, slotsTotal - 1);

    /// <summary>True while another per-task gate may start under <see cref="PerTaskGateLimit"/>.</summary>
    public static bool PerTaskGateSlotFree(int perTaskGates, int slotsTotal)
        => perTaskGates < PerTaskGateLimit(slotsTotal);

    /// <summary>
    /// AGT-3856: full suites outside the batch lane that may run on the gate
    /// host beside a running batch gate. The slot rule above still applies.
    /// </summary>
    public const int BatchCompanionSuiteLimit = 1;

    /// <summary>Wait reason of a full suite refused under <see cref="BatchCompanionSuiteLimit"/>.</summary>
    public const string CompanionSuiteWaitReason =
        "a batch gate runs and one other full suite already shares the gate host";

    /// <summary>
    /// AGT-3856: extends the AGT-3466 slot rule from slots to concurrent full
    /// suites. While a batch gate runs, at most
    /// <see cref="BatchCompanionSuiteLimit"/> other full suite runs on the
    /// gate host. On 3 and 4 November 2026 a published batch gate took a
    /// median 13.7 minutes with at most two gates on the host and 27.6 minutes
    /// with more. Returns the refusal reason, or null when the suite may start.
    /// The batch lane itself (batch gates, isolation children, base checks)
    /// is never refused here.
    /// </summary>
    public static string? CompanionSuiteRefusal(int batchGatesRunning, int companionSuitesRunning)
        => batchGatesRunning > 0 && companionSuitesRunning >= BatchCompanionSuiteLimit
            ? CompanionSuiteWaitReason : null;

    /// <summary>Look-ahead reason of a per-task gate chosen only because no batch partner was eligible.</summary>
    public const string BelowMinimumReason = "close condition reached below minimum batch size";

    /// <summary>Look-ahead reason of a per-task gate the operator's close-now forced below minimum size.</summary>
    public const string CloseNowBelowMinimumReason = "operator close-now below minimum batch size";

    /// <summary>Threshold reason of a per-task gate chosen only because no batch partner was eligible.</summary>
    public const string ThresholdNoPartnerReason = "oldest eligible card reached deadline without a batch partner";

    /// <summary>
    /// True when a planner chose the per-task route only for lack of a batch
    /// partner; <paramref name="planEvent"/> is the recorded plan reason, with
    /// or without its planner prefix. A risk, close-now or scope route is false.
    /// </summary>
    public static bool PartnerMissingRoute(string? planEvent)
        => planEvent is not null
           && (planEvent.EndsWith(BelowMinimumReason, StringComparison.Ordinal)
               || planEvent.EndsWith(ThresholdNoPartnerReason, StringComparison.Ordinal));

    /// <summary>
    /// AGT-3856: a per-card green suite is refused for a card that a batch
    /// gates anyway. A card in a formed or running batch is already a partner.
    /// A card that took the per-task route only for lack of a partner
    /// (<paramref name="facts"/>.<see cref="PerTaskSuiteFacts.PartnerMissingRoute"/>)
    /// is refused once partners can close a batch with it: eligible cards
    /// now, or cards that finish their pre-check before this card's deadline.
    /// A high-risk card, an operator close-now and a card refused before keep
    /// their per-task suite. Returns the refusal reason, or null.
    /// </summary>
    public static string? PerTaskSuiteRefusal(PerTaskSuiteFacts facts)
    {
        if (facts.BatchId is { } batchId)
            return $"already a member of batch {Short(batchId)}: the batch gate tests it";
        if (!facts.PartnerMissingRoute || facts.RefusedBefore || facts.MinimumSize <= 1) return null;
        if (facts.EligiblePartners + 1 >= facts.MinimumSize)
            return $"{facts.EligiblePartners} batch {(facts.EligiblePartners == 1 ? "partner waits" : "partners wait")}"
                + ": the next batch gates this card instead of a per-card suite";
        if (facts.NowUtc < facts.DeadlineUtc
            && facts.EligiblePartners + facts.QueuedPartners + 1 >= facts.MinimumSize)
            return $"{facts.QueuedPartners} batch {(facts.QueuedPartners == 1 ? "partner finishes its" : "partners finish their")}"
                + $" pre-check before this card's deadline at {facts.DeadlineUtc.UtcDateTime:HH:mm} UTC:"
                + " the next batch gates this card instead of a per-card suite";
        return null;
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>
    /// Plans one scope. The decision names the waiting cards it considered, so
    /// an authority that applies it later can tell which cards it did not know.
    /// </summary>
    public static BatchPlanDecision Plan(BatchPlanInput input)
        => PlanSnapshot(input) with
        {
            Considered = input.Waiting.Select(card => card.TaskKey).ToArray(),
        };

    private static BatchPlanDecision PlanSnapshot(BatchPlanInput input)
    {
        var waiting = input.Waiting.OrderBy(card => card.ReadyAtUtc)
            .ThenBy(card => card.TaskKey, StringComparer.Ordinal).ToArray();
        var upcoming = input.Upcoming.Select(card => new BatchPlanUpcomingView(
            card.TaskKey, card.Stage, card.EtaUtc, card.Risk,
            waiting.Where(ready => Overlaps(ready.Paths, card.Paths))
                .Select(ready => ready.TaskKey).ToArray())).ToArray();
        if (waiting.Length == 0)
            return input.QueuedPartners > 0
                ? new("wait", QueuedPartnersReason, null, [], 0, upcoming)
                : new("idle", "no eligible cards", null, [], 0, upcoming);

        var empirical = input.Calibration == "empirical"
            ? EmpiricalBatchPolicy.Resolve(input, waiting) : null;
        var riskLimit = empirical?.AdmissionLimit ?? HighRiskLimit(input.RiskPolicy);
        var ordered = Order(waiting, input.Overlap)
            .Where(card => card.Risk < riskLimit
                && (empirical is null || KnownDiff(card))).ToArray();
        var candidates = (input.Overlap == "separate"
                ? Separate(ordered) : ordered)
            .Take(input.MaximumSize).ToArray();
        // An ETA in the past is overdue, not arriving; waiting on it has no end.
        var arriving = input.Upcoming.Where(card => card.EtaUtc is { } eta
                && eta >= input.NowUtc
                && eta <= input.NowUtc.AddMinutes(input.LookAheadMinutes)
                && card.Risk < riskLimit
                && (empirical is null || card.Paths.Count > 0)
                && (input.Overlap != "separate" || candidates.All(ready =>
                    !Overlaps(ready.Paths, card.Paths))))
            .OrderBy(card => card.EtaUtc)
            .Select(card => new BatchPlanCard(card.TaskKey, card.EtaUtc!.Value,
                card.Risk, card.Paths, []));
        var target = empirical?.TargetSize ?? TargetSize(
            candidates.Concat(arriving).Take(input.MaximumSize).ToArray(),
            input.MinimumSize, input.MaximumSize, input.RiskPolicy);
        var oldest = waiting[0];
        var closeAt = oldest.ReadyAtUtc.AddMinutes(input.DeadlineMinutes);
        var urgent = waiting.Any(card => card.Tags.Any(tag => input.UrgentTags.Contains(tag,
            StringComparer.OrdinalIgnoreCase)));
        var highRisk = waiting[0].Risk >= riskLimit
            || empirical is not null && !KnownDiff(waiting[0]);
        var agedOut = AgedOut(oldest.ReadyAtUtc, input.NowUtc, input.DeadlineMinutes);
        // AGT-3219: behind a gate backlog a per-task gate only joins the slot
        // queue and can overrun its wait budget. A batch of the other cards
        // drains more of the queue per slot run, until the oldest card aged out.
        if (highRisk && input.GateBacklog && !agedOut && candidates.Length >= input.MinimumSize)
            return new("close", BacklogReason, null,
                candidates.Take(Math.Max(input.MinimumSize, target)).Select(card => card.TaskKey).ToArray(),
                target, upcoming);
        if (highRisk)
            return new("per-task", "oldest card exceeds batch risk limit", null,
                [oldest.TaskKey], 1, upcoming);
        var due = arriving.Any();
        if (!input.CloseNow && !urgent && input.NowUtc < closeAt
            && candidates.Length < target && due)
            return new("wait", "compatible card due inside look-ahead window",
                closeAt, candidates.Select(card => card.TaskKey).ToArray(), target, upcoming);
        if (!input.CloseNow && !urgent && input.NowUtc < closeAt
            && candidates.Length < target && !input.IdleGateHost)
            return new("wait", "waiting for target size or deadline",
                closeAt, candidates.Select(card => card.TaskKey).ToArray(), target, upcoming);
        if (candidates.Length < input.MinimumSize && input.GateBacklog && due && !agedOut && !input.CloseNow)
            return new("wait", "gate backlog: waiting for a batch partner instead of a per-task gate",
                arriving.First().ReadyAtUtc, candidates.Select(card => card.TaskKey).ToArray(), target, upcoming);
        // AGT-3466: a card past its deadline without an eligible partner waits
        // for the queued cards that only lack their pre-check; they reach the
        // batch at their own deadline at the latest. Before AGT-3466 each of
        // them took a per-task gate when it became eligible alone.
        if (candidates.Length < input.MinimumSize && !urgent && !input.CloseNow
            && candidates.Length + input.QueuedPartners >= input.MinimumSize)
            return new("wait", QueuedPartnersReason, null,
                candidates.Select(card => card.TaskKey).ToArray(), target, upcoming);
        if (candidates.Length < input.MinimumSize)
            return new("per-task", input.CloseNow ? CloseNowBelowMinimumReason : BelowMinimumReason,
                null, [oldest.TaskKey], input.MinimumSize, upcoming);
        var count = Math.Min(candidates.Length, Math.Max(input.MinimumSize, target));
        return new("close", input.CloseNow ? "operator close-now"
            : urgent ? "urgent tag" : input.IdleGateHost ? "idle gate host"
            : input.NowUtc >= closeAt ? "deadline reached" : "target size reached",
            null, candidates.Take(count).Select(card => card.TaskKey).ToArray(), target, upcoming);
    }

    public static double ExpectedRunsPerCard(IReadOnlyList<double> risks)
    {
        if (risks.Count == 0) return double.PositiveInfinity;
        var allPass = risks.Aggregate(1d, (product, risk) => product * (1d - Math.Clamp(risk, 0d, 1d)));
        return (1d + (1d - allPass) * (Math.Ceiling(Math.Log2(risks.Count)) + 1d)) / risks.Count;
    }

    private static int TargetSize(IReadOnlyList<BatchPlanCard> cards, int minimum,
        int maximum, string riskPolicy)
    {
        var limit = Math.Min(maximum, cards.Count);
        if (limit < minimum) return minimum;
        var best = minimum;
        var bestCost = double.PositiveInfinity;
        for (var size = minimum; size <= limit; size++)
        {
            var risks = cards.Take(size).Select(card => Math.Clamp(card.Risk
                * RiskMultiplier(riskPolicy), 0d, 1d)).ToArray();
            var cost = ExpectedRunsPerCard(risks);
            if (cost < bestCost) { best = size; bestCost = cost; }
        }
        return best;
    }

    private static BatchPlanCard[] Order(IReadOnlyList<BatchPlanCard> waiting, string overlap)
    {
        var chosen = new List<BatchPlanCard>();
        var remaining = waiting.ToList();
        while (remaining.Count > 0)
        {
            var next = overlap == "separate"
                ? remaining.FirstOrDefault(card => chosen.All(member =>
                    !Overlaps(member.Paths, card.Paths)))
                : chosen.Count == 0 ? null
                    : remaining.FirstOrDefault(card => Overlaps(chosen[^1].Paths, card.Paths));
            next ??= remaining[0];
            chosen.Add(next);
            remaining.Remove(next);
        }
        return chosen.ToArray();
    }

    private static BatchPlanCard[] Separate(IReadOnlyList<BatchPlanCard> ordered)
    {
        var chosen = new List<BatchPlanCard>();
        foreach (var card in ordered)
            if (chosen.All(member => !Overlaps(member.Paths, card.Paths)))
                chosen.Add(card);
        return chosen.ToArray();
    }

    private static bool Overlaps(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Any(path => right.Contains(path, StringComparer.OrdinalIgnoreCase));

    private static bool KnownDiff(BatchPlanCard card)
        => card.Paths.Count > 0 || card.Risk < BatchGateRiskModel.UnknownDiff;

    private static double HighRiskLimit(string policy) => policy switch
    {
        "conservative" => .35, "aggressive" => .75, _ => .55,
    };

    private static double RiskMultiplier(string policy) => policy switch
    {
        "conservative" => 1.35, "aggressive" => .75, _ => 1d,
    };
}

/// <summary>
/// AGT-3856: what the host knows about one card when its per-task suite would
/// start. <paramref name="BatchId"/> names a formed or running batch the card
/// is a member of. <paramref name="PartnerMissingRoute"/> is true when the
/// planner chose the per-task route only for lack of a batch partner.
/// <paramref name="EligiblePartners"/> counts other cards of the same scope a
/// batch could take now, <paramref name="QueuedPartners"/> the ones that still
/// wait for their pre-check. <paramref name="RefusedBefore"/> bounds the rule
/// to one refusal per review generation.
/// </summary>
public sealed record PerTaskSuiteFacts(
    string? BatchId, bool PartnerMissingRoute, bool RefusedBefore,
    int EligiblePartners, int QueuedPartners, int MinimumSize,
    DateTimeOffset NowUtc, DateTimeOffset DeadlineUtc);
