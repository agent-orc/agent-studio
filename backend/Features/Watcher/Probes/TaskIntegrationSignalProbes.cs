using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Watcher;

/// <summary>
/// Repetition detector (§10.2) wired to the durable integration-status
/// projection every task already carries. Groups cards in one project that
/// share the same integration failure fingerprint (<see cref="TaskIntegrationFailure.Code"/>)
/// - the real signal behind the QS-102 "nine reviewed deliveries blocked by
/// one dirty integration checkout" finding.
/// </summary>
public sealed class IntegrationFailureRepetitionProbe : IWatcherSignalProbe
{
    public string Name => "integration-failure-repetition";

    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _configuration;

    public IntegrationFailureRepetitionProbe(TaskScannerService scanner, IConfiguration configuration)
    {
        _scanner = scanner;
        _configuration = configuration;
    }

    public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc)
    {
        var minCards = Math.Max(2, _configuration.GetValue("Watcher:RepetitionMinAffectedCards", 2));
        var tasks = _scanner.ScanAllJobs()
            .Where(t => t.Integration is { Status: IntegrationStatuses.ConflictSkipped, Failure: not null });

        var results = new List<WatcherSignalObservation>();
        foreach (var group in tasks.GroupBy(t => (t.ProjectName, t.Integration!.Failure!.Code)))
        {
            var members = group.ToList();
            if (members.Count < minCards) continue;
            var failure = members[0].Integration!.Failure!;
            results.Add(new WatcherSignalObservation
            {
                DetectorClass = WatcherDetectorClasses.Repetition,
                Project = group.Key.ProjectName,
                FingerprintKey = group.Key.Code,
                Summary = $"{members.Count} cards in {group.Key.ProjectName} blocked by the same integration failure '{failure.Label}': {failure.Reason}",
                ObservedAtUtc = nowUtc,
                AffectedCards = members.Select(t => t.Id).ToList(),
                Details = new Dictionary<string, string>
                {
                    ["failureCode"] = failure.Code,
                    ["failureReason"] = failure.Reason,
                    ["rebaseRecoveryAvailable"] = failure.RebaseRecoveryAvailable.ToString(),
                    ["affectedCount"] = members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            });
        }
        return results;
    }
}

/// <summary>
/// Contradiction detector (§10.2) wired to the durable lane-vs-integration
/// projection. A card that reached a completed/reviewed lane while its own
/// integration truth says the work never landed is exactly the "Completed
/// without Git-proven integration" contradiction of the AGT-2531 mockup and
/// the AGT-2713 crash-plus-empty-completion finding of §10.1.
/// </summary>
public sealed class TaskLaneIntegrationContradictionProbe : IWatcherSignalProbe
{
    public string Name => "lane-integration-contradiction";

    private static readonly string[] ContradictingLanes = [TaskStates.Completed, TaskStates.AutoReview];
    private static readonly string[] ContradictingIntegrationStatuses = [IntegrationStatuses.NoBranch, IntegrationStatuses.ConflictSkipped];

    private readonly TaskScannerService _scanner;

    public TaskLaneIntegrationContradictionProbe(TaskScannerService scanner)
    {
        _scanner = scanner;
    }

    public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc)
    {
        var results = new List<WatcherSignalObservation>();
        foreach (var task in _scanner.ScanAllJobs())
        {
            if (!ContradictingLanes.Contains(task.State)) continue;
            var status = task.Integration?.Status ?? IntegrationStatuses.NoBranch;
            if (!ContradictingIntegrationStatuses.Contains(status)) continue;

            results.Add(new WatcherSignalObservation
            {
                DetectorClass = WatcherDetectorClasses.Contradiction,
                Project = task.ProjectName,
                FingerprintKey = $"{task.Id}:{status}",
                Summary = $"{task.Id} sits in {task.State} but integration status is '{status}': the lane projection and Git-proven integration truth disagree.",
                ObservedAtUtc = nowUtc,
                AffectedCards = [task.Id],
                Details = new Dictionary<string, string>
                {
                    ["lane"] = task.State,
                    ["integrationStatus"] = status,
                    ["integrationDetail"] = task.Integration?.Detail ?? "(none)",
                },
            });
        }
        return results;
    }
}
