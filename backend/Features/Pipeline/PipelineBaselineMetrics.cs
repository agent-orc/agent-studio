using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// The pipeline delivery-target baseline of one gate-economics row (AGT-3309,
/// Dossier AGT-3276 section 07). Every value is null when its input is
/// missing, never zero. <see cref="LeadTimeCards"/>,
/// <see cref="GateMinutesCards"/> and <see cref="GateSourceRuns"/> count the
/// measured samples; <see cref="LeadTimeUnknownCards"/> and
/// <see cref="GateMinutesUnknownCards"/> count integrated cards whose input
/// is missing.
/// </summary>
public sealed record PipelineBaseline(
    double? LeadTimeMedianMinutes, double? LeadTimeP90Minutes, int LeadTimeCards, int LeadTimeUnknownCards,
    double? GateMinutesPerCardMedian, double? GateMinutesPerCardP90, int GateMinutesCards,
    int GateMinutesUnknownCards, int? FullSuiteRuns, double? FullSuiteRunsPerDay,
    double? RedDevelopMedianMinutes, double? RedDevelopP90Minutes,
    double? GateSourceBytesMedian, double? GateSourceBytesP90,
    double? GateSourceSecondsMedian, double? GateSourceSecondsP90, int GateSourceRuns)
{
    public static readonly PipelineBaseline Unknown = new(null, null, 0, 0, null, null, 0, 0,
        null, null, null, null, null, null, null, null, 0);
}

/// <summary>
/// Server-side board read latency and git spawns per request for one handler,
/// read from <see cref="GitProcessTelemetry"/>. Installation-wide: the board
/// handlers serve every project in one request.
/// </summary>
public sealed record BoardReadMetric(string Label, string Name, int Requests,
    double? P50Ms, double? P95Ms, double? GitSpawnsPerRequest);

/// <summary>One job folder's evidence for <see cref="PipelineBaselineMetrics.Compute"/>.</summary>
internal sealed record BaselineFolder(IReadOnlyList<IntegrationLaneEvent> Events,
    IReadOnlyList<GateReceiptMetric> Receipts, IReadOnlyList<ReviewVerifyMetric> Reviews);

/// <summary>
/// T1 minutes of one Remote Review attempt: the executed tool commands of its
/// verification phase. Null when a counted command has no duration or the
/// attempt carries no command evidence.
/// </summary>
internal sealed record ReviewVerifyMetric(string AttemptId, DateTimeOffset AtUtc, double? Minutes);

internal static class PipelineBaselineMetrics
{
    /// <summary>The board and card-list handlers the board read target covers.</summary>
    internal static readonly (string Label, string Name)[] BoardHandlers =
    [
        ("tasks/grouped", "Board"),
        ("tasks/list", "Card list"),
        ("board/integration-status", "Board integration status"),
        ("board/merge-status", "Board merge status"),
    ];

    private static readonly Regex SourceBundle = new(
        @"Source bundle (\d+) bytes transferred in ([0-9]+(?:[.,][0-9]+)?)s\.",
        RegexOptions.CultureInvariant);
    private static readonly Regex SourceReady = new(
        @"Source ready ([0-9]+(?:[.,][0-9]+)?)s\.", RegexOptions.CultureInvariant);

