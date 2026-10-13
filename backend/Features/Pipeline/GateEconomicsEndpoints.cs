using AgentStudio.Tasks;
using AgentStudio.Registry;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record GateEconomicsRow(
    string Project, int GatedMerges, int GateGreen, int GateRed, int GateCancelled,
    double? GateMinutesMedian, double? GateMinutesP90,
    double? QueueWaitP90Ms, double? ExactReuseRate, double? DisjointReuseRate,
    double? FullSuiteShare, double? SameShaRetestRate, string? LastNightlyGuard,
    double? DeliveryLagP50Minutes, double? DeliveryLagP95Minutes,
    double? OldestWaitingMinutes, double? PreparationP50Minutes,
    int Queued, int Prepared, int Gating,
    int ReadyToPublish, double? LaneGateDurationP50Minutes, double? LaneGateDurationP95Minutes,
    int LaneGateGreen, int LaneGateRed, int LaneGateCancelled,
    double? GateTokenWaitP50Ms, double? GateTokenWaitP95Ms, string? GateHoldingProject,
    double? MergeLockWaitP95Ms, double? PublicationLockWaitP95Ms,
    int EjectedConflict, int EjectedAgentRound, int EjectedRed, int EjectedError,
    double ArrivalsPerHour, double MergesPerHour,
    string? ProjectShortCode = null, string? ProjectColor = null);

public sealed record GateEconomicsResponse(string Window, DateTimeOffset AsOfUtc,
    IReadOnlyList<GateEconomicsRow> Rows);

