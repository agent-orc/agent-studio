using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal sealed record CredentialRunbookStepContext(
    CredentialRunbookOperationDto Operation,
    CredentialRunbookDto Runbook,
    string StepId,
    string AttemptKey,
    bool CustodyChanged,
    string? ProvenGeneration);

internal sealed record CredentialRunbookStepResult(
    string Outcome,
    string? ObservedGeneration = null,
    IReadOnlyList<string>? EvidenceRefs = null,
    string? Detail = null,
    string? IncidentRef = null,
    string? LinkedRenewalKey = null,
    string? LinkedRenewalOperationId = null,
    bool PriorGenerationRestored = false)
{
    public const string Pending = "pending";

    public static CredentialRunbookStepResult Succeeded(string? generation, params string[] evidence)
        => new(CredentialRunbookStepOutcomes.Succeeded, generation, evidence);
    public static CredentialRunbookStepResult Failed(string detail)
        => new(CredentialRunbookStepOutcomes.Failed, Detail: detail);
    public static CredentialRunbookStepResult Waiting(string detail)
        => new(Pending, Detail: detail);
}

/// <summary>
/// One allowlisted opcode. An adapter must be idempotent on the attempt key: a retry
/// after a crash resumes the existing side effect instead of issuing again.
/// </summary>
internal interface ICredentialRunbookStepAdapter
{
    Task<CredentialRunbookStepResult> ExecuteAsync(CredentialRunbookStepContext context, CancellationToken ct);
}

internal sealed record CredentialGenerationFence(string? Generation, string? LastOperationId);

internal interface ICredentialRunbookJournal
{
    Task<CredentialRunbookOperationDto?> GetOperationAsync(string operationId, CancellationToken ct);
    Task<IReadOnlyList<CredentialRunbookStepReceiptDto>> ListReceiptsAsync(string operationId, CancellationToken ct);
    Task<CredentialRunbookStepReceiptDto> RecordReceiptAsync(CredentialRunbookOperationDto operation,
        CredentialRunbookStepReceiptDto receipt, CancellationToken ct);
    Task<CredentialRunbookOperationDto> UpdateOperationAsync(CredentialRunbookOperationDto operation, CancellationToken ct);
    Task<CredentialGenerationFence> ReadGenerationAsync(CredentialRunbookOperationDto operation, CancellationToken ct);
}

