namespace AgentStudio.Pipeline;

public sealed record AcceptedIntegrationAlertItem
{
    public string TaskKey { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime AcceptedAt { get; init; }
    public string IntegrationStatus { get; init; } = IntegrationStatuses.Pending;
    public string? LastOutcome { get; init; }
    public string? Detail { get; init; }
}

public sealed record AcceptedIntegrationAlertSnapshot
{
    public bool Active { get; init; }
    public int StalledTaskCount { get; init; }
    public int ThresholdMinutes { get; init; }
    public DateTime? OldestAcceptedAt { get; init; }
    public DateTime ObservedAt { get; init; }
    public IReadOnlyList<AcceptedIntegrationAlertItem> Items { get; init; } = [];
}

internal sealed record AcceptedIntegrationAlertCandidate
{
    public required TaskInfo Task { get; init; }
    public DateTime AcceptedAt { get; init; }
    public bool HasIntegrationRecord { get; init; }
    public bool HasHistoricalVerification { get; init; }
    public string? IntegrationStatus { get; init; }
    public string? LastOutcome { get; init; }
    public string? Detail { get; init; }
}

internal sealed record AcceptedIntegrationSweepSummary(
    int Attempted,
    int Merged,
    int AlreadyMerged,
    int Failed)
{
    public int Integrated => Merged;
}

/// <summary>
/// Pure decisions for truthful accepted-integration sweep telemetry and the
/// 30-minute accepted-without-integration invariant.
/// </summary>
internal static class AcceptedIntegrationBackstopPolicy
{
    public static bool IsRecoveryCandidate(TaskInfo task)
    {
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(task)) return false;
        // Historical verification rows are bookkeeping facts, never a request
        // to re-run integration or move a terminal card.
        if (TaskIntegrationRecordDetector.LatestVerification(task) is not null) return false;
        if (task.State is TaskStates.Completed or TaskStates.Archive) return true;
        return task.State == TaskStates.HumanReview
               && string.Equals(task.Phase, LifecyclePhases.Integrating, StringComparison.Ordinal);
    }

    public static bool IsAlertCandidate(AcceptedIntegrationAlertCandidate candidate)
    {
        if (candidate.HasHistoricalVerification) return false;
        if (!candidate.HasIntegrationRecord) return false;
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(candidate.Task)) return false;
        return candidate.Task.State is TaskStates.Completed or TaskStates.Archive;
    }

    public static AcceptedIntegrationSweepSummary Summarize(
        IEnumerable<MergeIntoIntegrationOutcome> outcomes)
    {
        var attempted = 0;
        var merged = 0;
        var alreadyMerged = 0;
        var failed = 0;

        foreach (var outcome in outcomes)
        {
            attempted++;
            switch (outcome)
            {
                case MergeIntoIntegrationOutcome.Merged:
                case MergeIntoIntegrationOutcome.MergedAfterRebase:
                    merged++;
                    break;
                case MergeIntoIntegrationOutcome.AlreadyMerged:
                case MergeIntoIntegrationOutcome.AlreadyOnIntegrationBranch:
                    alreadyMerged++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new AcceptedIntegrationSweepSummary(
            attempted,
            merged,
            alreadyMerged,
            failed);
    }

    public static AcceptedIntegrationAlertSnapshot EvaluateAlert(
        DateTime now,
        TimeSpan threshold,
        IEnumerable<AcceptedIntegrationAlertCandidate> candidates)
    {
        var items = candidates
            .Where(IsAlertCandidate)
            .Where(candidate => candidate.AcceptedAt != default)
            .Where(candidate => now - candidate.AcceptedAt.ToUniversalTime() >= threshold)
            .Where(candidate => !string.Equals(
                candidate.IntegrationStatus,
                IntegrationStatuses.Integrated,
                StringComparison.Ordinal))
            .OrderBy(candidate => candidate.AcceptedAt)
            .ThenBy(candidate => candidate.Task.TaskKey, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new AcceptedIntegrationAlertItem
            {
                TaskKey = ResolveTaskKey(candidate.Task),
                TaskId = candidate.Task.Id,
                ProjectName = candidate.Task.ProjectName,
                Title = candidate.Task.Title,
                AcceptedAt = candidate.AcceptedAt.ToUniversalTime(),
                IntegrationStatus = string.IsNullOrWhiteSpace(candidate.IntegrationStatus)
                    ? IntegrationStatuses.Pending
                    : candidate.IntegrationStatus,
                LastOutcome = candidate.LastOutcome,
                Detail = candidate.Detail,
            })
            .ToList();

        return new AcceptedIntegrationAlertSnapshot
        {
            Active = items.Count > 0,
            StalledTaskCount = items.Count,
            ThresholdMinutes = Math.Max(1, (int)Math.Ceiling(threshold.TotalMinutes)),
            OldestAcceptedAt = items.FirstOrDefault()?.AcceptedAt,
            ObservedAt = now,
            Items = items,
        };
    }

    private static string ResolveTaskKey(TaskInfo task)
    {
        if (!string.IsNullOrWhiteSpace(task.Key)) return task.Key;
        if (!string.IsNullOrWhiteSpace(task.TaskKey)) return task.TaskKey;
        return task.Id;
    }
}

internal static class AcceptedIntegrationBackstopTelemetry
{
    public static void LogSweep(
        ILogger logger,
        AcceptedIntegrationSweepSummary summary)
    {
        logger.LogInformation(
            "accepted-integration-backstop sweep attempted={Attempted} merged={Merged} alreadyMerged={AlreadyMerged} failed={Failed} integrated={Integrated}",
            summary.Attempted,
            summary.Merged,
            summary.AlreadyMerged,
            summary.Failed,
            summary.Integrated);
    }
}

internal sealed class AcceptedIntegrationAlertLogState
{
    private string? _warningSignature;
    private DateTime? _lastWarningAt;

    public void Publish(
        ILogger logger,
        AcceptedIntegrationAlertSnapshot previous,
        AcceptedIntegrationAlertSnapshot next,
        DateTime now)
    {
        if (!next.Active)
        {
            if (previous.Active)
                logger.LogInformation("accepted-integration-stall-recovered");
            _warningSignature = null;
            _lastWarningAt = null;
            return;
        }

        var taskKeys = string.Join(
            ',',
            next.Items
                .Select(item => item.TaskKey)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        var repeatDue = _lastWarningAt is null
                        || now - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (string.Equals(taskKeys, _warningSignature, StringComparison.Ordinal)
            && !repeatDue)
        {
            return;
        }

        logger.LogWarning(
            "accepted-integration-stalled thresholdMinutes={ThresholdMinutes} taskKeys={TaskKeys} oldestAcceptedAt={OldestAcceptedAt}",
            next.ThresholdMinutes,
            taskKeys,
            next.OldestAcceptedAt);
        _warningSignature = taskKeys;
        _lastWarningAt = now;
    }
}
