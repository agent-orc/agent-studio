using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Durable runbook operation journal (I9). Operations and step receipts are metadata
/// only; host side effects stay with the I4 renewal journal and host adapters.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions RunbookJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex RunbookIncident = new("^incident:[A-Za-z0-9._:-]{1,120}$", RegexOptions.Compiled);
    private static readonly Regex RunbookFactCode = new("^[a-z][a-z_]{0,39}$", RegexOptions.Compiled);
    private static readonly Regex RunbookEvidenceValue = new("^[A-Za-z0-9._:-]{1,80}$", RegexOptions.Compiled);
    private static readonly string[] RunbookSecretMarkers = ["sk-", "ghp_", "gho_", "ghs_", "github_pat_", "xoxb-"];
    private CredentialRunbookCatalogueDto? _runbookCatalogue;

    internal CredentialRunbookCatalogueDto RunbookCatalogue
    {
        get => _runbookCatalogue ??= CredentialRunbookCatalogueSource.Current;
        set => _runbookCatalogue = value;
    }

    /// <summary>Test seam for deterministic adapters; production uses the allowlisted Task Server adapters.</summary>
    internal Func<CredentialRunbookOperationDto, IReadOnlyDictionary<string, ICredentialRunbookStepAdapter>>?
        RunbookAdapterOverride { get; set; }

    /// <summary>Test seam for crash injection around durable journal writes.</summary>
    internal Func<ICredentialRunbookJournal, ICredentialRunbookJournal>? RunbookJournalDecorator { get; set; }

    public CredentialRunbookCatalogueDto GetCredentialRunbookCatalogue() => RunbookCatalogue;

    public CredentialRunbookSelection SelectCredentialRunbook(CredentialIncidentFacts facts)
    {
        ValidateRunbookFacts(facts);
        return CredentialRunbookPolicy.Select(RunbookCatalogue, facts);
    }

    public async Task<CredentialRunbookRunDto> BeginCredentialRunbookAsync(
        BeginCredentialRunbookRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        foreach (var value in new[] { request.InstallationId, request.HostId, request.CredentialId,
                     request.ExpectedGeneration, request.IdempotencyKey, actorId })
            RequireRenewalId(value);
        RequireRunbookIntent(request.Intent);
        ValidateRunbookFacts(request.Facts);
        var evidence = request.EvidenceRefs ?? [];
        RequireRunbookEvidence(evidence);
        if (request.IncidentRef is not null && !RunbookIncident.IsMatch(request.IncidentRef))
            throw new ArgumentException("Incident reference is invalid.");

        CredentialRunbookSelection? selection = null;
        string? operationId = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var record = await ReadRunbookCredentialAsync(connection, transaction,
                    request.InstallationId, request.HostId, request.CredentialId, ct)
                ?? throw new KeyNotFoundException("Credential binding is not registered.");
            // Registry metadata is authoritative for identity; a contradicting fact cannot select a runbook.
            if (request.Facts.CredentialKind is not null && request.Facts.CredentialKind != record.Kind ||
                request.Facts.RepositoryPurpose is not null && record.RepositoryPurpose is not null &&
                request.Facts.RepositoryPurpose != record.RepositoryPurpose)
                throw new TaskServerConflictException("runbook-fact-conflict", "Facts contradict the credential registry.");
            var facts = request.Facts with
            {
                CredentialKind = record.Kind,
                RepositoryPurpose = request.Facts.RepositoryPurpose ?? record.RepositoryPurpose,
            };
            selection = CredentialRunbookPolicy.Select(RunbookCatalogue, facts);
            var existing = await ReadRunbookOperationAsync(connection, transaction, """
                SELECT payload_json FROM credential_runbook_operations
                 WHERE installation_id = $installation AND host_id = $host
                   AND credential_id = $credential AND idempotency_key = $key;
                """, ct, ("$installation", request.InstallationId), ("$host", request.HostId),
                ("$credential", request.CredentialId), ("$key", request.IdempotencyKey));
            if (existing is not null)
            {
                if (existing.ExpectedGeneration != request.ExpectedGeneration || existing.Facts != facts ||
                    existing.ActorId != actorId || existing.Intent != request.Intent)
                    throw new TaskServerConflictException("runbook-idempotency-conflict",
                        "Operation key belongs to another runbook intent.");
                operationId = existing.OperationId;
                return;
            }
            if (selection.Mode == CredentialRunbookModes.None || selection.RunbookId is null) return;
            if (record.Generation != request.ExpectedGeneration)
                throw new TaskServerConflictException("stale-credential-generation",
                    "Runbook expected generation is no longer current.");
            if (await ScalarAsync(connection, """
                SELECT 1 FROM credential_runbook_operations
                 WHERE installation_id = $installation AND host_id = $host AND credential_id = $credential
                   AND state NOT IN ('complete','failed','recovery-required','diagnosis-only') LIMIT 1;
                """, ct, transaction, ("$installation", request.InstallationId), ("$host", request.HostId),
                ("$credential", request.CredentialId)) is not null)
                throw new TaskServerConflictException("runbook-binding-busy",
                    "Another runbook operation owns this credential binding.");
            var operation = new CredentialRunbookOperationDto(
                "credrb_" + Guid.NewGuid().ToString("N"), selection.RunbookId, selection.RunbookVersion!.Value,
                selection.Mode, request.Intent, request.InstallationId, request.HostId, request.CredentialId,
                request.ExpectedGeneration, actorId, request.IdempotencyKey, request.IncidentRef, evidence, facts,
                selection.Policy, CredentialRunbookStates.Requested, null, null, null, null, UtcNow, UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO credential_runbook_operations(operation_id, installation_id, host_id,
                    credential_id, idempotency_key, state, payload_json)
                VALUES($id,$installation,$host,$credential,$key,$state,$payload);
                """, ct, transaction, ("$id", operation.OperationId),
                ("$installation", operation.InstallationId), ("$host", operation.HostId),
                ("$credential", operation.CredentialId), ("$key", operation.IdempotencyKey),
                ("$state", operation.State), ("$payload", JsonSerializer.Serialize(operation, RunbookJson)));
            await AuditRunbookAsync(connection, transaction, actorId, "credential-runbook.requested", operation, null, ct);
            operationId = operation.OperationId;
        }, ct);
        if (operationId is null) return new(selection!, null, []);
        await RunbookExecutor().RunAsync(operationId, actorId, ct);
        return (await GetCredentialRunbookAsync(operationId, ct))! with { Selection = selection! };
    }

    public async Task<CredentialRunbookRunDto> ContinueCredentialRunbookAsync(
        string operationId, ContinueCredentialRunbookRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        RequireRenewalId(operationId);
        RequireRenewalId(actorId);
        RequireRunbookIntent(request.Intent);
        var journal = RunbookJournal();
        var operation = await journal.GetOperationAsync(operationId, ct)
            ?? throw new KeyNotFoundException("Runbook operation was not found.");
        if (request.Intent == CredentialRunbookIntents.Repair && operation.Intent != CredentialRunbookIntents.Repair &&
            !CredentialRunbookStates.IsTerminal(operation.State))
            await journal.UpdateOperationAsync(operation with { Intent = CredentialRunbookIntents.Repair }, ct);
        await RunbookExecutor().RunAsync(operationId, actorId, ct);
        return (await GetCredentialRunbookAsync(operationId, ct))!;
    }

    /// <summary>Accepts an explicit host or human receipt for the current step only.</summary>
    public async Task<CredentialRunbookRunDto> RecordCredentialRunbookStepAsync(
        string operationId, RecordCredentialRunbookStepRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        RequireRenewalId(operationId);
        RequireRenewalId(actorId);
        if (!CredentialRunbookProtocol.Steps.TryGetValue(request.StepId ?? "", out var step))
            throw new ArgumentException("Step is not an allowlisted runbook opcode.");
        var allowed = step.Owner switch
        {
            CredentialRunbookStepOwner.Host => request.Outcome is CredentialRunbookStepOutcomes.Started
                or CredentialRunbookStepOutcomes.Succeeded or CredentialRunbookStepOutcomes.Failed,
            CredentialRunbookStepOwner.Human => request.Outcome is CredentialRunbookStepOutcomes.Succeeded
                or CredentialRunbookStepOutcomes.Failed,
            _ => throw new TaskServerConflictException("runbook-step-server-owned",
                "A Task Server adapter owns this step; it cannot be asserted externally."),
        };
        if (!allowed) throw new ArgumentException("Receipt outcome is not valid for this step.");
        var evidence = request.EvidenceRefs ?? [];
        RequireRunbookEvidence(evidence);
        if (request.ObservedGeneration is not null && !IsRunbookSafe(request.ObservedGeneration))
            throw new ArgumentException("Observed generation is not an opaque identifier.");

        var journal = RunbookJournal();
        var operation = await journal.GetOperationAsync(operationId, ct)
            ?? throw new KeyNotFoundException("Runbook operation was not found.");
        if (!RunbookCatalogue.Runbooks.Any(runbook => runbook.Id == operation.RunbookId &&
                runbook.PlatformSteps.Contains(request.StepId!)))
            throw new ArgumentException("Step is not part of this runbook.");
        var receipts = await journal.ListReceiptsAsync(operationId, ct);
        var existing = receipts.FirstOrDefault(receipt => receipt.StepId == request.StepId);
        var final = existing?.Outcome is CredentialRunbookStepOutcomes.Succeeded or CredentialRunbookStepOutcomes.Failed;
        if (final && existing!.Outcome == request.Outcome && existing.ObservedGeneration == request.ObservedGeneration)
            return (await GetCredentialRunbookAsync(operationId, ct))!;
        if (final || CredentialRunbookStates.IsTerminal(operation.State))
            throw new TaskServerConflictException("runbook-receipt-conflict", "The step already has a different final receipt.");
        if (operation.CurrentStep != request.StepId || operation.State is not
                (CredentialRunbookStates.AwaitingHost or CredentialRunbookStates.AwaitingHuman))
            throw new TaskServerConflictException("runbook-step-not-current", "Only the current waiting step accepts a receipt.");
        await journal.RecordReceiptAsync(operation, new(operation.OperationId, request.StepId!, actorId,
            operation.HostId, operation.CredentialId, operation.ExpectedGeneration, request.ObservedGeneration,
            request.Outcome, evidence, UtcNow), ct);
        await RunbookExecutor().RunAsync(operationId, actorId, ct);
        return (await GetCredentialRunbookAsync(operationId, ct))!;
    }

    public async Task<CredentialRunbookRunDto?> GetCredentialRunbookAsync(string operationId, CancellationToken ct)
    {
        RequireRenewalId(operationId);
        var journal = new StoreRunbookJournal(this);
        var operation = await journal.GetOperationAsync(operationId, ct);
        if (operation is null) return null;
        var selection = CredentialRunbookPolicy.Select(RunbookCatalogue, operation.Facts);
        return new(selection, operation, await journal.ListReceiptsAsync(operationId, ct));
    }

    internal CredentialRunbookExecutor RunbookExecutor()
        => new(RunbookJournal(), RunbookCatalogue, RunbookAdapterOverride ?? RunbookAdapters, _clock);

    private ICredentialRunbookJournal RunbookJournal()
    {
        ICredentialRunbookJournal journal = new StoreRunbookJournal(this);
        return RunbookJournalDecorator?.Invoke(journal) ?? journal;
    }

    private static void RequireRunbookIntent(string? intent)
    {
        if (intent is not (CredentialRunbookIntents.Diagnose or CredentialRunbookIntents.Repair))
            throw new ArgumentException("Runbook intent must be diagnose or repair.");
    }

    private static void ValidateRunbookFacts(CredentialIncidentFacts? facts)
    {
        if (facts is null ||
            facts.CredentialKind is not null && !CredentialRegistryProtocol.Kinds.Contains(facts.CredentialKind) ||
            facts.ProbeOutcome is not null && !CredentialRunbookProtocol.Outcomes.Contains(facts.ProbeOutcome) ||
            facts.RequestNormalizedCode is not null && !RunbookFactCode.IsMatch(facts.RequestNormalizedCode) ||
            facts.IncidentCorroboration is not null &&
            !CredentialRunbookProtocol.Corroborations.Contains(facts.IncidentCorroboration) ||
            facts.RepositoryPurpose is not null && facts.RepositoryPurpose is not ("product" or "workspace"))
            throw new ArgumentException("Incident facts contain an untyped value.");
    }

    private static void RequireRunbookEvidence(IReadOnlyList<string>? evidence)
    {
        if (evidence is null || evidence.Count > 16 ||
            evidence.Any(reference => !RenewalEvidence.IsMatch(reference) || !IsRunbookSafe(reference)))
            throw new ArgumentException("Evidence references must be opaque evidence identifiers.");
    }

    private static bool IsRunbookSafe(string value)
        => IsRenewalId(value) &&
           !RunbookSecretMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string RunbookEvidenceRef(string kind, string? value)
        => "evidence:" + kind + ":" + (value is not null && RunbookEvidenceValue.IsMatch(value) && IsRunbookSafe(value)
            ? value : "redacted");

    private async Task AuditRunbookAsync(SqliteConnection connection, SqliteTransaction transaction, string actorId,
        string action, CredentialRunbookOperationDto operation, CredentialRunbookStepReceiptDto? receipt,
        CancellationToken ct)
        => await AuditAsync(connection, transaction, actorId, action, "credential", operation.CredentialId,
            JsonSerializer.Serialize(new
            {
                operation.OperationId, operation.RunbookId, operation.RunbookVersion, operation.HostId,
                operation.ExpectedGeneration, receipt?.ObservedGeneration, operation.IncidentRef,
                EvidenceRefs = receipt?.EvidenceRefs ?? operation.EvidenceRefs,
            }, RunbookJson), ct);

    private static async Task<CredentialRunbookOperationDto?> ReadRunbookOperationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var payload = await ScalarAsync(connection, sql, ct, transaction, parameters) as string;
        return payload is null ? null : JsonSerializer.Deserialize<CredentialRunbookOperationDto>(payload, RunbookJson);
    }

    private static async Task<CredentialRegistryRecordDto?> ReadRunbookCredentialAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string installationId, string hostId,
        string credentialId, CancellationToken ct)
    {
        var payload = await ScalarAsync(connection, """
            SELECT payload_json FROM credential_registry WHERE installation_id = $installation
               AND host_id = $host AND credential_id = $credential;
            """, ct, transaction, ("$installation", installationId), ("$host", hostId),
            ("$credential", credentialId)) as string;
        return payload is null ? null : JsonSerializer.Deserialize<CredentialRegistryRecordDto>(payload, CredentialRegistryJson);
    }

    private sealed class StoreRunbookJournal(TaskServerStore store) : ICredentialRunbookJournal
    {
        public async Task<CredentialRunbookOperationDto?> GetOperationAsync(string operationId, CancellationToken ct)
        {
            await using var connection = await store.OpenReadyAsync(ct);
            return await ReadRunbookOperationAsync(connection, null,
                "SELECT payload_json FROM credential_runbook_operations WHERE operation_id = $id;", ct,
                ("$id", operationId));
        }

        public async Task<IReadOnlyList<CredentialRunbookStepReceiptDto>> ListReceiptsAsync(
            string operationId, CancellationToken ct)
        {
            await using var connection = await store.OpenReadyAsync(ct);
            await using var command = Command(connection,
                "SELECT payload_json FROM credential_runbook_receipts WHERE operation_id = $id;", ("$id", operationId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var receipts = new List<CredentialRunbookStepReceiptDto>();
            while (await reader.ReadAsync(ct))
                receipts.Add(JsonSerializer.Deserialize<CredentialRunbookStepReceiptDto>(reader.GetString(0), RunbookJson)!);
            var order = store.RunbookCatalogue.Runbooks
                .FirstOrDefault(runbook => receipts.Count > 0 && runbook.PlatformSteps.Contains(receipts[0].StepId))
                ?.PlatformSteps.ToList() ?? [];
            return receipts.OrderBy(receipt => order.IndexOf(receipt.StepId)).ToArray();
        }

        /// <summary>
        /// One row per step. A retried start keeps the first attempt; a final receipt never
        /// downgrades, and a different final answer is a conflict rather than a second receipt.
        /// </summary>
        public async Task<CredentialRunbookStepReceiptDto> RecordReceiptAsync(
            CredentialRunbookOperationDto operation, CredentialRunbookStepReceiptDto receipt, CancellationToken ct)
        {
            if (receipt.ObservedGeneration is not null && !IsRunbookSafe(receipt.ObservedGeneration))
                throw new ArgumentException("Adapter returned a non-opaque generation.");
            RequireRunbookEvidence(receipt.EvidenceRefs);
            CredentialRunbookStepReceiptDto? stored = null;
            await store.InWriteTransactionAsync(async (connection, transaction) =>
            {
                var payload = await ScalarAsync(connection, """
                    SELECT payload_json FROM credential_runbook_receipts
                     WHERE operation_id = $id AND step_id = $step;
                    """, ct, transaction, ("$id", receipt.OperationId), ("$step", receipt.StepId)) as string;
                var existing = payload is null
                    ? null : JsonSerializer.Deserialize<CredentialRunbookStepReceiptDto>(payload, RunbookJson);
                if (existing is not null)
                {
                    var final = existing.Outcome is CredentialRunbookStepOutcomes.Succeeded
                        or CredentialRunbookStepOutcomes.Failed;
                    var same = existing.Outcome == receipt.Outcome &&
                               existing.ObservedGeneration == receipt.ObservedGeneration &&
                               !receipt.EvidenceRefs.Except(existing.EvidenceRefs).Any();
                    if (same || receipt.Outcome == CredentialRunbookStepOutcomes.Started ||
                        final && receipt.Outcome is CredentialRunbookStepOutcomes.AwaitingHost
                            or CredentialRunbookStepOutcomes.AwaitingHuman)
                    {
                        stored = existing;
                        return;
                    }
                    if (final)
                        throw new TaskServerConflictException("runbook-receipt-conflict",
                            "The step already has a different final receipt.");
                }
                await ExecuteAsync(connection, """
                    INSERT INTO credential_runbook_receipts(operation_id, step_id, outcome, payload_json)
                    VALUES($id, $step, $outcome, $payload)
                    ON CONFLICT(operation_id, step_id) DO UPDATE SET
                        outcome = excluded.outcome, payload_json = excluded.payload_json;
                    """, ct, transaction, ("$id", receipt.OperationId), ("$step", receipt.StepId),
                    ("$outcome", receipt.Outcome), ("$payload", JsonSerializer.Serialize(receipt, RunbookJson)));
                await store.AuditRunbookAsync(connection, transaction, receipt.ActorId,
                    $"credential-runbook.{receipt.StepId}.{receipt.Outcome}", operation, receipt, ct);
                stored = receipt;
            }, ct);
            return stored!;
        }

        public async Task<CredentialRunbookOperationDto> UpdateOperationAsync(
            CredentialRunbookOperationDto operation, CancellationToken ct)
        {
            var updated = operation with { UpdatedAt = store.UtcNow };
            await store.InWriteTransactionAsync(async (connection, transaction) =>
            {
                var previous = await ScalarAsync(connection,
                    "SELECT state FROM credential_runbook_operations WHERE operation_id = $id;", ct, transaction,
                    ("$id", updated.OperationId)) as string;
                await ExecuteAsync(connection, """
                    UPDATE credential_runbook_operations SET state = $state, payload_json = $payload
                     WHERE operation_id = $id;
                    """, ct, transaction, ("$state", updated.State),
                    ("$payload", JsonSerializer.Serialize(updated, RunbookJson)), ("$id", updated.OperationId));
                await store.AuditRunbookAsync(connection, transaction, updated.ActorId,
                    previous == updated.State ? "credential-runbook.updated" : "credential-runbook." + updated.State,
                    updated, null, ct);
            }, ct);
            return updated;
        }

        public async Task<CredentialGenerationFence> ReadGenerationAsync(
            CredentialRunbookOperationDto operation, CancellationToken ct)
        {
            await using var connection = await store.OpenReadyAsync(ct);
            var record = await ReadRunbookCredentialAsync(connection, null, operation.InstallationId,
                operation.HostId, operation.CredentialId, ct);
            return new(record?.Generation, record?.LastOperationId);
        }
    }
}
