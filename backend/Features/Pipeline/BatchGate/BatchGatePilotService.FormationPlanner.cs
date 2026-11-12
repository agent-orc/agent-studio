using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3568: the formation planner's host side (step 02 of the AGT-3552
/// split). Every formation and every replacement of formed batches is one
/// durable intent (<see cref="BatchGateIntent"/>) that
/// <see cref="BatchFormationPolicy"/> accepts only while no other batch or
/// open intent owns its pending generations. The planner decides, writes and
/// applies the intent under the project's planner lock: it closes the
/// successor (whose first phase record is the commit point), claims the
/// members, and only then retires the predecessors. A host that dies on the
/// way leaves an open intent the next tick completes, discards or rolls back.
/// </summary>
public sealed partial class BatchGatePilotService
{
    /// <summary>
    /// Commits one intent. Returns null once the successor is formed, or the
    /// refusal or roll-back reason. <paramref name="afterRetire"/> runs after
    /// an applied replacement retired its predecessors.
    /// </summary>
    private string? CommitIntent(BatchGateIntent intent, Action? afterRetire = null)
        => _store.WithPlannerLock(intent.Successor.Scope.Project, () =>
        {
            lock (_runningBatches)
            {
                var decision = _store.DecideIntent(intent, Assembling);
                if (!decision.Accepted)
                {
                    var conflicts = string.Join(", ", decision.Conflicts.Select(item => item.TaskKey));
                    _logger.LogWarning(
                        "batch-gate-intent-refused route={Route} successor={BatchId} refusal={Refusal} conflicts={Conflicts}",
                        intent.Route, intent.Successor.BatchId, decision.Refusal, conflicts);
                    return conflicts.Length == 0 ? decision.Refusal : $"{decision.Refusal}: {conflicts}";
                }
                var opened = _store.OpenIntent(intent);
                if (!_store.HasManifest(opened.Successor.BatchId)) _store.CloseManifest(opened.Successor);
                var refusal = ClaimAndRetire(opened);
                if (refusal is null) afterRetire?.Invoke();
                return refusal;
            }
        });

    private bool Assembling(string batchId)
    {
        lock (_runningBatches) return _assemblingBatchIds.Contains(batchId);
    }

    private BatchGateIntent FormationIntent(BatchIntentKind kind, string route, BatchGateManifest successor,
        IEnumerable<BatchGatePendingRecord> pending, params BatchGateIntentRetirement[] retiring)
    {
        var byKey = pending.GroupBy(item => item.Subject.TaskKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.MaxBy(item => item.Subject.EnqueueSequence)!,
                StringComparer.Ordinal);
        return new BatchGateIntent(Guid.NewGuid().ToString("N"), kind, route, _time.GetUtcNow(), successor,
            successor.Members.Where(member => byKey.ContainsKey(member.TaskKey))
                .Select(member => new BatchGateIntentMember(member.TaskKey, member.RunAttempt,
                    byKey[member.TaskKey].ReviewAttemptId)).ToArray(),
            retiring);
    }

    /// <summary>The terminal state a replacement writes for a predecessor, at the predecessor's own fence.</summary>
    private BatchGateIntentRetirement Retirement(BatchGateManifest predecessor, string reason,
        long? fence = null, bool ownFlight = false, bool successorTick = false)
        => new(new BatchGateState(predecessor.BatchId, predecessor.MembershipDigest, BatchPhase.Abandoned,
                null, fence ?? _store.LatestState(predecessor.BatchId).CoordinatorFence, _time.GetUtcNow(), reason),
            0, ownFlight, successorTick);

