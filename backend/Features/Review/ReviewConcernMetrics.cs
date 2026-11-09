using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using System.Text.Json;

namespace AgentStudio.Review;

public sealed record ReviewConcernMetricRow(DateOnly Day, string TaskKey, string AttemptId,
    string Aspect, bool Actionable, bool ReachedDevelop);

public sealed record ReviewConcernDailyCount(DateOnly Day, int Actionable, int Advisory,
    int ActionableReachedDevelop, int AdvisoryReachedDevelop);

/// <summary>Read-only daily counts from settled, deduplicated review attempts.</summary>
public static class ReviewConcernMetrics
{
    public static IReadOnlyList<ReviewConcernDailyCount> Summarize(IEnumerable<ReviewConcernMetricRow> rows)
        => rows.GroupBy(row => row.Day).OrderBy(group => group.Key).Select(group =>
            new ReviewConcernDailyCount(group.Key,
                group.Count(row => row.Actionable), group.Count(row => !row.Actionable),
                group.Count(row => row.Actionable && row.ReachedDevelop),
                group.Count(row => !row.Actionable && row.ReachedDevelop))).ToArray();

    public static IReadOnlyList<ReviewConcernMetricRow> Read(TaskScannerService scanner,
        BatchGateStore batchStore, string project, int days, DateOnly today)
    {
        var first = today.AddDays(1 - Math.Clamp(days, 1, 90));
        var rows = new List<ReviewConcernMetricRow>();
        foreach (var task in scanner.ScanAllJobs().Where(task =>
                     string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)))
        {
            var logs = TaskPaths.LogsDir(task.FolderPath);
            if (!Directory.Exists(logs)) continue;
            BatchGateOwnership? ownership;
            try { ownership = BatchGateOwnershipStore.Read(task.FolderPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { ownership = null; }
            var currentDelivery = RemoteDeliverySettlementStore.Read(task.FolderPath);
            foreach (var path in Directory.EnumerateFiles(logs, "remote-review-settlement-*.json"))
            {
                var attemptId = Path.GetFileNameWithoutExtension(path)["remote-review-settlement-".Length..];
                var read = RemoteReviewSettlementJournal.Read(task.FolderPath, attemptId);
                if (read is not { Status: RemoteReviewSettlementReadStatus.Ready, Entry: { } entry }) continue;
                var day = DateOnly.FromDateTime(entry.ReceivedAtUtc);
                if (day < first || day > today) continue;
                var delivery = currentDelivery;
                if (delivery?.ReviewAttemptId != attemptId) delivery = entry.Delivery;
                var direct = string.Equals(delivery?.IntegrationBranch, "develop", StringComparison.OrdinalIgnoreCase)
                    && delivery?.IntegrationOutcome is "Merged" or "MergedAfterRebase"
                        or "AlreadyMerged" or "AlreadyOnIntegrationBranch";
                var batch = ownership?.ReviewAttemptId == attemptId
                    && string.Equals(ownership.Subject.IntegrationBranch, "develop", StringComparison.OrdinalIgnoreCase)
                    && (ownership.FallbackIntegrated || ownership.ContainedInSha is not null
                        || ownership.BatchId is { } batchId
                            && batchStore.ReadPublication(batchId) is { } publication
                            && batchStore.TryReadMember(batchId, ownership.Subject.TaskKey,
                                publication.BatchRunId) is not null);
                var reachedDevelop = direct || batch;
                foreach (var verdict in MonolithTransitionAdapter.MergeReviewVerdicts(entry.Report.Verdicts)
                         .Where(verdict => ReviewGradingPolicy.IsConcernToken(verdict.Status)))
                {
                    var finding = new ReviewFollowUpFinding(verdict.Aspect, verdict.Status,
                        verdict.Summary, verdict.EvidenceChecked, verdict.Missing, verdict.Classification);
                    rows.Add(new ReviewConcernMetricRow(day, task.Key ?? task.Id, attemptId,
                        verdict.Aspect,
                        MonolithTransitionAdapter.ClassifyReviewFinding(finding)
                            == ReviewConcernActionability.Actionable,
                        reachedDevelop));
                }
            }
        }
        return rows;
    }
}
