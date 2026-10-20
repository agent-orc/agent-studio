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
/// queue, and the project raises one stall event per queue episode, repeated
/// while the episode stays open. A review generation whose summed queue wait
/// reaches its cap escalates as a gate capacity problem. Cards escalated for
/// either reason return to the queue through one operator release.
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

    private bool _queueWaitersRestored;

    /// <summary>
    /// Once per process, before lost per-task gates resume: every queued
    /// review whose durable per-task marker is active waits for a slot again,
    /// so the first tick does not read a drained queue and close the open
    /// episode. A review that overran before waits from its last overrun in
    /// the queue wait ledger. A resumed gate that reports its wait keeps the
    /// restored start; restored waiters that no longer wait are dropped.
    /// </summary>
    private void RestoreQueueWaiters()
    {
        if (_queueWaitersRestored && !_queueStalls.HasRestored) return;
        var resuming = _store.ListPending().Where(item =>
                _scanner.FindJob(item.Subject.TaskKey, item.WatchPath) is { } task
                && BatchGateOwnershipStore.Read(task.FolderPath) is
                    { FallbackGateActive: true, BatchId: null } ownership
                && ownership.ReviewAttemptId == item.ReviewAttemptId)
            .ToDictionary(item => item.ReviewAttemptId, StringComparer.Ordinal);
        if (!_queueWaitersRestored)
        {
            _queueWaitersRestored = true;
            foreach (var item in resuming.Values)
                _queueStalls.Restore(item.Subject.Project, item.ReviewAttemptId,
                    _store.ReadQueueWait(item.ReviewAttemptId)?.LastOverrunAtUtc ?? _time.GetUtcNow());
        }
        _queueStalls.PruneRestored(resuming.ContainsKey);
    }

    private void ObserveQueueEpisodes(IReadOnlyList<BatchGatePendingRecord> waiting)
    {
        foreach (var project in waiting.Select(item => item.Subject.Project)
                     .Concat(_settings.GetAll().Keys).Distinct(StringComparer.Ordinal)
                     .Where(project => _queueStalls.OpenEpisode(project) is not null))
        {
            var queued = waiting.Count(item =>
                string.Equals(item.Subject.Project, project, StringComparison.Ordinal) && Queued(item));
            if (_queueStalls.Observe(project, queued))
            {
                _logger.LogInformation("gate-queue-stall-drained project={Project}", project);
                continue;
            }
            // A sustained backlog keeps the episode open; it must not go quiet.
            if (_queueStalls.Remind(project, QueueFacts(project, queued)) is { } reminder
                && ProjectWatchPath(project, waiting) is { } watchPath)
                RaiseQueueStall(reminder, watchPath);
        }
    }

    private string? ProjectWatchPath(string project, IReadOnlyList<BatchGatePendingRecord> waiting)
        => waiting.FirstOrDefault(item => string.Equals(item.Subject.Project, project, StringComparison.Ordinal))
               ?.WatchPath
           ?? _scanner.GetWatchPaths().FirstOrDefault(entry =>
               string.Equals(entry.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;

    // A member that waits for its batch: no batch owns it and no per-task gate runs for it.
    private static bool Queued(BatchGatePendingRecord item)
        => item.Subject.OwningBatchId is null && !item.Subject.ActivePerTaskGate;

    private TimeSpan QueueBudget(string project)
        => BuildTestGateRunner.ResolveQueueWaitTimeout(null,
            TimeSpan.FromMinutes(_settings.Get(project).BatchGate.GateTimeoutMinutes), TimeSpan.FromMinutes(2));

    private GateQueueFacts QueueFacts(string project, int queuedMembers, TimeSpan? oldestWaitAtLeast = null)
    {
        var budget = QueueBudget(project);
        var configured = new[]
        {
            _gateSlots?.Snapshot().Total ?? 0,
            _configuration?.GetValue("RemoteGate:GateSlots", 0) ?? 0,
            _configuration?.GetValue("LocalGate:GateSlots", 0) ?? 0,
        }.Max();
        return _queueStalls.Facts(project, queuedMembers, budget, configured, oldestWaitAtLeast);
    }

    /// <summary>
    /// Re-reads the generation after a per-task gate returned. A result of any
    /// verdict for a review that is no longer current, or for a card that left
    /// Auto Review, resolves as superseded and never escalates. A queue overrun
    /// of the current review returns to the durable batch queue: a queued
    /// member keeps its record and sequence, an emergency gate is admitted now
    /// when the batch accepts it. Otherwise its marker stays active with a
    /// fresh start budget, so the admission retry resumes the gate. Every
    /// overrun adds its wait to the review generation's durable ledger; at the
    /// cap the card escalates as a gate capacity problem instead of looping.
    /// Returns false when the result is the card's to settle or a cancellation.
    /// </summary>
    private async Task<bool> ResolveBeforeSettlementAsync(TaskInfo task, BatchGatePendingRecord pending,
        BuildTestGateResult gate, string evidence, DateTimeOffset started, bool recordInStore, CancellationToken ct)
    {
        // GateCancellationPolicy classifies a cancelled gate, a stale one
        // included; the escalation itself refuses a generation that is not current.
        if (gate.FailureKind == BuildTestGateFailureKind.Cancellation)
            return false;
        var live = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        var current = Refresh(pending).Subject.CurrentGeneration;
        var waitedMs = Math.Max(0, gate.ViolatedBudget?.ConsumedMs ?? gate.GateQueueWaitMs);
        // The cap counts in budgets of the request that overran; the project
        // budget stands in when the gate did not report its limit.
        var cap = MonolithTransitionAdapter.GateQueueWaitCap(gate.ViolatedBudget is { LimitMs: > 0 } budget
            ? TimeSpan.FromMilliseconds(budget.LimitMs) : QueueBudget(pending.Subject.Project));
        var priorWaitMs = _store.ReadQueueWait(pending.ReviewAttemptId)?.TotalQueueWaitMs ?? 0;
        var action = MonolithTransitionAdapter.GateQueueOverrun(gate.ViolatedBudget?.Name,
            gate.ViolatedBudget?.Phase, live?.State, current, TimeSpan.FromMilliseconds(priorWaitMs + waitedMs), cap);
        if (action == GateQueueOverrunAction.Settle)
            return false;
        if (action == GateQueueOverrunAction.Supersede)
        {
            _logger.LogWarning(
                "batch-fallback-result-superseded task={TaskKey} attempt={AttemptId} verdict={Verdict} lane={Lane} pending_intent={PendingIntent}",
                pending.Subject.TaskKey, pending.ReviewAttemptId, gate.Verdict, live?.State ?? "missing",
                live?.PendingIntent is not null);
            // A gate that reached a slot still cost host minutes. The evidence
            // follows the card's folder; a moved card must not regain its old one.
            if (live is not null
                && !MonolithTransitionAdapter.IsGateQueueOverrun(gate.ViolatedBudget?.Name, gate.ViolatedBudget?.Phase))
                RecordFallbackExecution(pending, gate,
                    Path.Combine(TaskPaths.LogsDir(live.FolderPath), Path.GetFileName(evidence)), started);
            SupersedeFallbackResult(live ?? task, pending, recordInStore);
            return true;
        }
        var ledger = _store.AddQueueWait(pending.ReviewAttemptId, pending.Subject.TaskKey, waitedMs,
            _time.GetUtcNow());
        if (action == GateQueueOverrunAction.EscalateCapacity)
        {
            await EscalateGateCapacityAsync(live!, pending, gate, evidence, ledger, cap, recordInStore, ct)
                .ConfigureAwait(false);
            return true;
        }
        var queued = recordInStore ? _store.ReadPending(pending.ReviewAttemptId) : AdmitAfterQueueOverrun(pending);
        await BatchGateOwnershipStore.WriteAsync(live!.FolderPath, queued is null
            ? new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject, FallbackGateActive: true)
            : new BatchGateOwnership(pending.ReviewAttemptId, queued.Subject), ct).ConfigureAwait(false);
        Lane(queued ?? pending, "gate-queue-stalled", queueWaitMs: waitedMs,
            outcome: queued is null ? "resume-per-task" : "requeued", reason: gate.Reason);
        _logger.LogWarning(
            "batch-fallback-queue-overrun task={TaskKey} attempt={AttemptId} waited_ms={WaitedMs} total_wait_ms={TotalWaitMs} overruns={Overruns} requeued={Requeued}",
            pending.Subject.TaskKey, pending.ReviewAttemptId, waitedMs, ledger.TotalQueueWaitMs, ledger.Overruns,
            queued is not null);
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

    /// <summary>
    /// The review generation waited its queue wait cap without a slot. The gate
    /// result is kept as evidence, so the operator release can return the card.
    /// </summary>
    private async Task EscalateGateCapacityAsync(TaskInfo task, BatchGatePendingRecord pending,
        BuildTestGateResult gate, string evidence, GateQueueWaitLedger ledger, TimeSpan cap,
        bool recordInStore, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.WriteAllBytesAsync(evidence, JsonSerializer.SerializeToUtf8Bytes(gate), ct).ConfigureAwait(false);
        _logger.LogWarning(
            "batch-fallback-gate-capacity task={TaskKey} attempt={AttemptId} total_wait_ms={TotalWaitMs} overruns={Overruns} cap_ms={CapMs}",
            pending.Subject.TaskKey, pending.ReviewAttemptId, ledger.TotalQueueWaitMs, ledger.Overruns,
            (long)cap.TotalMilliseconds);
        await EscalateFallbackAsync(task, pending, HumanReviewEscalation.FormatReason(
                GateQueueStallPolicy.GateCapacityCategory,
                $"The gate request waited {Minutes(ledger.TotalQueueWaitMs)} min for a gate slot across "
                + $"{ledger.Overruns} queue overrun(s), the cap is {Minutes((long)cap.TotalMilliseconds)} min. "
                + "The code was not tested; add gate capacity or release the card to the gate queue. "
                + "Last overrun: " + gate.Reason),
            recordInStore, ct, GateQueueStallPolicy.GateCapacityCategory).ConfigureAwait(false);
    }

    /// <summary>
    /// A batch candidate whose request spent the machine gate queue budget never
    /// ran. The batch is abandoned and its members return to the queue for the
    /// next batch, with no same-candidate retry. Each member's wait counts
    /// toward its review generation's queue wait cap, and the overrun feeds the
    /// project's stall episode.
    /// </summary>
    private async Task RequeueBatchAfterQueueOverrunAsync(BatchGateManifest manifest, BuildTestGateResult gate,
        IReadOnlyList<BatchGatePendingRecord> gated, long fence, CancellationToken ct)
    {
        State(manifest, BatchPhase.Abandoned, null, fence, "gate-queue-overrun: " + gate.Reason);
        ReturnToPending(manifest);
        var waitedMs = Math.Max(0, gate.ViolatedBudget?.ConsumedMs ?? gate.GateQueueWaitMs);
        var cap = MonolithTransitionAdapter.GateQueueWaitCap(gate.ViolatedBudget is { LimitMs: > 0 } budget
            ? TimeSpan.FromMilliseconds(budget.LimitMs) : QueueBudget(manifest.Scope.Project));
        foreach (var member in gated)
        {
            var live = _scanner.FindJob(member.Subject.TaskKey, member.WatchPath);
            var priorWaitMs = _store.ReadQueueWait(member.ReviewAttemptId)?.TotalQueueWaitMs ?? 0;
            var action = MonolithTransitionAdapter.GateQueueOverrun(gate.ViolatedBudget?.Name,
                gate.ViolatedBudget?.Phase, live?.State, Refresh(member).Subject.CurrentGeneration,
                TimeSpan.FromMilliseconds(priorWaitMs + waitedMs), cap);
            if (action == GateQueueOverrunAction.Supersede)
            {
                ResolveSuperseded(member, "superseded");
                continue;
            }
            var ledger = _store.AddQueueWait(member.ReviewAttemptId, member.Subject.TaskKey, waitedMs,
                _time.GetUtcNow());
            if (action == GateQueueOverrunAction.EscalateCapacity)
            {
                var evidence = Path.Combine(TaskPaths.LogsDir(live!.FolderPath),
                    $"batch-fallback-{member.ReviewAttemptId}.json");
                await EscalateGateCapacityAsync(live, member, gate, evidence, ledger, cap, recordInStore: true, ct)
                    .ConfigureAwait(false);
                continue;
            }
            Lane(member, "gate-queue-stalled", queueWaitMs: waitedMs, outcome: "requeued",
                reason: $"batch:{manifest.BatchId}");
        }
        _logger.LogWarning(
            "batch-gate-queue-overrun batch={BatchId} project={Project} members={Members} waited_ms={WaitedMs}",
            manifest.BatchId, manifest.Scope.Project, gated.Count, waitedMs);
        var queuedMembers = _store.ListPending().Select(Refresh).Count(item =>
            string.Equals(item.Subject.Project, manifest.Scope.Project, StringComparison.Ordinal) && Queued(item));
        var facts = QueueFacts(manifest.Scope.Project, queuedMembers, TimeSpan.FromMilliseconds(waitedMs));
        if (gated.Count > 0
            && _queueStalls.QueueOverrun(manifest.Scope.Project, gated[0].Subject.TaskKey, facts) is { } stall)
            RaiseQueueStall(stall, gated[0].WatchPath);
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
            "gate-queue-stall project={Project} episode={EpisodeId} raise={Raise} depth={Depth} oldest_wait_ms={OldestWaitMs} slots_busy={SlotsBusy} slots_total={SlotsTotal} drain_ms={DrainMs} budget_ms={BudgetMs} idle_configured_slots={IdleSlots}",
            stall.Project, stall.EpisodeId, stall.Raise, stall.QueueDepth, stall.OldestWaitMs, stall.SlotsBusy,
            stall.SlotsTotal, stall.EstimatedDrainMs, stall.QueueBudgetMs, stall.IdleConfiguredSlots);
        _services?.GetService<OrchestratorLog>()?.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = stall.RaisedAtUtc.UtcDateTime,
            Kind = OrchestratorLogKinds.Alert,
            Topic = QueueStallTopic,
            Summary = (stall.Raise > 1 && stall.EpisodeOpenedAtUtc is { } opened
                    ? $"Gate queue still stalled for {stall.Project} since {opened.UtcDateTime:yyyy-MM-dd HH:mm} UTC "
                      + $"(reminder {stall.Raise - 1}): "
                    : $"Gate queue stalled for {stall.Project}: ")
                + $"{stall.QueueDepth} gate request(s) wait, "
                + $"{stall.SlotsBusy}/{stall.SlotsTotal} slots busy, oldest wait {Minutes(stall.OldestWaitMs)} min, "
                + $"estimated drain {Minutes(stall.EstimatedDrainMs)} min. Cards stay in {TaskStates.AutoReview}.",
            Reasoning = stall.Advisory,
        });
    }

    private static long Minutes(long milliseconds) => (long)Math.Ceiling(milliseconds / 60_000d);

    public GateQueueStallReport QueueStallReport(string project)
        => new(project, QueueStalledEscalations(project).Where(item => item.Refusal is null)
                .Select(item => item.Task.Key ?? item.Task.Id).ToArray(),
            _queueStalls.OpenEpisode(project),
            _store.ListQueueStalls(project, RecentQueueStalls));

    /// <summary>
    /// Returns every card of the project that was escalated for a machine gate
    /// queue overrun, by an earlier build or at its queue wait cap, to Auto
    /// Review and the batch queue. A pending
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
            foreach (var (task, review, source, refusal) in QueueStalledEscalations(project))
            {
                var key = task.Key ?? task.Id;
                if (refusal is not null)
                {
                    refused.Add(new(key, RefusalText(refusal.Value)));
                    continue;
                }
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
                    // The operator's release grants the review a fresh queue wait cap.
                    _store.ClearQueueWait(review.AttemptId);
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
    /// budget and the park reason is that overrun. A card whose last move into
    /// Escalated was not the fallback's escalation out of Auto Review, or that
    /// carries a follow-up, is listed with its refusal.
    /// </summary>
    private IReadOnlyList<(TaskInfo Task, ReviewAttemptDto Review, RunAttemptDto Source,
            GateQueueReleaseRefusal? Refusal)>
        QueueStalledEscalations(string project)
    {
        var found = new List<(TaskInfo, ReviewAttemptDto, RunAttemptDto, GateQueueReleaseRefusal?)>();
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
            if (!parkedForQueue) continue;
            var escalation = LastEscalation(task);
            var refusal = MonolithTransitionAdapter.GateQueueStallReleaseRefusal(new GateQueueReleaseFacts(
                task.State, gate.ViolatedBudget?.Name, gate.ViolatedBudget?.Phase, CurrentGeneration: true, passed,
                Detail(escalation, "from"), Detail(escalation, LaneChangeCauses.DetailKey),
                Detail(escalation, LaneChangeCauses.DetailQualifierKey),
                task.PendingIntent is not null || _mutations.ReadStashedPendingIntent(task.FolderPath) is not null));
            // Only a queue overrun of the current passed review is listed at all.
            if (refusal is null or GateQueueReleaseRefusal.NotFallbackEscalation or GateQueueReleaseRefusal.PendingIntent)
                found.Add((task, review, source, refusal));
        }
        return found;
    }

    private TimelineEvent? LastEscalation(TaskInfo task)
        => _timeline?.ReadAll(task.FolderPath).LastOrDefault(row =>
            string.Equals(row.Kind, TimelineEventKinds.LaneChanged, StringComparison.Ordinal)
            && Detail(row, "to") == TaskStates.Escalated);

    private static string? Detail(TimelineEvent? row, string key)
        => row?.Details is { } details && details.TryGetValue(key, out var value) ? value : null;

    private static string RefusalText(GateQueueReleaseRefusal refusal) => refusal switch
    {
        GateQueueReleaseRefusal.NotFallbackEscalation =>
            $"The card was not escalated out of {TaskStates.AutoReview} by the per-task gate; "
            + "its delivery may predate a later lane move, so it is not returned to the gate queue.",
        GateQueueReleaseRefusal.PendingIntent =>
            "A follow-up waits on the card; it must run before the card returns to the gate queue.",
        _ => $"The card is not releasable: {refusal}.",
    };

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