    /// <summary>
    /// Computes the baseline over the folders of one row. A card is one
    /// <c>published</c> delivery in the window. Its lead time runs from the
    /// review pass of its last round (the latest arrival's delivered time) to
    /// the publication. Its gate minutes add the T1 review verification and
    /// the T2 integration gate minutes spent since the card's previous
    /// publication, without queue wait.
    /// </summary>
    public static PipelineBaseline Compute(IEnumerable<BaselineFolder> folders,
        IReadOnlyList<BatchGateMetric> batches, DateTimeOffset since, DateTimeOffset now)
    {
        var leadTimes = new List<double>();
        var leadUnknown = 0;
        var gateMinutes = new List<double>();
        var gateUnknown = 0;
        var fullSuiteRuns = 0;
        var knownLevelRuns = 0;
        var sourceBytes = new List<double>();
        var sourceSeconds = new List<double>();
        var publishedBatches = batches.Where(sample => sample.Published
                && sample.Fact.Outcome is "Ok" or "Warn" && sample.Members > 0)
            .GroupBy(sample => (sample.Fact.BatchId, sample.Fact.BatchRunId))
            .Select(run => run.OrderBy(sample => sample.Fact.CompletedAtUtc).Last()).ToArray();
        foreach (var folder in folders)
        {
            var executed = folder.Receipts.Where(Executed).ToArray();
            foreach (var receipt in executed)
            {
                if (receipt.TestLevel is not null and not "unknown") knownLevelRuns++;
                if (receipt.TestLevel == "full") fullSuiteRuns++;
                if (receipt.SourceBytes is { } bytes && receipt.SourceSeconds is { } seconds)
                {
                    sourceBytes.Add(bytes);
                    sourceSeconds.Add(seconds);
                }
            }

            var publications = folder.Events.Where(e => e.Kind == "published")
                .GroupBy(e => (e.Project, e.JobId, e.Sequence))
                .Select(group => group.OrderBy(e => e.AtUtc).Last())
                .OrderBy(e => e.AtUtc).ToArray();
            foreach (var publication in publications.Where(e => e.AtUtc >= since && e.AtUtc <= now))
            {
                var arrival = folder.Events.Where(e => e.Kind is "enqueued" or "agent-round-re-entry"
                        && e.Project == publication.Project && e.JobId == publication.JobId
                        && e.AtUtc <= publication.AtUtc && e.DeliveredAtUtc is not null)
                    .OrderBy(e => e.AtUtc).LastOrDefault();
                if (arrival is null) leadUnknown++;
                else leadTimes.Add(Math.Max(0,
                    (publication.AtUtc - arrival.DeliveredAtUtc.GetValueOrDefault()).TotalMinutes));

                var previous = publications.Where(e => e.JobId == publication.JobId
                        && e.AtUtc < publication.AtUtc)
                    .Select(e => (DateTimeOffset?)e.AtUtc).LastOrDefault() ?? DateTimeOffset.MinValue;
                bool InCard(DateTimeOffset at) => at > previous && at >= since && at <= publication.AtUtc;
                var t1 = ReviewMinutes(folder.Reviews.Where(review => InCard(review.AtUtc)).ToArray());
                var t2 = GateMinutes(folder.Receipts.Where(receipt => InCard(receipt.AtUtc)).ToArray())
                    ?? BatchShare(publishedBatches, publication.JobId, previous, publication.AtUtc);
                if (t1 is null || t2 is null) gateUnknown++;
                else gateMinutes.Add(t1.Value + t2.Value);
            }
        }

        var batchRuns = batches.Count;
        var days = Math.Max((now - since).TotalDays, 1d / 24);
        int? fullSuites = knownLevelRuns + batchRuns == 0 ? null : fullSuiteRuns + batchRuns;
        return new PipelineBaseline(
            Percentile(leadTimes, .5), Percentile(leadTimes, .9), leadTimes.Count, leadUnknown,
            Percentile(gateMinutes, .5), Percentile(gateMinutes, .9), gateMinutes.Count, gateUnknown,
            fullSuites, fullSuites / days,
            // Red develop time needs the post-merge T3 verdicts (Dossier P4).
            null, null,
            Percentile(sourceBytes, .5), Percentile(sourceBytes, .9),
            Percentile(sourceSeconds, .5), Percentile(sourceSeconds, .9), sourceBytes.Count);
    }

    /// <summary>
    /// The board handlers from one telemetry snapshot. A handler without a
    /// request in the window reports unknown latency and spawns.
    /// </summary>
    public static IReadOnlyList<BoardReadMetric> BoardRead(IReadOnlyList<EndpointStats> endpoints)
        => BoardHandlers.Select(handler =>
            endpoints.FirstOrDefault(stats => stats.Label == handler.Label) is { SampleCount: > 0 } stats
                ? new BoardReadMetric(handler.Label, handler.Name, stats.SampleCount,
                    stats.PercentileSampleCount > 0 ? stats.P50Ms : null,
                    stats.PercentileSampleCount > 0 ? stats.P95Ms : null,
                    (double)stats.TotalSpawns / stats.SampleCount)
                : new BoardReadMetric(handler.Label, handler.Name, 0, null, null, null)).ToArray();