    /// <summary>
    /// Claims the successor's members and retires the predecessors, each by a
    /// compare-and-swap on the version the intent was decided on. A refused
    /// retirement means a predecessor moved on first; the successor is then
    /// rolled back so every generation keeps one owner.
    /// </summary>
    private string? ClaimAndRetire(BatchGateIntent intent)
    {
        var successor = intent.Successor;
        if (_store.PhaseOrNull(successor.BatchId) is null) _store.InitializeFormed(successor, _time.GetUtcNow());
        var phase = _store.PhaseOrNull(successor.BatchId);
        if (!MonolithTransitionAdapter.BatchMayRetirePredecessors(phase))
        {
            _store.CloseIntent(intent, BatchGateStore.IntentDiscarded, $"successor is {phase}", _time.GetUtcNow());
            return $"successor is {phase}";
        }
        var pending = PendingByAttempt();
        if (phase == BatchPhase.Formed)
            foreach (var member in intent.Members)
            {
                if (!pending.TryGetValue(member.ReviewAttemptId, out var item)) continue;
                var task = _scanner.FindJob(member.TaskKey, item.WatchPath);
                if (task is null)
                {
                    _logger.LogWarning("batch-gate-intent-member-missing successor={BatchId} task={TaskKey}",
                        successor.BatchId, member.TaskKey);
                    continue;
                }
                BatchGateOwnershipStore.Write(task.FolderPath, new BatchGateOwnership(item.ReviewAttemptId,
                    successor.Members.Single(subject => subject.TaskKey == member.TaskKey), successor.BatchId));
            }
        foreach (var retiring in intent.Retiring)
        {
            var id = retiring.State.BatchId;
            if (_store.PhaseOrNull(id) == BatchPhase.Abandoned) continue;
            var result = _store.TryTransition(retiring.State with { RecordedAtUtc = _time.GetUtcNow() },
                retiring.ExpectedVersion);
            if (!result.Accepted)
            {
                var reason = $"predecessor {id} moved on: {result.Refusal}";
                RollBack(intent, reason);
                return reason;
            }
            ReturnToPending(_store.ReadManifest(id));
            if (retiring.RequestSuccessorTick)
                _store.RequestSuccessorTick(successor.Scope.Project, id,
                    $"replaced by {successor.BatchId}", _time.GetUtcNow());
        }
        _store.CloseIntent(intent, BatchGateStore.IntentApplied, null, _time.GetUtcNow());
        return null;
    }

    /// <summary>
    /// Retires a formed successor whose predecessor kept its members: each
    /// member's ownership names the predecessor that still holds it, or its
    /// queued marker.
    /// </summary>
    private void RollBack(BatchGateIntent intent, string reason)
    {
        var successor = intent.Successor;
        if (_store.PhaseOrNull(successor.BatchId) == BatchPhase.Formed)
            _store.AppendState(new BatchGateState(successor.BatchId, successor.MembershipDigest,
                BatchPhase.Abandoned, null, _store.LatestState(successor.BatchId).CoordinatorFence,
                _time.GetUtcNow(), "replacement rolled back: " + reason));
        var pending = PendingByAttempt();
        var holders = intent.Retiring.Select(retiring => retiring.State.BatchId)
            .Where(id => _store.PhaseOrNull(id) is { } phase && !MonolithTransitionAdapter.BatchPhaseIsTerminal(phase))
            .Select(_store.ReadManifest).ToArray();
        foreach (var member in intent.Members)
        {
            if (!pending.TryGetValue(member.ReviewAttemptId, out var item)) continue;
            var task = _scanner.FindJob(member.TaskKey, item.WatchPath);
            if (task is null || BatchGateOwnershipStore.Read(task.FolderPath)?.BatchId != successor.BatchId) continue;
            var holder = holders.FirstOrDefault(manifest => manifest.Members.Any(subject =>
                subject.TaskKey == member.TaskKey && subject.RunAttempt == member.RunAttempt));
            BatchGateOwnershipStore.Write(task.FolderPath, holder is null
                ? new BatchGateOwnership(item.ReviewAttemptId, item.Subject)
                : new BatchGateOwnership(item.ReviewAttemptId,
                    holder.Members.First(subject => subject.TaskKey == member.TaskKey), holder.BatchId));
            if (holder is null) Lane(item, "requeued", reason: $"batch:{successor.BatchId}");
        }
        _store.CloseIntent(intent, BatchGateStore.IntentRolledBack, reason, _time.GetUtcNow());
        _logger.LogWarning("batch-gate-intent-rolled-back route={Route} successor={BatchId} reason={Reason}",
            intent.Route, successor.BatchId, reason);
    }

