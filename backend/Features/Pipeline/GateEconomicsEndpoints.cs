using AgentStudio.Tasks;
using AgentStudio.Registry;
using AgentStudio.Security;
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
    IReadOnlyList<GateEconomicsRow> Rows, int LaneWindowHours = GateEconomicsEndpoints.LaneWindowHours);

public static class GateEconomicsEndpoints
{
    public const string AllProjects = "All projects";
    /// <summary>The lane metrics always cover this window, whatever the gate window.</summary>
    public const int LaneWindowHours = 24;
    /// <summary>Label for a holding project the caller is not a member of.</summary>
    public const string OtherProject = "another project";

    public static void MapGateEconomicsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/gate-economics", (HttpContext context, string? window,
            GateEconomicsProjection projection, ProjectRegistry registry) =>
        {
            if (window is not (null or "7d" or "30d"))
                return Results.BadRequest(new { error = "window must be 7d or 30d" });
            // An unaddressed collection: a scoped account sees only its projects,
            // and the all-projects row aggregates exactly those rows.
            var response = projection.Read(window ?? "7d", AccessFilter(context, registry));
            return response is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(response);
        });
    }

    /// <summary>Null for an unrestricted caller; otherwise the project-membership test.</summary>
    public static Func<string, bool>? AccessFilter(HttpContext context, ProjectRegistry registry)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
           && !ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)
            ? project => ProjectAccessAuthorization.Allows(human.User, project, registry)
            : null;

    /// <summary>
    /// The visible rows for one caller: per-project rows plus one all-projects
    /// row over the same projects. A holding project outside the caller's
    /// membership is named only as <see cref="OtherProject"/>.
    /// </summary>
    internal static IReadOnlyList<GateEconomicsRow> Rows(
        IReadOnlyList<(string Name, IReadOnlyList<string> Folders, GateRetestReport Retest,
            string? ShortCode, string? Color)> projects,
        IReadOnlyList<BatchGateMetric> batchSamples,
        DateTimeOffset since, DateTimeOffset now, Func<string, bool>? allowed)
    {
        var names = projects.Select(project => project.Name).ToArray();
        IEnumerable<BatchGateMetric> For(string name) => batchSamples.Where(sample =>
            string.Equals(sample.Project, name, StringComparison.OrdinalIgnoreCase));
        var rows = projects.Select(project => ProjectRow(project.Name, project.Folders,
            since, now, project.Retest, For(project.Name), project.ShortCode, project.Color)).ToList();
        var executed = projects.Sum(project => project.Retest.Executed);
        var retests = projects.Sum(project => project.Retest.SameShaRetests);
        var allReport = new GateRetestReport(executed, retests,
            executed == 0 ? 0 : (double)retests / executed,
            projects.Sum(project => project.Retest.CacheHits), GateResultCache.RetestWindowSize);
        var all = ProjectRow(AllProjects, projects.SelectMany(project => project.Folders),
            since, now, allReport, batchSamples.Where(sample =>
                names.Contains(sample.Project, StringComparer.OrdinalIgnoreCase)));
        return [.. new[] { all }.Concat(rows).Select(row => row.GateHoldingProject is { } holder
            && allowed is not null && !allowed(holder)
                ? row with { GateHoldingProject = OtherProject } : row)];
    }

    internal static GateEconomicsRow ProjectRow(string name, IEnumerable<string> folders,
        DateTimeOffset since, DateTimeOffset now, GateRetestReport? retest,
        IEnumerable<BatchGateMetric>? batchMetrics = null,
        string? shortCode = null, string? color = null)
    {
        var receipts = new List<GateReceiptMetric>();
        var events = new List<IntegrationLaneEvent>();
        var integratedReceipts = new List<GateReceiptMetric>();
        var laneSince = now.AddHours(-LaneWindowHours);
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folderReceipts = new List<GateReceiptMetric>();
            var post = Path.Combine(folder, "post-steps");
            if (Directory.Exists(post))
                foreach (var path in Directory.EnumerateFiles(post, "pre-develop-build-gate-*.log"))
                {
                    // A receipt is written after its gate, so a file older than
                    // the window can be skipped unread. A newer file (clone,
                    // checkout, copy) is placed by its own recorded time.
                    var written = File.GetLastWriteTimeUtc(path);
                    if (written < since.UtcDateTime) continue;
                    try
                    {
                        var receipt = GateReceiptMetric.Read(path, written, since, now);
                        if (receipt.AtUtc >= since && receipt.AtUtc <= now)
                        {
                            receipts.Add(receipt);
                            folderReceipts.Add(receipt);
                        }
                    }
                    catch (IOException ex) { SilentCatch.Note(ex, "GateEconomics: receipt unavailable"); }
                }
            var folderEvents = IntegrationLaneEvents.Read(folder);
            events.AddRange(folderEvents.Where(e => e.AtUtc >= laneSince && e.AtUtc <= now));
            // The gate receipt proves verification; the published event proves
            // that the publication fence and push completed for that SHA.
            foreach (var publication in folderEvents.Where(e => e.Kind == "published"
                         && e.AtUtc >= since && e.AtUtc <= now
                         && !string.IsNullOrWhiteSpace(e.Receipt))
                         .GroupBy(e => (e.Project, e.JobId, e.Sequence))
                         .Select(group => group.OrderBy(e => e.AtUtc).Last()))
            {
                var final = folderReceipts.Where(r => string.Equals(r.TestedSha, publication.Receipt,
                            StringComparison.OrdinalIgnoreCase)
                        && r.AtUtc <= publication.AtUtc)
                    .OrderBy(r => r.AtUtc).ThenBy(r => r.ReceiptIndex).LastOrDefault();
                if (final?.Verdict is "Ok" or "Warn") integratedReceipts.Add(final);
            }
        }
        // Every batch attempt stays in the gate outcome counts. Published
        // members receive only the final successful attempt's minutes.
        var batches = (batchMetrics ?? []).ToArray();
        // Economics per merge uses the final gate that actually integrated the
        // delivery. Red attempts remain gate runs in the outcome counts below.
        var publishedBatches = batches.Where(sample => sample.Published)
            .GroupBy(sample => (sample.Fact.BatchId, sample.Fact.BatchRunId))
            .Select(run => run.OrderBy(sample => sample.Fact.CompletedAtUtc).Last())
            .Where(sample => sample.Fact.Outcome is "Ok" or "Warn").ToArray();
        var durations = integratedReceipts.Where(r => r.DurationMs is >= 0)
            .Select(r => r.DurationMs!.Value / 60000d)
            .Concat(publishedBatches.Where(sample => sample.Members > 0 && sample.Fact.DurationMs is >= 0)
                .SelectMany(sample => Enumerable.Repeat(
                    sample.Fact.DurationMs!.Value / 60000d / sample.Members,
                    sample.Members))).ToArray();
        var waits = receipts.Where(r => r.GateQueueWaitMs is >= 0 && r.MergeLockWaitMs is >= 0)
            .Select(r => (double)(r.GateQueueWaitMs!.Value + r.MergeLockWaitMs!.Value))
            .Concat(batches.Select(sample => (double)sample.Fact.QueueWaitMs)).ToArray();
        var batchRuns = batches.Select(sample => new GateReceiptMetric(
            sample.Fact.DurationMs, sample.Fact.QueueWaitMs, null, "full", "full",
            sample.Fact.Outcome, sample.Fact.CompletedAtUtc)).ToArray();
        // A gate that never ran (missing source) has no rung: unknown, not full.
        var knownIntegratedRungs = integratedReceipts.Where(r =>
            r.Rung is "exact" or "disjoint" or "impact" or "full").ToArray();
        var knownLevels = receipts.Where(r => r.TestLevel is not null and not "unknown")
            .Concat(batchRuns).ToArray();
        var outcomes = receipts.Where(r => r.Rung != "none").Concat(batchRuns).ToArray();
        // Infrastructure retries retain separate execution facts and gate
        // costs, but publication releases the members of a batch run once.
        var gatedMerges = integratedReceipts.Count + publishedBatches.Sum(sample => sample.Members);
        double? ReuseRate(string rung) => gatedMerges == 0 ||
            knownIntegratedRungs.Length == 0 && publishedBatches.Length == 0
                ? null : (double)knownIntegratedRungs.Count(receipt => receipt.Rung == rung) / gatedMerges;

        var enqueued = events.Where(e => e.Kind is "enqueued" or "agent-round-re-entry").ToArray();
        var published = events.Where(e => e.Kind == "published").ToArray();
        var lags = (from arrival in enqueued
            join publication in published on (arrival.Project, arrival.JobId, arrival.Sequence)
                equals (publication.Project, publication.JobId, publication.Sequence)
            where arrival.DeliveredAtUtc is not null
            select (publication.AtUtc - arrival.DeliveredAtUtc.GetValueOrDefault()).TotalMinutes).ToArray();
        var lastByDelivery = events.GroupBy(e => (e.Project, e.JobId, e.Sequence))
            .Select(group => group.Where(e => IsTerminal(e.Kind))
                .OrderBy(e => e.AtUtc).LastOrDefault()
                ?? group.OrderBy(e => e.AtUtc).Last()).ToArray();
        var waiting = lastByDelivery.Where(e => !IsTerminal(e.Kind)).ToArray();
        var oldest = waiting.Select(e => events.FirstOrDefault(a => a.Project == e.Project
            && a.JobId == e.JobId && a.Sequence == e.Sequence
            && a.Kind is "enqueued" or "agent-round-re-entry")?.DeliveredAtUtc)
            .Where(t => t is not null).Select(t => (now - t!.Value).TotalMinutes).ToArray();
        var laneGates = events.Where(e => e.Kind == "gate-finished").ToArray();
        var tokenWaits = laneGates.Where(e => e.QueueWaitMs is >= 0)
            .Select(e => (double)e.QueueWaitMs!.Value).ToArray();
        var laneDurations = laneGates.Where(e => e.DurationMs is >= 0)
            .Select(e => e.DurationMs!.Value / 60000d).ToArray();
        var lockWaits = events.Where(e => e.Kind == "merge-lock-acquired" && e.MergeLockWaitMs is >= 0)
            .Select(e => (double)e.MergeLockWaitMs!.Value).ToArray();
        var publicationWaits = events.Where(e => e.Kind == "publication-lock-acquired"
            && e.PublicationLockWaitMs is >= 0)
            .Select(e => (double)e.PublicationLockWaitMs!.Value).ToArray();
        var ejections = events.Where(e => e.Kind == "ejected").ToArray();
        // Preparation runs from dequeue to the delivery's first gate request,
        // without the merge-lock wait (the batch path has no merge lock).
        var preparation = events.Where(e => e.Kind == "dequeued").Select(start =>
            {
                bool Same(IntegrationLaneEvent e) => e.Project == start.Project
                    && e.JobId == start.JobId && e.Sequence == start.Sequence && e.AtUtc >= start.AtUtc;
                var gate = events.Where(e => e.Kind == "gate-requested" && Same(e))
                    .OrderBy(e => e.AtUtc).FirstOrDefault();
                if (gate is null) return (double?)null;
                var lockWait = events.FirstOrDefault(e => e.Kind == "merge-lock-acquired" && Same(e))
                    ?.MergeLockWaitMs ?? 0;
                return Math.Max(0, (gate.AtUtc - start.AtUtc).TotalMinutes - lockWait / 60000d);
            }).Where(value => value is not null).Select(value => value!.Value).ToArray();
        // One delivery is one arrival, also when a batch fallback hands it to
        // the coordinator under a second sequence.
        var arrivals = enqueued.Select(e => (e.Project, e.JobId, e.DeliveredAtUtc ?? e.AtUtc))
            .Distinct().Count();
        return new GateEconomicsRow(name, gatedMerges,
            outcomes.Count(sample => sample.Verdict is "Ok" or "Warn"),
            outcomes.Count(sample => sample.Verdict == "Fail"),
            outcomes.Count(sample => sample.Verdict is "Cancelled" or "Canceled"),
            Percentile(durations, .5),
            Percentile(durations, .9), Percentile(waits, .9),
            ReuseRate("exact"),
            ReuseRate("disjoint"),
            Rate(knownLevels, r => r.TestLevel == "full"),
            retest is { Executed: > 0 } ? retest.SameShaRetestRate : null,
            null, Percentile(lags, .5), Percentile(lags, .95),
            oldest.Length == 0 ? null : oldest.Max(), Percentile(preparation, .5),
            waiting.Count(e => e.Kind is "enqueued" or "agent-round-re-entry" or "requeued"),
            waiting.Count(e => e.Kind is "dequeued" or "merge-lock-acquired" or "prepared"),
            waiting.Count(e => e.Kind is "gate-requested" or "gate-token-waiting" or "gate-token-acquired"
                || e.Kind == "gate-finished" && e.Outcome is not ("Ok" or "Warn")),
            waiting.Count(e => e.Kind is "ready-to-publish" or "publication-queued" or "publication-lock-acquired"
                || e.Kind == "gate-finished" && e.Outcome is "Ok" or "Warn"),
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
            arrivals / (double)LaneWindowHours, published.Length / (double)LaneWindowHours,
            shortCode, color);
    }

    // A batch delivery handed to the per-task fallback continues under the
    // coordinator's own sequence, so its batch sequence ends there.
    private static bool IsTerminal(string kind) => kind is "published" or "ejected" or "handed-off";

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

