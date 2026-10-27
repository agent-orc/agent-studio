using System.Collections.Concurrent;
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

    // One writer per subject: an engine receipt, the in-process path, a
    // recovery and a person's Undo never interleave their read and save.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _subjects = new(StringComparer.Ordinal);

    private SemaphoreSlim SubjectLock(string subject) => _subjects.GetOrAdd(subject, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// After this time from the decision no restart of the record can still
    /// run: the Operations Server and the agent refuse the command after its
    /// deadline (<see cref="OperationsServerRestartControl.CommandWindow"/>),
    /// the agent bounds one restart to its validate and recreate timeouts, and
    /// the rest is margin for clock skew and result spooling.
    /// </summary>
    public static readonly TimeSpan RestartFenceHorizon = OperationsServerRestartControl.CommandWindow + TimeSpan.FromMinutes(10);

    /// <summary>
    /// Releases a durable claim drain once its remote attempt is terminal, or
    /// once the command can no longer run (absent, unreadable or unreachable
    /// attempts past <see cref="RestartFenceHorizon"/>).
    /// </summary>
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
            // An absent, unreadable or unreachable attempt is ambiguous until
            // the command expired: hold the fence until then, never forever.
            var expiredAtUtc = record.DecidedAtUtc + RestartFenceHorizon;
            var expired = time.GetUtcNow().UtcDateTime >= expiredAtUtc;
            if (terminal != true && !expired) continue;
            workloads.ReleaseRestartFence(record.Host, role);
            if (workloads.HasRestartFence(record.Host, role)) continue;
            var gate = SubjectLock(record.Subject);
            await gate.WaitAsync(ct);
            try
            {
                var current = store.Find(record.Id) ?? record;
                logger.LogInformation("stall-intervention-restart-fence-released id={Id} host={Host} role={Role} terminal={Terminal}",
                    record.Id, record.Host, role, terminal);
                var updated = current with { Outcome = (current.Outcome ?? string.Empty)
                    + (terminal == true
                        ? " The remote restart attempt is terminal; new claims may start."
                        : $" The restart attempt could not be confirmed, but its command expired before {expiredAtUtc:u}"
                            + " and no restart can still run; new claims may start.")
                    + " The service effect still needs its own check." };
                store.Save(updated);
                receipts.Publish(updated, store.All());
            }
            finally
            {
                gate.Release();
            }
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
        var gate = SubjectLock(stall.Subject);
        await gate.WaitAsync(ct);
        try
        {
            await ApplyLockedAsync(application, plan, stall, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ApplyLockedAsync(AiOperatorApplication application, StallInterventionPlan plan,
        StallInterventionFacts stall, CancellationToken ct)
    {
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
            if (original?.Action == StallInterventionActions.RerouteProvider
                && plan.Action == StallInterventionActions.ConfirmCleared)
            {
                routes.Restore(original.Host, original.Provider ?? "unknown", original.Id);
                if (routes.IsExcluded(original.Host, original.Provider ?? "unknown"))
                    settled = settled with
                    {
                        Status = StallInterventionStatuses.Escalated,
                        EffectObserved = false,
                        EffectEvidence = "the provider exclusion remains in force",
                        Outcome = "The login stall cleared, but the route exclusion remains in force."
                            + " I handed the event to a person and kept the original reroute undoable.",
                    };
            }
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
        if (record.Action == StallInterventionActions.RestoreAdmission && !CalmNow(record))
        {
            // Not a person's change: the restore waits for the next calm pass.
            logger.LogInformation("stall-intervention-restore-held subject={Subject} reason=not-calm-by-fresh-telemetry",
                stall.Subject);
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
        var gate = SubjectLock(record.Subject);
        gate.Wait();
        try
        {
            // Settled meanwhile (by the apply that was still running): nothing to recover.
            var current = store.Find(record.Id);
            return current is null || current.Status != StallInterventionStatuses.Applying
                ? current ?? record : RecoverInterruptedLocked(current);
        }
        finally
        {
            gate.Release();
        }
    }

    private StallInterventionRecord RecoverInterruptedLocked(StallInterventionRecord record)
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
    /// Routes a provider back to a host whose reroute is still in force although
    /// its record settled (an escalation, an interrupted apply, an effect not
    /// observed), once the host's login stall is no longer open. Only the
    /// exclusion this intervention owns is lifted; a failed write is retried
    /// on the next pass.
    /// </summary>
    public int ReleaseClearedReroutes(IReadOnlySet<string> openSubjects)
    {
        var released = 0;
        foreach (var record in store.All().Where(item => item.RouteMayBeInForce
            && !StallInterventionStatuses.Pending.Contains(item.Status) && !openSubjects.Contains(item.Subject)).ToArray())
        {
            var gate = SubjectLock(record.Subject);
            gate.Wait();
            try
            {
                var current = store.Find(record.Id);
                if (current is null || !current.RouteMayBeInForce || StallInterventionStatuses.Pending.Contains(current.Status)) continue;
                var provider = current.Provider ?? "unknown";
                var owned = routes.ExcludedBy(current.Host, provider) == current.Id;
                if (owned) routes.Restore(current.Host, provider, current.Id);
                if (owned && routes.IsExcluded(current.Host, provider)) continue;
                var settled = current with
                {
                    RevertedBy = owned ? "login stall cleared" : "exclusion no longer in force",
                    Undoable = false,
                    ResolvedAtUtc = current.ResolvedAtUtc ?? time.GetUtcNow().UtcDateTime,
                    Outcome = (current.Outcome ?? string.Empty) + (owned
                        ? $" The login stall on {current.Host} cleared; I routed {provider} to it again."
                        : $" The exclusion of {provider} on {current.Host} is no longer this intervention's; nothing to route back."),
                };
                store.Save(settled);
                receipts.Publish(settled, store.All());
                released++;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "stall-intervention-route-release-failed id={Id}", record.Id);
            }
            finally
            {
                gate.Release();
            }
        }
        return released;
    }

    /// <summary>
    /// The admission no longer reads the lowered value (a person changed it,
    /// or the write never took): the reduction is no longer in force and is
    /// never restored over the live value.
    /// </summary>
    public StallInterventionRecord ReleaseLowering(StallInterventionRecord record, string evidence)
    {
        var gate = SubjectLock(record.Subject);
        gate.Wait();
        try
        {
            var current = store.Find(record.Id) ?? record;
            return current.AdmissionInForce ? ReleaseLoweringLocked(current, evidence) : current;
        }
        finally
        {
            gate.Release();
        }
    }

    private StallInterventionRecord ReleaseLoweringLocked(StallInterventionRecord record, string evidence)
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

    /// <summary>
    /// A person's undo. Restarts, gate drops, restores and escalations have
    /// nothing to undo: a restored baseline is the person's own value.
    /// </summary>
    public StallInterventionUndoResult Undo(string id, string actor)
    {
        var found = store.Find(id);
        if (found is null) return new(false, "intervention-not-found", null);
        var gate = SubjectLock(found.Subject);
        gate.Wait();
        try
        {
            return UndoLocked(id, actor);
        }
        finally
        {
            gate.Release();
        }
    }

    private StallInterventionUndoResult UndoLocked(string id, string actor)
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
            case StallInterventionActions.LowerAdmission:
                if (record.HostClientId is not { } clientId || record.PreviousAdmission is not { } previousAdmission
                    || record.TargetAdmission is not { } aiAdmission)
                    return new(false, "intervention-not-undoable", record);
                undone = admission.TrySetAdmission(clientId, aiAdmission, previousAdmission, record.AdmissionStampUtc) is not null;
                var value = admission.Read(clientId, StallInterventionOptions.FromConfiguration(configuration)
                    .Limits.RestoreBelowTargetFraction, now)?.Admission;
                evidence = $"admission of {record.Host} reads {value?.ToString() ?? "unknown"}";
                break;
            case StallInterventionActions.RerouteProvider:
                var routedProvider = record.Provider ?? "unknown";
                try
                {
                    routes.Restore(record.Host, routedProvider, record.Id);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "stall-intervention-undo-route-failed id={Id}", record.Id);
                    return new(false, $"undo-not-observed: {exception.Message}", record);
                }
                // Undone once this reroute no longer holds the exclusion; another one may still.
                undone = routes.ExcludedBy(record.Host, routedProvider) != record.Id;
                evidence = !routes.IsExcluded(record.Host, routedProvider) ? $"{record.Provider} is routed to {record.Host} again"
                    : undone ? $"this reroute is no longer in force; {routes.ExcludedBy(record.Host, routedProvider)} still excludes {record.Provider}"
                    : "the route exclusion is still in force";
                break;
            case StallInterventionActions.RequeueFromDelivery:
                var cleared = record.Members.Where(card => continuations.Clear(card, record.Id)).ToArray();
                // A card that left Ready already started from the pinned base: nothing to take back there.
                var started = record.Members.Except(cleared).Where(continuations.LeftReady).ToArray();
                var holding = record.Members.Except(cleared).Except(started).Where(card => continuations.Read(card) is { } pin
                    && pin.Reason == DeliveryContinuationControl.Reason).ToArray();
                undone = holding.Length == 0 && (cleared.Length > 0 || started.Length < record.Members.Count);
                evidence = $"cleared the base on {cleared.Length} of {record.Members.Count} cards"
                    + (started.Length == 0 ? string.Empty
                        : $"; {string.Join(", ", started)} already started from it, which an Undo cannot take back")
                    + (holding.Length == 0 ? string.Empty : $"; {string.Join(", ", holding)} still hold it");
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

    /// <summary>A restore raises admission only into a load that is calm by fresh telemetry now.</summary>
    private bool CalmNow(StallInterventionRecord record)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var reading = record.HostClientId is { } clientId ? admission.Read(clientId,
            StallInterventionOptions.FromConfiguration(configuration).Limits.RestoreBelowTargetFraction, now) : null;
        return reading?.CalmSinceUtc is not null && !StallInterventionFactsBuilder.IsCurrentPressure(record.Kind, reading, now,
            WatcherOptions.FromConfiguration(configuration).OperationalThresholds);
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
                // A restore writes over the AI value only while nobody saved the capacity since the lowering.
                var lowering = record.Action == StallInterventionActions.RestoreAdmission
                    ? store.Find(record.PreviousInterventionId ?? string.Empty) : null;
                var stamp = admission.TrySetAdmission(record.HostClientId!, expected, target, lowering?.AdmissionStampUtc);
                if (stamp is null)
                {
                    // Refused: a person changed or saved the capacity (superseded), or the
                    // write did not take, which the effect check below escalates.
                    var live = admission.Read(record.HostClientId!, StallInterventionOptions.FromConfiguration(configuration)
                        .Limits.RestoreBelowTargetFraction, now);
                    if (live?.Admission != expected || lowering?.AdmissionStampUtc is { } aiStamp && live.CapacityStampUtc != aiStamp)
                        return Superseded(record, now, record.Action == StallInterventionActions.RestoreAdmission
                            ? $"the capacity of {record.Host} was changed after the lowering; I do not restore over it"
                            : "the admission changed before the atomic update");
                }
                record = record with { AdmissionStampUtc = stamp };
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
        or StallInterventionActions.RerouteProvider or StallInterventionActions.RequeueFromDelivery;

    /// <summary>The follow-up or restore closes the intervention it checked.</summary>
    private void Settle(StallInterventionRecord original, StallInterventionRecord followUp, DateTime now)
    {
        if (followUp.Action == StallInterventionActions.RestoreAdmission
            && followUp.Status == StallInterventionStatuses.Superseded)
        {
            ReleaseLoweringLocked(original, followUp.EffectEvidence ?? "the admission changed");
            return;
        }
        var restored = followUp.Action == StallInterventionActions.RestoreAdmission
            && followUp.Status is StallInterventionStatuses.Applied or StallInterventionStatuses.Verified;
        var status = followUp.Action switch
        {
            _ when followUp.Trigger == StallInterventionTriggers.Restore => original.Status,
            StallInterventionActions.Escalate => StallInterventionStatuses.Escalated,
            StallInterventionActions.ConfirmCleared => followUp.Status == StallInterventionStatuses.Verified
                ? StallInterventionStatuses.Verified : StallInterventionStatuses.Escalated,
            StallInterventionActions.RestoreAdmission => restored ? StallInterventionStatuses.Verified : StallInterventionStatuses.Escalated,
            _ => original.Status,
        };
        if (status == original.Status && !restored) return;
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
