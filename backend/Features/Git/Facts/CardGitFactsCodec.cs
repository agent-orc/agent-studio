using System.Text.Json;

namespace AgentStudio.Git;

/// <summary>
/// Maps a repository's <see cref="TaskListGitProjection"/> to
/// <c>card_git_facts</c> rows and back. S2 persists exactly what the producer
/// already computes per card; S3 changes the computation, not this mapping's
/// role. A row whose document has another <see cref="Version"/> or does not
/// parse is unreadable: the card answers warming and the restore is not
/// confirmed without a recompute.
/// </summary>
internal static class CardGitFactsCodec
{
    internal const int Version = 1;
    internal const string UnavailableCode = "card-facts-unavailable";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    internal static IReadOnlyList<CardGitFactRow> ToRows(TaskListGitProjection projection,
        IReadOnlyCollection<TaskInfo> tasks)
    {
        var rows = new List<CardGitFactRow>(tasks.Count);
        foreach (var task in tasks)
        {
            var key = task.TaskKey;
            var signature = projection.TaskSignatures.TryGetValue(key, out var signed)
                ? signed : TaskGitSignature.For(task);
            var facts = projection.ForTask(key);
            var empty = facts.Merge is null && facts.Integration is null && facts.Publish is null
                && facts.TestEvidence is null && facts.ReviewProjection is null && facts.ReconstructedCommits is null;
            rows.Add(new CardGitFactRow(key, signature,
                empty ? null : JsonSerializer.Serialize(new CardGitFactsDocument(Version, facts.Merge,
                    facts.Integration, facts.Publish, facts.TestEvidence, facts.ReviewProjection,
                    facts.ReconstructedCommits), Json),
                projection.UnavailableTasks.Contains(key) ? UnavailableCode : null));
        }
        return rows;
    }

    /// <summary>
    /// The restored projection. Review-subject versions are this process's
    /// (all zero after a start), so a subject change the watcher sees after the
    /// restore withholds the card's facts until the next publish.
    /// </summary>
    internal static TaskListGitProjection ToProjection(IReadOnlyList<CardGitFactRow> rows, out int unreadable)
    {
        unreadable = 0;
        var merge = new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal);
        var integration = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        var publish = new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal);
        var testRuns = new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal);
        var review = new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal);
        var commits = new Dictionary<string, IReadOnlyList<TaskCommitInfo>>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        var subjects = new Dictionary<string, long>(StringComparer.Ordinal);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            CardGitFactsDocument? document = null;
            if (row.Facts is not null)
            {
                try { document = JsonSerializer.Deserialize<CardGitFactsDocument>(row.Facts, Json); }
                catch (JsonException ex)
                {
                    SilentCatch.Note(ex, "CardGitFactsCodec: stored card facts unreadable");
                }
                if (document is null || document.V != Version)
                {
                    unreadable++;
                    continue;
                }
            }
            signatures[row.TaskKey] = row.InputSignature;
            subjects[row.TaskKey] = 0;
            if (row.ErrorCode is not null) unavailable.Add(row.TaskKey);
            if (document is null) continue;
            if (document.Merge is { } m) merge[row.TaskKey] = m;
            if (document.Integration is { } i) integration[row.TaskKey] = i;
            if (document.Publish is { } p) publish[row.TaskKey] = p;
            if (document.TestRuns is { } t) testRuns[row.TaskKey] = t;
            if (document.ReviewProjection is { } r) review[row.TaskKey] = r;
            if (document.ReconstructedCommits is { } c) commits[row.TaskKey] = c;
        }
        return new TaskListGitProjection(merge, integration, publish, testRuns, review,
            commits, signatures, subjects, unavailable);
    }

    private sealed record CardGitFactsDocument(
        int V,
        TaskMergeSignal? Merge,
        TaskIntegrationStatus? Integration,
        TaskPublishSignal? Publish,
        TaskTestRunEvidence? TestRuns,
        AgentStudio.Review.ReviewProjectionView? ReviewProjection,
        IReadOnlyList<TaskCommitInfo>? ReconstructedCommits);
}