/// <summary>One batch gate run; <paramref name="Published"/> when that run's candidate was published.</summary>
internal sealed record BatchGateMetric(string Project, int Members, BatchGateExecutionFact Fact,
    bool Published = false);

internal sealed record GateReceiptMetric(long? DurationMs, long? GateQueueWaitMs,
    long? MergeLockWaitMs, string? Rung, string? TestLevel,
    string? Verdict, DateTimeOffset AtUtc = default, string? TestedSha = null,
    int ReceiptIndex = 0)
{
    /// <summary>
    /// Reads the economics tokens. The receipt time is its own recordedAtUtc
    /// token, else the gate's originalCompletedAtUtc; only a legacy receipt
    /// with neither falls back to <paramref name="writtenAtUtc"/>.
    /// </summary>
    public static GateReceiptMetric Read(string path, DateTime writtenAtUtc,
        DateTimeOffset? since = null, DateTimeOffset? now = null)
    {
        using var reader = new StreamReader(path);
        var first = reader.ReadLine() ?? "";
        var shaLine = reader.ReadLine() ?? "";
        _ = reader.ReadLine();
        long? duration = TokenLong(first, "durationMs=");
        long? wait = null;
        long? mergeWait = null;
        string? rung = null;
        string? testLevel = null;
        DateTimeOffset? recordedAt = null;
        DateTimeOffset? completedAt = null;
        for (var i = 0; i < 16 && reader.ReadLine() is { } line; i++)
        {
            if (line.StartsWith("---", StringComparison.Ordinal)) break;
            wait ??= TokenLong(line, "gateQueueWaitMs=");
            mergeWait ??= TokenLong(line, "mergeLockWaitMs=");
            rung ??= Token(line, "gateRung=");
            testLevel ??= Token(line, "testLevel=");
            recordedAt ??= TokenTime(line, "recordedAtUtc=");
            completedAt ??= TokenTime(line, "originalCompletedAtUtc=");
        }
        var at = recordedAt ?? completedAt ?? new DateTimeOffset(writtenAtUtc, TimeSpan.Zero);
        if ((since is null || at >= since) && (now is null || at <= now))
            testLevel ??= LegacyTestLevel(path);
        var numbered = Path.GetFileNameWithoutExtension(path).Split('-').LastOrDefault();
        _ = int.TryParse(numbered, out var index);
        return new GateReceiptMetric(duration, wait, mergeWait, rung, testLevel,
            Token(first, "verdict="), at, Token(shaLine, "testedSha="), index);
    }

    private static DateTimeOffset? TokenTime(string line, string key)
        => DateTimeOffset.TryParse(Token(line, key), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value) ? value : null;

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
