using AgentStudio.Shared;

namespace AgentStudio.Security;

public sealed partial class AccessSecurityStore
{
    private readonly HashSet<string> _executingBatches = [];
    public IReadOnlyList<OperatorBatch> ListOperatorBatches()
    {
        lock (_gate)
        {
            var state = StateLocked();
            return state.OperatorBatches.Select(batch => batch.State == "executing" && !_executingBatches.Contains(batch.Id)
                ? batch with { State = "confirmed", Items = InterruptedBatchItems(batch) } : batch.State == "pending" && batch.ExpiresAt <= Now
                ? batch with { State = "expired", Items = batch.Items.Select(item => item with
                    { State = "skipped", ResultReason = "Batch lapsed after 30 minutes." }).ToList() }
                : batch.State == "pending" && !BatchSessionLive(state, batch)
                    ? batch with { State = "cancelled", Items = batch.Items.Select(item => item with
                        { State = "skipped", ResultReason = "The session or elevated class ended." }).ToList() } : batch).ToList();
        }
    }

    private bool BatchSessionLive(SecurityState state, OperatorBatch batch)
        => state.ServiceAccounts.FirstOrDefault(agent => agent.Id == batch.PrincipalId && agent.DisabledAt is null)
            ?.Sessions?.Any(session => session.Id == batch.SessionId && !SessionEnded(state, session, Now)
                && session.Elevation?.Classes.Any(entry => entry.Id == batch.Class) == true) == true;

    public void RequireBatchSession(string agentId, string sessionId, string classId)
    {
        lock (_gate)
        {
            if (ElevatedActionClasses.Find(classId)?.Mode != ElevatedActionClasses.Batch)
                throw new SecurityOperationException(400, "batch-class-invalid", "Choose an override batch class.");
            if (!BatchSessionLive(StateLocked(), new("", agentId, sessionId, "", "", classId, "", Now, Now, [])))
                throw new SecurityOperationException(403, "batch-class-required", "This session has no live confirmation for the batch class.");
        }
    }

    public OperatorBatch SubmitOperatorBatch(ServiceAccountPrincipal principal, SubmitOperatorBatchRequest request,
        IReadOnlyList<OperatorBatchItem> items)
    {
        lock (_gate)
        {
            RequireBatchSession(principal.AccountId, principal.TokenId, request.Class);
            var next = Copy(StateLocked());
            if (ListOperatorBatches().Count(pendingBatch => pendingBatch.SessionId == principal.TokenId && pendingBatch.State == "pending") >= MaxOpenApprovalsPerSession)
                throw new SecurityOperationException(429, "approval-queue-full", "Decide the open batches first.");
            next.OperatorBatches.RemoveAll(batch => batch.SubmittedAt < Now.AddDays(-30));
            var batch = new OperatorBatch("opb_" + Guid.NewGuid().ToString("N"), principal.AccountId, principal.TokenId,
                principal.Operator!.OwnerId, principal.ActorId, request.Class, OperatorInstructionSources.Normalize(request.InstructionSource),
                Now, Now.AddMinutes(30), items) { PrincipalName = principal.AccountName };
            next.OperatorBatches.Add(batch);
            SaveLocked(next);
            using var scope = OperatorActorScope.Enter(principal.Operator!);
            AppendIamAudit(batch.Actor, "operator-batch.submit", "operator-batch", batch.Id, batch);
            return batch;
        }
    }

