namespace AgentStudio.Watcher;

/// <summary>
/// Builds the normalized sweep input from live sources. The fixture matrix
/// supplies the same shape, so detection is identical in production and in
/// replay.
/// </summary>
public interface IWatcherEvidenceCollector
{
    WatcherSweepInput Collect(DateTime nowUtc);
}

/// <summary>
/// The in-process collector. It reads only what the Task Server already holds
/// and records an absent source as an explicit fact rather than guessing.
/// </summary>
/// <remarks>
/// <para>
/// Two of the eight rules are wired to live sources in this slice, because
/// their signals are complete inside this process: the empty-completion
/// contradiction (task state plus commit attribution) and the aged
/// descriptor-validation hygiene rule (the catalogue already recomputes its
/// errors on every list).
/// </para>
/// <para>
/// The other six rules stay fixture-covered until their producer emits a
/// durable signal. Each gap is a real one, not an oversight, and is listed in
/// the dossier's implementation state:
/// </para>
/// <list type="bullet">
///   <item><b>probe-error-repeat</b> needs a probe history. <c>quota-cache.json</c>
///   keeps only the latest snapshot and overwrites <c>ProbeFailedAt</c> on every
///   failure, so "N consecutive cycles" cannot be derived from it.</item>
///   <item><b>runner-snapshot-silence</b> and <b>review-attempt-repeat</b> live in
///   the Task Server's SQLite plane, reachable only across the versioned API.</item>
///   <item><b>integration-failure-repeat</b> needs the failure reason kept as a
///   normalized fingerprint; today it is free text on the integration result.</item>
///   <item><b>tool-drift-dependent-failure</b> needs the CLI version change to be
///   durable; <c>CliVersionTracker</c> only logs it.</item>
///   <item><b>projection-count-mismatch</b> needs the escalation banner counts
///   server-side; they are computed in the frontend projection today.</item>
/// </list>
/// </remarks>
public sealed class WatcherEvidenceCollector : IWatcherEvidenceCollector
{
    /// <summary>Lanes in which a completion claim is already visible to an operator.</summary>
    private static readonly HashSet<string> SettledLanes = new(StringComparer.Ordinal)
    {
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
    };

    private readonly TaskScannerService _scanner;
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly ILogger<WatcherEvidenceCollector> _logger;

    public WatcherEvidenceCollector(
        TaskScannerService scanner,
        WorkbenchCatalogueService catalogue,
        ILogger<WatcherEvidenceCollector> logger)
    {
        _scanner = scanner;
        _catalogue = catalogue;
        _logger = logger;
    }

    public WatcherSweepInput Collect(DateTime nowUtc) => new()
    {
        NowUtc = nowUtc,
        Completions = CollectCompletions(),
        ValidationErrors = CollectValidationErrors(),
    };

    /// <summary>
    /// A card that reads as finished but carries no attributed commit. The
    /// detector decides whether that is a contradiction; the collector only
    /// reports the two facts.
    /// </summary>
    private List<WatcherCompletionSignal> CollectCompletions()
    {
        var signals = new List<WatcherCompletionSignal>();
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (!SettledLanes.Contains(job.State)) continue;
            if (job.NoBranchExpected) continue;

            var attributed = TaskIntegrationStatusService.AttributedCommits(job);
            if (attributed.Count > 0) continue;

            signals.Add(new WatcherCompletionSignal
            {
                Project = job.ProjectName,
                TaskKey = job.TaskKey,
                CompletedAtUtc = job.ExternalCompletion?.CompletedAt ?? job.EnteredLaneAt,
                PrecedingTypedOutcome = job.OutcomeIssue?.Kind,
                ExternalCompletion = job.ExternalCompletion is not null,
                BaseSha = null,
                ResultSha = null,
                AttributedCommits = 0,
                LandedState = job.State,
            });
        }

        return signals;
    }

    /// <summary>
    /// Descriptor errors the catalogue already computes. The catalogue has no
    /// first-seen timestamp, so the card's own edit time is the closest honest
    /// proxy for how long the error has stood.
    /// </summary>
    private List<WatcherValidationErrorSignal> CollectValidationErrors()
    {
        var signals = new List<WatcherValidationErrorSignal>();
        foreach (var project in _scanner.GetWatchPaths())
        {
            WorkbenchCatalogue? catalogue;
            try
            {
                catalogue = _catalogue.List(project.Name, includeHistory: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "watcher-catalogue-unreadable project={Project}", project.Name);
                continue;
            }

            if (catalogue is null) continue;
            foreach (var item in catalogue.Items.Where(row => !row.Valid))
            {
                signals.Add(new WatcherValidationErrorSignal
                {
                    Project = project.Name,
                    Subject = item.EntryPath is { Length: > 0 } ? item.EntryPath : item.Id,
                    Message = item.Error ?? "Descriptor needs repair.",
                    FirstSeenAtUtc = item.UpdatedAtUtc,
                });
            }
        }

        return signals;
    }
}