    private Dictionary<string, BatchGatePendingRecord> PendingByAttempt()
        => _store.ListPending().GroupBy(item => item.ReviewAttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    /// <summary>
    /// Start of tick: completes every intent a dead host left open
    /// (<see cref="BatchFormationPolicy.Recover"/>) before any other route
    /// reads batch ownership, and serves successor ticks whose flight is gone.
    /// </summary>
    internal void RecoverFormationIntents()
    {
        foreach (var intent in _store.ListOpenIntents())
        {
            try
            {
                _store.WithPlannerLock(intent.Successor.Scope.Project, () =>
                {
                    lock (_runningBatches)
                    {
                        if (_store.ReadIntentOutcome(intent) is not null) return 0;
                        var step = MonolithTransitionAdapter.BatchIntentRecovery(new BatchIntentRecoveryFacts(
                            _store.HasManifest(intent.Successor.BatchId), _store.PhaseOrNull(intent.Successor.BatchId),
                            _store.RetiringFacts(intent, Assembling)));
                        _logger.LogWarning("batch-gate-intent-recovery route={Route} successor={BatchId} step={Step}",
                            intent.Route, intent.Successor.BatchId, step);
                        switch (step)
                        {
                            case BatchIntentStep.Discard:
                                _store.CloseIntent(intent, BatchGateStore.IntentDiscarded,
                                    "successor not durable", _time.GetUtcNow());
                                break;
                            case BatchIntentStep.RollBack:
                                RollBack(intent, "a predecessor left the formed phase before the replacement");
                                break;
                            default:
                                ClaimAndRetire(intent);
                                break;
                        }
                        return 0;
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "batch-gate-intent-recovery-failed intent={IntentId}", intent.IntentId);
            }
        }
        foreach (var request in _store.ListSuccessorTicks())
        {
            bool live;
            lock (_runningBatches) live = _startedBatchIds.Contains(request.BatchId);
            // This tick is the successor tick of a flight that is gone.
            if (!live && _store.TryConsumeSuccessorTick(request.Project, request.BatchId))
                _logger.LogInformation("batch-gate-successor-tick-served batch={BatchId} reason={Reason}",
                    request.BatchId, request.Reason);
        }
    }

    private BatchPlannerCard Card(BatchGatePendingRecord item)
        => new(item.Subject.TaskKey, _snapshots.Paths(item, _time.GetUtcNow()));

    /// <summary>
    /// A closed snapshot is immutable. Before starting the next flight, retire
    /// only the formed snapshots whose members move into a larger snapshot
    /// (<see cref="BatchFormationPolicy.Repack"/>). A flight already past Formed
    /// is never considered here, and the replaced snapshots retire only after
    /// their successor is durable.
    /// </summary>
    internal async Task RepackFormedBatchesAsync(CancellationToken ct)
    {
        var formed = _store.ListRecoverableManifests()
            .Where(manifest => manifest.ParentBatchId is null
                && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .ToArray();
        if (formed.Length == 0) return;
        var pending = _store.ListPending().Select(Refresh).ToArray();
        var activeKeys = _store.ListRecoverableManifests()
            .SelectMany(manifest => manifest.Members)
            .Select(member => member.TaskKey).ToHashSet(StringComparer.Ordinal);
        foreach (var group in formed.GroupBy(manifest => (manifest.Scope, manifest.BaseSha)))
        {
            var scope = group.Key.Scope;
            var baseSha = group.Key.BaseSha;
            var options = _settings.Get(scope.Project).BatchGate;
            if (options.Planner != "threshold") continue;
            var waiting = group.ToArray();
            var members = waiting.SelectMany(manifest => manifest.Members)
                .Select(member => pending.FirstOrDefault(item => item.Subject.TaskKey == member.TaskKey
                    && item.Subject.RunAttempt == member.RunAttempt))
                .OfType<BatchGatePendingRecord>().ToArray();
            var eligible = pending.Where(item => EligibleForWaitingBatch(item, scope, baseSha, options, activeKeys))
                .OrderBy(item => item.Subject.EnqueueSequence).ToArray();
            var plan = MonolithTransitionAdapter.BatchRepack(new BatchRepackFacts(
                waiting.Select(manifest => new BatchRepackWaiting(manifest.BatchId,
                    manifest.Members.Select(member => member.TaskKey).ToArray(), manifest.ClosedAtUtc,
                    BatchGateFlightQueue.QueuedSince(_store, manifest))).ToArray(),
                members.Select(Card).ToArray(), eligible.Select(Card).ToArray(),
                options.MinimumSize, options.MaximumSize, options.Overlap));
            if (plan.Action == BatchRepackAction.Replace)
                await ReplaceFormedBatchesAsync(plan, waiting, members.Concat(eligible)
                    .DistinctBy(item => item.Subject.TaskKey).ToArray(), scope, baseSha, options, ct)
                    .ConfigureAwait(false);
            else if (plan.Action == BatchRepackAction.Annotate)
                AnnotateWaiting(plan, waiting);
        }
    }

    private async Task ReplaceFormedBatchesAsync(BatchRepackDecision plan, IReadOnlyList<BatchGateManifest> waiting,
        IReadOnlyList<BatchGatePendingRecord> all, BatchGateScope scope, string baseSha,
        BatchGateFormationOptions options, CancellationToken ct)
    {
        var moving = waiting.Where(manifest => plan.Retiring.Contains(manifest.BatchId)).ToArray();
        var clean = all.Select(item => item.Subject with { OwningBatchId = null }).ToArray();
        var close = BatchGatePolicy.Close(clean, scope, baseSha, options, plan.SelectedKeys, _time.GetUtcNow());
        if (close.Manifest is null) return;
        close = await AdmitFormationAsync(close, all, options, ct,
            moving.Select(manifest => manifest.BatchId).ToHashSet(StringComparer.Ordinal),
            routeRejected: false).ConfigureAwait(false);
        if (close.Manifest is null) return;
        var selected = plan.Candidates.Where(card => plan.SelectedKeys.Contains(card.TaskKey)).ToArray();
        var exclusions = close.Manifest.Exclusions.Select(exclusion =>
        {
            var card = plan.Candidates.FirstOrDefault(item => item.TaskKey == exclusion.TaskKey)
                ?? new BatchPlannerCard(exclusion.TaskKey, null);
            var (overlap, detail) = MonolithTransitionAdapter.BatchRepackExclusion(card, selected,
                options.MaximumSize, options.Overlap);
            return exclusion with
            {
                Reason = overlap ? BatchExclusionReason.OverlapSeparate : BatchExclusionReason.MaximumSize,
                Detail = detail,
            };
        }).ToArray();
        // AGT-3710: the replacement keeps the queue place of the oldest batch it replaces.
        var replacement = close.Manifest with { Exclusions = exclusions, QueuedSinceUtc = plan.QueuedSinceUtc };
        BatchGateIntentRetirement[] retiring;
        lock (_runningBatches)
            // A snapshot with a flight waiting for the reservation triggers a
            // successor tick when that flight exits; the request is durable.
            retiring = moving.Select(old => Retirement(old,
                $"formed batch replaced by {replacement.BatchId} before gate start",
                successorTick: _startedBatchIds.Contains(old.BatchId))).ToArray();
        var refusal = CommitIntent(FormationIntent(BatchIntentKind.Replacement, "repack", replacement, all, retiring));
        if (refusal is not null) return;
        AnnotateWaiting(plan, waiting);
    }

    private void AnnotateWaiting(BatchRepackDecision plan, IReadOnlyList<BatchGateManifest> waiting)
    {
        foreach (var manifest in waiting)
        {
            if (!plan.Annotations.TryGetValue(manifest.BatchId, out var reason)) continue;
            var state = _store.LatestState(manifest.BatchId);
            if (state.Phase == BatchPhase.Formed && state.Reason != reason)
                State(manifest, BatchPhase.Formed, null, 0, reason);
        }
    }
}
