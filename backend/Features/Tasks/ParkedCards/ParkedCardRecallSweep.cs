using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>
/// Wiedervorlage for parked cards: re-checks the recorded precondition of every
/// card sitting in a human-decision lane and REPORTS the ones whose blocker is
/// gone.
///
/// <para>The incident this closes: AGT-2220 was parked on 2026-07-29 with
/// "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto
/// rerun". On 2026-08-02 the documented remedy ran and the precondition was gone.
/// Nothing reacted, because nothing was watching - four days of standstill on a
/// card that was ready. This sweep is the thing that watches.</para>
///
/// <para><b>Report-only, on purpose.</b> The sweep never moves a card and never
/// re-queues one. "No auto rerun" was a deliberate decision by whoever parked the
/// card, and a resolved infrastructure precondition does not overrule it - it only
/// means the card is worth a person's attention again.
/// <see cref="ParkedCardRecall"/> has no target lane for exactly that reason, and
/// the sweep is not on the <c>HumanReviewVerdictDriftTest</c> whitelist because
/// it performs no lane write at all.</para>
/// </summary>
public sealed class ParkedCardRecallSweep
{
    private readonly TaskScannerService _scanner;
    private readonly IParkedBlockerProbe _probe;
    private readonly TimelineLog? _timeline;
    private readonly GitService? _git;
    private readonly ILogger<ParkedCardRecallSweep> _logger;
    private readonly TimeProvider _clock;

    public ParkedCardRecallSweep(
        TaskScannerService scanner,
        IParkedBlockerProbe probe,
        ILogger<ParkedCardRecallSweep> logger,
        TimelineLog? timeline = null,
        GitService? git = null,
        TimeProvider? clock = null)
    {
        _scanner = scanner;
        _probe = probe;
        _logger = logger;
        _timeline = timeline;
        _git = git;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Evaluates every parked card once. Returns one entry per parked card,
    /// including the ones that stay blocked, so callers and tests can assert the
    /// full picture rather than re-deriving it from the markers.
    /// </summary>
    public IReadOnlyList<ParkedCardRecall> Sweep(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var results = new List<ParkedCardRecall>();
        // One resolution per project, not per card: the lookup walks the watch
        // paths and can shell out to git, and this runs on the request path of
        // GET /api/parked-cards as well as on the timer.
        var repositoryRoots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in _scanner.ScanAllAutomationJobs())
        {
            ct.ThrowIfCancellationRequested();
            if (!ParkedBlockerCatalog.IsParkedLane(task.State)) continue;
            if (string.IsNullOrWhiteSpace(task.FolderPath)) continue;

            // A card parked before this feature existed has no marker. Backfill
            // one from the lane-entry stamp so legacy parks age visibly instead
            // of staying invisible - the AGT-2220 card itself is in this class.
            var stored = ParkedBlockerMarker.TryRead(task.FolderPath, _logger);
            var record = stored
                ?? ParkedBlockerCatalog.Build(task.State, reason: null, parkedAt: task.EnteredLaneAt)!;

            var evaluation = Evaluate(record, task, ResolveRepositoryRoot(task, repositoryRoots), now);
            var announced = ParkedCardRecallPolicy.ShouldAnnounce(record, evaluation);
            var recall = ParkedCardRecallPolicy.Decide(ToCandidate(task), record, evaluation, now);

            if (announced) Announce(task, recall);

            var folded = ParkedCardRecallPolicy.Fold(record, evaluation, announced, now);
            if (ParkedCardRecallPolicy.NeedsPersist(stored, folded))
                ParkedBlockerMarker.Write(task.FolderPath, folded, _logger);

            results.Add(recall);
        }

        return results;
    }

    private ParkedBlockerEvaluation Evaluate(
        ParkedBlockerRecord record, TaskInfo task, string? repositoryRoot, DateTime now)
    {
        try
        {
            return _probe.Evaluate(record.Condition, BuildContext(task, repositoryRoot), now);
        }
        catch (Exception ex)
        {
            // A probe fault must never drop the card out of the report: an
            // unreported parked card is the failure mode this sweep exists to
            // remove.
            _logger.LogWarning(ex,
                "parked-card-recall: probe failed for {Project}/{JobId} (condition={Kind})",
                task.ProjectName, task.Id, record.Condition.Kind);
            return new ParkedBlockerEvaluation
            {
                Status = ParkedBlockerStatuses.Undeterminable,
                At = now,
                Detail = "The blocker condition could not be evaluated on this pass.",
            };
        }
    }

    private string? ResolveRepositoryRoot(TaskInfo task, Dictionary<string, string?> memo)
    {
        var key = task.WatchPath ?? string.Empty;
        if (memo.TryGetValue(key, out var cached)) return cached;

        string? root = null;
        try { root = _git?.ResolveRepoRootForWatchPath(task.WatchPath); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "parked-card-recall: optional repository-root resolution for the blocker context.");
        }

        memo[key] = root;
        return root;
    }

    private static ParkedBlockerContext BuildContext(TaskInfo task, string? repositoryRoot)
    {
        var taskBranch = task.Provenance?.Branch;
        if (string.IsNullOrWhiteSpace(taskBranch))
            taskBranch = task.Commits.Select(commit => commit.Branch).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));

        return new ParkedBlockerContext(
            repositoryRoot,
            string.IsNullOrWhiteSpace(taskBranch) ? null : taskBranch,
            TaskIntegrationBranch.Name(task.IntegrationBranch),
            !string.IsNullOrWhiteSpace(task.Key) ? task.Key : task.TaskKey);
    }

    private static ParkedCardCandidate ToCandidate(TaskInfo task)
        => new(task.ProjectName, task.Id, task.TaskKey, task.Title, task.State, task.EnteredLaneAt);

    private void Announce(TaskInfo task, ParkedCardRecall recall)
    {
        var days = Math.Round(recall.ParkedForSeconds / 86400d, 1);
        try
        {
            _timeline?.Append(
                task.FolderPath,
                TimelineEventKinds.ParkedBlockerResolved,
                TimelineActors.System,
                summary: $"Parked blocker resolved after {days}d - ready for a human to re-queue: {recall.Detail}",
                details: new Dictionary<string, string>
                {
                    ["lane"] = recall.Lane,
                    ["blockerType"] = recall.BlockerType,
                    ["conditionKind"] = recall.ConditionKind,
                    ["parkedForSeconds"] = recall.ParkedForSeconds
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["detail"] = recall.Detail,
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "parked-card-recall: failed to append the recall ledger row for {Project}/{JobId}",
                task.ProjectName, task.Id);
        }

        _logger.LogInformation(
            "parked-card-recall: {Project}/{JobId} is recallable after {Days}d in {Lane} ({BlockerType}): {Detail}",
            task.ProjectName, task.Id, days, recall.Lane, recall.BlockerType, recall.Detail);
    }
}
