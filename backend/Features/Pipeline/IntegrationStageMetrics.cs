namespace AgentStudio.Pipeline;

public sealed record IntegrationTimingRecord(string Project, string JobId, long Sequence,
    DateTimeOffset CompletedAtUtc, string? Sha, string? Outcome, int GateCycles,
    IReadOnlyList<string> SecondCycleReasons, IReadOnlyDictionary<string, long?> StagesMs, bool HasIncompleteGateTimings = false);

public sealed record IntegrationStageSummary(string Stage, double? MedianMs, double? P90Ms, int Samples);

/// <summary>Last 50 terminal integrations, retaining unknown legacy measurements as null.</summary>
public static class IntegrationStageMetrics
{
    public static readonly string[] Stages = ["queueWait", "dispatchQueueWait", "gateQueueWait", "bundlePreparation", "bundleTransfer",
        "sourceLockWait", "sourceObjects", "sourceCheckout", "restore", "build", "releaseBuildCheck",
        "frontendBuild", "verdictHandBack", "merge", "push", "reviewToPushed"];

    public static IReadOnlyList<IntegrationTimingRecord> Recent(IEnumerable<IntegrationLaneEvent> events)
        => events.GroupBy(row => (row.Project, row.JobId, row.Sequence))
            .Select(group => Record(group.OrderBy(row => row.AtUtc).ToArray()))
            .OfType<IntegrationTimingRecord>().OrderByDescending(row => row.CompletedAtUtc).Take(50).ToArray();

    public static IReadOnlyList<IntegrationStageSummary> Summarize(IReadOnlyList<IntegrationTimingRecord> records)
        => Stages.Select(stage =>
        {
            var values = records.Select(row => row.StagesMs.GetValueOrDefault(stage))
                .OfType<long>().Order().ToArray();
            double? At(double percentile) => PipelineBaselineMetrics.Percentile(values.Select(value => (double)value), percentile);
            return new IntegrationStageSummary(stage, At(.5), At(.9), values.Length);
        }).ToArray();

    private static IntegrationTimingRecord? Record(IntegrationLaneEvent[] rows)
    {
        var terminal = rows.LastOrDefault(row => row.Kind is "published" or "ejected");
        if (terminal is null) return null;
        var stages = Stages.ToDictionary(stage => stage, _ => (long?)null);
        foreach (var row in rows.Where(row => row.AtUtc <= terminal.AtUtc && row.StageDurationsMs is not null))
            foreach (var (stage, duration) in row.StageDurationsMs!)
                if (duration >= 0) stages[stage] = stages.GetValueOrDefault(stage).GetValueOrDefault() + duration;
        var enqueued = rows.FirstOrDefault(row => row.Kind is "enqueued" or "agent-round-re-entry" or "requeued");
        DateTimeOffset? queuedAt = null;
        long queueMs = 0;
        var measuredQueue = false;
        foreach (var row in rows)
        {
            if (row.Kind is "enqueued" or "agent-round-re-entry" or "requeued" or "held") queuedAt = row.AtUtc;
            else if (row.Kind == "dequeued" && queuedAt is { } queued && row.AtUtc >= queued)
            {
                queueMs += (long)(row.AtUtc - queued).TotalMilliseconds;
                measuredQueue = true;
                queuedAt = null;
            }
        }
        if (measuredQueue) stages["queueWait"] = queueMs;
        if (terminal.Kind == "published" && enqueued?.ReviewPassedAtUtc is { } reviewed && terminal.AtUtc >= reviewed)
            stages["reviewToPushed"] = (long)(terminal.AtUtc - reviewed).TotalMilliseconds;
        var gates = Math.Max(rows.Count(row => row.Kind == "gate-requested"),
            rows.Where(row => row.Kind == "gate-finished").Sum(row => row.GateAttempts ?? 1));
        var incomplete = rows.Any(row => row.StageTimingsIncomplete);
        if (incomplete)
            foreach (var stage in GateStageTimings.GateStages) stages[stage] = null;
        var reasons = rows.Where(row => row.Kind is "integration-cycle" or "gate-capacity-retry")
            .Select(row => row.Reason).OfType<string>()
            .Concat(rows.SelectMany(row => row.GateRetryReasons ?? [])).Distinct().ToArray();
        if (gates > 1 && reasons.Length == 0)
            reasons = ["Multiple gate requests recorded; this legacy record does not state why."];
        return new(terminal.Project, terminal.JobId, terminal.Sequence, terminal.AtUtc, terminal.Receipt,
            terminal.Outcome, gates, reasons, stages, incomplete);
    }
}