public static class GateEconomicsEndpoints
{
    public static void MapGateEconomicsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/gate-economics", (string? window,
            TaskScannerService scanner, ProjectRegistry registry, BatchGateStore batches) =>
        {
            if (window is not (null or "7d" or "30d"))
                return Results.BadRequest(new { error = "window must be 7d or 30d" });
            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(window == "30d" ? -30 : -7);
            var jobs = scanner.ScanAllJobsWithArchive();
            var registered = registry.List().Where(project => !project.Archived).ToArray();
            var names = registered
                .Select(project => project.DisplayName)
                .Concat(jobs.Select(job => job.ProjectName))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            var cache = new GateResultCache();
            var projectNames = names.ToArray();
            var batchFacts = batches.ListAllExecutions()
                .Where(fact => fact.CompletedAtUtc >= since && fact.BatchRunId is not null)
                .ToArray();
            int Members(BatchGateExecutionFact fact)
            {
                try { return batches.ReadManifest(fact.BatchId!).Members.Count; }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    SilentCatch.Note(ex, "GateEconomics: batch manifest unavailable");
                    return 0;
                }
            }
            var batchSamples = batchFacts.Select(fact => new BatchGateMetric(
                fact.Project, Members(fact), fact)).ToArray();
            var rows = projectNames.Select(name => ProjectRow(name,
                jobs.Where(job => string.Equals(job.ProjectName, name,
                    StringComparison.OrdinalIgnoreCase)).Select(job => job.FolderPath),
                since, now, cache.Report(name), batchSamples.Where(sample =>
                    string.Equals(sample.Project, name, StringComparison.OrdinalIgnoreCase)),
                registered.FirstOrDefault(project => string.Equals(project.DisplayName, name,
                    StringComparison.OrdinalIgnoreCase))?.ShortCode,
                registered.FirstOrDefault(project => string.Equals(project.DisplayName, name,
                    StringComparison.OrdinalIgnoreCase))?.Color)).ToArray();
            var reports = projectNames.Select(cache.Report).ToArray();
            var allReport = new GateRetestReport(reports.Sum(report => report.Executed),
                reports.Sum(report => report.SameShaRetests),
                reports.Sum(report => report.Executed) == 0 ? 0
                    : (double)reports.Sum(report => report.SameShaRetests) / reports.Sum(report => report.Executed),
                reports.Sum(report => report.CacheHits), GateResultCache.RetestWindowSize);
            var all = ProjectRow("All projects", jobs
                    .Where(job => projectNames.Contains(job.ProjectName, StringComparer.OrdinalIgnoreCase))
                    .Select(job => job.FolderPath),
                since, now, allReport, batchSamples.Where(sample =>
                    projectNames.Contains(sample.Project, StringComparer.OrdinalIgnoreCase)));
            return Results.Ok(new GateEconomicsResponse(window ?? "7d", now, [all, .. rows]));
        });
    }

    internal static GateEconomicsRow ProjectRow(string name, IEnumerable<string> folders,
        DateTimeOffset since, DateTimeOffset now, GateRetestReport? retest,
        IEnumerable<BatchGateMetric>? batchMetrics = null,
        string? shortCode = null, string? color = null)
    {
        var receipts = new List<GateReceiptMetric>();
        var events = new List<IntegrationLaneEvent>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var post = Path.Combine(folder, "post-steps");
            if (Directory.Exists(post))
                foreach (var path in Directory.EnumerateFiles(post, "pre-develop-build-gate-*.log"))
                {
                    if (File.GetLastWriteTimeUtc(path) < since.UtcDateTime) continue;
                    try { receipts.Add(GateReceiptMetric.Read(path)); }
                    catch (IOException ex) { SilentCatch.Note(ex, "GateEconomics: receipt unavailable"); }
                }
            events.AddRange(IntegrationLaneEvents.Read(folder).Where(e => e.AtUtc >= now.AddHours(-24)));
        }
        var batches = (batchMetrics ?? []).Where(sample => sample.Members > 0).ToArray();
        var durations = receipts.Where(r => r.DurationMs is >= 0)
            .Select(r => r.DurationMs!.Value / 60000d)
            .Concat(batches.Where(sample => sample.Fact.DurationMs is >= 0)
                .SelectMany(sample => Enumerable.Repeat(
                    sample.Fact.DurationMs!.Value / 60000d / sample.Members,
                    sample.Members))).ToArray();
        var waits = receipts.Where(r => r.GateQueueWaitMs is >= 0 && r.MergeLockWaitMs is >= 0)
            .Select(r => (double)(r.GateQueueWaitMs!.Value + r.MergeLockWaitMs!.Value))
            .Concat(batches.Select(sample => (double)sample.Fact.QueueWaitMs)).ToArray();
        var knownRungs = receipts.Where(r => r.Rung is not null).ToList();
        knownRungs.AddRange(batches.SelectMany(sample => Enumerable.Repeat(
            new GateReceiptMetric(null, sample.Fact.QueueWaitMs, null, "full", "full", sample.Fact.Outcome), sample.Members)));
        var knownLevels = receipts.Where(r => r.TestLevel is not null and not "unknown").ToList();
        knownLevels.AddRange(batches.SelectMany(sample => Enumerable.Repeat(
            new GateReceiptMetric(null, sample.Fact.QueueWaitMs, null, "full", "full", sample.Fact.Outcome), sample.Members)));
        var enqueued = events.Where(e => e.Kind is "enqueued" or "agent-round-re-entry").ToArray();
        var published = events.Where(e => e.Kind == "published").ToArray();
        var lags = (from arrival in enqueued
            join publication in published on (arrival.Project, arrival.JobId, arrival.Sequence)
                equals (publication.Project, publication.JobId, publication.Sequence)
            where arrival.DeliveredAtUtc is not null
            select (publication.AtUtc - arrival.DeliveredAtUtc.GetValueOrDefault()).TotalMinutes).ToArray();
        var lastByDelivery = events.GroupBy(e => (e.Project, e.JobId, e.Sequence))
            .Select(group => group.Where(e => e.Kind is "published" or "ejected")
                .OrderBy(e => e.AtUtc).LastOrDefault()
                ?? group.OrderBy(e => e.AtUtc).Last()).ToArray();
        var waiting = lastByDelivery.Where(e => e.Kind is not ("published" or "ejected")).ToArray();
        var oldest = waiting.Select(e => events.FirstOrDefault(a => a.Project == e.Project
            && a.JobId == e.JobId && a.Sequence == e.Sequence
            && a.Kind is "enqueued" or "agent-round-re-entry")?.DeliveredAtUtc)
            .Where(t => t is not null).Select(t => (now - t!.Value).TotalMinutes).ToArray();
        var tokenWaits = events.Where(e => e.Kind == "gate-finished" && e.QueueWaitMs is >= 0)
            .Select(e => (double)e.QueueWaitMs!.Value).ToArray();
        var laneGates = events.Where(e => e.Kind == "gate-finished").ToArray();
        var laneDurations = laneGates.Where(e => e.DurationMs is >= 0)
            .Select(e => e.DurationMs!.Value / 60000d).ToArray();
        var lockWaits = events.Where(e => e.Kind == "merge-lock-acquired" && e.MergeLockWaitMs is >= 0)
            .Select(e => (double)e.MergeLockWaitMs!.Value).ToArray();
        var publicationWaits = events.Where(e => e.Kind == "publication-lock-acquired"
            && e.PublicationLockWaitMs is >= 0)
            .Select(e => (double)e.PublicationLockWaitMs!.Value).ToArray();
        var ejections = events.Where(e => e.Kind == "ejected").ToArray();
        var preparation = (from start in events.Where(e => e.Kind == "dequeued")
            join gate in events.Where(e => e.Kind == "gate-requested")
                on (start.Project, start.JobId, start.Sequence)
                equals (gate.Project, gate.JobId, gate.Sequence)
            let lockWait = events.FirstOrDefault(e => e.Kind == "merge-lock-acquired"
                && e.Project == start.Project && e.JobId == start.JobId
                && e.Sequence == start.Sequence)?.MergeLockWaitMs
            where lockWait is not null
            select Math.Max(0, (gate.AtUtc - start.AtUtc).TotalMinutes
                - lockWait.GetValueOrDefault() / 60000d)).ToArray();
        var outcomes = receipts.Concat(batches.SelectMany(sample => Enumerable.Repeat(
            new GateReceiptMetric(null, sample.Fact.QueueWaitMs, null, "full", "full", sample.Fact.Outcome),
            sample.Members))).ToArray();
        return new GateEconomicsRow(name, receipts.Count + batches.Sum(sample => sample.Members),
            outcomes.Count(sample => sample.Verdict is "Ok" or "Warn"),
            outcomes.Count(sample => sample.Verdict == "Fail"),
            outcomes.Count(sample => sample.Verdict is "Cancelled" or "Canceled"),
            Percentile(durations, .5),
            Percentile(durations, .9), Percentile(waits, .9),
            Rate(knownRungs.ToArray(), r => r.Rung == "exact"),
            Rate(knownRungs.ToArray(), r => r.Rung == "disjoint"),
            Rate(knownLevels.ToArray(), r => r.TestLevel == "full"),
            retest is { Executed: > 0 } ? retest.SameShaRetestRate : null,
            null, Percentile(lags, .5), Percentile(lags, .95),
            oldest.Length == 0 ? null : oldest.Max(), Percentile(preparation, .5),
            waiting.Count(e => e.Kind is "enqueued" or "agent-round-re-entry"),
            waiting.Count(e => e.Kind is "dequeued" or "merge-lock-acquired" or "prepared"),
            waiting.Count(e => e.Kind is "gate-requested" or "gate-token-waiting" or "gate-token-acquired"),
            waiting.Count(e => e.Kind is "gate-finished" or "ready-to-publish" or "publication-lock-acquired"),
            Percentile(laneDurations, .5), Percentile(laneDurations, .95),
            laneGates.Count(e => e.Outcome is "Ok" or "Warn"),
            laneGates.Count(e => e.Outcome == "Fail"),
            laneGates.Count(e => e.Outcome is "Cancelled" or "Canceled"),
            Percentile(tokenWaits, .5), Percentile(tokenWaits, .95),
            waiting.Where(e => e.Kind == "gate-token-waiting")
                .OrderByDescending(e => e.AtUtc).FirstOrDefault()?.HoldingProject,
            Percentile(lockWaits, .95), Percentile(publicationWaits, .95),
            ejections.Count(e => e.Outcome == "Conflict"),
            ejections.Count(e => e.Outcome == "AgentRoundRequired"),
            ejections.Count(e => e.Outcome == "GateFailed"),
            ejections.Count(e => e.Outcome is not ("Conflict" or "AgentRoundRequired" or "GateFailed")),
            enqueued.Length / 24d, published.Length / 24d, shortCode, color);
    }

    private static double? Rate<T>(T[] rows, Func<T, bool> predicate)
        => rows.Length == 0 ? null : (double)rows.Count(predicate) / rows.Length;

    private static double? Percentile(IEnumerable<double> numbers, double fraction)
    {
        var values = numbers.Order().ToArray();
        if (values.Length == 0) return null;
        var position = (values.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }
}

