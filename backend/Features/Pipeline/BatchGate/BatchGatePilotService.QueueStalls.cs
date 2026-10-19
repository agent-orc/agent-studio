using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Pipeline;

/// <summary>Releasable cards, the open stall episode and recent stall events of one project.</summary>
public sealed record GateQueueStallReport(
    string Project, IReadOnlyList<string> Releasable,
    GateQueueStallEvent? OpenEpisode, IReadOnlyList<GateQueueStallEvent> RecentEvents);

public sealed record GateQueueStallRefusal(string TaskKey, string Reason);

public sealed record GateQueueStallRelease(
    IReadOnlyList<string> Released, IReadOnlyList<GateQueueStallRefusal> Refused);

/// <summary>
/// AGT-3219: a gate request that spent its budget waiting for a machine slot
/// never ran. The card stays in Auto Review, its request returns to the batch
/// queue, and the project raises one stall event per queue episode. Cards that
/// an earlier build escalated for this reason return to the queue through one
/// operator release.
/// </summary>
public sealed partial class BatchGatePilotService
{
    public const string QueueStallTopic = "gate-queue-stall";
    internal const string QueueReleaseDetail = "gate-queue-release";
    private const int RecentQueueStalls = 10;

    public GateQueueStallMonitor QueueStalls => _queueStalls;

    /// <summary>
    /// True when the review waits in the durable batch queue, for example after
    /// its emergency per-task gate overran the queue and was admitted there.
    /// </summary>
    public bool IsQueuedForBatch(string reviewAttemptId) => _store.IsQueued(reviewAttemptId);

    /// <summary>
    /// True when the slots in use cannot drain this scope's queue inside the
    /// queue wait budget of one gate request; the planner then prefers batches.
    /// </summary>
    private bool GateBacklogged(string project, IReadOnlyList<BatchGatePendingRecord> current)
    {
        var facts = QueueFacts(project, current.Count(Queued));
        var assessment = MonolithTransitionAdapter.GateQueue(facts);
        if (assessment.Backlogged)
            _logger.LogDebug(
                "batch-gate-backlog project={Project} depth={Depth} slots={Slots} drain_ms={DrainMs} budget_ms={BudgetMs} advisory={Advisory}",
                project, facts.QueueDepth, facts.SlotsTotal, (long)assessment.EstimatedDrain.TotalMilliseconds,
                (long)facts.QueueBudget.TotalMilliseconds, assessment.Advisory ?? "none");
        return assessment.Backlogged;
    }

    private void ObserveQueueEpisodes(IReadOnlyList<BatchGatePendingRecord> waiting)
    {
        foreach (var project in waiting.Select(item => item.Subject.Project)
                     .Concat(_settings.GetAll().Keys).Distinct(StringComparer.Ordinal)
                     .Where(project => _queueStalls.OpenEpisode(project) is not null))
        {
            if (_queueStalls.Observe(project, waiting.Count(item =>
                    string.Equals(item.Subject.Project, project, StringComparison.Ordinal) && Queued(item))))
                _logger.LogInformation("gate-queue-stall-drained project={Project}", project);
        }
    }

    // A member that waits for its batch: no batch owns it and no per-task gate runs for it.
    private static bool Queued(BatchGatePendingRecord item)
        => item.Subject.OwningBatchId is null && !item.Subject.ActivePerTaskGate;

    private GateQueueFacts QueueFacts(string project, int queuedMembers, TimeSpan? oldestWaitAtLeast = null)
    {
        var budget = BuildTestGateRunner.ResolveQueueWaitTimeout(null,
            TimeSpan.FromMinutes(_settings.Get(project).BatchGate.GateTimeoutMinutes), TimeSpan.FromMinutes(2));
        var configured = new[]
        {
            _gateSlots?.Snapshot().Total ?? 0,
            _configuration?.GetValue("RemoteGate:GateSlots", 0) ?? 0,
            _configuration?.GetValue("LocalGate:GateSlots", 0) ?? 0,
        }.Max();
        return _queueStalls.Facts(project, queuedMembers, budget, configured, oldestWaitAtLeast);
    }

