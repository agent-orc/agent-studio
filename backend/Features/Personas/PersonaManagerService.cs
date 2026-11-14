using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Operator;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Personas;

public sealed record PersonaManagerRequest(string? ItemId = null, string? CardId = null,
    string? TargetState = null, string? Revision = null, int? TargetIndex = null,
    int? Adjustment = null, string? Reason = null, string? FromPersonaId = null,
    string? ToPersonaId = null, int? Tokens = null);

public sealed record PersonaManagerReceipt(string Id, string WorkspaceId, string Operation,
    string Actor, string GrantId, DateTime AtUtc, string Outcome, string? Refusal,
    string? ItemId, string? CardId, string? PreviousLane, string? TargetLane,
    string? PreviousRevision, string? NextRevision, int? PreviousImportance,
    int? NextImportance, string? FromPersonaId, string? ToPersonaId, int? Tokens,
    string? PromotedTaskKey = null,
    DateTime? UndoneAtUtc = null, bool CalibrationReversal = false,
    int? PreviousIndex = null, string? Reason = null);

/// <summary>Applies manager T1 bounds after the access middleware has admitted the grant.</summary>
public sealed class PersonaManagerService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string? _root;
    private readonly PersonaItemStore _items;
    private readonly PersonaActivationStore _activations;
    private readonly PersonaProposalLifecycle _proposals;
    private readonly TaskScannerService _cards;
    private readonly TaskTransitionService _transitions;
    private readonly TaskStateMachine _states;
    private readonly RunLeaseService _leases;
    private readonly OperatorCoordinationStore _attention;
    private readonly TimelineLog _timeline;
    private readonly AccessSecurityStore _security;
    private readonly AgentMessageBusStore _bus;
    private readonly TimeProvider _time;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PersonaManagerService(IConfiguration config, PersonaItemStore items,
        PersonaActivationStore activations, PersonaProposalLifecycle proposals,
        TaskScannerService cards, TaskTransitionService transitions,
        TaskStateMachine states,
        RunLeaseService leases,
        OperatorCoordinationStore attention, TimelineLog timeline,
        AccessSecurityStore security, AgentMessageBusStore bus, TimeProvider time)
    {
        _root = config["TaskRepository"];
        _items = items; _activations = activations; _proposals = proposals;
        _cards = cards; _transitions = transitions; _states = states;
        _leases = leases; _attention = attention;
        _timeline = timeline; _security = security; _bus = bus; _time = time;
    }

    public IReadOnlyList<PersonaManagerReceipt> List(string workspaceId)
    {
        lock (_gate) return [.. Load(workspaceId)];
    }

    public async Task<PersonaManagerReceipt> RecordExpiryAsync(string workspaceId, string personaId,
        string itemId, string sessionId, string grantId, DateTime now, CancellationToken ct)
    {
        var receipt = new PersonaManagerReceipt("pmr_" + Guid.CreateVersion7().ToString("N"),
            workspaceId, "persona.decision.expire", "persona:" + personaId + "/session:" + sessionId,
            grantId, now, "applied", null, itemId, null, null, null, null, null,
            null, null, null, null, null);
        Save(receipt);
        var delegatedBy = _security.ListStandingGrants()
            .FirstOrDefault(g => g.Id == grantId)?.IssuedBy;
        _security.AppendPersonaAudit(receipt.Actor, "persona.decision.expire", workspaceId,
            personaId, new { receipt.Id, receipt.ItemId, receipt.GrantId, delegatedBy });
        foreach (var link in _items.Find(workspaceId, itemId)?.Evidence
                     .Where(e => e.Ref.StartsWith("card:", StringComparison.Ordinal))
                     .Select(e => e.Ref[5..]) ?? [])
            if (_cards.FindJob(link) is { } card)
                _timeline.Append(card.FolderPath, "persona-manager", receipt.Actor,
                    "Decision request expired", details: new Dictionary<string, string>
                    { ["receiptId"] = receipt.Id, ["grantId"] = grantId });
        if (_root is not null)
        {
            await _bus.RegisterParticipantAsync(_root, new AgentParticipant
                { Id = "persona:" + personaId, Kind = "Persona", DisplayName = personaId }, ct);
            await _bus.AppendAsync(_root, new AgentMessage
            {
                Id = Guid.CreateVersion7().ToString("N"), CreatedAt = now,
                ParticipantId = "persona:" + personaId, Role = "actor", Kind = "advisory",
                Severity = "Info", Summary = "Decision request expired",
                CorrelationId = receipt.Id, Payload = JsonSerializer.SerializeToElement(receipt, Json),
            }, ct);
        }
        return receipt;
    }

    public async Task<PersonaManagerReceipt> UndoAsync(string workspaceId, string receiptId,
        HumanPrincipal owner, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
        _security.RequirePersonaOwner(owner);
        var now = _time.GetUtcNow().UtcDateTime;
        PersonaManagerReceipt original;
        lock (_gate)
            original = Load(workspaceId).FirstOrDefault(r => r.Id == receiptId)
                ?? throw new PersonaManagerRefusal("manager-receipt-not-found");
        if (original.Outcome != "applied" || original.UndoneAtUtc is not null
            || now > original.AtUtc.AddHours(24))
            throw new PersonaManagerRefusal("manager-undo-unavailable");
        var actor = "human:" + owner.User.Id;
        string? restoredRevision = null;
        switch (original.Operation)
        {
            case "persona.importance.adjust":
                _items.UndoManagerItem(workspaceId, original.ItemId!, original.NextImportance,
                    original.PreviousImportance, false, actor, now);
                break;
            case "persona.proposal.accept":
            {
                var card = _cards.FindJob(original.PromotedTaskKey ?? "")
                    ?? throw new PersonaManagerRefusal("promoted-card-not-found");
                if (card.State != TaskStates.Preparation)
                    throw new PersonaManagerRefusal("promoted-card-started");
                var moved = await _transitions.MoveAsync(card.Id, TaskStates.Archive,
                    card.WatchPath, ct, cause: actor, reason: "Owner undid manager promotion");
                if (moved.Status != MoveJobStatus.Success)
                    throw new PersonaManagerRefusal("promotion-undo-refused");
                _items.UndoManagerItem(workspaceId, original.ItemId!, null, null, true, actor, now);
                break;
            }
            case "card.requeue":
            {
                var card = _cards.FindJob(original.CardId ?? "")
                    ?? throw new PersonaManagerRefusal("card-not-found");
                if (card.State != original.TargetLane ||
                    OperatorCoordinationStore.Revision(card.FolderPath) != original.NextRevision)
                    throw new PersonaManagerRefusal("undo-state-changed");
                if (original.PreviousLane == original.TargetLane)
                {
                    if (!_states.SetOrderInLane(card.Id, card.WatchPath,
                            original.PreviousIndex ?? 0))
                        throw new PersonaManagerRefusal("lane-undo-refused");
                }
                else
                {
                    var moved = await _transitions.MoveAsync(card.Id, original.PreviousLane!,
                        card.WatchPath, ct, targetIndex: original.PreviousIndex,
                        cause: actor, reason: "Owner undid manager lane move",
                        expectedSourceState: original.TargetLane);
                    if (moved.Status != MoveJobStatus.Success)
                        throw new PersonaManagerRefusal("lane-undo-refused");
                }
                if (_cards.FindJob(card.Id, card.WatchPath) is { } restored)
                    restoredRevision = OperatorCoordinationStore.Revision(restored.FolderPath);
                break;
            }
            case "persona.budget.rebalance":
                _activations.Rebalance(workspaceId, original.ToPersonaId!,
                    original.FromPersonaId!, original.Tokens!.Value, actor, now);
                break;
            case "persona.pause":
                _activations.Resume(workspaceId, original.ToPersonaId!,
                    new PersonaChangeRequest("Owner undid manager pause"), owner);
                break;
            case "persona.decision.expire":
                _items.UndoManagerItem(workspaceId, original.ItemId!, null, null,
                    false, actor, now, unexpire: true);
                break;
            default: throw new PersonaManagerRefusal("manager-undo-unavailable");
        }
        var undo = original with { Id = "pmr_" + Guid.CreateVersion7().ToString("N"),
            Actor = actor, AtUtc = now, Outcome = "owner-undo", UndoneAtUtc = now,
            CalibrationReversal = true, PreviousRevision = original.NextRevision,
            NextRevision = restoredRevision ?? original.NextRevision };
        Write(workspaceId, all =>
        {
            var index = all.FindIndex(r => r.Id == receiptId);
            all[index] = original with { UndoneAtUtc = now };
            all.Add(undo);
        });
        _security.AppendPersonaAudit(actor, "persona.manager.owner-undo", workspaceId,
            original.Actor, new { receiptId, undoId = undo.Id, original.GrantId });
        IEnumerable<string> itemCards = original.ItemId is { } linkedItemId
            ? _items.Find(workspaceId, linkedItemId)?.Evidence
                .Where(e => e.Ref.StartsWith("card:", StringComparison.Ordinal))
                .Select(e => e.Ref[5..]) ?? [] : [];
        foreach (var linkedCard in itemCards.Concat(original.CardId is { } cardId ? [cardId] : [])
                     .Distinct(StringComparer.Ordinal))
            if (_cards.FindJob(linkedCard) is { } cardAfter)
                _timeline.Append(cardAfter.FolderPath, "persona-manager-undo", actor,
                    "Owner undid manager action", details: new Dictionary<string, string>
                    { ["receiptId"] = receiptId, ["grantId"] = original.GrantId });
        if (_root is not null)
            await _bus.AppendAsync(_root, new AgentMessage
            {
                Id = Guid.CreateVersion7().ToString("N"), CreatedAt = now,
                ParticipantId = "persona:" + original.Actor.Split('/')[0].Replace("persona:", "", StringComparison.Ordinal),
                Role = "system", Kind = "advisory", Severity = "Info",
                Summary = "Owner undid manager action", CorrelationId = undo.Id,
                Payload = JsonSerializer.SerializeToElement(undo, Json),
            }, ct);
        return undo;
        }
        finally { _serial.Release(); }
    }

    public async Task<PersonaManagerReceipt> ApplyAsync(string workspaceId, string personaId,
        string operation, PersonaManagerRequest request, ServiceAccountPrincipal principal,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var actor = principal.ActorId;
        var grant = _security.ListStandingGrants(principal.AccountId).FirstOrDefault(g =>
            g.RevokedAt is null && g.ExpiresAt > now && g.Operations.Contains(operation));
        var grantId = grant?.Id ?? "none";
        var receipt = new PersonaManagerReceipt("pmr_" + Guid.CreateVersion7().ToString("N"),
            workspaceId, operation, actor, grantId, now, "applied", null,
            request.ItemId, request.CardId, null, request.TargetState, request.Revision,
            null, null, null, request.FromPersonaId, request.ToPersonaId, request.Tokens,
            Reason: request.Reason);
        await _serial.WaitAsync(ct);
        try
        {
        try
        {
            if (principal.Origin != ServiceAccountOrigins.Persona || principal.PersonaId != personaId
                || principal.PersonaWorkspaceId != workspaceId || grant is null)
                throw new PersonaManagerRefusal("manager-grant-required");
            if (!_security.ListStandingGrants(principal.AccountId).Any(g => g.Id == grantId
                && g.RevokedAt is null && g.ExpiresAt > _time.GetUtcNow().UtcDateTime))
                throw new PersonaManagerRefusal("manager-grant-required");
            var activation = _activations.List(workspaceId).Personas
                .FirstOrDefault(p => p.Id == personaId)?.Activation;
            if (activation is null || activation.State != PersonaStates.Active)
                throw new PersonaManagerRefusal("manager-not-active");
            PersonaManagerPolicy.RequireRole(activation.RoleId ?? activation.Descriptor.Id, operation);
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
                throw new PersonaManagerRefusal("reason-required");
            var prior = List(workspaceId);
            switch (operation)
            {
                case "persona.importance.adjust":
                {
                    var old = _items.Find(workspaceId, request.ItemId ?? "")
                        ?? throw new PersonaManagerRefusal("item-not-found");
                    if (_activations.List(workspaceId).Personas.FirstOrDefault(p => p.Id == old.PersonaId)
                        ?.Activation?.State == PersonaStates.Retired)
                        throw new PersonaManagerRefusal("persona-retired");
                    RequireItemScope(activation, old);
                    var next = _items.Adjust(workspaceId, old.Id, request.Adjustment ?? 0,
                        request.Reason, actor, now);
                    receipt = receipt with { PreviousImportance = old.Importance, NextImportance = next.Importance };
                    break;
                }
                case "persona.proposal.accept":
                {
                    var item = _items.Find(workspaceId, request.ItemId ?? "")
                        ?? throw new PersonaManagerRefusal("item-not-found");
                    RequireItemScope(activation, item);
                    PersonaManagerPolicy.RequirePromotion(item, personaId,
                        prior.Count(r => r.Operation == operation && r.Outcome == "applied" &&
                            r.AtUtc.Date == now.Date && r.Actor.StartsWith("persona:" + personaId + "/", StringComparison.Ordinal)));
                    var accepted = _proposals.Decide(workspaceId, item.Id,
                        new PersonaItemDecision(PersonaProposalLifecycle.Accepted, request.Reason), now);
                    receipt = receipt with { PromotedTaskKey = accepted.PromotedTaskKey };
                    break;
                }
                case "card.requeue":
                {
                    await LeaseEndpoints.ClaimGate.WaitAsync(ct);
                    try
                    {
                    var card = _cards.FindJob(request.CardId ?? "")
                        ?? throw new PersonaManagerRefusal("card-not-found");
                    if (!activation.Scope.Projects.Contains("*", StringComparer.Ordinal)
                        && !activation.Scope.Projects.Contains(card.ProjectName, StringComparer.OrdinalIgnoreCase))
                        throw new PersonaManagerRefusal("card-out-of-scope");
                    PersonaManagerPolicy.RequireMove(card, request.TargetState ?? "", request.TargetIndex,
                        prior.Count(r => r.Operation == operation && r.Outcome == "applied" &&
                            r.AtUtc.Date == now.Date && r.Actor.StartsWith("persona:" + personaId + "/", StringComparison.Ordinal)));
                    if (_leases.Peek(card.TaskKey).Lease is not null)
                        throw new PersonaManagerRefusal("running-card-forbidden");
                    if (request.TargetIndex is not null && _cards.GetResidentIdentityFacts()
                        .Any(other => other.WatchPath == card.WatchPath
                            && other.State == request.TargetState
                            && _leases.Peek(other.TaskKey).Lease is not null))
                        throw new PersonaManagerRefusal("running-card-order-frozen");
                    if (_attention.Current(card.TaskKey) is { } held && held.Holder != actor)
                        throw new PersonaManagerRefusal("attention-held");
                    var revision = OperatorCoordinationStore.Revision(card.FolderPath);
                    if (request.Revision is null) throw new PersonaManagerRefusal("card-revision-required");
                    if (request.Revision != revision) throw new PersonaManagerRefusal("stale-card-revision");
                    if (_attention.Acquire(card.TaskKey, actor, false) is null)
                        throw new PersonaManagerRefusal("attention-held");
                    var moved = card.State == request.TargetState
                        ? _states.SetOrderInLane(card.Id, card.WatchPath, request.TargetIndex!.Value)
                            ? new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: card.FolderPath)
                            : new MoveJobOutcome(MoveJobStatus.NotFound)
                        : await _transitions.MoveAsync(card.Id, request.TargetState!, card.WatchPath,
                            ct, request.TargetIndex, cause: actor, reason: request.Reason,
                            expectedSourceState: card.State);
                    if (moved.Status != MoveJobStatus.Success)
                        throw new PersonaManagerRefusal("card-move-refused");
                    var current = _cards.FindJob(card.Id, card.WatchPath);
                    receipt = receipt with { PreviousLane = card.State,
                        PreviousIndex = Math.Max(0, card.Order - 1),
                        NextRevision = moved.NewFolderPath is { } newPath
                            ? OperatorCoordinationStore.Revision(newPath)
                            : current is null ? null : OperatorCoordinationStore.Revision(current.FolderPath) };
                    }
                    finally { LeaseEndpoints.ClaimGate.Release(); }
                    break;
                }
                case "persona.budget.rebalance":
                {
                    var source = _activations.List(workspaceId).Personas
                        .FirstOrDefault(p => p.Id == request.FromPersonaId)?.Activation;
                    var to = _activations.List(workspaceId).Personas
                        .FirstOrDefault(p => p.Id == request.ToPersonaId)?.Activation;
                    if (source is null || to is null) throw new PersonaManagerRefusal("persona-not-active");
                    var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
                    var movedFrom = prior.Where(r => r.Operation == operation && r.Outcome == "applied"
                        && r.AtUtc >= weekStart && r.FromPersonaId == source.PersonaId)
                        .Sum(r => r.Tokens ?? 0);
                    var movedTo = prior.Where(r => r.Operation == operation && r.Outcome == "applied"
                        && r.AtUtc >= weekStart && r.ToPersonaId == to.PersonaId)
                        .Sum(r => r.Tokens ?? 0);
                    PersonaManagerPolicy.RequireRebalance(request.Tokens ?? 0,
                        source.Allowance.TokensPerWeek + movedFrom,
                        to.Allowance.TokensPerWeek - movedTo, movedFrom, movedTo);
                    _activations.Rebalance(workspaceId, source.PersonaId, to.PersonaId,
                        request.Tokens!.Value, actor, now);
                    break;
                }
                case "persona.pause":
                {
                    var target = request.ToPersonaId ?? "";
                    var targetActivation = _activations.List(workspaceId).Personas
                        .FirstOrDefault(p => p.Id == target)?.Activation
                        ?? throw new PersonaManagerRefusal("persona-not-active");
                    var targetRole = targetActivation.RoleId ?? targetActivation.Descriptor.Id;
                    if (target == personaId || targetRole is "chief-of-staff" or "budget-steward")
                        throw new PersonaManagerRefusal("pause-target-forbidden");
                    if (targetRole == "operations-warden" && _items.List(workspaceId).Any(i =>
                            i.State == "open" && i.Floor >= 90))
                        throw new PersonaManagerRefusal("warden-floor-open");
                    _activations.ManagerPause(workspaceId, target, actor, now);
                    break;
                }
                default: throw new PersonaManagerRefusal("manager-operation-forbidden");
            }
        }
        catch (PersonaManagerRefusal ex)
        {
            receipt = receipt with { Outcome = "refused", Refusal = ex.Code };
        }
        catch (PersonaDecisionConflictException ex)
        {
            receipt = receipt with { Outcome = "refused", Refusal = ex.Code };
        }
        catch (SecurityOperationException ex)
        {
            receipt = receipt with { Outcome = "refused", Refusal = ex.Code };
        }
        catch (ArgumentException)
        {
            receipt = receipt with { Outcome = "refused", Refusal = "manager-input-invalid" };
        }
        Save(receipt);
        _security.AppendPersonaAudit(actor, "persona.manager." + receipt.Outcome,
            workspaceId, personaId, new { receipt.Id, receipt.Operation, receipt.GrantId,
                delegatedBy = grant?.IssuedBy, receipt.Refusal, receipt.ItemId,
                receipt.CardId, receipt.Reason });
        IEnumerable<string> itemCards = receipt.ItemId is { } itemId
            ? _items.Find(workspaceId, itemId)?.Evidence
                .Where(e => e.Ref.StartsWith("card:", StringComparison.Ordinal))
                .Select(e => e.Ref[5..]) ?? [] : [];
        var linkedCards = itemCards
            .Concat(receipt.CardId is { } cardId ? [cardId] : [])
            .Distinct(StringComparer.Ordinal);
        foreach (var linkedCard in linkedCards)
            if (_cards.FindJob(linkedCard) is { } cardAfter)
                _timeline.Append(cardAfter.FolderPath, "persona-manager", actor,
                    $"Manager {operation}: {receipt.Outcome}", details: new Dictionary<string, string>
                    { ["receiptId"] = receipt.Id, ["grantId"] = receipt.GrantId,
                        ["refusal"] = receipt.Refusal ?? "" });
        if (_root is not null)
        {
            await _bus.RegisterParticipantAsync(_root, new AgentParticipant
                { Id = "persona:" + personaId, Kind = "Persona", DisplayName = personaId }, ct);
            await _bus.AppendAsync(_root, new AgentMessage
            {
                Id = Guid.CreateVersion7().ToString("N"), CreatedAt = now,
                ParticipantId = "persona:" + personaId, Role = "actor", Kind = "advisory",
                Severity = receipt.Outcome == "refused" ? "Warn" : "Info",
                Summary = $"Manager {operation}: {receipt.Outcome}", CorrelationId = receipt.Id,
                Payload = JsonSerializer.SerializeToElement(receipt, Json),
            }, ct);
        }
        return receipt;
        }
        finally { _serial.Release(); }
    }

    private List<PersonaManagerReceipt> Load(string workspaceId)
    {
        var path = PathFor(workspaceId);
        return path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<List<PersonaManagerReceipt>>(File.ReadAllText(path), Json) ?? [] : [];
    }

    private static void RequireItemScope(PersonaActivationV1 activation, PersonaItemV1 item)
    {
        if (item.Project is not null && !activation.Scope.Projects.Contains("*", StringComparer.Ordinal)
            && !activation.Scope.Projects.Contains(item.Project, StringComparer.OrdinalIgnoreCase))
            throw new PersonaManagerRefusal("item-out-of-scope");
    }

    private void Save(PersonaManagerReceipt receipt) => Write(receipt.WorkspaceId, all => all.Add(receipt));

    /// <summary>The one receipt write path: load, change and replace the ledger atomically.</summary>
    private void Write(string workspaceId, Action<List<PersonaManagerReceipt>> change)
    {
        lock (_gate)
        {
            var path = PathFor(workspaceId);
            if (path is null) return;
            var all = Load(workspaceId);
            change(all);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, path, true);
        }
    }

    private string? PathFor(string workspaceId)
    {
        if (_root is null) return null;
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId))
            throw new ArgumentException("Invalid workspace id.");
        return Path.Combine(_root, ".metadata", "persona-manager", workspaceId + ".json");
    }
}
