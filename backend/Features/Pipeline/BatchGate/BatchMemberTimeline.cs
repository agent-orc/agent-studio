namespace AgentStudio.Pipeline;

/// <summary>UTC boundaries reconstructed from durable review, gate and publication facts.</summary>
public sealed record BatchMemberTimeline(
    DateTimeOffset ReviewPassedAtUtc, DateTimeOffset? PreCheckQueuedAtUtc,
    DateTimeOffset? PreCheckStartedAtUtc, DateTimeOffset? PreCheckFinishedAtUtc,
    DateTimeOffset? JoinedBatchAtUtc, DateTimeOffset? BatchStartedAtUtc,
    DateTimeOffset? BatchFinishedAtUtc, DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? TerminalAtUtc, string Outcome, bool DiagnosticRerun);

public sealed record BatchPhasePercentiles(double? P50, double? P90, int Cards);
public sealed record BatchPreCheckYield(int Runs, int Red, int CardsKeptOut, double CostMinutes);

public static class BatchMemberTimelineProjection
{
    public static IEnumerable<DateOnly> Days(DateTimeOffset first, DateTimeOffset last)
    {
        var end = DateOnly.FromDateTime(last.UtcDateTime);
        var start = DateOnly.FromDateTime(first.UtcDateTime);
        var count = Math.Clamp(end.DayNumber - start.DayNumber + 1, 1, GateRunLedger.RetentionDays);
        return Enumerable.Range(0, count).Select(offset => end.AddDays(offset - count + 1));
    }

    public static IReadOnlyList<GateRunLedgerRow> Rows(GateRunLedger? ledger, string project,
        IEnumerable<DateOnly>? days = null)
        => ledger is null ? [] : days is null ? ledger.ReadAll(project)
            : days.Distinct().SelectMany(day => ledger.ReadAll(project, day)).ToArray();

    public static BatchMemberTimeline Build(BatchGateStore store, BatchGateSubject subject,
        BatchGateManifest? manifest, IReadOnlyList<GateRunLedgerRow> rows,
        IReadOnlyList<BatchGatePreCheckVerdict> checks,
        IReadOnlyList<BatchGateFallbackCause> causes,
        IReadOnlyList<BatchGatePendingRecord>? allPending = null)
    {
        var pending = (allPending ?? store.ListAllPending()).Where(item =>
                item.Subject.Project == subject.Project && item.Subject.TaskKey == subject.TaskKey
                && item.Subject.RunAttempt == subject.RunAttempt
                && item.Subject.ResultSha == subject.ResultSha)
            .OrderByDescending(item => item.EnqueuedAtUtc).FirstOrDefault();
        var check = checks.Where(item => item.TaskKey == subject.TaskKey
                && item.ResultSha == subject.ResultSha
                && (pending is null || item.ReviewAttemptId == pending.ReviewAttemptId))
            .OrderByDescending(item => item.CompletedAtUtc).FirstOrDefault();
        var checkRow = rows.Where(item => item.Type == "batch-pre-check"
                && item.TriggerEvent == "pending:" + (check?.ReviewAttemptId ?? pending?.ReviewAttemptId))
            .OrderByDescending(item => item.FinishedAtUtc).FirstOrDefault();
        var fallback = causes.LastOrDefault(item => item.TaskKey == subject.TaskKey
            && item.RunAttempt == subject.RunAttempt
            && (pending is null || item.ReviewAttemptId == pending.ReviewAttemptId));
        var publication = manifest is null ? null : store.ReadPublication(manifest.BatchId);
        var runs = manifest is null ? [] : store.ListRuns(manifest.BatchId)
            .OrderBy(item => item.StartedAtUtc).ToArray();
        var run = publication is null ? runs.LastOrDefault()
            : runs.FirstOrDefault(item => item.BatchRunId == publication.BatchRunId);
        var verdict = run is null ? null : store.ReadVerdict(run.BatchId, run.BatchRunId);
        var runRow = run is null ? null : rows.Where(item => item.Type == "batch-gate"
                && item.BatchId == run.BatchId && item.RunId == run.BatchRunId)
            .OrderByDescending(item => item.RequestedAtUtc).FirstOrDefault();
        var replay = manifest is null ? null : store.TryReadReplay(manifest.BatchId, subject.TaskKey);
        var released = publication is not null && run is not null
            && store.TryReadMember(manifest!.BatchId, subject.TaskKey, run.BatchRunId) is not null;
        var outcome = released ? "published"
            : replay is { Outcome: "unattributable" or "conflict" } ? "ejected"
            : fallback is not null ? "fallback"
            : check?.Outcome == "red" && pending is not null
                && store.ReadPendingOutcome(pending.ReviewAttemptId) == "pre-check-red" ? "pre-check-red"
            : verdict?.Outcome == "fail" ? "red" : "pending";
        var terminal = outcome switch
        {
            "published" => publication!.VerifiedAtUtc,
            "ejected" => replay!.RecordedAtUtc,
            "fallback" => fallback!.RecordedAtUtc,
            "pre-check-red" => check!.CompletedAtUtc,
            "red" => verdict!.CompletedAtUtc,
            _ => (DateTimeOffset?)null,
        };
        return new BatchMemberTimeline(subject.ReviewCompletedAtUtc,
            checkRow?.RequestedAtUtc, checkRow?.StartedAtUtc ?? check?.StartedAtUtc,
            check?.CompletedAtUtc ?? checkRow?.FinishedAtUtc,
            manifest?.ClosedAtUtc, runRow?.StartedAtUtc ?? run?.StartedAtUtc,
            verdict?.CompletedAtUtc,
            released ? publication!.VerifiedAtUtc : null, terminal, outcome,
            rows.Any(item => item.Type == "clean-diagnostic-rerun"
                && item.Cards.Contains(subject.TaskKey)
                && item.RequestedAtUtc >= subject.ReviewCompletedAtUtc
                && (publication is null || item.RequestedAtUtc <= publication.VerifiedAtUtc)));
    }

    public static IReadOnlyDictionary<string, BatchPhasePercentiles> Phases(
        IEnumerable<BatchMemberTimeline> timelines)
    {
        var completed = timelines.Where(item => item.Outcome == "published").ToArray();
        static double? Minutes(DateTimeOffset? start, DateTimeOffset? end)
            => start is { } a && end is { } b && b >= a ? (b - a).TotalMinutes : null;
        return new Dictionary<string, BatchPhasePercentiles>
        {
            ["reviewToPreCheck"] = Percentiles(completed.Select(item => Minutes(item.ReviewPassedAtUtc, item.PreCheckFinishedAtUtc))),
            ["formationWait"] = Percentiles(completed.Select(item => Minutes(item.PreCheckFinishedAtUtc, item.BatchStartedAtUtc))),
            ["batchRun"] = Percentiles(completed.Select(item => Minutes(item.BatchStartedAtUtc, item.BatchFinishedAtUtc))),
            ["publish"] = Percentiles(completed.Select(item => Minutes(item.BatchFinishedAtUtc, item.PublishedAtUtc))),
        };
    }

    private static BatchPhasePercentiles Percentiles(IEnumerable<double?> samples)
    {
        var values = samples.Where(value => value is not null).Select(value => value!.Value).Order().ToArray();
        if (values.Length == 0) return new(null, null, 0);
        double median = values.Length % 2 == 1 ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        return new(median, values[(int)Math.Ceiling(values.Length * .9) - 1], values.Length);
    }
}
