using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Tasks;

/// <summary>Reads the settled review that belongs to the current delivery.</summary>
public static class CompletionContentEvidence
{
    public const string LocalContextFile = "content-review-context.json";
    public const string LocalRunBriefFile = "local-run-brief-version";
    private static readonly JsonSerializerOptions AdmissionJson = new(JsonSerializerDefaults.Web);

    public sealed record AdmissionReceipt(string ReviewAttemptId, string ResultSha, string? BriefVersion,
        string ReportSha256, IReadOnlyList<ReviewContentAdmission> Dispositions, string? FollowUpReceiptRunId = null);

    public static string AdmissionPath(string folder, string attemptId)
    {
        var journal = RemoteReviewSettlementJournal.PathFor(folder, attemptId);
        return Path.Combine(Path.GetDirectoryName(journal)!, $"content-admission-{attemptId}.json");
    }

    /// <summary>Prepared before authority settlement. An orphan receipt confers no authority.</summary>
    public static void WriteAdmission(string folder, string attemptId,
        AgentStudio.TaskServer.Contracts.ReviewReportRequest report, string reportHash,
        IReadOnlyList<ReviewContentAdmission>? dispositions, string? followUpReceiptRunId = null,
        string? reviewBriefVersion = null)
    {
        if (dispositions is null) return;
        var path = AdmissionPath(folder, attemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AdmissionReceipt(attemptId,
                report.Workspace.ExpectedResultSha, report.BriefSha256 ?? reviewBriefVersion,
                reportHash, dispositions, followUpReceiptRunId), AdmissionJson));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

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
        => ReadVerdict(task, authority) with { MissingReport = ReportDeliverable.MissingRequiredReport(task) };

    private static CompletionContentFacts ReadVerdict(TaskInfo task, AttemptAuthorityService? authority)
    {
        var current = BriefVersionStore.ReadCurrent(task.FolderPath);
        var required = TaskModes.IsConcept(task.Mode) ? "concept-fit" : "requirement-fit";
        if (authority is not null)
        {
            // Match run claims, including the composite identity of legacy
            // cards without a stable key.
            var projection = authority.GetTaskProjection(TaskIdentity.AuthorityKey(task));
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
                // Report cards get no remote review: the lightweight pipeline
                // reviews the delivered report on this server and binds the
                // verdict to the run it reviewed (AGT-3565).
                if (review is null && TaskModes.IsReportOnly(task.Mode)
                    && ReadLocal(task.FolderPath, required) is { } local
                    && string.Equals(local.RunAttemptId, run.AttemptId, StringComparison.Ordinal))
                    return new CompletionContentFacts(
                        task.Mode, current, run.BriefVersion, local.BriefVersion,
                        required, local.Status, local.Ran, [],
                        ContentSummary: local.Summary,
                        EvidenceChecked: local.EvidenceChecked,
                        Missing: local.Missing,
                        RunAttemptId: run.AttemptId,
                        ReviewedAtUtc: local.ReviewedAtUtc,
                        ReportRef: $"aspect-{required}.json");
                var reviewVersion = report?.BriefSha256 ?? review?.Subject.Plan?.BriefSha256;
                reviewVersion = NormalizeLegacyConceptReviewVersion(task, current,
                    run.BriefVersion, reviewVersion);
                var facts = new CompletionContentFacts(
                    task.Mode, current, run.BriefVersion,
                    reviewVersion,
                    required, content?.Status, ran, skipped,
                    ContentSummary: content?.Summary,
                    EvidenceChecked: content?.EvidenceChecked,
                    Missing: content?.Missing,
                    RunAttemptId: run.AttemptId,
                    ReviewAttemptId: review?.AttemptId,
                    ReviewedAtUtc: read?.Entry?.ReceivedAtUtc is { } received && received != default ? received : null,
                    ReportRef: review is null ? null : RemoteReviewReportEvidence.EvidenceFileName(review.AttemptId),
                    Settlement: review is null && TaskModes.IsReportOnly(task.Mode)
                        ? new(null, CompletionSettlementProblem.NoReportReview, null)
                        : Settlement(run.AttemptId, projection.CurrentReviewAttempt, review, read));
                return ApplyAdmission(task.FolderPath, facts, run, review, read?.Entry);
            }
        }

