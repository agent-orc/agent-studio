using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;

namespace AgentStudio.Review.Observations;

/// <summary>Task-local append-only observations plus a read projection of executed review steps.</summary>
public sealed class ReviewObservationStore(
    TaskScannerService scanner, PipelineExecutionLog pipeline, ITokenPricing pricing,
    TimeProvider clock)
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ReviewObservationReport Report)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public const string Method = "Observed task review executions and cited later defects; estimated historical list prices. Missing prices and unknown delivery SHAs remain unknown. These are Studio observations, not published Token Economy facts.";

    public ReviewObservationReport Project(string project, string watchPath)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(watchPath, out var cached)
            && now - cached.At < TimeSpan.FromSeconds(15)) return cached.Report;
        var reviews = new List<ReviewObservation>();
        var later = new List<ReviewLaterEvidence>();
        var dispositions = new List<ReviewFindingDisposition>();
        var gateDefects = new List<(string JobId, string Sha, DateTime FoundAt, string EvidenceRef, string? RoundId)>();
        foreach (var task in scanner.ScanAllAutomationJobsWithArchive())
        {
            if (!string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
            var record = pipeline.Read(task.FolderPath);
            if (record is not null)
            {
                foreach (var run in record.PreviousAttempts.Append(record))
                {
                    AddPipelineReviews(reviews, task.Id, run, pricing);
                    foreach (var gate in run.Steps.Where(step =>
                                 step.StepId == PipelineCatalogue.BuildTestGateStepId
                                 && step.ReviewFindings?.Blocker > 0
                                 && ValidSha(step.ReviewedResultSha) is not null
                                 && step.CompletedAt.HasValue))
                        gateDefects.Add((task.Id, gate.ReviewedResultSha!.ToLowerInvariant(),
                            gate.CompletedAt!.Value.ToUniversalTime(),
                            gate.EvidenceRef ?? PipelineExecutionLog.FileName, gate.ExecutionAttemptId));
                }
            }
            var journal = Read(task.FolderPath);
            reviews.AddRange(journal.Reviews);
            later.AddRange(journal.Evidence);
            dispositions.AddRange(journal.Dispositions);
        }
        reviews = reviews.GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderBy(row => row.ReviewedAt).ToList();
        var latestDispositions = dispositions.GroupBy(row => row.ReviewId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        reviews = reviews.Select(row => latestDispositions.TryGetValue(row.Id, out var outcome)
            ? row with { ConfirmedFindings = outcome.Confirmed, DismissedFindings = outcome.Dismissed }
            : row).ToList();
        later.AddRange(InferLaterReviewMisses(reviews));
        foreach (var gate in gateDefects)
        foreach (var review in reviews.Where(row => row.JobId == gate.JobId
                     && row.ResultSha == gate.Sha && row.ReviewedAt < gate.FoundAt
                     && (row.ReviewRoundId is null || row.ReviewRoundId != gate.RoundId)
                     && ReviewObservationPolicy.Approved(row)))
            later.Add(new ReviewLaterEvidence(
                $"gate:{gate.JobId}:{gate.FoundAt.Ticks}:{review.Id}", review.Id,
                gate.Sha, gate.FoundAt, "gate", gate.EvidenceRef, "blocker",
                "Later verification gate found a product blocker on the same delivered SHA."));
        later = later.GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderBy(row => row.FoundAt).ToList();
        var report = new ReviewObservationReport(project, now.UtcDateTime, Method,
            reviews, later, ReviewObservationPolicy.Aggregate(reviews, later));
        _cache[watchPath] = (now, report);
        return report;
    }

    public bool RecordReview(string folder, string watchPath, ReviewObservation review)
    {
        var appended = AppendReview(folder, review);
        if (appended) _cache.TryRemove(watchPath, out _);
        return appended;
    }

    public bool RecordEvidence(string folder, string watchPath, ReviewLaterEvidence evidence)
    {
        var appended = AppendEvidence(folder, evidence);
        if (appended) _cache.TryRemove(watchPath, out _);
        return appended;
    }

    public void RecordDisposition(string folder, string watchPath, ReviewFindingDisposition disposition)
    {
        AppendDisposition(folder, disposition);
        _cache.TryRemove(watchPath, out _);
    }

    public static (IReadOnlyList<ReviewObservation> Reviews, IReadOnlyList<ReviewLaterEvidence> Evidence,
        IReadOnlyList<ReviewFindingDisposition> Dispositions)
        Read(string folder)
    {
        var path = TaskPaths.ReviewObservationsLog(folder);
        if (!File.Exists(path)) return ([], [], []);
        var reviews = new List<ReviewObservation>();
        var evidence = new List<ReviewLaterEvidence>();
        var dispositions = new List<ReviewFindingDisposition>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var row = JsonSerializer.Deserialize<JournalRow>(line, Json);
                if (row?.Review is not null) reviews.Add(row.Review);
                if (row?.Evidence is not null) evidence.Add(row.Evidence);
                if (row?.Disposition is not null) dispositions.Add(row.Disposition);
            }
            catch (JsonException ex)
            {
                SilentCatch.Note(ex, "ReviewObservationStore: skip torn observation row");
            }
        }
        return (reviews, evidence, dispositions);
    }

    public static bool AppendReview(string folder, ReviewObservation review)
        => Append(folder, new JournalRow(review, null), review.Id);

    public static bool AppendEvidence(string folder, ReviewLaterEvidence evidence)
        => Append(folder, new JournalRow(null, evidence), evidence.Id);

    internal static void AppendDisposition(string folder, ReviewFindingDisposition disposition)
    {
        var path = TaskPaths.ReviewObservationsLog(folder);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            Directory.CreateDirectory(TaskPaths.ResultsDir(folder));
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(new JournalRow(null, null, disposition), Json));
        }
    }

    private static bool Append(string folder, JournalRow row, string id)
    {
        var path = TaskPaths.ReviewObservationsLog(folder);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            var existing = Read(folder);
            if (existing.Reviews.Any(item => item.Id == id)
                || existing.Evidence.Any(item => item.Id == id)) return false;
            Directory.CreateDirectory(TaskPaths.ResultsDir(folder));
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(row, Json));
            return true;
        }
    }

    internal static void AddPipelineReviews(List<ReviewObservation> target, string jobId,
        PipelineExecutionRecord record, ITokenPricing pricing)
    {
        foreach (var step in record.Steps.Where(row => row.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.StepId, PipelineCatalogue.CodeReviewGradeStepId, StringComparison.OrdinalIgnoreCase)))
        {
            var executions = StepCostMeasurement.Executions(pricing, step);
            for (var index = 0; index < executions.Count; index++)
            {
                var execution = executions[index];
                if (execution.CompletedAt is not { } completed || string.IsNullOrWhiteSpace(execution.Verdict)) continue;
                var level = step.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase) ? "aspect" : "grade";
                var findings = execution.ReviewFindings ?? CountsFromVerdict(execution.Verdict);
                target.Add(new ReviewObservation(
                    $"{jobId}:{record.Attempt}:{step.StepId}:{index}", jobId, level,
                    ValidSha(execution.ReviewedResultSha),
                    completed.ToUniversalTime(), AgentStudio.Tokens.ModelIdentity.StoredId(execution.Model), execution.ThinkingLevel,
                    execution.InputTokens, execution.OutputTokens, execution.CacheReadTokens,
                    execution.CacheCreationTokens, execution.DurationMs,
                    execution.Verdict, findings, execution.EstimatedCostUsd,
                    execution.EvidenceRef ?? step.EvidenceRef ?? PipelineExecutionLog.FileName,
                    Cli: execution.ReviewCli, Aspect: step.StepId,
                    FilesReviewed: execution.ReviewedFileCount,
                    ReviewRoundId: execution.ExecutionAttemptId ?? $"{jobId}:run:{record.Attempt}"));
            }
        }
    }

    private static ReviewFindingCounts CountsFromVerdict(string verdict)
        => verdict.ToLowerInvariant() switch
        {
            "block" or "d" => new(Blocker: 1),
            "concerns" or "c" => new(Major: 1),
            _ => new(),
        };

    public static string? ValidSha(string? sha)
        => sha is { Length: 40 or 64 } && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null;

    internal static IEnumerable<ReviewLaterEvidence> InferLaterReviewMisses(
        IReadOnlyList<ReviewObservation> reviews)
    {
        foreach (var found in reviews.Where(row => row.Findings.Blocker > 0 && row.ResultSha is not null))
        foreach (var earlier in reviews.Where(row => row.JobId == found.JobId
                     && row.ResultSha == found.ResultSha && row.ReviewedAt < found.ReviewedAt
                     && (row.ReviewRoundId is null || found.ReviewRoundId is null
                         || row.ReviewRoundId != found.ReviewRoundId)
                     && ReviewObservationPolicy.Approved(row)))
            yield return new ReviewLaterEvidence(
                $"later-review:{found.Id}:{earlier.Id}", earlier.Id, found.ResultSha!,
                found.ReviewedAt, "later-review", found.EvidenceRef, "blocker",
                $"Later {found.Level} review found a blocker on the same delivered SHA.");
    }

    private sealed record JournalRow(ReviewObservation? Review, ReviewLaterEvidence? Evidence,
        ReviewFindingDisposition? Disposition = null);
}