    public OperatorBatch DecideOperatorBatch(string id, DecideOperatorBatchRequest request, HumanPrincipal owner)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireSignInOwnerLocked(next, owner);
            var batch = ListOperatorBatches().FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "batch-not-found", "Batch not found.");
            if (batch.State != "pending")
                throw new SecurityOperationException(409, "batch-already-settled", "This batch has ended or was already decided.");
            if (request.Selected is null || request.Selected.Distinct().Count() != request.Selected.Count
                || request.Selected.Any(index => !batch.Items.Any(item => item.Index == index && item.Refusal is null)))
                throw new SecurityOperationException(400, "batch-selection-invalid", "Only qualifying items can be selected.");
            ConsumeStepUpLocked(next, owner, "Verify a second factor before deciding a batch.");
            var decided = batch with { State = request.Approve ? "executing" : "rejected", ApprovedBy = owner.User.Id, DecidedAt = Now,
                Items = batch.Items.Select(item => item with
                {
                    State = request.Approve && item.Refusal is null && request.Selected.Contains(item.Index) ? "waiting" : "skipped",
                    ResultReason = item.Refusal ?? (!request.Approve ? "Batch rejected." : !request.Selected.Contains(item.Index)
                        ? "Deselected by the Owner." : "Waiting for Studio execution."),
                }).ToList() };
            if (request.Approve) _executingBatches.Add(id);
            next.OperatorBatches[next.OperatorBatches.FindIndex(candidate => candidate.Id == id)] = decided;
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, request.Approve ? "operator-batch.confirm" : "operator-batch.reject", "operator-batch", id,
                new { request.Selected, decided.ApprovedBy });
            return decided;
        }
    }

    public string? ConsumeBatchItem(OperatorBatch batch, string? project)
    {
        lock (_gate)
        {
            if (!BatchSessionLive(StateLocked(), batch)) return "The operator session or elevated class ended.";
            var next = Copy(StateLocked());
            var agent = next.ServiceAccounts.First(account => account.Id == batch.PrincipalId);
            var session = agent.Sessions!.First(candidate => candidate.Id == batch.SessionId);
            var elevation = session.Elevation!;
            var entry = elevation.Classes.First(item => item.Id == batch.Class);
            var uses = entry.Uses.Where(use => use.At > Now.AddHours(-1) && use.At <= Now).ToList();
            if (uses.Count >= ElevatedActionClasses.Find(batch.Class)!.RatePerHour)
            {
                var refusals = elevation.RateRefusals.Where(at => at > Now.AddMinutes(-10) && at <= Now).Append(Now).ToList();
                var ended = refusals.Count >= 3;
                var refused = ended ? session with { Classes = [], Elevation = null, ElevationRateEndedAt = Now }
                    : session with { Elevation = elevation with { RateRefusals = refusals } };
                next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
                    { Sessions = agent.Sessions!.Select(item => item.Id == session.Id ? refused : item).ToList() };
                SaveLocked(next);
                if (ended) AppendIamAudit(session.CreatedBy, "operator-elevation.rate-ended", "operator-agent", agent.Id,
                    new { sessionId = session.Id, batch.Class });
                return ended ? "Elevation ended after three hourly-rate refusals in ten minutes." : "The batch class reached its hourly item limit.";
            }
            var used = entry with { Uses = [.. uses, new(Now, project)] };
            var updated = session with { Elevation = elevation with { Classes = elevation.Classes.Select(item => item.Id == entry.Id ? used : item).ToList() } };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
                { Sessions = agent.Sessions!.Select(item => item.Id == session.Id ? updated : item).ToList() };
            SaveLocked(next);
            return null;
        }
    }

    private static IReadOnlyList<OperatorBatchItem> InterruptedBatchItems(OperatorBatch batch)
        => batch.Items.Select(item => item.State == "waiting" ? item with
            { State = "skipped", ResultReason = "Confirmation interrupted before an execution result was recorded; inspect the card timeline." } : item).ToList();

    public OperatorBatch FinishOperatorBatch(string id)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var index = next.OperatorBatches.FindIndex(candidate => candidate.Id == id);
            var finished = next.OperatorBatches[index] with { State = "confirmed", Items = InterruptedBatchItems(next.OperatorBatches[index]) };
            next.OperatorBatches[index] = finished;
            SaveLocked(next);
            _executingBatches.Remove(id);
            return finished;
        }
    }

    public OperatorBatch RecordBatchItem(string id, int index, bool executed, string reason)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var position = next.OperatorBatches.FindIndex(candidate => candidate.Id == id);
            var batch = next.OperatorBatches[position];
            var updated = batch with { Items = batch.Items.Select(item => item.Index == index
                ? item with { State = executed ? "executed" : "skipped", ResultReason = reason } : item).ToList() };
            next.OperatorBatches[position] = updated;
            SaveLocked(next);
            using var scope = OperatorActorScope.Enter(new(batch.PrincipalId, batch.SessionId, batch.OwnerId)
                { ApprovalId = batch.Id, ApprovedBy = batch.ApprovedBy, BatchId = batch.Id });
            AppendIamAudit(batch.Actor, "operator-batch.item", "operator-batch", id, new { index, executed, reason, batchId = id, approvedBy = batch.ApprovedBy, elevationClass = batch.Class, batch.InstructionSource });
            var recordedItem = updated.Items.First(candidate => candidate.Index == index);
            var device = next.ServiceAccounts.FirstOrDefault(agent => agent.Id == batch.PrincipalId)
                ?.Sessions?.FirstOrDefault(session => session.Id == batch.SessionId)?.Device;
            ControlPlaneAudit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.OperatorAgent, batch.Actor,
                ElevatedActionClasses.Find(batch.Class)!.Operations[0], executed ? "executed" : "skipped",
                Target: $"card:{recordedItem.Request.Card}", Session: batch.SessionId,
                Details: new { elevationClass = batch.Class, batchId = id, approvedBy = batch.ApprovedBy,
                    deviceThumbprint = device?.DeviceKeyThumbprint ?? device?.KeyThumbprint,
                    reason = recordedItem.Request.Reason, instructionSource = batch.InstructionSource, result = reason, index }, At: Now));
            return updated;
        }
    }
}
