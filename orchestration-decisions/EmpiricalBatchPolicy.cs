using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Bounded batch-level learning. The legacy card score is only an ordering
/// signal; observed candidate verdicts supply the red frequency.
/// </summary>
public static class EmpiricalBatchPolicy
{
    public sealed record Limits(int TargetSize, double AdmissionLimit);

    public static Limits Resolve(BatchPlanInput input, IReadOnlyList<BatchPlanCard> cards)
    {
        var prior = new Limits(Math.Clamp(4, input.MinimumSize, input.MaximumSize),
            input.RiskPolicy switch
            {
                "conservative" => .35, "aggressive" => .75, _ => .55,
            });
        var observations = (input.Observations ?? [])
            .Where(row => row.Size is >= 2 and <= 8 && row.MaxRisk is >= 0 and <= 1
                && row.Classification is "pass" or "flaky-quarantined-pass" or "deterministic-suite-red")
            .OrderBy(row => row.AtUtc).ToArray();
        if (observations.Length == 0) return prior;

        var areas = cards.SelectMany(card => card.Paths)
            .Select(path => path.Split('/', 2)[0].ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var overlap = cards.Any(card => card.Paths.Count == 0) ? "unknown"
            : cards.SelectMany(card => card.Paths)
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1) ? "shared-path" : "disjoint";
        var current = prior;
        var first = DateOnly.FromDateTime(observations[0].AtUtc.UtcDateTime);
        var last = DateOnly.FromDateTime(input.NowUtc.UtcDateTime);
        // One change per UTC day. Today's outcomes cannot move today's rule.
        for (var day = first.AddDays(1); day <= last; day = day.AddDays(1))
        {
            if (!observations.Any(row =>
                    DateOnly.FromDateTime(row.AtUtc.UtcDateTime) == day.AddDays(-1)))
                continue;
            var history = observations.Where(row => row.AtUtc.UtcDateTime.Date
                < day.ToDateTime(TimeOnly.MinValue)).ToArray();
            if (history.Length < 12) continue;
            var matched = history.Where(row => row.OverlapGroup == overlap
                && (areas.Count == 0 || row.Areas.Any(areas.Contains))).ToArray();
            var sample = matched.Length >= 8 ? matched : history;
            var target = current.TargetSize;
            var next = sample.Where(row => row.Size == target + 1).ToArray();
            var atTarget = sample.Where(row => row.Size == target).ToArray();
            if (atTarget.Length >= 6 && RedRate(atTarget, target) > .30)
                target = Math.Max(input.MinimumSize, target - 1);
            else if (target < input.MaximumSize && next.Length >= 6
                && RedRate(next, target + 1) <= .20
                && IsolationPerCard(next, target + 1) <= IsolationPerCard(atTarget, target))
                target++;

            var near = sample.Where(row => row.MaxRisk >= current.AdmissionLimit - .10
                && row.MaxRisk < current.AdmissionLimit).ToArray();
            var limit = current.AdmissionLimit;
            if (near.Length >= 8 && RedRate(near, target) > .30)
                limit -= .05;
            else if (near.Length >= 8 && sample.Length >= 20
                && RedRate(near, target) <= .15)
                limit += .05;
            current = new(target, Math.Round(Math.Clamp(limit, .25, .80), 2));
        }
        return current;
    }

    public static double RedRate(IReadOnlyList<BatchPlanObservation> rows, int size)
        => (rows.Count(row => row.Red) + 12d * (.08 + .03 * Math.Clamp(size, 2, 8)))
            / (rows.Count + 12d);

    private static double IsolationPerCard(IReadOnlyList<BatchPlanObservation> rows, int size)
    {
        var red = RedRate(rows, size);
        var observedReds = rows.Where(row => row.Red && row.IsolationMinutes > 0).ToArray();
        // Twenty candidate minutes and sixty diagnostic minutes are the
        // conservative cost prior until linked isolation receipts exist.
        var isolationMinutes = observedReds.Length == 0 ? 60d
            : observedReds.Average(row => row.IsolationMinutes);
        return (20 + red * isolationMinutes) / (size * (1 - red));
    }
}
