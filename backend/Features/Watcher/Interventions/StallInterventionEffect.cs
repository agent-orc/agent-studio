namespace AgentStudio.Watcher;

/// <summary>Result of a person's undo request.</summary>
public sealed record StallInterventionUndoResult(bool Undone, string Message, StallInterventionRecord? Record);

/// <summary>
/// AGT-3207: applies the AI Operator's <c>stall-intervention</c> decision,
/// in process (Monolith) or from the engine receipt (Engine). Every applied
/// action is measured afterwards; an action whose effect cannot be observed is
/// escalated at once instead of being reported as done. The effect is
/// idempotent per write key, and a stall that cleared before an engine receipt
/// arrived is settled without an action.
/// </summary>
public sealed class StallInterventionEffect(
    StallInterventionStore store,
    WatcherCaseStore cases,
    StallInterventionFactsBuilder facts,
    IHostAdmissionControl admission,
    IServiceRestartControl restarts,
    IProviderRouteControl routes,
    IBatchGateControl gate,
    IContinuationControl continuations,
    StallInterventionReceipts receipts,
    IConfiguration configuration,
    TimeProvider time) : IAiOperatorProposalEffect
{
    public string Decider => AiOperatorDeciders.StallIntervention;

    public async Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        var plan = application.Decision.Intervention
            ?? throw new InvalidDataException("The stall-intervention receipt has no plan.");
        var stall = application.Payload.StallIntervention
            ?? throw new InvalidDataException("The stall-intervention occurrence has no facts.");
        if (application.Decision.Disposition == AiOperatorDisposition.Hold
            || store.FindByWriteKey(application.WriteKey) is not null) return;
        var options = StallInterventionOptions.FromConfiguration(configuration);
        var now = time.GetUtcNow().UtcDateTime;
        var all = store.All();
        var original = stall.FollowUp is { } check ? all.FirstOrDefault(item => item.Id == check.InterventionId) : null;
        var watcherCase = Case(stall.Subject, original?.Fingerprint);
        if (stall.Trigger == StallInterventionTriggers.Stall
            && (watcherCase is null || !StallInterventionFactsBuilder.IsOpen(watcherCase))) return;
        if (stall.Trigger == StallInterventionTriggers.FollowUp && original?.Status != StallInterventionStatuses.Applied) return;

        var previous = original ?? all.LastOrDefault(item => item.Subject == stall.Subject
            && now - item.DecidedAtUtc <= TimeSpan.FromHours(options.Limits.LoopWindowHours));
        var record = new StallInterventionRecord
        {
            Id = StallInterventionStore.IdFor(application.WriteKey),
            WriteKey = application.WriteKey,
            Subject = stall.Subject,
            Kind = stall.Kind,
            Host = plan.HostName,
            Action = plan.Action,
            Autonomy = plan.Autonomy.ToString(),
            Disposition = application.Decision.Disposition.ToString(),
            ReasonCode = plan.ReasonCode,
            Status = StallInterventionStatuses.Applied,
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

        record = application.Decision.Disposition switch
        {
            AiOperatorDisposition.Propose => record with { Status = StallInterventionStatuses.Proposed, ResolvedAtUtc = now },
            AiOperatorDisposition.Ask => record with { Status = StallInterventionStatuses.Escalated, ResolvedAtUtc = now },
            _ when plan.Action == StallInterventionActions.ConfirmCleared => record with
            {
                Status = StallInterventionStatuses.Verified, ResolvedAtUtc = now, EffectObserved = true,
            },
            _ => await ActAsync(record, application.WriteKey, ct),
        };
        if (record.Status == StallInterventionStatuses.Applied && record.FollowUpDueUtc is null)
            record = record with { Status = StallInterventionStatuses.Verified, ResolvedAtUtc = now };
        store.Save(record);
        if (original is not null) Settle(original, record, now);
        receipts.Publish(record, store.All());
    }

    /// <summary>A person's undo. Restarts and escalations have nothing to undo.</summary>
    public StallInterventionUndoResult Undo(string id, string actor)
    {
        var record = store.Find(id);
        if (record is null) return new(false, "intervention-not-found", null);
        if (!record.Undoable || record.UndoneAtUtc is not null)
            return new(false, record.UndoneAtUtc is null ? "intervention-not-undoable" : "intervention-already-undone", record);
        var now = time.GetUtcNow().UtcDateTime;
        string evidence;
        bool undone;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                var value = admission.SetAdmission(record.Host, record.PreviousAdmission ?? 1);
                undone = value == record.PreviousAdmission;
                evidence = $"admission of {record.Host} reads {value?.ToString() ?? "unknown"}";
                break;
            case StallInterventionActions.RerouteProvider:
                routes.Restore(record.Host, record.Provider ?? "unknown");
                undone = !routes.IsRerouted(record.Host, record.Provider ?? "unknown");
                evidence = undone ? $"{record.Provider} is routed to {record.Host} again" : "the route exclusion is still in force";
                break;
            case StallInterventionActions.RequeueFromDelivery:
                var cleared = record.Members.Where(card => continuations.Clear(card, record.Id)).ToArray();
                undone = record.Members.All(card => continuations.Read(card) is null || continuations.LeftReady(card));
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
            Outcome = $"Undone by {actor}: {evidence}.",
        };
        store.Save(updated);
        receipts.Publish(updated, store.All());
        return new(true, "undone", updated);
    }

    private async Task<StallInterventionRecord> ActAsync(StallInterventionRecord record, string writeKey, CancellationToken ct)
    {
        bool undoable = false;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                admission.SetAdmission(record.Host, record.TargetAdmission
                    ?? throw new InvalidDataException("The admission plan has no target."));
                undoable = true;
                break;
            case StallInterventionActions.RestartService or StallInterventionActions.RetryEngineStart:
                var outcome = await restarts.RestartAsync(record.Host, record.Service ?? "unknown", writeKey, record.Done, ct);
                record = record with { EffectObserved = outcome.Succeeded, EffectEvidence = outcome.Evidence };
                break;
            case StallInterventionActions.RerouteProvider:
                routes.Reroute(record.Host, record.Provider ?? "unknown", record.Id);
                undoable = true;
                break;
            case StallInterventionActions.ReconcileBatchGate:
                var dropped = gate.Drop(record.Project, record.Members, $"ai-operator:{record.Id}");
                var closed = record.BatchId is null || gate.CloseNow(record.Project);
                record = record with
                {
                    Outcome = $"dropped {dropped.Count} of {record.Members.Count} members"
                        + (record.BatchId is null ? string.Empty : closed ? "; close-now recorded" : "; close-now refused"),
                    EffectObserved = closed ? null : false,
                    EffectEvidence = closed ? null : "the close-now request was refused",
                };
                break;
            case StallInterventionActions.RequeueFromDelivery:
                foreach (var card in record.Members) continuations.PinLastDelivery(card, record.Id);
                undoable = true;
                break;
            default:
                throw new InvalidOperationException($"Unknown stall intervention {record.Action}.");
        }

        // 17.10.2026 lesson: an automatic action counts only by its measured effect.
        var now = time.GetUtcNow().UtcDateTime;
        var observed = facts.Effect(record, StallInterventionOptions.FromConfiguration(configuration), now,
            out var evidence, out _);
        if (record.EffectObserved == false)
        {
            observed = false;
            evidence = record.EffectEvidence ?? "the action failed";
        }
        if (observed)
            return record with { EffectObserved = true, EffectEvidence = evidence, Undoable = undoable };
        return record with
        {
            Status = StallInterventionStatuses.Escalated,
            EffectObserved = false,
            EffectEvidence = evidence,
            ResolvedAtUtc = now,
            Undoable = false,
            Outcome = $"The effect was not observed ({evidence}); I handed the event to a person.",
        };
    }

    /// <summary>The follow-up closes the intervention it checked.</summary>
    private void Settle(StallInterventionRecord original, StallInterventionRecord followUp, DateTime now)
    {
        var status = followUp.Action switch
        {
            StallInterventionActions.Escalate => StallInterventionStatuses.Escalated,
            StallInterventionActions.ConfirmCleared or StallInterventionActions.RestoreAdmission
                => StallInterventionStatuses.Verified,
            _ => original.Status,
        };
        if (status == original.Status) return;
        if (original.Action == StallInterventionActions.RerouteProvider
            && followUp.Action == StallInterventionActions.ConfirmCleared)
            routes.Restore(original.Host, original.Provider ?? "unknown");
        store.Save(original with
        {
            Status = status,
            ResolvedAtUtc = now,
            Outcome = $"{followUp.Id}: {followUp.Done}",
            // Undo is offered while the intervention is in force; a settled one
            // was confirmed, reverted by the follow-up or handed to a person.
            Undoable = false,
        });
    }

    private WatcherCase? Case(string subject, string? fingerprint)
    {
        var root = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) return null;
        var all = cases.All(root);
        return (fingerprint is null ? null : all.FirstOrDefault(item => item.Fingerprint == fingerprint))
            ?? all.Where(item => item.Subject == subject && StallInterventionFactsBuilder.KindOf(item) is not null)
                .OrderByDescending(item => item.LastSeenUtc).FirstOrDefault();
    }
}
