namespace AgentStudio.Runner;

/// <summary>
/// Collects delivery exposure for dispatch. Current branch membership ends
/// exposure independently of verification and acceptance. Durable merge facts
/// remain fallback evidence. Terminal cards and stale lane snapshots never block.
/// </summary>
public static class OverlapComparisonSet
{
    /// <summary>Lanes after coding whose card holds its delivery until it integrates.</summary>
    private static readonly string[] PostCodingLanes = [TaskStates.AutoReview, TaskStates.HumanReview];

    private static readonly string[] IntegratedRecordClasses =
    [
        IntegrationRecordClasses.IntegratedVerified,
        IntegrationRecordClasses.IntegratedHistorical,
        IntegrationRecordClasses.CuratedMapping,
        IntegrationRecordClasses.NoCodeExpected,
    ];

    /// <summary>The comparison set of one project on the remote claim.</summary>
    public sealed record ProjectSet(IReadOnlyList<RunningTask> Running, IReadOnlyList<InFlightTask> InFlight);

    /// <summary>
    /// The remote claim's comparison set: the project's Progress cards, which
    /// hold the leased runs and the project occupancy, as running cards, and
    /// under <see cref="OverlapWindows.InFlight"/> every other in-flight card.
    /// </summary>
    public static ProjectSet ForProject(
        IEnumerable<TaskInfo> cards,
        string project,
        string window,
        Func<TaskInfo, TaskParallelism> predict,
        IReadOnlyDictionary<string, TaskIntegrationStatus>? integration = null, string? target = null)
    {
        var projectCards = cards
            .Where(card => card is not null && !card.Fixture
                && string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Select(card => integration?.TryGetValue(card.TaskKey, out var status) == true
                && (string.IsNullOrWhiteSpace(target) || TaskIntegrationBranch.Name(status.IntegrationBranch) == TaskIntegrationBranch.Name(target))
                    ? card with { Integration = status } : card)
            .Where(IsCurrentCard)
            .ToArray();
        var running = projectCards
            .Where(card => string.Equals(card.State, TaskStates.Progress, StringComparison.Ordinal) && IsInFlight(card))
            .Select(card => new RunningTask(card.Id, predict(card) ?? TaskParallelism.Default)
                { CardKey = card.Key ?? card.TaskKey ?? card.Id, Version = DeliveryVersion(card) })
            .ToArray();
        if (OverlapWindows.Normalize(window) == OverlapWindows.Running)
            return new ProjectSet(running, Array.Empty<InFlightTask>());
        var runningIds = new HashSet<string>(running.Select(r => r.TaskId), StringComparer.OrdinalIgnoreCase);
        return new ProjectSet(running, Collect(projectCards, project, runningIds, predict));
    }

    /// <summary>
    /// The in-flight cards of <paramref name="project"/> in
    /// <paramref name="cards"/>, without the cards in
    /// <paramref name="runningIds"/> (they are compared as running cards).
    /// </summary>
    public static IReadOnlyList<InFlightTask> Collect(
        IEnumerable<TaskInfo> cards,
        string project,
        IReadOnlySet<string> runningIds,
        Func<TaskInfo, TaskParallelism> predict)
    {
        var result = new List<InFlightTask>();
        foreach (var card in cards)
        {
            if (card is null || card.Fixture) continue;
            if (!string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase)) continue;
            if (runningIds.Contains(card.Id)) continue;
            if (!IsCurrentCard(card) || !IsInFlight(card)) continue;
            // An exclusive card blocks only while it runs, so an in-flight
            // entry carries its scope and never the exclusive flag.
            var scope = predict(card)?.PredictedScope ?? Array.Empty<string>();
            result.Add(new InFlightTask(card.Id, new TaskParallelism(false, scope), card.State)
                { CardKey = card.Key ?? card.TaskKey ?? card.Id, Version = DeliveryVersion(card) });
        }
        return result;
    }

    /// <summary>
    /// The part of <paramref name="inFlight"/> a candidate is compared with. A
    /// candidate that already started (a merge round in Ready, a Progress card
    /// without a run) is compared only with post-coding cards. Two started
    /// cards that both wait for a pickup can then never hold each other, while
    /// a fresh candidate still waits for every started card it overlaps.
    /// </summary>
    public static IReadOnlyList<InFlightTask> For(TaskInfo candidate, IReadOnlyList<InFlightTask> inFlight)
    {
        if (!HasStartedCodingRun(candidate)) return inFlight;
        return inFlight.Where(item => PostCodingLanes.Contains(item.Lane, StringComparer.Ordinal)).ToArray();
    }