internal sealed record BatchGateMetric(string Project, int Members, BatchGateExecutionFact Fact);

internal sealed record GateReceiptMetric(long? DurationMs, long? GateQueueWaitMs,
    long? MergeLockWaitMs, string? Rung, string? TestLevel,
    string? Verdict)
{
    public static GateReceiptMetric Read(string path)
    {
        using var reader = new StreamReader(path);
        var first = reader.ReadLine() ?? "";
        _ = reader.ReadLine();
        _ = reader.ReadLine();
        long? duration = TokenLong(first, "durationMs=");
        long? wait = null;
        long? mergeWait = null;
        string? rung = null;
        string? testLevel = null;
        for (var i = 0; i < 16 && reader.ReadLine() is { } line; i++)
        {
            if (line.StartsWith("---", StringComparison.Ordinal)) break;
            wait ??= TokenLong(line, "gateQueueWaitMs=");
            mergeWait ??= TokenLong(line, "mergeLockWaitMs=");
            rung ??= Token(line, "gateRung=");
            testLevel ??= Token(line, "testLevel=");
        }
        testLevel ??= LegacyTestLevel(path);
        return new GateReceiptMetric(duration, wait, mergeWait, rung, testLevel,
            Token(first, "verdict="));
    }

    private static string? LegacyTestLevel(string path)
    {
        using var reader = new StreamReader(path);
        var found = false;
        var json = new System.Text.StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line == "--- test-selection.json ---")
            {
                found = true;
                continue;
            }
            if (!found) continue;
            if (line.StartsWith("--- ", StringComparison.Ordinal)) break;
            json.AppendLine(line);
        }
        if (!found) return null;
        try
        {
            using var document = JsonDocument.Parse(json.ToString());
            return document.RootElement.TryGetProperty("Level", out var level)
                ? level.GetString() : null;
        }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "GateEconomics: legacy selection audit unavailable");
            return null;
        }
    }

    private static long? TokenLong(string line, string key)
        => long.TryParse(Token(line, key), out var value) ? value : null;

    private static string? Token(string line, string key)
    {
        var index = line.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return null;
        var start = index + key.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }
}