        // The local review runner stamps its brief context with its verdict.
        // Old loose aspect files have no lineage and cannot establish a pass.
        if (ReadLocal(task.FolderPath, required) is { } evidence)
            return new(task.Mode, current, evidence.BriefVersion, evidence.BriefVersion, required,
                evidence.Status, evidence.Ran, ["not recorded for local review"],
                ContentSummary: evidence.Summary,
                EvidenceChecked: evidence.EvidenceChecked,
                Missing: evidence.Missing,
                RunAttemptId: evidence.RunAttemptId,
                ReviewedAtUtc: evidence.ReviewedAtUtc,
                ReportRef: $"aspect-{required}.json");
        return new(task.Mode, current, null, null, required, null, [], []);
    }

    private static CompletionContentFacts ApplyAdmission(string folder, CompletionContentFacts facts,
        RunAttemptDto run, ReviewAttemptDto? review, RemoteReviewSettlementEntry? entry)
    {
        if (entry is null || review is null) return facts;
        if (!RemoteReviewSettlementPolicy.MatchesAcceptedReview(entry, review)
            || !string.Equals(run.ResultSha, review.Subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
            return facts with { ContentStatus = null, AdmissionProblem = "The settled content report does not match the current delivery; rerun review." };
        AdmissionReceipt? receipt = null;
        var path = AdmissionPath(folder, review.AttemptId);
        var receiptRef = Path.GetFileName(path);
        try
        {
            if (File.Exists(path))
                receipt = JsonSerializer.Deserialize<AdmissionReceipt>(File.ReadAllText(path), AdmissionJson)
                    ?? throw new JsonException("Empty content admission receipt.");
            // Budget recovery already owns this immutable, hashed decision.
            else if (entry.ReviewBudgetDecision?.ContentAdmissions is { } dispositions)
            {
                receipt = new(review.AttemptId, entry.Report.Workspace.ExpectedResultSha,
                    entry.Report.BriefSha256 ?? review.Subject.Plan?.BriefSha256,
                    RemoteReviewSettlementJournal.ReportHash(entry), dispositions);
                receiptRef = Path.GetFileName(RemoteReviewSettlementJournal.PathFor(folder, review.AttemptId));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "CompletionContentEvidence: content admission receipt needs repair");
            return facts with { ContentStatus = null, AdmissionProblem = "The content-admission receipt is unreadable; repair the settled review receipt." };
        }
        if (receipt is null) return facts;
        if (receipt.ReviewAttemptId != review.AttemptId
            || !string.Equals(receipt.ResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.BriefVersion, entry.Report.BriefSha256 ?? review.Subject.Plan?.BriefSha256,
                StringComparison.OrdinalIgnoreCase)
            || receipt.ReportSha256 != RemoteReviewSettlementJournal.ReportHash(entry))
            return facts with { ContentStatus = null, AdmissionProblem = "The content-admission receipt has stale delivery, brief or settlement identity; repair the settled review receipt." };
        var admission = receipt.Dispositions?.LastOrDefault(item =>
            item is not null && string.Equals(item.Aspect, facts.RequiredAspect, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(facts.ContentStatus)) return facts;
        if (admission is null)
            return facts with { ContentStatus = null, AdmissionProblem = $"The content-admission receipt has no {facts.RequiredAspect} disposition; repair the settled review receipt." };
        var originalStatus = entry.ReviewBudgetOriginalVerdicts?.LastOrDefault(item =>
            string.Equals(item.Aspect, facts.RequiredAspect, StringComparison.OrdinalIgnoreCase))?.Status
            ?? facts.ContentStatus;
        if (!string.Equals(admission.OriginalStatus, originalStatus, StringComparison.OrdinalIgnoreCase))
            return facts with { ContentStatus = null, AdmissionProblem = "The content-admission receipt does not match the original finding; repair the settled review receipt." };
        var followUp = ReviewRoundBudgetStore.Read(folder).Rounds
            .LastOrDefault(round => round.AttemptId == review.AttemptId)?.FollowUpTaskKey;
        if (followUp is null && receipt.FollowUpReceiptRunId is { } orchestrationRunId)
        {
            using var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance);
            followUp = timeline.ReadAll(folder).LastOrDefault(item =>
                item.Kind == TimelineEventKinds.ReviewFollowUpRouted
                && item.Details?.GetValueOrDefault("orchestrationRunId") == orchestrationRunId)
                ?.Details?.GetValueOrDefault("followUpTaskKey");
        }
        if (!admission.Admitted(followUp))
            return facts with { AdmissionProblem = admission.RequiresFollowUp && string.IsNullOrWhiteSpace(followUp)
                ? $"Review {review.AttemptId} still owes its linked follow-up; retry settlement recovery before completion."
                : admission.Reason };
        // The completion claim and readiness projection already carry these
        // fields. Preserve the literal finding and the admission receipt there.
        return facts with
        {
            ContentStatus = "pass",
            ContentSummary = $"{facts.ContentSummary} Content admission: {admission.Disposition}; original {admission.OriginalStatus}. "
                + admission.Reason + (followUp is null ? "" : $" Follow-up: {followUp}.")
                + $" Receipt: {receiptRef}; review {review.AttemptId}; delivery {run.ResultSha}.",
        };
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
    /// Writes the lineage of a local content verdict. A report review names
    /// the run attempt it reviewed, so a verdict for an older run never
    /// counts for a newer delivery.
    /// </summary>
    public static void WriteLocalContext(string folder, string? briefVersion, string? runAttemptId, DateTime reviewedAt)
        => File.WriteAllText(Path.Combine(folder, LocalContextFile),
            JsonSerializer.Serialize(new { briefVersion, runAttemptId, reviewedAt }));

    private sealed record LocalContent(
        string? BriefVersion,
        string? RunAttemptId,
        string? Status,
        IReadOnlyList<string> Ran,
        string? Summary,
        string? EvidenceChecked,
        string? Missing,
        DateTime? ReviewedAtUtc);

    private static LocalContent? ReadLocal(string folder, string required)
    {
        var contextPath = Path.Combine(folder, LocalContextFile);
        var contentPath = Path.Combine(folder, $"aspect-{required}.json");
        if (!File.Exists(contextPath) || !File.Exists(contentPath)) return null;
        try
        {
            using var context = JsonDocument.Parse(File.ReadAllText(contextPath));
            var version = context.RootElement.GetProperty("briefVersion").GetString();
            var runAttemptId = context.RootElement.TryGetProperty("runAttemptId", out var runValue)
                && runValue.ValueKind == JsonValueKind.String ? runValue.GetString() : null;
            var reviewedAt = context.RootElement.TryGetProperty("reviewedAt", out var date)
                && date.ValueKind == JsonValueKind.String && date.TryGetDateTime(out var parsedDate)
                    ? (DateTime?)parsedDate : null;
            using var document = JsonDocument.Parse(File.ReadAllText(contentPath));
            var root = document.RootElement;
            var ran = Directory.GetFiles(folder, "aspect-*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)
                .Select(name => name!["aspect-".Length..])
                .ToArray();
            return new LocalContent(version, runAttemptId,
                root.GetProperty("status").GetString(), ran,
                root.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                root.TryGetProperty("evidenceChecked", out var checkedValue) ? checkedValue.GetString() : null,
                root.TryGetProperty("missing", out var missing) ? missing.GetString() : null,
                reviewedAt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Malformed evidence is treated as absent, never as a pass.
            SilentCatch.Note(ex, "CompletionContentEvidence: malformed local content review evidence");
            return null;
        }
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
