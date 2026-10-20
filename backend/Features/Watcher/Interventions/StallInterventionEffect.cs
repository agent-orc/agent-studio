using AgentStudio.Runner;

namespace AgentStudio.Watcher;

/// <summary>Result of a person's undo request.</summary>
public sealed record StallInterventionUndoResult(bool Undone, string Message, StallInterventionRecord? Record);

/// <summary>
/// AGT-3207: applies the AI Operator's <c>stall-intervention</c> decision,
/// in process (Monolith) or from the engine receipt (Engine). The record is
/// written ahead as <c>applying</c> before any side effect and settled after
/// it, so an exception or a restart in between never repeats the action. The
/// write key comes from the case, its episode, the action and the repeat
/// index, so a replayed or duplicated receipt maps to the same record. Before
/// it acts, the effect compares the live value with the plan's before-value
/// and records a person's change as superseded instead of acting. Every
/// applied action is measured afterwards; an action whose effect cannot be
/// observed is escalated at once instead of being reported as done.
/// </summary>
public sealed class StallInterventionEffect(
    StallInterventionStore store,
    WatcherCaseStore cases,
    StallInterventionFactsBuilder facts,
    IHostAdmissionControl admission,
    IServiceRestartControl restarts,
    IServiceWorkload workloads,
    IProviderRouteControl routes,
    IBatchGateControl gate,
    IContinuationControl continuations,
    StallInterventionReceipts receipts,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<StallInterventionEffect> logger) : IAiOperatorProposalEffect
{
    /// <summary>An <c>applying</c> record older than this was interrupted; it is measured and settled, never repeated.</summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(10);

    public string Decider => AiOperatorDeciders.StallIntervention;

    /// <summary>Releases a durable claim drain only after its remote attempt is terminal.</summary>
    public async Task RecoverRestartFencesAsync(CancellationToken ct)
    {
        var latest = store.All().Where(item => item.Action == StallInterventionActions.RestartService
                && item.Status is not (StallInterventionStatuses.Superseded or StallInterventionStatuses.Observed))
            .GroupBy(item => item.Subject, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.DecidedAtUtc).First());
        foreach (var record in latest)
        {
            var role = record.Subject.Split(':') is { Length: > 2 } parts ? parts[2] : "coding";
            if (!workloads.HasRestartFence(record.Host, role)) continue;
            var terminal = await restarts.IsTerminalAsync(record.WriteKey, ct);
            // An absent attempt can only be declared safe after the command's
            // submission deadline and the interrupted-record window have passed.
            if (terminal != true && !(terminal is null
                    && time.GetUtcNow().UtcDateTime - record.DecidedAtUtc > InterruptedAfter)) continue;
            workloads.ReleaseRestartFence(record.Host, role);
            if (workloads.HasRestartFence(record.Host, role)) continue;
            var updated = record with { Outcome = (record.Outcome ?? string.Empty)
                + " The remote restart attempt is terminal or its submission window expired; new claims may start."
                + " The service effect still needs its own check." };
            store.Save(updated);
            receipts.Publish(updated, store.All());
        }
    }

    /// <summary>
    /// The idempotency key of one decision: case id, episode start, action and
    /// repeat index for a stall, the checked intervention for a follow-up or
    /// restore. The Operations Server receives the same key.
    /// </summary>
    public static string WriteKey(StallInterventionFacts stall, string action, bool observe)
    {
        var key = stall.Trigger switch
        {
            StallInterventionTriggers.FollowUp or StallInterventionTriggers.Restore
                => $"{stall.Trigger}:{stall.FollowUp?.InterventionId}:{action}",
            _ => $"stall:{stall.CaseId ?? stall.Subject}:{stall.SinceUtc?.ToString("O") ?? "none"}:{action}:"
                + stall.History.Count(item => item.Subject == stall.Subject && item.Action == action
                    && (stall.SinceUtc is not { } since || item.AppliedUtc >= since)),
        };
        return (observe ? "observe:" : string.Empty) + "stall-intervention:" + key;
    }

    /// <summary>True when the mutating plan only writes a "would do" receipt (observe mode or a switched-off action).</summary>
    public static bool Observes(StallInterventionOptions options, string action)
        => StallInterventionActions.Mutating.Contains(action) && !options.Acts(action);

    public async Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        var plan = application.Decision.Intervention
            ?? throw new InvalidDataException("The stall-intervention receipt has no plan.");
        var stall = application.Payload.StallIntervention
            ?? throw new InvalidDataException("The stall-intervention occurrence has no facts.");
        if (application.Decision.Disposition == AiOperatorDisposition.Hold) return;
        var options = StallInterventionOptions.FromConfiguration(configuration);
        var observe = Observes(options, plan.Action);
        var writeKey = WriteKey(stall, plan.Action, observe);
        if (store.FindByWriteKey(writeKey) is not null) return;
        var now = time.GetUtcNow().UtcDateTime;
        var all = store.All();
        var original = stall.FollowUp is { } check ? all.FirstOrDefault(item => item.Id == check.InterventionId) : null;
        var watcherCase = Case(stall.Subject, original?.Fingerprint, stall.CaseId);
        if (stall.Trigger == StallInterventionTriggers.Stall
            && (watcherCase is null || !StallInterventionFactsBuilder.IsActionable(watcherCase))) return;
        if (stall.Trigger == StallInterventionTriggers.FollowUp && original?.Status != StallInterventionStatuses.Applied) return;
        if (stall.Trigger == StallInterventionTriggers.Restore && original?.AdmissionInForce != true) return;

        var previous = original ?? all.LastOrDefault(item => item.Subject == stall.Subject
            && now - item.DecidedAtUtc <= TimeSpan.FromHours(options.Limits.LoopWindowHours));
        var record = new StallInterventionRecord
        {
            Id = StallInterventionStore.IdFor(writeKey),
            WriteKey = writeKey,
            Subject = stall.Subject,
            Kind = stall.Kind,
            Host = plan.HostName,
            HostClientId = plan.HostId ?? original?.HostClientId,
            Action = plan.Action,
            Autonomy = plan.Autonomy.ToString(),
            Disposition = application.Decision.Disposition.ToString(),
            ReasonCode = plan.ReasonCode,
            Status = StallInterventionStatuses.Applying,
            Project = watcherCase?.Project ?? original?.Project ?? QuotaProbeSignalProbe.WorkspaceProject,
            CaseId = watcherCase?.Id ?? original?.CaseId,
            Fingerprint = watcherCase?.Fingerprint ?? original?.Fingerprint,
            Trigger = stall.Trigger,
            DecidedBy = application.RunId.StartsWith("monolith:", StringComparison.Ordinal) ? "monolith" : "engine",
            OccurrenceRunId = application.RunId.StartsWith("monolith:", StringComparison.Ordinal) ? null : application.RunId,
            Observed = plan.Observed,
            Done = plan.Done,
            Why = plan.Why,
            Before = plan.Before,
            After = plan.After,
            Undo = plan.Undo,
            Receipt = plan.Receipt,
            Proposal = plan.Proposal,
            AffectedCards = (original?.AffectedCards ?? stall.AffectedCards.ToList()).Take(50).ToList(),
            Members = plan.Members?.ToList() ?? [],
            AttemptIds = plan.AttemptIds?.ToList() ?? [],
            Service = plan.Service,
            Provider = plan.Provider,
            BatchId = plan.BatchId,
            TargetAdmission = plan.TargetAdmission,
            PreviousAdmission = plan.PreviousAdmission,
            DecidedAtUtc = now,
            FollowUpDueUtc = plan.FollowUpMinutes > 0 ? now.AddMinutes(plan.FollowUpMinutes) : null,
            PreviousInterventionId = previous?.Id,
            Chain = previous is null ? [] : previous.Chain.Append(previous.Id).TakeLast(10).ToList(),
        };

        var settled = application.Decision.Disposition switch
        {
            AiOperatorDisposition.Propose => record with { Status = StallInterventionStatuses.Proposed, ResolvedAtUtc = now },
            AiOperatorDisposition.Ask => record with { Status = StallInterventionStatuses.Escalated, ResolvedAtUtc = now },
            _ when plan.Action == StallInterventionActions.ConfirmCleared => record with
            {
                Status = StallInterventionStatuses.Verified, ResolvedAtUtc = now, EffectObserved = true,
            },
            _ when observe => WouldDo(record, options, now),
            _ => null,
        };
        if (settled is not null)
        {
            store.Save(settled);
            if (original is not null && settled.Status != StallInterventionStatuses.Observed) Settle(original, settled, now);
            receipts.Publish(settled, store.All());
            return;
        }

        // Apply-time checks: one pending intervention per subject, and the
        // live value must still be the plan's before-value.
        var other = all.FirstOrDefault(item => item.Subject == stall.Subject && item.Id != original?.Id
            && StallInterventionStatuses.Pending.Contains(item.Status));
        if (other is not null)
        {
            logger.LogInformation("stall-intervention-refused subject={Subject} pending={Pending} action={Action}",
                stall.Subject, other.Id, plan.Action);
            return;
        }
        if (LiveMismatch(record) is { } mismatch)
        {
            var superseded = record with
            {
                Status = StallInterventionStatuses.Superseded,
                ResolvedAtUtc = now,
                FollowUpDueUtc = null,
                EffectEvidence = mismatch,
                Outcome = mismatch.StartsWith("the overload or memory pressure", StringComparison.Ordinal)
                    ? $"Superseded: {mismatch}. I did not act."
                    : $"Superseded by a person: {mismatch}. I did not act.",
            };
            store.Save(superseded);
            if (original is not null) Settle(original, superseded, now);
            receipts.Publish(superseded, store.All());
            return;
        }

        store.Save(record);
        try
        {
            record = await ActAsync(record, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The record stays undoable: part of the change may be in force.
            logger.LogWarning(exception, "stall-intervention-action-failed id={Id} action={Action}", record.Id, record.Action);
            record = record with
            {
                Status = StallInterventionStatuses.Escalated,
                EffectObserved = false,
                EffectEvidence = $"the action failed: {exception.Message}",
                ResolvedAtUtc = time.GetUtcNow().UtcDateTime,
                Undoable = UndoableAction(record.Action),
                Outcome = $"The action failed ({exception.Message}); I handed the event to a person.",
            };
        }
        if (record.Status == StallInterventionStatuses.Applying)
            record = record with { Status = StallInterventionStatuses.Applied };
        if (record.Status == StallInterventionStatuses.Applied && record.FollowUpDueUtc is null)
            record = record with { Status = StallInterventionStatuses.Verified, ResolvedAtUtc = now };
        store.Save(record);
        if (original is not null) Settle(original, record, now);
        receipts.Publish(record, store.All());
    }

    /// <summary>
    /// Settles an <c>applying</c> record whose process ended between action and
    /// receipt. The action is measured, never repeated: an observed effect
    /// continues to its follow-up, anything else goes to a person.
    /// </summary>
    public StallInterventionRecord RecoverInterrupted(StallInterventionRecord record)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var options = StallInterventionOptions.FromConfiguration(configuration);
        var observed = facts.Effect(record, options, now, out var evidence, out _);
        var recovered = observed && record.FollowUpDueUtc is not null
            ? record with
            {
                Status = StallInterventionStatuses.Applied,
                EffectObserved = true,
                EffectEvidence = evidence,
                Undoable = UndoableAction(record.Action),
                Outcome = "The action was interrupted before its receipt settled; its effect is observed, so I did not repeat it.",
            }
            : record with
            {
                Status = StallInterventionStatuses.Escalated,
                EffectObserved = observed,
                EffectEvidence = evidence,
                ResolvedAtUtc = now,
                Undoable = UndoableAction(record.Action),
                Outcome = "The action was interrupted (a restart or failure) before its receipt settled"
                    + (observed ? "" : " and its effect is not observed") + "; I did not repeat it and handed the event to a person.",
            };
        store.Save(recovered);
        if (record.Trigger != StallInterventionTriggers.Stall
            && store.Find(record.PreviousInterventionId ?? string.Empty) is { } original)
            Settle(original, recovered, now);
        receipts.Publish(recovered, store.All());
        return recovered;
    }

    /// <summary>
    /// The admission no longer reads the lowered value (a person changed it,
    /// or the write never took): the reduction is no longer in force and is
    /// never restored over the live value.
    /// </summary>
    public StallInterventionRecord ReleaseLowering(StallInterventionRecord record, string evidence)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var released = record with
        {
            RevertedBy = $"admission changed: {evidence}",
            Undoable = false,
            Status = record.Status == StallInterventionStatuses.Applied ? StallInterventionStatuses.Verified : record.Status,
            ResolvedAtUtc = record.ResolvedAtUtc ?? now,
            Outcome = $"The admission no longer reads the lowered value ({evidence}); the reduction is no longer in force "
                + "and I do not restore over it.",
        };
        store.Save(released);
        return released;
    }

    /// <summary>A person's undo. Restarts, gate drops and escalations have nothing to undo.</summary>
    public StallInterventionUndoResult Undo(string id, string actor)
    {
        var record = store.Find(id);
        if (record is null) return new(false, "intervention-not-found", null);
        if (record.UndoneAtUtc is not null) return new(false, "intervention-already-undone", record);
        if (!record.Undoable && !record.AdmissionInForce) return new(false, "intervention-not-undoable", record);
        var now = time.GetUtcNow().UtcDateTime;
        string evidence;
        bool undone;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                if (record.HostClientId is not { } clientId || record.PreviousAdmission is not { } previousAdmission
                    || record.TargetAdmission is not { } aiAdmission)
                    return new(false, "intervention-not-undoable", record);
                undone = admission.TrySetAdmission(clientId, aiAdmission, previousAdmission);
                var value = admission.Read(clientId, StallInterventionOptions.FromConfiguration(configuration)
                    .Limits.RestoreBelowTargetFraction, now)?.Admission;
                evidence = $"admission of {record.Host} reads {value?.ToString() ?? "unknown"}";
                break;
            case StallInterventionActions.RerouteProvider:
                routes.Restore(record.Host, record.Provider ?? "unknown");
                undone = !routes.IsRerouted(record.Host, record.Provider ?? "unknown");
                evidence = undone ? $"{record.Provider} is routed to {record.Host} again" : "the route exclusion is still in force";
                break;
            case StallInterventionActions.RequeueFromDelivery:
                var cleared = record.Members.Where(card => continuations.Clear(card, record.Id)).ToArray();
                undone = record.Members.All(card => continuations.Read(card) is not { } pin
                    || pin.Reason != DeliveryContinuationControl.Reason || continuations.LeftReady(card));
                evidence = $"cleared the base on {cleared.Length} of {record.Members.Count} cards";
                break;
            default:
                return new(false, "intervention-not-undoable", record);
        }
        if (!undone) return new(false, $"undo-not-observed: {evidence}", record);
        var updated = record with
        {
            Status = StallInterventionStatuses.Undone,
            UndoneAtUtc = now,
            UndoneBy = actor,
            ResolvedAtUtc = now,
            Undoable = false,
            RevertedBy = record.RevertedBy ?? $"undo: {actor}",
            Outcome = $"Undone by {actor}: {evidence}.",
        };
        store.Save(updated);
        receipts.Publish(updated, store.All());
        return new(true, "undone", updated);
    }

    private StallInterventionRecord WouldDo(StallInterventionRecord record, StallInterventionOptions options, DateTime now)
    {
        var why = options.Mode == StallInterventionModes.Act
            ? $"The action {record.Action} is switched off (StallIntervention:Actions:{record.Action})"
            : "The AI Operator runs in observe mode (StallIntervention:Mode)";
        var done = $"{why}, so nothing was changed. I would have done this: {record.Done}";
        return record with
        {
            Status = StallInterventionStatuses.Observed,
            Done = done,
            Undo = "Nothing to undo.",
            Receipt = string.Join(" ", new[] { record.Observed, done, record.Why,
                record.Before.Length == 0 && record.After.Length == 0 ? string.Empty
                    : $"Would change: {record.Before} to {record.After}." }.Where(part => part.Length > 0)),
            FollowUpDueUtc = null,
            ResolvedAtUtc = now,
        };
    }

    /// <summary>The live value differs from the plan's before-value: someone acted first.</summary>
    private string? LiveMismatch(StallInterventionRecord record)
    {
        var options = StallInterventionOptions.FromConfiguration(configuration);
        var now = time.GetUtcNow().UtcDateTime;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                if (record.HostClientId is not { } clientId) return "the host has no client identity to set the admission on";
                var reading = admission.Read(clientId, options.Limits.RestoreBelowTargetFraction, now);
                if (record.Action == StallInterventionActions.LowerAdmission
                    && !StallInterventionFactsBuilder.IsCurrentPressure(record.Kind, reading, now,
                        WatcherOptions.FromConfiguration(configuration).OperationalThresholds))
                    return "the overload or memory pressure has no fresh confirming telemetry";
                var live = reading?.Admission;
                return live == record.PreviousAdmission ? null
                    : $"the admission of {record.Host} reads {live?.ToString() ?? "unknown"}, the plan expected {record.PreviousAdmission}";
            case StallInterventionActions.RerouteProvider:
                return routes.IsRerouted(record.Host, record.Provider ?? "unknown")
                    ? $"{record.Provider} is already routed away from {record.Host}" : null;
            case StallInterventionActions.RestartService:
                var parts = record.Subject.Split(':');
                var workload = workloads.Read(record.Host, parts.Length > 2 ? parts[2] : "coding", now);
                return workload is { BusySlots: 0, RunningWork: false, ClaimableWaiting: > 0 } ? null
                    : $"{record.Service} on {record.Host} now runs "
                        + (workload?.BusySlots?.ToString() ?? "unknown") + " items"
                        + (workload?.RunningWork == true ? " and an active gate" : string.Empty)
                        + $" with {workload?.ClaimableWaiting ?? 0} claimable waiting";
            default:
                return null;
        }
    }

    private async Task<StallInterventionRecord> ActAsync(StallInterventionRecord record, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                if (record.Action == StallInterventionActions.LowerAdmission && LiveMismatch(record) is { } admissionMismatch)
                    return Superseded(record, now, admissionMismatch);
                if (record.PreviousAdmission is not { } expected || record.TargetAdmission is not { } target)
                    throw new InvalidDataException("The admission plan has no before-value or target.");
                if (!admission.TrySetAdmission(record.HostClientId!, expected, target)
                    && admission.Read(record.HostClientId!, StallInterventionOptions.FromConfiguration(configuration)
                        .Limits.RestoreBelowTargetFraction, now)?.Admission != expected)
                    return Superseded(record, now, "the admission changed before the atomic update");
                break;
            case StallInterventionActions.RestartService or StallInterventionActions.RetryEngineStart:
                var role = record.Subject.Split(':') is { Length: > 2 } subjectParts ? subjectParts[2] : "coding";
                using (var fence = record.Action == StallInterventionActions.RestartService
                    ? workloads.TryFenceClaims(record.Host, role) : null)
                {
                    if (record.Action == StallInterventionActions.RestartService && fence is null)
                        return Superseded(record, now, "a review lease started before the restart claim fence");
                    if (record.Action == StallInterventionActions.RestartService && LiveMismatch(record) is { } restartMismatch)
                        return Superseded(record, now, restartMismatch);
                    if (fence is IRestartClaimFence durableFence) durableFence.Retain();
                    var outcome = await restarts.RestartAsync(record.Host, record.Service ?? "unknown", record.WriteKey, record.Done, ct);
                    if (outcome.TerminalConfirmed && fence is IRestartClaimFence confirmedFence)
                        confirmedFence.ConfirmTerminal();
                    record = record with { EffectObserved = outcome.Succeeded, EffectEvidence = outcome.Evidence };
                }
                break;
            case StallInterventionActions.RerouteProvider:
                routes.Reroute(record.Host, record.Provider ?? "unknown", record.Id);
                break;
            case StallInterventionActions.ReconcileBatchGate:
                // Only the attempts frozen at decision time, re-checked against their card now.
                var dropped = gate.Drop(record.Project, record.AttemptIds, $"ai-operator:{record.Id}");
                if (dropped.Count == 0 && record.BatchId is null)
                    return Superseded(record, now, "every frozen member changed state before I acted; nothing was dropped");
                var closed = record.BatchId is null || gate.CloseNow(record.Project);
                record = record with
                {
                    AttemptIds = dropped.ToList(),
                    Outcome = $"dropped {dropped.Count} of {record.AttemptIds.Count} frozen members"
                        + (record.BatchId is null ? string.Empty : closed ? "; close-now recorded" : "; close-now refused"),
                    EffectObserved = closed ? null : false,
                    EffectEvidence = closed ? null : "the close-now request was refused",
                };
                break;
            case StallInterventionActions.RequeueFromDelivery:
                var pinned = record.Members.Where(card => continuations.PinLastDelivery(card, record.Id) is not null).ToList();
                if (pinned.Count == 0)
                    return Superseded(record, now, "no card still waited in Ready without a base after a rate-limited run");
                record = record with
                {
                    Outcome = pinned.Count == record.Members.Count ? null
                        : $"pinned {pinned.Count} of {record.Members.Count} cards; the others changed before I acted",
                    Members = pinned,
                };
                break;
            default:
                throw new InvalidOperationException($"Unknown stall intervention {record.Action}.");
        }

        // 17.10.2026 lesson: an automatic action counts only by its measured effect.
        now = time.GetUtcNow().UtcDateTime;
        var observed = facts.Effect(record, StallInterventionOptions.FromConfiguration(configuration), now,
            out var evidence, out _);
        if (record.EffectObserved == false)
        {
            observed = false;
            evidence = record.EffectEvidence ?? "the action failed";
        }
        var undoable = UndoableAction(record.Action);
        if (observed)
            return record with { EffectObserved = true, EffectEvidence = evidence, Undoable = undoable };
        // A partial change may be in force: the record keeps its Undo.
        return record with
        {
            Status = StallInterventionStatuses.Escalated,
            EffectObserved = false,
            EffectEvidence = evidence,
            ResolvedAtUtc = now,
            Undoable = undoable,
            Outcome = $"The effect was not observed ({evidence}); I handed the event to a person"
                + (undoable ? "; Undo reverts whatever part of the change is in force." : "."),
        };
    }

    private static StallInterventionRecord Superseded(StallInterventionRecord record, DateTime now, string evidence)
        => record with
        {
            Status = StallInterventionStatuses.Superseded,
            ResolvedAtUtc = now,
            FollowUpDueUtc = null,
            EffectEvidence = evidence,
            Outcome = $"Superseded: {evidence}. I did not act.",
        };

    private static bool UndoableAction(string action) => action is StallInterventionActions.LowerAdmission
        or StallInterventionActions.RestoreAdmission or StallInterventionActions.RerouteProvider
        or StallInterventionActions.RequeueFromDelivery;

    /// <summary>The follow-up or restore closes the intervention it checked.</summary>
    private void Settle(StallInterventionRecord original, StallInterventionRecord followUp, DateTime now)
    {
        if (followUp.Action == StallInterventionActions.RestoreAdmission
            && followUp.Status == StallInterventionStatuses.Superseded)
        {
            ReleaseLowering(original, followUp.EffectEvidence ?? "the admission changed");
            return;
        }
        var restored = followUp.Action == StallInterventionActions.RestoreAdmission
            && followUp.Status is StallInterventionStatuses.Applied or StallInterventionStatuses.Verified;
        var status = followUp.Action switch
        {
            _ when followUp.Trigger == StallInterventionTriggers.Restore => original.Status,
            StallInterventionActions.Escalate => StallInterventionStatuses.Escalated,
            StallInterventionActions.ConfirmCleared => StallInterventionStatuses.Verified,
            StallInterventionActions.RestoreAdmission => restored ? StallInterventionStatuses.Verified : StallInterventionStatuses.Escalated,
            _ => original.Status,
        };
        if (status == original.Status && !restored) return;
        if (original.Action == StallInterventionActions.RerouteProvider
            && followUp.Action == StallInterventionActions.ConfirmCleared)
            routes.Restore(original.Host, original.Provider ?? "unknown");
        store.Save(original with
        {
            Status = status,
            ResolvedAtUtc = original.ResolvedAtUtc ?? now,
            Outcome = $"{followUp.Id}: {followUp.Done}",
            RevertedBy = restored ? followUp.Id : original.RevertedBy,
            // Undo stays while a change is in force (an escalated reduction
            // keeps it through AdmissionInForce); a confirmed or restored one has none.
            Undoable = status == StallInterventionStatuses.Escalated && !restored && original.Undoable,
        });
    }

    private WatcherCase? Case(string subject, string? fingerprint, string? caseId)
    {
        var root = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) return null;
        var all = cases.All(root);
        return (caseId is null ? null : all.FirstOrDefault(item => item.Id == caseId))
            ?? (fingerprint is null ? null : all.FirstOrDefault(item => item.Fingerprint == fingerprint))
            ?? all.Where(item => item.Subject == subject && StallInterventionFactsBuilder.KindOf(item) is not null)
                .OrderByDescending(item => item.LastSeenUtc).FirstOrDefault();
    }
}
