using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts.BatchGate;

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
            lock (_flights)
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
        lock (_flights) return _assemblingBatchIds.Contains(batchId);
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
                    lock (_flights)
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
            lock (_flights) live = _startedBatchIds.Contains(request.BatchId);
            // This tick is the successor tick of a flight that is gone.
            if (!live && _store.TryConsumeSuccessorTick(request.Project, request.BatchId))
                _logger.LogInformation("batch-gate-successor-tick-served batch={BatchId} reason={Reason}",
                    request.BatchId, request.Reason);
        }
    }

    /// <summary>
    /// The facts the tick collected for one planning scope: its candidates in
    /// start preference order, the pre-check partners still queued, and the
    /// members proven green on <paramref name="BaseSha"/>.
    /// </summary>
    private sealed record ScopeFormationFacts(BatchGatePendingRecord First, string ScopeKey, BatchGateScope Scope,
        string BaseSha, BatchGateFormationOptions Options, BatchGatePendingRecord[] Current, int QueuedPartners,
        bool GateBacklog, IReadOnlySet<string> ProvenGreenKeys);

    /// <summary>
    /// The planner's formation pass over one scope (AGT-3568): it takes the
    /// snapshot, decides close, per-task or wait, admits the proposed order
    /// and commits the formation as one durable intent. Returns true when the
    /// integration base moved during admission.
    /// </summary>
    private async Task<bool> PlanScopeFormationAsync(ScopeFormationFacts facts, CancellationToken ct)
    {
        var (first, _, scope, baseSha, options, current, queuedPartners, gateBacklog, provenGreenKeys) = facts;
        var project = first.Subject.Project;
        // The state route reads risk from this cache and starts no Git (AGT-3413).
        _snapshots.Refresh(scope.IntegrationBranch, current, _time.GetUtcNow());
        var eligibleKeys = MonolithTransitionAdapter.BatchSnapshotEligible(current.Select(item =>
            (item.Subject.TaskKey, BatchGatePolicy.Exclusion(item.Subject, scope, options) is not null)));
        BatchGateFormation formed;
        string planReason;
        if (options.Planner == "look-ahead")
        {
            var eligible = current.Where(item => eligibleKeys.Contains(item.Subject.TaskKey)).ToArray();
            var snapshot = _snapshots.Build(project, scope.IntegrationBranch, eligible, options,
                _time.GetUtcNow(), _store.HasCloseNow(project), GateHostIdle(), gateBacklog, queuedPartners);
            var decision = await PlanLookAheadAsync(project, facts.ScopeKey, snapshot, ct).ConfigureAwait(false);
            // An engine-owned decision is pending; the scope waits for its intent.
            if (decision is null) return false;
            decision = WithExclusions(decision, project, options);
            _store.AppendPlanDecision(project, decision);
            planReason = decision.Reason;
            var route = MonolithTransitionAdapter.BatchLookAheadRoute(decision.Decision);
            if (MonolithTransitionAdapter.BatchConsumesCloseNow(route)) _store.ClearCloseNow(project);
            formed = route switch
            {
                BatchFormationRoute.Close => BatchGatePolicy.Close(current.Select(item => item.Subject),
                    scope, baseSha, options,
                    MonolithTransitionAdapter.BatchLookAheadCloseKeys(current
                        .Where(item => provenGreenKeys.Contains(item.Subject.TaskKey))
                        .Select(item => item.Subject.TaskKey), decision.ProposedMembers),
                    _time.GetUtcNow()),
                BatchFormationRoute.PerTask => new BatchGateFormation(null, [], decision.ProposedMembers, true),
                _ => new BatchGateFormation(null, [], [], false),
            };
        }
        else
        {
            if (_store.HasCloseNow(project))
            {
                formed = BatchGatePolicy.Close(current.Select(item => item.Subject),
                    scope, baseSha, options, eligibleKeys, _time.GetUtcNow());
                _store.ClearCloseNow(project);
            }
            else
            {
                formed = BatchGatePolicy.Form(current.Select(item => item.Subject),
                    scope, baseSha, options, _time.GetUtcNow(),
                    _authority.ListPendingReviewAttempts().Count(review =>
                        string.Equals(_scanner.FindJob(review.TaskKey)?.ProjectName,
                            project, StringComparison.OrdinalIgnoreCase)),
                    _load?.Current.Throttle == true, gateBacklog,
                    _store.ReadBaseVerdict(project, baseSha, first.Subject.GateProfileDigest)?.Outcome == "fail",
                    provenGreenKeys, queuedPartners,
                    ProjectGateBusy(scope) && (!_store.ListRecoverableManifests()
                        .Any(manifest => manifest.Scope == scope
                            && manifest.BaseSha == baseSha
                            && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
                        || current.Any(item => CanJoinWaitingBatch(item, scope, baseSha, options))));
            }
            formed = SeparateThresholdOverlap(formed, current, scope, baseSha, options);
            var plan = MonolithTransitionAdapter.BatchThresholdPlan(formed.Manifest is not null,
                formed.UsePerTaskGate, formed.Reason, queuedPartners, formed.EligibleKeys.Count, current.Length);
            planReason = plan.Reason;
            _store.AppendPlanDecision(project, WithExclusions(new BatchPlanDecision(plan.Decision, planReason,
                    formed.Manifest is null && current.Length > 0
                        ? current.Min(item => item.Subject.ReviewCompletedAtUtc).AddMinutes(options.DeadlineMinutes)
                        : null,
                    formed.Manifest?.Members.Select(member => member.TaskKey).ToArray() ?? formed.EligibleKeys,
                    options.CloseSize, []), project, options));
        }
        if (formed.UsePerTaskGate)
        {
            foreach (var item in current.Where(item => formed.EligibleKeys.Contains(item.Subject.TaskKey)))
            {
                // Only a batch that can actually absorb this card holds
                // its per-task fallback while repacking runs.
                if (options.Planner == "threshold" && CanJoinWaitingBatch(item, scope, baseSha, options)) continue;
                ScheduleFallback(item, ct, new PerTaskFallbackCause(PerTaskFallbackRules.PlannerPerTask,
                    $"{options.Planner} plan: {planReason}"));
            }
            return false;
        }
        if (formed.Manifest is null) return false;
        BeforeFormationAdmissionForTesting?.Invoke();
        var proposed = formed.Manifest;
        formed = await AdmitFormationAsync(formed, current, options, ct).ConfigureAwait(false);
        if (formed.Manifest is null)
        {
            _store.AppendPlanDecision(project, new BatchPlanDecision("wait",
                formed.Reason ?? "formation admission changed", null, [], options.CloseSize, []));
            return formed.Reason == BaseMovedBeforeFormation;
        }
        if (formed.Manifest.MembershipDigest != proposed.MembershipDigest)
            _store.AppendPlanDecision(project, new BatchPlanDecision("close", "formation admission removed: "
                    + string.Join("; ", formed.Exclusions.Except(proposed.Exclusions)
                        .Select(item => item.TaskKey + " " + item.Reason)),
                null, formed.Manifest.Members.Select(item => item.TaskKey).ToArray(), options.CloseSize, []));
        var formationRisks = _snapshots.Cached(project, current);
        formed = formed with { Manifest = formed.Manifest with
        {
            FormationReason = WaitingFormationReason(formed.Manifest, options),
            PlanningCards = formed.Manifest.Members.Select(member => formationRisks.TryGetValue(member.TaskKey,
                    out var risk)
                ? new BatchPlanCard(member.TaskKey, member.ReviewCompletedAtUtc, risk.Risk, risk.Paths, [])
                : new BatchPlanCard(member.TaskKey, member.ReviewCompletedAtUtc,
                    BatchGateRiskModel.Unknown(false, false, false), [], [])).ToArray(),
        } };
        // AGT-3568: the formation is one intent that owns its members.
        var refused = CommitIntent(FormationIntent(BatchIntentKind.Formation, "planner", formed.Manifest, current));
        if (refused is not null)
        {
            _store.AppendPlanDecision(project, new BatchPlanDecision("wait", "formation intent refused: " + refused,
                null, [], options.CloseSize, []));
            return false;
        }
        if (options.Planner == "look-ahead" && _store.LatestPlanDecision(project)?.Advisor is { } advice)
            _store.RecordAdvice(formed.Manifest.BatchId, advice);
        return false;
    }

    /// <summary>The pending members of <paramref name="items"/> a pending or scope rule excludes, with that rule.</summary>
    private BatchPlanExcludedMember[] ExcludedMembers(IEnumerable<BatchGatePendingRecord> items,
        BatchGateFormationOptions options)
        => items.Select(item => new BatchPlanExcludedMember(item.Subject.TaskKey,
                _snapshots.PendingExclusion(item)
                ?? BatchGatePolicy.Exclusion(item.Subject, Scope(item.Subject), options)?.ToString()
                ?? string.Empty))
            .Where(item => item.Rule.Length > 0).ToArray();

    private BatchPlanDecision WithExclusions(BatchPlanDecision plan, string project,
        BatchGateFormationOptions options)
        => MonolithTransitionAdapter.BatchPlanWithExcluded(plan, ExcludedMembers(_store.ListPending().Select(Refresh)
            .Where(item => string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase)),
            options));

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
        lock (_flights)
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

    private const string BaseMovedBeforeFormation = "integration base moved before formation";

    /// <summary>
    /// Admission of a proposed formation (AGT-3568: planner-owned; the order
    /// of the checks and the outcome are <see cref="BatchFormationPolicy"/>).
    /// Probe the exact proposed order before writing a closed manifest. Git replay
    /// is side effect free with respect to the integration ref; assembly repeats
    /// it under the coordinator lease because the result can still move later.
    /// </summary>
    private async Task<BatchGateFormation> AdmitFormationAsync(
        BatchGateFormation formation, IReadOnlyList<BatchGatePendingRecord> candidates,
        BatchGateFormationOptions options, CancellationToken ct,
        IReadOnlySet<string>? replaceableBatchIds = null,
        bool routeRejected = true)
    {
        var manifest = formation.Manifest;
        if (manifest is null) return formation;
        var byKey = candidates.ToDictionary(item => item.Subject.TaskKey, StringComparer.Ordinal);
        var pendingAttempts = _store.ListPending().Select(item => item.ReviewAttemptId)
            .ToHashSet(StringComparer.Ordinal);
        var admitted = new List<BatchGateSubject>();
        var rejected = new List<BatchGateExclusion>();
        var actions = new List<(BatchGatePendingRecord Pending, BatchReplayResult? Replay,
            BatchExclusionReason Reason)>();
        var tip = manifest.BaseSha;
        foreach (var member in manifest.Members)
        {
            if (!byKey.TryGetValue(member.TaskKey, out var candidate)
                || !pendingAttempts.Contains(candidate.ReviewAttemptId))
            {
                rejected.Add(new(member.TaskKey, BatchExclusionReason.NoLongerPending));
                continue;
            }
            var item = Refresh(candidate);
            var subject = item.Subject;
            if (subject.OwningBatchId is { } owner
                && replaceableBatchIds?.Contains(owner) == true)
                subject = subject with { OwningBatchId = null };
            var pendingReason = _snapshots.PendingExclusion(item);
            var policyReason = pendingReason is null
                ? BatchGatePolicy.Exclusion(subject, manifest.Scope, options) : null;
            var memberVerdict = MonolithTransitionAdapter.BatchAdmitMember(pendingReason is not null,
                policyReason is not null,
                pendingReason is null && policyReason is null
                && _store.HasEject(manifest.Scope.Project, member.TaskKey));
            if (memberVerdict != BatchAdmissionVerdict.Admit)
            {
                rejected.Add(memberVerdict switch
                {
                    BatchAdmissionVerdict.SupersededGeneration => new(member.TaskKey,
                        BatchExclusionReason.SupersededGeneration, pendingReason),
                    BatchAdmissionVerdict.PolicyExcluded => new(member.TaskKey, policyReason!.Value),
                    _ => new(member.TaskKey, BatchExclusionReason.OtherBatchOwnsMember, "operator eject"),
                });
                continue;
            }
            if (options.PreCheckFor(manifest.Scope.Project))
            {
                var verdict = _store.ReadPreCheck(manifest.Scope.Project, subject.ResultSha,
                    manifest.BaseSha, subject.GateProfileDigest);
                var preCheck = BatchGatePreCheckPolicy.Decide(true, verdict?.Outcome,
                    item.EnqueuedAtUtc, _time.GetUtcNow(), options.DeadlineMinutes,
                    item.Subject.OwningBatchId is { } owned
                    && replaceableBatchIds?.Contains(owned) == true);
                if (preCheck != BatchGatePreCheckAction.Admit)
                {
                    rejected.Add(new(member.TaskKey, BatchExclusionReason.PreCheckNotAdmitted,
                        verdict?.Outcome ?? "waiting"));
                    actions.Add((item, null, BatchExclusionReason.PreCheckNotAdmitted));
                    continue;
                }
            }
            if (_git.IsDeliveryPublished(candidate.RepositoryPath, subject.ResultSha,
                    manifest.BaseSha))
            {
                rejected.Add(new(member.TaskKey, BatchExclusionReason.AlreadyIntegrated));
                actions.Add((item, null, BatchExclusionReason.AlreadyIntegrated));
                continue;
            }
            var replay = _git.ReplayBatchMember(candidate.RepositoryPath,
                subject.ResultRef, subject.ResultSha, tip);
            var replayVerdict = BatchGatePolicy.AdmissionReplay(replay);
            if (replayVerdict == BatchAdmissionReplay.Conflict)
            {
                rejected.Add(new(member.TaskKey, BatchExclusionReason.ReplayConflict,
                    string.Join(", ", replay.ConflictedFiles)));
                actions.Add((item, replay, BatchExclusionReason.ReplayConflict));
                continue;
            }
            switch (replayVerdict)
            {
                case BatchAdmissionReplay.Pause:
                    _logger.LogWarning("batch-formation-replay-unavailable task={TaskKey} reason={Reason}",
                        member.TaskKey, replay.Error);
                    return formation with { Manifest = null, UsePerTaskGate = false,
                        Reason = "member replay unavailable before formation: " + member.TaskKey };
                case BatchAdmissionReplay.EjectUnattributable:
                    rejected.Add(new(member.TaskKey, BatchExclusionReason.UnattributableReplay,
                        replay.Error));
                    actions.Add((item, replay, BatchExclusionReason.UnattributableReplay));
                    continue;
                default:
                    admitted.Add(subject);
                    tip = replay.TipSha!;
                    break;
            }
        }
        // A remote push may have changed the base while planning or probing.
        // Leave the queue untouched; the next tick plans on the fetched tip.
        var latestBase = _git.FetchBatchIntegrationTip(byKey[manifest.Members[0].TaskKey]
            .RepositoryPath, manifest.Scope.IntegrationBranch, ct);
        var outcome = MonolithTransitionAdapter.BatchAdmissionOutcome(latestBase is null
                || !string.Equals(latestBase, manifest.BaseSha, StringComparison.OrdinalIgnoreCase),
            !routeRejected, rejected.Count, admitted.Count, options.MinimumSize);
        if (outcome == BatchAdmissionOutcome.BaseMoved)
            return formation with { Manifest = null, UsePerTaskGate = false, Reason = BaseMovedBeforeFormation };
        if (outcome == BatchAdmissionOutcome.ReplacementChanged)
            return formation with { Manifest = null, UsePerTaskGate = false,
                Reason = "replacement members changed before formation" };
        foreach (var (item, replay, reason) in actions)
        {
            if (reason == BatchExclusionReason.PreCheckNotAdmitted)
            {
                var verdict = _store.ReadPreCheck(manifest.Scope.Project, item.Subject.ResultSha,
                    manifest.BaseSha, item.Subject.GateProfileDigest);
                if (verdict?.Outcome == "red") SchedulePreCheckRed(item, verdict, ct);
                else SchedulePreCheck(item, manifest.BaseSha, ct);
            }
            else if (reason == BatchExclusionReason.AlreadyIntegrated)
                ScheduleFallback(item, ct, new PerTaskFallbackCause(
                    PerTaskFallbackRules.AlreadyIntegrated,
                    $"the integration branch at {Short(manifest.BaseSha)} already contains the result"),
                    containedInSha: manifest.BaseSha);
            else if (reason == BatchExclusionReason.ReplayConflict)
                await StartConflictRoundAsync(item, new BatchGateReplayRecord(
                    manifest.BatchId, manifest.MembershipDigest, item.Subject.TaskKey,
                    item.Subject.ResultSha, manifest.BaseSha, null, replay!.Replacements,
                    replay.ConflictedFiles, "conflict", _time.GetUtcNow())).ConfigureAwait(false);
            else
                ScheduleFallback(item, ct, new PerTaskFallbackCause(
                    PerTaskFallbackRules.UnattributableReplay,
                    "batch formation could not attribute the replay: "
                    + (replay?.Error ?? "no commit mapping")));
        }
        if (outcome == BatchAdmissionOutcome.BelowMinimum)
            return formation with { Manifest = null, UsePerTaskGate = false,
                Exclusions = formation.Exclusions.Concat(rejected).ToArray(),
                EligibleKeys = admitted.Select(item => item.TaskKey).ToArray(),
                Reason = "admitted members below minimum batch size: "
                    + string.Join("; ", rejected.Select(item =>
                        item.TaskKey + " " + item.Reason + (item.Detail is null ? "" : " (" + item.Detail + ")"))) };
        if (outcome == BatchAdmissionOutcome.Unchanged) return formation;
        var kept = admitted.ToArray();
        return formation with { Manifest = manifest with
        {
            Members = kept,
            EligibleKeys = manifest.EligibleKeys.Except(rejected.Select(item => item.TaskKey),
                StringComparer.Ordinal).ToArray(),
            MembershipDigest = BatchGatePolicy.MembershipDigest(manifest.BaseSha,
                manifest.Scope, kept),
            Exclusions = manifest.Exclusions.Concat(rejected).ToArray(),
        }, Exclusions = formation.Exclusions.Concat(rejected).ToArray(),
            EligibleKeys = formation.EligibleKeys.Except(rejected.Select(item => item.TaskKey),
                StringComparer.Ordinal).ToArray() };
    }
}