/// <summary>
/// Walks a runbook in order from durable receipts. A side-effect step records its
/// started receipt before the adapter runs, so a crash at any boundary resumes the
/// same attempt. Repair starts only after typed classification and a generation fence.
/// </summary>
internal sealed class CredentialRunbookExecutor(
    ICredentialRunbookJournal journal,
    CredentialRunbookCatalogueDto catalogue,
    Func<CredentialRunbookOperationDto, IReadOnlyDictionary<string, ICredentialRunbookStepAdapter>> adapters,
    TimeProvider clock)
{
    public async Task<CredentialRunbookOperationDto> RunAsync(string operationId, string actorId, CancellationToken ct)
    {
        var operation = await journal.GetOperationAsync(operationId, ct)
            ?? throw new KeyNotFoundException("Runbook operation was not found.");
        if (CredentialRunbookStates.IsTerminal(operation.State)) return operation;
        var runbook = catalogue.Runbooks.SingleOrDefault(item => item.Id == operation.RunbookId);
        if (runbook is null || runbook.Version != operation.RunbookVersion)
            return await SettleAsync(operation, CredentialRunbookStates.Failed, null, "runbook-version-changed", ct);
        var receipts = (await journal.ListReceiptsAsync(operationId, ct))
            .ToDictionary(receipt => receipt.StepId, StringComparer.Ordinal);
        var available = adapters(operation);
        var custodyChanged = false;
        var installed = false;
        var issued = false;
        string? proven = null;

        foreach (var stepId in runbook.PlatformSteps)
        {
            var step = CredentialRunbookProtocol.Steps[stepId];
            receipts.TryGetValue(stepId, out var receipt);
            if (receipt?.Outcome == CredentialRunbookStepOutcomes.Succeeded)
            {
                Track(step, receipt.ObservedGeneration);
                continue;
            }
            // An issuance failure leaves the prior generation live. Once staging began,
            // the binding needs explicit proof of the old or the new generation.
            var failedState = installed || step.ChangesCustody && step.Effect != CredentialRunbookStepEffect.Issue &&
                receipt is not null
                ? CredentialRunbookStates.RecoveryRequired : CredentialRunbookStates.Failed;
            if (receipt?.Outcome == CredentialRunbookStepOutcomes.Failed)
                return await SettleAsync(operation, failedState, stepId, "step-failed", ct);
            if (step.Effect != CredentialRunbookStepEffect.Observe &&
                operation.SelectionMode != CredentialRunbookModes.Repair)
                return await SettleAsync(operation, CredentialRunbookStates.DiagnosisOnly, stepId,
                    "classification-incomplete", ct);
            if (step.ChangesCustody && operation.Intent != CredentialRunbookIntents.Repair)
                return await SettleAsync(operation, CredentialRunbookStates.AwaitingRepair, stepId,
                    "repair-intent-required", ct);
            if (step.Effect != CredentialRunbookStepEffect.Observe)
            {
                var fence = await journal.ReadGenerationAsync(operation, ct);
                var ours = fence.Generation == operation.ExpectedGeneration ||
                           proven is not null && fence.Generation == proven ||
                           operation.LinkedRenewalOperationId is not null &&
                           fence.LastOperationId == operation.LinkedRenewalOperationId;
                if (!ours)
                    return await SettleAsync(operation, failedState, stepId, "stale-credential-generation", ct);
            }
            if (step.Effect is CredentialRunbookStepEffect.Revoke or CredentialRunbookStepEffect.Advertise &&
                (proven is null || issued && proven == operation.ExpectedGeneration))
                return await SettleAsync(operation, failedState, stepId, "unverified-generation", ct);
            if (!available.TryGetValue(stepId, out var adapter))
                return await SettleAsync(operation,
                    step.Owner == CredentialRunbookStepOwner.Human
                        ? CredentialRunbookStates.AwaitingHuman : CredentialRunbookStates.AwaitingHost,
                    stepId, step.HumanBoundary ?? "host-receipt-required", ct);
            if (step.Effect != CredentialRunbookStepEffect.Observe && receipt is null)
                await journal.RecordReceiptAsync(operation,
                    Receipt(operation, stepId, actorId, CredentialRunbookStepOutcomes.Started, null, []), ct);

            var result = await adapter.ExecuteAsync(new(operation, runbook, stepId,
                operation.OperationId + ":" + stepId, custodyChanged, proven), ct);
            if (result.IncidentRef is not null && result.IncidentRef != operation.IncidentRef ||
                result.LinkedRenewalOperationId is not null &&
                result.LinkedRenewalOperationId != operation.LinkedRenewalOperationId)
                operation = await journal.UpdateOperationAsync(operation with
                {
                    IncidentRef = result.IncidentRef ?? operation.IncidentRef,
                    LinkedRenewalKey = result.LinkedRenewalKey ?? operation.LinkedRenewalKey,
                    LinkedRenewalOperationId = result.LinkedRenewalOperationId ?? operation.LinkedRenewalOperationId,
                }, ct);
            switch (result.Outcome)
            {
                case CredentialRunbookStepOutcomes.Succeeded:
                    await journal.RecordReceiptAsync(operation, Receipt(operation, stepId, actorId,
                        result.Outcome, result.ObservedGeneration, result.EvidenceRefs ?? []), ct);
                    Track(step, result.ObservedGeneration);
                    continue;
                case CredentialRunbookStepOutcomes.Failed:
                    await journal.RecordReceiptAsync(operation, Receipt(operation, stepId, actorId,
                        result.Outcome, result.ObservedGeneration, result.EvidenceRefs ?? []), ct);
                    // A proved rollback leaves the prior generation live on both units: bounded recovery.
                    return await SettleAsync(operation,
                        result.PriorGenerationRestored ? CredentialRunbookStates.Failed : failedState,
                        stepId, result.Detail ?? "step-failed", ct);
                case CredentialRunbookStepOutcomes.AwaitingHuman:
                    await journal.RecordReceiptAsync(operation, Receipt(operation, stepId, actorId,
                        result.Outcome, null, result.EvidenceRefs ?? []), ct);
                    return await SettleAsync(operation, CredentialRunbookStates.AwaitingHuman, stepId,
                        result.Detail ?? step.HumanBoundary, ct);
                default:
                    return await SettleAsync(operation,
                        step.Effect == CredentialRunbookStepEffect.Observe
                            ? CredentialRunbookStates.AwaitingRecovery : CredentialRunbookStates.AwaitingHost,
                        stepId, result.Detail, ct);
            }
        }
        return await SettleAsync(operation, CredentialRunbookStates.Complete, null, null, ct);

        void Track(CredentialRunbookStepDefinition step, string? observed)
        {
            if (step.ChangesCustody) custodyChanged = true;
            if (step.ChangesCustody && step.Effect != CredentialRunbookStepEffect.Issue) installed = true;
            if (step.Effect is CredentialRunbookStepEffect.Issue or CredentialRunbookStepEffect.Stage)
            {
                issued = true;
                proven = null;
            }
            if (step.Proves && observed is not null) proven = observed;
        }
    }

    private CredentialRunbookStepReceiptDto Receipt(CredentialRunbookOperationDto operation, string stepId,
        string actorId, string outcome, string? observed, IReadOnlyList<string> evidence)
        => new(operation.OperationId, stepId, actorId, operation.HostId, operation.CredentialId,
            operation.ExpectedGeneration, observed, outcome, evidence, clock.GetUtcNow().UtcDateTime);

    private async Task<CredentialRunbookOperationDto> SettleAsync(CredentialRunbookOperationDto operation,
        string state, string? step, string? detail, CancellationToken ct)
    {
        if (operation.State == state && operation.CurrentStep == step && operation.Detail == detail) return operation;
        return await journal.UpdateOperationAsync(operation with
        {
            State = state, CurrentStep = step, Detail = detail, UpdatedAt = clock.GetUtcNow().UtcDateTime,
        }, ct);
    }
}
