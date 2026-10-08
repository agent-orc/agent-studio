using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    internal async Task ApplyCredentialRunbookMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS credential_runbook_operations(
                operation_id TEXT PRIMARY KEY,
                runbook_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                policy TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                host_id TEXT NOT NULL,
                credential_id TEXT NOT NULL,
                expected_generation TEXT NOT NULL,
                incident_id TEXT NOT NULL,
                evidence_correlation_id TEXT NOT NULL,
                facts_json TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS credential_runbook_steps(
                operation_id TEXT NOT NULL REFERENCES credential_runbook_operations(operation_id),
                step_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                state TEXT NOT NULL,
                receipt_json TEXT,
                claimed_at TEXT,
                PRIMARY KEY(operation_id, step_id),
                UNIQUE(operation_id, ordinal)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_credential_runbook_active_binding
                ON credential_runbook_operations(host_id, credential_id)
                WHERE status = 'active';
            CREATE UNIQUE INDEX IF NOT EXISTS ux_credential_runbook_incident_generation
                ON credential_runbook_operations(host_id, credential_id, expected_generation, incident_id);
            """, ct);
    }

    public async Task<CredentialRunbookOperation> BeginCredentialRunbookAsync(
        BeginCredentialRunbookRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ValidateRunbookRequest(request, actorId);
        var selection = CredentialRunbookCatalog.Classify(request.Facts)
            ?? throw new TaskServerConflictException("runbook-unclassified", "Incident facts do not select a runbook.");
        var factsJson = JsonSerializer.Serialize(request.Facts);
        CredentialRunbookOperation? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadCredentialRunbookAsync(connection, transaction, request.OperationId, ct);
            if (existing is not null)
            {
                var priorFacts = await ScalarAsync(connection, """
                    SELECT facts_json FROM credential_runbook_operations WHERE operation_id = $operation;
                    """, ct, transaction, ("$operation", request.OperationId)) as string;
                if (priorFacts != factsJson || existing.RunbookId != selection.RunbookId || existing.Policy != selection.Policy ||
                    existing.ActorId != actorId || existing.HostId != request.HostId ||
                    existing.CredentialId != request.CredentialId ||
                    existing.ExpectedGeneration != request.ExpectedGeneration ||
                    existing.IncidentId != request.IncidentId ||
                    existing.EvidenceCorrelationId != request.EvidenceCorrelationId)
                    throw new TaskServerConflictException("runbook-idempotency-conflict",
                        "Operation id is already bound to different incident facts.");
                result = existing;
                return;
            }
            await RequireRunbookGenerationAsync(connection, transaction, request.HostId,
                request.CredentialId, request.ExpectedGeneration, ct);
            await using (var credential = Command(connection, """
                SELECT payload_json, observed_at FROM credential_registry
                 WHERE host_id = $host AND credential_id = $credential LIMIT 1;
                """, transaction, ("$host", request.HostId), ("$credential", request.CredentialId)))
            {
                await using var reader = await credential.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new TaskServerConflictException("runbook-evidence-mismatch",
                        "Current credential classification and evidence correlation are required.");
                var record = JsonSerializer.Deserialize<CredentialRegistryRecordDto>(
                    reader.GetString(0), CredentialRegistryJson);
                if (record?.Kind != request.Facts.CredentialKind ||
                    record.LastOutcome != request.Facts.ProbeOutcome ||
                    !record.EvidenceRefs.Contains(request.EvidenceCorrelationId) ||
                    Parse(reader.GetString(1)) < UtcNow.AddMinutes(-10))
                    throw new TaskServerConflictException("runbook-evidence-mismatch",
                        "Current credential classification and evidence correlation are required.");
            }
            var active = await ScalarAsync(connection, """
                SELECT operation_id FROM credential_runbook_operations
                 WHERE host_id = $host AND credential_id = $credential AND status = 'active' LIMIT 1;
                """, ct, transaction, ("$host", request.HostId), ("$credential", request.CredentialId));
            if (active is not null)
                throw new TaskServerConflictException("runbook-active", "This credential already has an active runbook operation.");
            var duplicate = await ScalarAsync(connection, """
                SELECT operation_id FROM credential_runbook_operations
                 WHERE host_id = $host AND credential_id = $credential
                   AND expected_generation = $generation AND incident_id = $incident LIMIT 1;
                """, ct, transaction, ("$host", request.HostId),
                ("$credential", request.CredentialId), ("$generation", request.ExpectedGeneration),
                ("$incident", request.IncidentId));
            if (duplicate is not null)
                throw new TaskServerConflictException("runbook-incident-exists",
                    "This incident and credential generation already have a runbook operation.");
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO credential_runbook_operations(operation_id, runbook_id, version, policy,
                    actor_id, host_id, credential_id, expected_generation, incident_id,
                    evidence_correlation_id, facts_json, status, created_at)
                VALUES ($operation, $runbook, $version, $policy, $actor, $host, $credential,
                    $generation, $incident, $evidence, $facts, 'active', $now);
                """, ct, transaction,
                ("$operation", request.OperationId), ("$runbook", selection.RunbookId),
                ("$version", selection.Version), ("$policy", selection.Policy),
                ("$actor", actorId), ("$host", request.HostId),
                ("$credential", request.CredentialId), ("$generation", request.ExpectedGeneration),
                ("$incident", request.IncidentId), ("$evidence", request.EvidenceCorrelationId),
                ("$facts", factsJson), ("$now", now));
            var ordinal = 0;
            foreach (var step in CredentialRunbookCatalog.StepsFor(selection.RunbookId, selection.Policy))
                await ExecuteAsync(connection, """
                    INSERT INTO credential_runbook_steps(operation_id, step_id, ordinal, state)
                    VALUES ($operation, $step, $ordinal, 'pending');
                    """, ct, transaction, ("$operation", request.OperationId),
                    ("$step", step), ("$ordinal", ordinal++));
            result = await ReadCredentialRunbookAsync(connection, transaction, request.OperationId, ct);
        }, ct);
        return result!;
    }

    public async Task<CredentialRunbookOperation?> GetCredentialRunbookAsync(string operationId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadCredentialRunbookAsync(connection, null, operationId, ct);
    }

    public async Task<bool> RunnerOwnsCredentialRunbookHostAsync(
        string runnerId, string operationId, string? instanceId, CancellationToken ct)
    {
        if (!SafeRunbookId(instanceId)) return false;
        await using var connection = await OpenReadyAsync(ct);
        var host = await ScalarAsync(connection, """
            SELECT r.host_id FROM runners r JOIN credential_runbook_operations o
              ON o.host_id = r.host_id
             JOIN credential_registry c ON c.host_id = o.host_id AND c.credential_id = o.credential_id
             WHERE r.id = $runner AND o.operation_id = $operation AND r.status = 'active'
               AND r.instance_id = $instance AND c.source_instance_id = $instance;
            """, ct, null, ("$runner", runnerId), ("$operation", operationId),
            ("$instance", instanceId));
        return host is not null;
    }

    public async Task<IReadOnlyList<CredentialRunbookStepReceipt>> ListCredentialRunbookReceiptsAsync(
        string operationId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT receipt_json FROM credential_runbook_steps
             WHERE operation_id = $operation AND receipt_json IS NOT NULL ORDER BY ordinal;
            """, ("$operation", operationId));
        var result = new List<CredentialRunbookStepReceipt>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(JsonSerializer.Deserialize<CredentialRunbookStepReceipt>(reader.GetString(0))!);
        return result;
    }

    public async Task<CredentialRunbookStepClaim> ClaimCredentialRunbookStepAsync(
        string operationId, string hostId, string expectedGeneration, CancellationToken ct)
    {
        RequireWritable();
        CredentialRunbookStepClaim? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var operation = await ReadCredentialRunbookAsync(connection, transaction, operationId, ct)
                ?? throw new KeyNotFoundException("Runbook operation was not found.");
            RequireRunbookHost(operation, hostId, expectedGeneration);
            if (operation.Status != "active")
            {
                result = new("complete", null, operationId, hostId, expectedGeneration);
                return;
            }
            await RequireRunbookCurrentGenerationAsync(connection, transaction, operation, null, null, ct);
            var step = await ReadNextRunbookStepAsync(connection, transaction, operationId, ct);
            if (step is null)
            {
                result = new("complete", null, operationId, hostId, expectedGeneration);
                return;
            }
            if (step.Value.State == "claimed")
            {
                result = new("reconcile-required", step.Value.Id, operationId, hostId, expectedGeneration);
                return;
            }
            if (CredentialRunbookCatalog.RequiresHuman(step.Value.Id))
            {
                result = new("human-required", step.Value.Id, operationId, hostId, expectedGeneration);
                return;
            }
            await ExecuteAsync(connection, """
                UPDATE credential_runbook_steps SET state = 'claimed', claimed_at = $now
                 WHERE operation_id = $operation AND step_id = $step AND state = 'pending';
                """, ct, transaction, ("$now", Iso(UtcNow)),
                ("$operation", operationId), ("$step", step.Value.Id));
            result = new("claimed", step.Value.Id, operationId, hostId, expectedGeneration);
        }, ct);
        return result!;
    }

    public Task<CredentialRunbookStepReceipt> CompleteCredentialRunbookStepAsync(
        string operationId, string stepId, CompleteCredentialRunbookStepRequest request, CancellationToken ct)
        => CompleteRunbookStepAsync(operationId, stepId, request, human: false, null, ct);

    public Task<CredentialRunbookStepReceipt> CompleteCredentialRunbookStepAsync(
        string operationId, string stepId, CompleteCredentialRunbookStepRequest request,
        string executingActorId, CancellationToken ct)
        => CompleteRunbookStepAsync(operationId, stepId, request, human: false, executingActorId, ct);

    public Task<CredentialRunbookStepReceipt> CompleteCredentialRunbookHumanStepAsync(
        string operationId, string stepId, CompleteCredentialRunbookStepRequest request, CancellationToken ct)
        => CompleteRunbookStepAsync(operationId, stepId, request, human: true, null, ct);

    public Task<CredentialRunbookStepReceipt> CompleteCredentialRunbookHumanStepAsync(
        string operationId, string stepId, CompleteCredentialRunbookStepRequest request,
        string executingActorId, CancellationToken ct)
        => CompleteRunbookStepAsync(operationId, stepId, request, human: true, executingActorId, ct);

    private async Task<CredentialRunbookStepReceipt> CompleteRunbookStepAsync(
        string operationId, string stepId, CompleteCredentialRunbookStepRequest request,
        bool human, string? executingActorId, CancellationToken ct)
    {
        RequireWritable();
        if (request.EvidenceRefs is null ||
            (request.ObservedGeneration is not null && !SafeRunbookId(request.ObservedGeneration)) ||
            request.VerificationResult is not (null or "unauthorized" or "healthy" or
                "fetch-verified" or "fetch-and-push-verified" or "advertised") ||
            request.Outcome != (human ? "human-approved" : "verified") ||
            request.EvidenceRefs.Count == 0 || request.EvidenceRefs.Count > 8 ||
            request.EvidenceRefs.Any(item => !SafeRunbookId(item)) ||
            (executingActorId is not null && !SafeRunbookId(executingActorId)))
            throw new ArgumentException("A bounded evidence reference and the expected typed outcome are required.");
        CredentialRunbookStepReceipt? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var operation = await ReadCredentialRunbookAsync(connection, transaction, operationId, ct)
                ?? throw new KeyNotFoundException("Runbook operation was not found.");
            RequireRunbookHost(operation, request.HostId, request.ExpectedGeneration);
            var next = await ReadNextRunbookStepAsync(connection, transaction, operationId, ct);
            if (next is null || next.Value.Id != stepId)
            {
                await using var prior = Command(connection, """
                    SELECT receipt_json FROM credential_runbook_steps
                     WHERE operation_id = $operation AND step_id = $step;
                    """, transaction, ("$operation", operationId), ("$step", stepId));
                var json = await prior.ExecuteScalarAsync(ct) as string;
                if (json is not null)
                {
                    var receipt = JsonSerializer.Deserialize<CredentialRunbookStepReceipt>(json)!;
                    if (receipt.Outcome == request.Outcome && receipt.ObservedGeneration == request.ObservedGeneration &&
                        receipt.ExecutingActorId == executingActorId &&
                        receipt.VerificationResult == request.VerificationResult &&
                        receipt.EvidenceRefs.SequenceEqual(request.EvidenceRefs))
                    {
                        result = receipt;
                        return;
                    }
                }
                throw new TaskServerConflictException("runbook-step-order", "Step is not the current step or has a different receipt.");
            }
            if (CredentialRunbookCatalog.RequiresHuman(stepId) != human ||
                next.Value.State != (human ? "pending" : "claimed"))
                throw new TaskServerConflictException("runbook-step-claim", "Step requires its own authorized boundary.");
            if (stepId is not ("verify_real_request" or "verify_recovery_canary" or
                    "verify_repository_access" or "advertise_generation") &&
                request.VerificationResult is not null)
                throw new ArgumentException("This step does not accept a verification result.");
            await RequireRunbookCurrentGenerationAsync(connection, transaction, operation,
                stepId, request.ObservedGeneration, ct);
            if (stepId is "verify_real_request" or "verify_recovery_canary" or "verify_repository_access"
                && !SafeRunbookId(request.ObservedGeneration))
                throw new ArgumentException("Verification needs an observed credential generation.");
            if (stepId == "verify_real_request" &&
                request.VerificationResult != (operation.RunbookId == "RB-CODEX-INCIDENT" ? "unauthorized" : "healthy"))
                throw new TaskServerConflictException("runbook-real-check-result",
                    "The real request result does not satisfy this step.");
            if (stepId == "verify_recovery_canary" && request.VerificationResult != "healthy")
                throw new TaskServerConflictException("runbook-canary-required",
                    "A successful real recovery canary is required.");
            if (stepId == "verify_repository_access")
            {
                var factsJson = await ScalarAsync(connection, """
                    SELECT facts_json FROM credential_runbook_operations WHERE operation_id = $operation;
                    """, ct, transaction, ("$operation", operationId)) as string;
                var facts = JsonSerializer.Deserialize<CredentialIncidentFacts>(factsJson!);
                if (facts?.RepositoryRequiresPush is null ||
                    request.VerificationResult != (facts.RepositoryRequiresPush.Value
                        ? "fetch-and-push-verified" : "fetch-verified"))
                    throw new TaskServerConflictException("runbook-repository-proof-required",
                        "Exact-origin fetch and required push proof are needed before identity switch.");
            }
            if (stepId == "advertise_generation" && request.VerificationResult != "advertised")
                throw new TaskServerConflictException("runbook-advertisement-required",
                    "A fresh generation advertisement is required.");
            if (stepId is "verify_real_request" or "verify_recovery_canary")
            {
                var current = await CurrentRunbookGenerationAsync(connection, transaction,
                    operation.HostId, operation.CredentialId, ct);
                if (request.ObservedGeneration != current)
                    throw new TaskServerConflictException("runbook-verification-generation",
                        "Real verification must observe the current credential generation.");
            }
            if (stepId is "switch_repository_identity" or "retire_old_generation")
            {
                await using var proofCommand = Command(connection, """
                    SELECT receipt_json FROM credential_runbook_steps WHERE operation_id = $operation
                     AND step_id IN ('verify_real_request', 'verify_repository_access')
                     AND state = 'complete' AND receipt_json IS NOT NULL LIMIT 1;
                    """, transaction, ("$operation", operationId));
                var proofJson = await proofCommand.ExecuteScalarAsync(ct) as string;
                var proof = proofJson is null ? null : JsonSerializer.Deserialize<CredentialRunbookStepReceipt>(proofJson);
                if (proof is null)
                    throw new TaskServerConflictException("runbook-verification-required", "Candidate proof is required before retirement.");
                var current = await CurrentRunbookGenerationAsync(connection, transaction,
                    operation.HostId, operation.CredentialId, ct);
                if (proof.ObservedGeneration != current)
                    throw new TaskServerConflictException("runbook-verification-generation",
                        "Candidate proof must match the active generation before switch or retirement.");
            }
            result = new(operationId, stepId, operation.ActorId, operation.HostId,
                operation.CredentialId, operation.ExpectedGeneration, request.ObservedGeneration,
                request.Outcome, request.EvidenceRefs, UtcNow, operation.IncidentId,
                operation.EvidenceCorrelationId, executingActorId, request.VerificationResult);
            await ExecuteAsync(connection, """
                UPDATE credential_runbook_steps SET state = 'complete', receipt_json = $receipt
                 WHERE operation_id = $operation AND step_id = $step;
                """, ct, transaction, ("$receipt", JsonSerializer.Serialize(result)),
                ("$operation", operationId), ("$step", stepId));
            var remaining = await ScalarAsync(connection, """
                SELECT 1 FROM credential_runbook_steps WHERE operation_id = $operation
                 AND state <> 'complete' LIMIT 1;
                """, ct, transaction, ("$operation", operationId));
            if (remaining is null)
                await ExecuteAsync(connection, """
                    UPDATE credential_runbook_operations SET status = 'complete' WHERE operation_id = $operation;
                    """, ct, transaction, ("$operation", operationId));
        }, ct);
        return result!;
    }

    private static void RequireRunbookHost(CredentialRunbookOperation operation,
        string hostId, string generation)
    {
        if (operation.HostId != hostId || operation.ExpectedGeneration != generation)
            throw new TaskServerConflictException("runbook-binding-mismatch", "Host or expected generation differs from this operation.");
    }

    private static async Task RequireRunbookGenerationAsync(SqliteConnection connection,
        SqliteTransaction transaction, string hostId, string credentialId, string generation, CancellationToken ct)
    {
        var current = await CurrentRunbookGenerationAsync(connection, transaction, hostId, credentialId, ct);
        if (current != generation)
            throw new TaskServerConflictException("runbook-stale-generation", "Credential registry generation changed or is unavailable.");
    }

    private static async Task<string?> CurrentRunbookGenerationAsync(SqliteConnection connection,
        SqliteTransaction transaction, string hostId, string credentialId, CancellationToken ct)
        => await ScalarAsync(connection, """
            SELECT generation FROM credential_registry WHERE host_id = $host AND credential_id = $credential LIMIT 1;
            """, ct, transaction, ("$host", hostId), ("$credential", credentialId)) as string;

    private static async Task RequireRunbookCurrentGenerationAsync(SqliteConnection connection,
        SqliteTransaction transaction, CredentialRunbookOperation operation,
        string? completingStep, string? observedGeneration, CancellationToken ct)
    {
        var current = await CurrentRunbookGenerationAsync(connection, transaction,
            operation.HostId, operation.CredentialId, ct);
        if (current == operation.ExpectedGeneration) return;
        if (current is null)
            throw new TaskServerConflictException("runbook-stale-generation", "Credential registry generation is unavailable.");
        // An installation may advance the registry before its receipt is saved.
        // Only that claimed installation step may reconcile the new generation.
        if (completingStep is "install_generation" or "switch_repository_identity"
            && observedGeneration == current)
            return;
        await using var command = Command(connection, """
            SELECT receipt_json FROM credential_runbook_steps
             WHERE operation_id = $operation AND step_id IN ('install_generation', 'switch_repository_identity')
               AND state = 'complete' LIMIT 1;
            """, transaction, ("$operation", operation.OperationId));
        var json = await command.ExecuteScalarAsync(ct) as string;
        var receipt = json is null ? null : JsonSerializer.Deserialize<CredentialRunbookStepReceipt>(json);
        if (receipt?.ObservedGeneration != current)
            throw new TaskServerConflictException("runbook-stale-generation",
                "Credential generation changed outside this operation.");
    }

    private static async Task<CredentialRunbookOperation?> ReadCredentialRunbookAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT operation_id, runbook_id, version, policy, actor_id, host_id, credential_id,
                   expected_generation, incident_id, evidence_correlation_id, status, created_at
              FROM credential_runbook_operations WHERE operation_id = $id;
            """, transaction, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new CredentialRunbookOperation(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), Parse(reader.GetString(11))) : null;
    }

    private static async Task<(string Id, string State)?> ReadNextRunbookStepAsync(
        SqliteConnection connection, SqliteTransaction transaction, string operationId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT step_id, state FROM credential_runbook_steps WHERE operation_id = $operation
             AND state <> 'complete' ORDER BY ordinal LIMIT 1;
            """, transaction, ("$operation", operationId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static bool SafeRunbookId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':') &&
        !value.StartsWith("sk_", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("gho_", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase) &&
        !UnsafeCredentialMetadata.IsMatch(value);

    private static void ValidateRunbookRequest(BeginCredentialRunbookRequest request, string actorId)
    {
        if (request.Facts is null || new[] { request.OperationId, request.HostId, request.CredentialId,
                request.ExpectedGeneration, request.IncidentId, request.EvidenceCorrelationId, actorId }
            .Any(value => !SafeRunbookId(value)))
            throw new ArgumentException("Runbook identity and correlation fields must be bounded opaque identifiers.");
        if (!CredentialRegistryProtocol.Kinds.Contains(request.Facts.CredentialKind) ||
            request.Facts.ProbeOutcome is not ("credential_invalid" or "provider_incident" or "indeterminate" or "network_failure" or "quota_exhausted") ||
            request.Facts.NormalizedRequestCode is not (null or "unauthorized") ||
            request.Facts.IncidentCorroboration is not (null or "official-applicable" or "independent-known-good-match") ||
            request.Facts.RepositoryPurpose is not (null or "workspace" or "product"))
            throw new ArgumentException("Incident facts must use the typed evidence vocabulary.");
    }
}
