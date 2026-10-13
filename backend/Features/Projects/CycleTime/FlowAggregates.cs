namespace AgentStudio.Projects;

/// <summary>Project rollups over the exact per-card episode partition.</summary>
public static class FlowAggregates
{
    public static IReadOnlyList<CycleTimeStageAggregate> BuildTotals(IReadOnlyList<TaskCycleTime> rows)
    {
        var episodes = rows.Where(r => r.Flow is not null).Select(r => r.Flow!).ToList();
        return new[]
        {
            Total("machineCycle", episodes.Select(e => e.MachineCycleSeconds)),
            Total("active", episodes.Select(e => e.ActiveSeconds)),
            Total("resourceWait", episodes.Select(e => e.ResourceWaitSeconds)),
            Total("systemHold", episodes.Select(e => e.SystemHoldSeconds)),
            Total("humanWait", episodes.Select(e => e.HumanWaitSeconds)),
            Total("dependencyWait", episodes.Select(e => e.DependencyWaitSeconds)),
            Total("episode", episodes.Select(e => e.EpisodeSeconds)),
            Total("tokensPerCard", episodes.Select(e => (double)e.Tokens), "count"),
            Total("costPerCardUsd", episodes.Select(e => (double)e.CostUsd), "count"),
        };
    }

    public static IReadOnlyList<FlowDimensionAggregate> BuildDimensions(IReadOnlyList<TaskCycleTime> rows, string @class)
    {
        var machineTotal = rows.Sum(r => r.MachineCycleSeconds ?? 0);
        return rows.Where(r => r.Flow is not null)
            .SelectMany(r => r.Flow!.Segments.Select(s => (r.TaskId, Segment: s)))
            .Where(x => x.Segment.Class == @class)
            .GroupBy(x => @class == "active" ? x.Segment.Step : @class == "resourceWait"
                ? x.Segment.Resource : x.Segment.Kind, StringComparer.Ordinal)
            .Where(g => g.Key is not null)
            .Select(g =>
            {
                var cards = g.GroupBy(x => x.TaskId).ToList();
                var totals = cards.Select(c => c.Sum(x => x.Segment.Seconds));
                var duration = Total(g.Key!, totals);
                var costs = cards.Select(c => (double)c.Sum(x => x.Segment.CostUsd))
                    .OrderBy(v => v).ToList();
                return new FlowDimensionAggregate(g.Key!, Label(g.Key!), @class, duration,
                    g.Sum(x => x.Segment.Tokens), g.Sum(x => x.Segment.CostUsd),
                    g.Sum(x => x.Segment.UnpricedCalls),
                    costs.Count == 0 ? null : Math.Round(CycleTimeStatistics.Median(costs), 2),
                    @class == "resourceWait" && machineTotal > 0
                        ? Math.Round(duration.Total / machineTotal, 4) : null,
                    @class is "systemHold" or "humanWait")
                {
                    InputTokens = g.Sum(x => x.Segment.InputTokens),
                    OutputTokens = g.Sum(x => x.Segment.OutputTokens),
                    CacheReadTokens = g.Sum(x => x.Segment.CacheReadTokens),
                    CacheCreationTokens = g.Sum(x => x.Segment.CacheCreationTokens),
                };
            })
            .OrderByDescending(g => g.Duration.Total)
            .ToList();
    }

    public static IReadOnlyList<CycleTimeThroughputDay> BuildThroughputDays(
        IReadOnlyList<TaskCycleAnalysis> analyses, DateTime now, DateTime? since)
    {
        var firstDay = (since ?? now.AddDays(-29)).Date;
        var lastDay = now.Date;
        var rows = analyses.Where(a => a.Row is not null).Select(a => a.Row!).ToList();
        var days = new List<CycleTimeThroughputDay>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            var next = day.AddDays(1);
            bool Within(DateTime t) => t >= day && t < next && t <= now && (since is null || t >= since);
            days.Add(new CycleTimeThroughputDay(day,
                analyses.Sum(a => a.Deliveries.Count(Within) > 0 || a.Row is null
                    ? a.Deliveries.Count(Within)
                    : a.Row.Transitions?.Count(t => t.From == TaskStates.Progress
                        && t.To == TaskStates.AutoReview && t.Cause == "delivered" && Within(t.At)) ?? 0),
                rows.Count(r => r.MachineDoneAt is DateTime t && Within(t)),
                rows.Count(r => r.CompletionSource == TaskCycleTimeAnalyzer.LedgerCompletionSource
                    && Within(r.CompletedAt)),
                rows.Sum(r => r.ReAcceptanceArtefactTimes.Count(Within))));
        }
        return days;
    }

    private static CycleTimeStageAggregate Total(string id, IEnumerable<double> values, string unit = "seconds")
    {
        var aggregate = CycleTimeStatistics.Aggregate(id, "flow", unit, values);
        return aggregate with { Label = Label(id) };
    }

    private static string Label(string id) => id switch
    {
        "machineCycle" => "Machine cycle",
        "resourceWait" => "Resource wait",
        "systemHold" => "System hold",
        "humanWait" => "Human wait",
        "dependencyWait" => "Dependency wait",
        "episode" => "Episode",
        "tokensPerCard" => "Tokens per card",
        "costPerCardUsd" => "Cost per card (USD)",
        "codingSlot" => "Coding slot",
        "reviewSlot" => "Review slot",
        "gateHost" => "Gate host",
        "integrationLane" => "Integration lane",
        "reviewGate" => "Review gate",
        "reviewAspects" => "Review aspects",
        "batchGate" => "Batch gate",
        "codingNoDelivery" => "Coding without delivery",
        "reviewFailedAttempt" => "Failed review attempt",
        _ => id,
    };
}
