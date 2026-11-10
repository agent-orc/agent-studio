using System.Text.Json;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>Reads the settled review that belongs to the current delivery.</summary>
public static class CompletionContentEvidence
{
    public const string LocalContextFile = "content-review-context.json";
    public const string LocalRunBriefFile = "local-run-brief-version";

    public static void StampLocalRun(string folder)
    {
        var path = Path.Combine(folder, ".metadata", LocalRunBriefFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BriefVersionStore.ReadCurrent(folder));
        File.Delete(Path.Combine(folder, LocalContextFile));
    }

    public static string? ReadLocalRunVersion(string folder)
    {
        var path = Path.Combine(folder, ".metadata", LocalRunBriefFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public static CompletionContentFacts Read(TaskInfo task, AttemptAuthorityService? authority)
    {
        var current = BriefVersionStore.ReadCurrent(task.FolderPath);
        var required = TaskModes.IsConcept(task.Mode) ? "concept-fit" : "requirement-fit";
        if (authority is not null)
        {
            // Attempt authority keys remote runs by the card's stable key, not
            // the scanner's watch-path-qualified TaskKey.
            var projection = authority.GetTaskProjection(task.Key ?? task.Id);
            var run = projection.CurrentRunAttempt;
            if (run is not null)
            {
                var review = projection.CurrentReviewAttempt is
                    { State: AttemptLifecycleState.Completed or AttemptLifecycleState.Failed or AttemptLifecycleState.Cancelled } candidate
                    && candidate.SourceRunAttemptId == run.AttemptId
                    ? candidate : null;
                var read = review is null ? null
                    : RemoteReviewSettlementJournal.Read(task.FolderPath, review.AttemptId);
                var report = read?.Entry?.Report;
                var content = report?.Verdicts.LastOrDefault(item =>
                    string.Equals(item.Aspect, required, StringComparison.OrdinalIgnoreCase));
                var ran = report?.Verdicts.Select(item => item.Aspect)
                    .Concat(report.Commands.Select(command => command.Aspect))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? [];
                var skipped = report?.SkippedAspects?.Select(item => item.Aspect).ToArray() ?? [];
                var reviewVersion = report?.BriefSha256 ?? review?.Subject.Plan?.BriefSha256;
                reviewVersion = NormalizeLegacyConceptReviewVersion(task, current,
                    run.BriefVersion, reviewVersion);
                return new CompletionContentFacts(
                    task.Mode, current, run.BriefVersion,
                    reviewVersion,
                    required, content?.Status, ran, skipped,
                    ContentSummary: content?.Summary,
                    EvidenceChecked: content?.EvidenceChecked,
                    Missing: content?.Missing,
                    Settlement: Settlement(run.AttemptId, projection.CurrentReviewAttempt, review, read));
            }
        }

        // The local review runner stamps its brief context with its verdict.
        // Old loose aspect files have no lineage and cannot establish a pass.
        var contextPath = Path.Combine(task.FolderPath, LocalContextFile);
        if (File.Exists(contextPath))
        {
            try
            {
                using var context = JsonDocument.Parse(File.ReadAllText(contextPath));
                var version = context.RootElement.GetProperty("briefVersion").GetString();
                var contentPath = Path.Combine(task.FolderPath, $"aspect-{required}.json");
                if (File.Exists(contentPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(contentPath));
                    var root = document.RootElement;
                    var ran = Directory.GetFiles(task.FolderPath, "aspect-*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(name => name is not null)
                        .Select(name => name!["aspect-".Length..])
                        .ToArray();
                    return new(task.Mode, current, version, version, required,
                        root.GetProperty("status").GetString(), ran,
                        ["not recorded for local review"],
                        ContentSummary: root.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                        EvidenceChecked: root.TryGetProperty("evidenceChecked", out var checkedValue) ? checkedValue.GetString() : null);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            {
                // Malformed evidence is treated as absent, never as a pass.
                SilentCatch.Note(ex, "CompletionContentEvidence: malformed local content review evidence");
            }
        }
        return new(task.Mode, current, null, null, required, null, [], []);
    }

    internal static string? NormalizeLegacyConceptReviewVersion(TaskInfo task,
        string current, string? delivery, string? review)
    {
        if (!TaskModes.IsConcept(task.Mode)
            || !string.Equals(current, delivery, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(task.FolderPath, ".metadata", "brief-version")))
            return review;
        var prompt = Path.Combine(task.FolderPath, "prompt.md");
        if (!File.Exists(prompt) || !string.Equals(review,
                AttemptAuthorityService.Hash(File.ReadAllText(prompt)),
                StringComparison.OrdinalIgnoreCase))
            return review;
        // One-time compatibility for a settled review that hashed the whole
        // prompt including follow-up notes. A later authored edit changes the
        // recorded brief version and keeps the older verdict stale.
        return current;
    }

    /// <summary>
    /// Why the current run has no usable settled report, or null when it has
    /// one. Completion names this instead of "only no aspects ran" (AGT-3467).
    /// </summary>
    private static CompletionSettlementProblem? Settlement(
        string runAttemptId,
        ReviewAttemptDto? currentReview,
        ReviewAttemptDto? review,
        RemoteReviewSettlementRead? read)
    {
        if (review is null)
            return new(currentReview?.AttemptId, currentReview is null
                ? CompletionSettlementProblem.NoReview
                : currentReview.SourceRunAttemptId != runAttemptId
                    ? CompletionSettlementProblem.ReviewForOtherRun
                    : CompletionSettlementProblem.ReviewNotSettled, null);
        return read!.Status switch
        {
            RemoteReviewSettlementReadStatus.Ready => null,
            RemoteReviewSettlementReadStatus.Missing => new(review.AttemptId,
                CompletionSettlementProblem.JournalMissing, null),
            _ => new(review.AttemptId, CompletionSettlementProblem.JournalRepair, read.Reason),
        };
    }
}