    /// <summary>True when the card started a coding run and has not integrated it yet.</summary>
    public static bool IsInFlight(TaskInfo card)
    {
        if (!IsCurrentCard(card)) return false;
        if (card.NoBranchExpected) return false;
        if (TaskModes.IsReportOnly(card.Mode)) return false;
        var lane = card.State;
        if (lane is TaskStates.Completed or TaskStates.Archive) return false;
        if (HasIntegratedSinceLastStart(card)) return false;
        if (string.Equals(lane, TaskStates.Progress, StringComparison.Ordinal))
            return true;
        if (lane == TaskStates.HumanReview || lane == TaskStates.Escalated)
            return HasOpenDelivery(card);
        if (lane == TaskStates.AutoReview) return true;
        if (string.Equals(lane, TaskStates.Ready, StringComparison.Ordinal))
            return card.PendingIntent is not null
                   && HasStartedCodingRun(card)
                   && !HasIntegratedSinceLastStart(card);
        return false;
    }

    /// <summary>
    /// True when the card entered Progress before or carries a delivery commit.
    /// A card in Progress or a post-coding lane has started by definition.
    /// </summary>
    public static bool HasStartedCodingRun(TaskInfo card)
    {
        if (string.Equals(card.State, TaskStates.Progress, StringComparison.Ordinal)
            || PostCodingLanes.Contains(card.State, StringComparer.Ordinal))
            return true;
        return LastCodingStart(card) is not null
               || card.Commits.Count > 0
               || card.Commit is not null;
    }

    /// <summary>
    /// True when a durable integration fact is newer than the start of the
    /// current coding run: the provenance merge record, or an integration
    /// record of an integrated class. Without a recorded start any such fact
    /// counts.
    /// </summary>
    public static bool HasIntegratedSinceLastStart(TaskInfo card)
    {
        if (card.Integration?.Status == IntegrationStatuses.Integrated) return true;
        var since = LastCodingStart(card);
        if (card.Provenance?.Merge is { MergeCommit.Length: > 0 } merge
            && (since is null || merge.AtUtc >= since.Value))
            return true;
        return card.IntegrationRecords.Any(record =>
            IntegratedRecordClasses.Contains(record.Classification, StringComparer.Ordinal)
            && (since is null || record.RecordedAtUtc >= since.Value));
    }

    public static bool HasOpenDelivery(TaskInfo card)
        => TaskIntegrationStatusService.AttributedCommitRecords(card).Count > 0
           || TaskIntegrationStatusService.CurrentReviewSubject(card) is { ResultSha.Length: > 0 };

    /// <summary>Do not hold work behind a stale index entry after a lane move or folder removal.</summary>
    internal static bool IsCurrentCard(TaskInfo card)
    {
        if (string.IsNullOrEmpty(card.FolderPath)) return true;
        var file = Path.Combine(card.FolderPath, "task.json");
        if (!File.Exists(file)) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            return !json.RootElement.TryGetProperty("state", out var state)
                || state.GetString() == card.State;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Overlap comparison: current lane unavailable");
            return false;
        }
    }

    public static bool IsDeliveryRecovery(TaskInfo card)
    {
        var reason = card.PendingIntent?.SavedReason ?? "";
        return reason is "delivery-attribution-ambiguous" or "merge-conflict" or "source-needs-rebase"
            or "delivery-verdict-stale" or "delivery-merge-round"
            || reason.StartsWith("acceptance-rail:", StringComparison.Ordinal)
            || reason.StartsWith("integration-", StringComparison.Ordinal);
    }

    internal static string DeliveryVersion(TaskInfo card)
        => $"{card.EnteredLaneAt.Ticks}:{card.PendingIntent?.SavedAt.Ticks}:{string.Join(',', card.Commits.Select(commit => commit.Sha))}";

    private static DateTime? LastCodingStart(TaskInfo card)
        => card.Provenance?.Transitions
            .LastOrDefault(transition => string.Equals(transition.Lane, TaskStates.Progress, StringComparison.Ordinal))
            ?.AtUtc;
}