    /// <summary>
    /// Applies the queue overrun rule to a per-task gate result. A requeued
    /// request returns to the durable batch queue: a queued member keeps its
    /// record and sequence, an emergency gate is admitted now when the batch
    /// accepts it. Otherwise its marker stays active with a fresh start budget,
    /// so the admission retry resumes the gate.
    /// </summary>
    private async Task<bool> RequeueAfterQueueOverrunAsync(TaskInfo task, BatchGatePendingRecord pending,
        BuildTestGateResult gate, bool recordInStore, CancellationToken ct)
    {
        var live = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (MonolithTransitionAdapter.GateQueueOverrun(gate.ViolatedBudget?.Name, gate.ViolatedBudget?.Phase,
                live?.State, Refresh(pending).Subject.CurrentGeneration) != GateQueueOverrunAction.Requeue)
            return false;
        var queued = recordInStore ? _store.ReadPending(pending.ReviewAttemptId) : AdmitAfterQueueOverrun(pending);
        await BatchGateOwnershipStore.WriteAsync(live!.FolderPath, queued is null
            ? new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject, FallbackGateActive: true)
            : new BatchGateOwnership(pending.ReviewAttemptId, queued.Subject), ct).ConfigureAwait(false);
        var waitedMs = gate.ViolatedBudget?.ConsumedMs ?? gate.GateQueueWaitMs;
        Lane(queued ?? pending, "gate-queue-stalled", queueWaitMs: waitedMs,
            outcome: queued is null ? "resume-per-task" : "requeued", reason: gate.Reason);
        _logger.LogWarning(
            "batch-fallback-queue-overrun task={TaskKey} attempt={AttemptId} waited_ms={WaitedMs} requeued={Requeued}",
            pending.Subject.TaskKey, pending.ReviewAttemptId, waitedMs, queued is not null);
        // The requeued request itself waits again; its flight is still registered.
        var queuedMembers = 1 + _store.ListPending().Select(Refresh).Count(item =>
            string.Equals(item.Subject.Project, pending.Subject.Project, StringComparison.Ordinal)
            && item.ReviewAttemptId != pending.ReviewAttemptId && Queued(item));
        var facts = QueueFacts(pending.Subject.Project, queuedMembers,
            TimeSpan.FromMilliseconds(Math.Max(0, waitedMs)));
        if (_queueStalls.QueueOverrun(pending.Subject.Project, pending.Subject.TaskKey, facts) is { } stall)
            RaiseQueueStall(stall, live.WatchPath);
        return true;
    }

    private BatchGatePendingRecord? AdmitAfterQueueOverrun(BatchGatePendingRecord pending)
    {
        var options = _settings.Get(pending.Subject.Project).BatchGate;
        if (!options.Enabled || BatchGatePolicy.Exclusion(pending.Subject with { EnqueueSequence = 1 },
                Scope(pending.Subject), options) is not null)
            return null;
        return _store.Enqueue(pending);
    }

    private void RaiseQueueStall(GateQueueStallEvent stall, string watchPath)
    {
        _logger.LogWarning(
            "gate-queue-stall project={Project} episode={EpisodeId} depth={Depth} oldest_wait_ms={OldestWaitMs} slots_busy={SlotsBusy} slots_total={SlotsTotal} drain_ms={DrainMs} budget_ms={BudgetMs} idle_configured_slots={IdleSlots}",
            stall.Project, stall.EpisodeId, stall.QueueDepth, stall.OldestWaitMs, stall.SlotsBusy,
            stall.SlotsTotal, stall.EstimatedDrainMs, stall.QueueBudgetMs, stall.IdleConfiguredSlots);
        _services?.GetService<OrchestratorLog>()?.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = stall.RaisedAtUtc.UtcDateTime,
            Kind = OrchestratorLogKinds.Alert,
            Topic = QueueStallTopic,
            Summary = $"Gate queue stalled for {stall.Project}: {stall.QueueDepth} gate request(s) wait, "
                + $"{stall.SlotsBusy}/{stall.SlotsTotal} slots busy, oldest wait {Minutes(stall.OldestWaitMs)} min, "
                + $"estimated drain {Minutes(stall.EstimatedDrainMs)} min. Cards stay in {TaskStates.AutoReview}.",
            Reasoning = stall.Advisory,
        });
    }

    private static long Minutes(long milliseconds) => (long)Math.Ceiling(milliseconds / 60_000d);

    public GateQueueStallReport QueueStallReport(string project)
        => new(project, QueueStalledEscalations(project).Select(item => item.Task.Key ?? item.Task.Id).ToArray(),
            _queueStalls.OpenEpisode(project),
            _store.ListQueueStalls(project).TakeLast(RecentQueueStalls).ToArray());

    /// <summary>
    /// Returns every card of the project that an earlier build escalated for a
    /// machine gate queue overrun to Auto Review and the batch queue. A pending
    /// record that the escalation resolved is reopened, so it keeps its sequence.
    /// </summary>
    public async Task<GateQueueStallRelease> ReleaseQueueStalledAsync(string project, string actor,
        CancellationToken ct)
    {
        var released = new List<string>();
        var refused = new List<GateQueueStallRefusal>();
        // The tick resolves the pending record of an escalated card; it must
        // not run between the lane move and the admission.
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (task, review, source) in QueueStalledEscalations(project))
            {
                var key = task.Key ?? task.Id;
                var reviewedAt = ReviewedAt(review);
                var candidate = BuildPending(task, review, source, reviewedAt);
                if (BatchGatePolicy.Exclusion(candidate.Subject with { EnqueueSequence = 1 },
                        Scope(candidate.Subject), _settings.Get(project).BatchGate) is { } exclusion)
                {
                    refused.Add(new(key, $"The batch queue refuses the card: {exclusion}."));
                    continue;
                }
                var moved = await _transitions.MoveAsync(task.Id, TaskStates.AutoReview, task.WatchPath, ct,
                    cause: "batch-gate-queue-release",
                    reason: $"{actor} released the card to the batch gate queue after a machine gate queue overrun.",
                    suppressProductExecution: true, expectedSourceState: TaskStates.Escalated,
                    transitionCause: LaneChangeCauses.EscalationRequeue,
                    transitionDetail: QueueReleaseDetail).ConfigureAwait(false);
                if (moved.Status != MoveJobStatus.Success)
                {
                    refused.Add(new(key, $"The lane move failed: {moved.Status}."));
                    continue;
                }
                _scanner.InvalidateCache();
                var live = _scanner.FindJob(task.Id, task.WatchPath) ?? task;
                try
                {
                    _store.ReopenPending(review.AttemptId);
                    await EnqueueAsync(live, review, source, reviewedAt, ct).ConfigureAwait(false);
                    released.Add(key);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
                {
                    _logger.LogError(ex, "batch-gate-queue-release-enqueue-failed task={TaskKey}", key);
                    refused.Add(new(key, "The batch queue refused the card; it returned to Escalated."));
                    await _reviewJournal.EscalateAsync(live.Id, live.WatchPath, live.ProjectName,
                        HumanReviewEscalationCategories.AutoReviewEscalation,
                        "Release to the batch gate queue failed: " + ex.Message, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _tick.Release();
        }
        _logger.LogInformation("batch-gate-queue-release project={Project} actor={Actor} released={Released} refused={Refused}",
            project, actor, released.Count, refused.Count);
        return new GateQueueStallRelease(released, refused);
    }

    /// <summary>
    /// Escalated cards of the project whose current, passed review lost its
    /// per-task gate to a queue overrun: the gate evidence names the queue
    /// budget and the park reason is that overrun.
    /// </summary>
    private IReadOnlyList<(TaskInfo Task, ReviewAttemptDto Review, RunAttemptDto Source)>
        QueueStalledEscalations(string project)
    {
        var found = new List<(TaskInfo, ReviewAttemptDto, RunAttemptDto)>();
        foreach (var task in _scanner.ScanAllJobs().Where(task => task.State == TaskStates.Escalated
                     && string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)))
        {
            var projection = _authority.GetTaskProjection(AuthorityKey(task));
            if (projection.CurrentReviewAttempt is not { } review || projection.CurrentRunAttempt is not { } source
                || review.SourceRunAttemptId != source.AttemptId)
                continue;
            if (ReadFallbackEvidence(task, review.AttemptId) is not { } gate) continue;
            var parkedForQueue = task.ParkedBlocker?.Reason is { } reason
                && reason.Contains(GateQueueStallPolicy.QueueBudgetName, StringComparison.Ordinal)
                && reason.Contains("phase=" + GateQueueStallPolicy.QueuePhase, StringComparison.Ordinal);
            var passed = review.Outcome is ReviewTerminalOutcome.Pass or ReviewTerminalOutcome.IntegrationBranchDefect
                && review.Subject.Plan?.BuildTestDeferredToBatch == true;
            if (parkedForQueue && MonolithTransitionAdapter.GateQueueStallReleasable(task.State,
                    gate.ViolatedBudget?.Name, gate.ViolatedBudget?.Phase, currentGeneration: true, passed))
                found.Add((task, review, source));
        }
        return found;
    }

    private BuildTestGateResult? ReadFallbackEvidence(TaskInfo task, string reviewAttemptId)
    {
        var path = Path.Combine(TaskPaths.LogsDir(task.FolderPath), $"batch-fallback-{reviewAttemptId}.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<BuildTestGateResult>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "batch-fallback-evidence-unreadable task={TaskKey} path={Path}",
                task.Key ?? task.Id, path);
            return null;
        }
    }

    // The batch planner ages a member by its accepted report, as the admission does.
    private static DateTimeOffset ReviewedAt(ReviewAttemptDto review)
    {
        var reviewedAt = review.Reports.LastOrDefault(report => report.AuthorityStatus == AttemptWriteStatus.Accepted)
                ?.ReceivedAt
            ?? review.TerminalAt
            ?? throw new InvalidDataException("The released review has no accepted report.");
        return new DateTimeOffset(DateTime.SpecifyKind(reviewedAt, DateTimeKind.Utc));
    }
}