    /// <summary>Source bytes and checkout-ready seconds; old receipts use transfer seconds.</summary>
    internal static (long Bytes, double Seconds)? ParseSourceTransfer(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains("Source ready unavailable.", StringComparison.Ordinal)) return null;
        var match = SourceBundle.Matches(text).LastOrDefault();
        if (match is null) return null;
        var ready = SourceReady.Matches(text).LastOrDefault();
        var secondsText = ready?.Groups[1].Value ?? match.Groups[2].Value;
        return long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            && double.TryParse(secondsText.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds)
                ? (bytes, seconds) : null;
    }

    /// <summary>
    /// Reads the Remote Review attempts of one job folder. The settlement
    /// journal (<c>logs/remote-review-settlement-*.json</c>) carries each
    /// command's start and finish; an attempt without one falls back to its
    /// <c>remote-review-grade-*.md</c> report and that report's command budgets.
    /// An unreadable file is skipped; it never counts as zero minutes.
    /// </summary>
    public static IReadOnlyList<ReviewVerifyMetric> ReadReviews(string folder, DateTimeOffset since)
    {
        if (!Directory.Exists(folder)) return [];
        var reviews = new Dictionary<string, ReviewVerifyMetric>(StringComparer.Ordinal);
        // A file is rewritten, never back-dated: one older than the window
        // cannot belong to a card the window measures.
        IEnumerable<(string Path, DateTimeOffset Written)> Recent(string directory, string pattern)
            => Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern)
                    .Select(path => (path, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)))
                    .Where(file => file.Item2 >= since)
                : [];
        try
        {
            foreach (var (path, _) in Recent(TaskPaths.LogsDir(folder), "remote-review-settlement-*.json"))
                if (BoundedFileRead.ReadAllTextOrNull(path, BoundedFileRead.EvidenceTextBytes) is { } json
                    && ParseSettlement(json) is { } review)
                    reviews.TryAdd(review.AttemptId, review);
            foreach (var (path, written) in Recent(folder, "remote-review-grade-*.md"))
                if (BoundedFileRead.ReadAllTextOrNull(path, BoundedFileRead.EvidenceTextBytes) is { } text)
                {
                    var review = ParseReview(text,
                        Path.GetFileNameWithoutExtension(path)["remote-review-grade-".Length..], written);
                    reviews.TryAdd(review.AttemptId, review);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GateEconomics: review evidence unavailable");
        }
        return [.. reviews.Values];
    }

    /// <summary>
    /// T1 minutes from one settlement journal: tool commands of the
    /// verification phase (builds, tests, fast checks), finish minus start.
    /// Agent aspects, preparation and reused results or baselines are not
    /// counted. Null for a journal that cannot be read as a report.
    /// </summary>
    internal static ReviewVerifyMetric? ParseSettlement(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (Text(root, "attemptId") is not { Length: > 0 } attemptId
                || !DateTimeOffset.TryParse(Text(root, "receivedAtUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var receivedAt)
                || !root.TryGetProperty("report", out var report)
                || !report.TryGetProperty("commands", out var commands)
                || commands.ValueKind != JsonValueKind.Array)
                return null;
            var total = 0d;
            foreach (var command in commands.EnumerateArray())
            {
                if (Text(command, "phase") is not ("verification" or null)
                    || (Text(command, "executionKind") ?? Contract.ReviewCommandKinds.Tool) != Contract.ReviewCommandKinds.Tool
                    || Text(command, "verifyReusedFromAttemptId") is { Length: > 0 }
                    || (Text(command, "workspaceRole") ?? "").StartsWith("baseline-", StringComparison.Ordinal)
                    && Text(command, "baselineReusedFromAttemptId") is { Length: > 0 })
                    continue;
                if (!DateTimeOffset.TryParse(Text(command, "startedAt"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var started)
                    || !DateTimeOffset.TryParse(Text(command, "finishedAt"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var finished))
                    return new ReviewVerifyMetric(attemptId, receivedAt, null);
                total += Math.Max(0, (finished - started).TotalMinutes);
            }
            return new ReviewVerifyMetric(attemptId, receivedAt, total);
        }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "GateEconomics: review settlement journal unreadable");
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static ReviewVerifyMetric ParseReview(string text, string fallbackId, DateTimeOffset writtenAtUtc)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        string? attemptId = null;
        DateTimeOffset? receivedAt = null;
        if (lines.Length > 0 && lines[0].Trim() == "---")
            foreach (var line in lines.Skip(1).TakeWhile(line => line.Trim() != "---"))
            {
                if (line.StartsWith("attemptId:", StringComparison.Ordinal))
                    attemptId = line["attemptId:".Length..].Trim().Trim('"');
                if (line.StartsWith("receivedAt:", StringComparison.Ordinal)
                    && DateTimeOffset.TryParse(line["receivedAt:".Length..].Trim().Trim('"'),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    receivedAt = parsed;
            }
        return new ReviewVerifyMetric(attemptId ?? fallbackId, receivedAt ?? writtenAtUtc, VerifyMinutes(lines));
    }

    private static double? VerifyMinutes(string[] lines)
    {
        var start = Array.FindIndex(lines, line => line.Trim() == "## Command evidence");
        if (start < 0) return null;
        var rows = lines.Skip(start + 1).TakeWhile(line => !line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line.Trim()).Where(line => line.StartsWith('|')).Select(Cells).ToArray();
        if (rows.Length == 0) return null;
        var header = rows[0];
        int Column(string name) => Array.FindIndex(header, cell => cell.Equals(name, StringComparison.OrdinalIgnoreCase));
        var (phase, workspace, step, budget) = (Column("Phase"), Column("Workspace"), Column("Step"), Column("Budget"));
        var (baseline, result) = (Column("Baseline"), Column("Result"));
        if (phase < 0 || step < 0 || budget < 0) return null;
        string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";
        var total = 0d;
        foreach (var row in rows.Skip(1).Where(row => !row.All(cell => cell.All(ch => ch is '-' or ':'))))
        {
            if (!Cell(row, phase).Equals("verification", StringComparison.OrdinalIgnoreCase)
                || !IsVerifyStep(Cell(row, step))) continue;
            // A reused result or baseline never ran in this attempt.
            if (Cell(row, result).StartsWith("verify result reused", StringComparison.OrdinalIgnoreCase)
                || Cell(row, workspace).StartsWith("baseline-", StringComparison.Ordinal)
                && Cell(row, baseline).StartsWith("baseline result reused", StringComparison.OrdinalIgnoreCase))
                continue;
            var consumed = Regex.Match(Cell(row, budget), @":\s*(\d+)/\d+ ms");
            if (!consumed.Success) return null;
            total += long.Parse(consumed.Groups[1].Value, CultureInfo.InvariantCulture) / 60000d;
        }
        return total;
    }

    // Splits a Markdown table row; the report escapes a literal pipe as "\|".
    private static string[] Cells(string line)
        => Regex.Split(line.Trim().TrimStart('|'), @"(?<!\\)\|").Select(cell => cell.Trim()).ToArray()
            is var cells && cells.Length > 0 && cells[^1].Length == 0 ? cells[..^1] : cells;

    private static bool IsVerifyStep(string step)
        => step.Length > "verify-".Length && step.StartsWith("verify-", StringComparison.OrdinalIgnoreCase)
           && step["verify-".Length..].All(char.IsDigit);

    // A cache hit copies an earlier verdict and a missing-source or exact
    // review-reuse gate ran no tests: none of them is a gate execution.
    private static bool Executed(GateReceiptMetric receipt)
        => receipt.VerdictSource != nameof(GateVerdictSource.CacheHit) && receipt.Rung is not ("none" or "exact");

    private static double? ReviewMinutes(IReadOnlyList<ReviewVerifyMetric> reviews)
        => reviews.Count == 0 || reviews.Any(review => review.Minutes is null)
            ? null : reviews.Sum(review => review.Minutes!.Value);

    private static double? GateMinutes(IReadOnlyList<GateReceiptMetric> receipts)
    {
        if (receipts.Count == 0) return null;
        var total = 0d;
        foreach (var receipt in receipts.Where(Executed))
        {
            if (receipt.DurationMs is not >= 0) return null;
            total += receipt.DurationMs.Value / 60000d;
        }
        return total;
    }

    private static double? BatchShare(IReadOnlyList<BatchGateMetric> published, string jobId,
        DateTimeOffset after, DateTimeOffset until)
    {
        var runs = published.Where(sample => sample.Fact.MemberKeys?.Contains(jobId, StringComparer.OrdinalIgnoreCase) == true
            && sample.Fact.CompletedAtUtc > after && sample.Fact.CompletedAtUtc <= until).ToArray();
        if (runs.Length == 0 || runs.Any(sample => sample.Fact.DurationMs is not >= 0)) return null;
        return runs.Sum(sample => sample.Fact.DurationMs!.Value / 60000d / sample.Members);
    }

    internal static double? Percentile(IEnumerable<double> numbers, double fraction)
    {
        var values = numbers.Order().ToArray();
        if (values.Length == 0) return null;
        var position = (values.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }
}
