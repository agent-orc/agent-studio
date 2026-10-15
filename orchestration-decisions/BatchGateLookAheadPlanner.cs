using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Pure batch membership and wait decision. Risk is the chance a card adds a red gate.</summary>
public static class BatchGateLookAheadPlanner
{
    public static BatchPlanDecision Plan(BatchPlanInput input)
    {
        var waiting = input.Waiting.OrderBy(card => card.ReadyAtUtc)
            .ThenBy(card => card.TaskKey, StringComparer.Ordinal).ToArray();
        var upcoming = input.Upcoming.Select(card => new BatchPlanUpcomingView(
            card.TaskKey, card.Stage, card.EtaUtc, card.Risk,
            waiting.Where(ready => Overlaps(ready.Paths, card.Paths))
                .Select(ready => ready.TaskKey).ToArray())).ToArray();
        if (waiting.Length == 0)
            return new("idle", "no eligible cards", null, [], 0, upcoming);

        var riskLimit = HighRiskLimit(input.RiskPolicy);
        var ordered = Order(waiting, input.Overlap)
            .Where(card => card.Risk < riskLimit).ToArray();
        var candidates = (input.Overlap == "separate"
                ? Separate(ordered) : ordered)
            .Take(input.MaximumSize).ToArray();
        var arriving = input.Upcoming.Where(card => card.EtaUtc is { } eta
                && eta <= input.NowUtc.AddMinutes(input.LookAheadMinutes)
                && card.Risk < riskLimit
                && (input.Overlap != "separate" || candidates.All(ready =>
                    !Overlaps(ready.Paths, card.Paths))))
            .OrderBy(card => card.EtaUtc)
            .Select(card => new BatchPlanCard(card.TaskKey, card.EtaUtc!.Value,
                card.Risk, card.Paths, []));
        var target = TargetSize(candidates.Concat(arriving).Take(input.MaximumSize).ToArray(),
            input.MinimumSize, input.MaximumSize,
            input.RiskPolicy);
        var oldest = waiting[0];
        var closeAt = oldest.ReadyAtUtc.AddMinutes(input.DeadlineMinutes);
        var urgent = waiting.Any(card => card.Tags.Any(tag => input.UrgentTags.Contains(tag,
            StringComparer.OrdinalIgnoreCase)));
        var highRisk = waiting[0].Risk >= riskLimit;
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
        if (candidates.Length < input.MinimumSize)
            return new("per-task", "close condition reached below minimum batch size",
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

    private static double HighRiskLimit(string policy) => policy switch
    {
        "conservative" => .35, "aggressive" => .75, _ => .55,
    };

    private static double RiskMultiplier(string policy) => policy switch
    {
        "conservative" => 1.35, "aggressive" => .75, _ => 1d,
    };
}
