using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions RenewalJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex RenewalIdentifier = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex RenewalEvidence = new("^evidence:[A-Za-z0-9._:-]{1,120}$", RegexOptions.Compiled);
    private static readonly HashSet<string> RenewalTerminal =
        ["complete", "cancelled", "failed", "recovery-required"];

    public async Task<ProviderRenewalReceiptDto> BeginProviderRenewalAsync(
        BeginProviderRenewalRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        foreach (var value in new[] { request.InstallationId, request.HostId, request.CredentialId,
                     request.ExpectedGeneration, request.IdempotencyKey, actorId })
            RequireRenewalId(value);
        if (!ProviderRenewalProtocol.Methods.Contains(request.Method) ||
            request.Deadline.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Renewal method or deadline is invalid.");

        ProviderRenewalReceiptDto? result = null;
        var needsRecovery = false;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadRenewalAsync(connection, transaction, """
                SELECT payload_json FROM provider_renewal_operations
                 WHERE installation_id = $installation AND host_id = $host
                   AND credential_id = $credential AND idempotency_key = $key;
                """, ct, ("$installation", request.InstallationId), ("$host", request.HostId),
                ("$credential", request.CredentialId), ("$key", request.IdempotencyKey));
            if (existing is not null)
            {
                if (existing.ExpectedGeneration != request.ExpectedGeneration || existing.Method != request.Method ||
                    existing.ActorId != actorId)
                    throw new TaskServerConflictException("renewal-idempotency-conflict", "Operation key belongs to another renewal intent.");
                result = existing;
                return;
            }
            if (request.Deadline <= UtcNow || request.Deadline > UtcNow.AddMinutes(20))
                throw new ArgumentException("Renewal deadline must be within twenty minutes.");
            var currentGeneration = await ScalarAsync(connection, """
                SELECT generation FROM credential_registry WHERE installation_id = $installation
                 AND host_id = $host AND credential_id = $credential;
                """, ct, transaction, ("$installation", request.InstallationId),
                ("$host", request.HostId), ("$credential", request.CredentialId)) as string;
            if (currentGeneration != request.ExpectedGeneration)
                throw new TaskServerConflictException("stale-credential-generation", "Renewal expected generation is no longer current.");
            var unresolved = await ReadRenewalAsync(connection, transaction, """
                SELECT payload_json FROM provider_renewal_operations
                 WHERE installation_id = $installation AND host_id = $host
                   AND credential_id = $credential AND step = 'recovery-required' LIMIT 1;
                """, ct, ("$installation", request.InstallationId), ("$host", request.HostId),
                ("$credential", request.CredentialId));
            if (unresolved is not null)
                throw new TaskServerConflictException("renewal-recovery-required",
                    "Resolve the previous host generation before another login can begin.");
            var active = await ReadRenewalAsync(connection, transaction, """
                SELECT payload_json FROM provider_renewal_operations
                 WHERE installation_id = $installation AND host_id = $host
                   AND credential_id = $credential AND step NOT IN ('complete','cancelled','failed','recovery-required')
                 LIMIT 1;
                """, ct, ("$installation", request.InstallationId), ("$host", request.HostId),
                ("$credential", request.CredentialId));
            if (active is not null)
            {
                if (active.Deadline > UtcNow)
                    throw new TaskServerConflictException("renewal-binding-busy", "Another renewal owns this credential binding.");
                if (Array.IndexOf(ProviderRenewalProtocol.Steps, active.Step) >=
                    Array.IndexOf(ProviderRenewalProtocol.Steps, "preflight"))
                {
                    await WriteRenewalAsync(connection, transaction, active with {
                        Step = "recovery-required", UpdatedAt = UtcNow,
                    }, ct);
                    needsRecovery = true;
                    return;
                }
                var expired = active with {
                    Step = "cancelled",
                    UpdatedAt = UtcNow,
                };
                await WriteRenewalAsync(connection, transaction, expired, ct);
            }
            result = new ProviderRenewalReceiptDto(
                "renewal_" + Guid.NewGuid().ToString("N"), request.InstallationId,
                request.HostId, request.CredentialId, request.ExpectedGeneration, request.Method,
                request.IdempotencyKey, actorId, request.Deadline, "requested", null, null,
                [], false, [], UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO provider_renewal_operations(operation_id, installation_id, host_id,
                    credential_id, idempotency_key, step, payload_json)
                VALUES($id,$installation,$host,$credential,$key,$step,$payload);
                """, ct, transaction, ("$id", result.OperationId),
                ("$installation", result.InstallationId), ("$host", result.HostId),
                ("$credential", result.CredentialId), ("$key", result.IdempotencyKey),
                ("$step", result.Step), ("$payload", JsonSerializer.Serialize(result, RenewalJson)));
            await AuditAsync(connection, transaction, actorId, "provider-renewal.requested",
                "credential", request.CredentialId,
                JsonSerializer.Serialize(new { result.OperationId, request.HostId, request.ExpectedGeneration }), ct);
        }, ct);
        if (needsRecovery)
            throw new TaskServerConflictException("renewal-recovery-required",
                "An installed generation needs recovery before another login can begin.");
        return result!;
    }

    public async Task<ProviderRenewalReceiptDto> AdvanceProviderRenewalAsync(
        string operationId, AdvanceProviderRenewalRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        RequireRenewalId(operationId);
        RequireRenewalId(actorId);
        if (!ProviderRenewalProtocol.Steps.Contains(request.Step) &&
            request.Step is not ("cancelled" or "failed" or "recovery-required"))
            throw new ArgumentException("Unknown renewal step.");
        if (request.EvidenceRefs is null || request.EvidenceRefs.Count > 16 ||
            request.EvidenceRefs.Any(reference => !RenewalEvidence.IsMatch(reference)) ||
            request.VerifiedUnits is null || request.VerifiedUnits.Count > 4 ||
            request.VerifiedUnits.Any(unit => unit is not
                ("agent-host.service" or "agent-runner.service" or "agent-runner-review.service")) ||
            request.ObservedGeneration is not null && !IsRenewalId(request.ObservedGeneration) ||
            request.EffectiveSource is not null && request.EffectiveSource is not
                ("environment-file" or "native-cli-store"))
            throw new ArgumentException("Renewal receipt contains unsupported metadata.");
        ProviderRenewalReceiptDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var previous = await ReadRenewalAsync(connection, transaction,
                "SELECT payload_json FROM provider_renewal_operations WHERE operation_id = $id;",
                ct, ("$id", operationId))
                ?? throw new TaskServerConflictException("renewal-not-found", "Renewal operation was not found.");
            if (previous.ActorId != actorId)
                throw new TaskServerConflictException("renewal-actor-mismatch", "Renewal actor changed.");
            if (previous.Step == request.Step)
            {
                if (request.ObservedGeneration is not null && request.ObservedGeneration != previous.ObservedGeneration ||
                    request.EffectiveSource is not null && request.EffectiveSource != previous.EffectiveSource ||
                    request.RealRequestSucceeded && !previous.RealRequestSucceeded ||
                    request.VerifiedUnits.Except(previous.VerifiedUnits).Any() ||
                    request.EvidenceRefs.Except(previous.EvidenceRefs).Any())
                    throw new TaskServerConflictException("renewal-step-conflict", "A repeated step has different evidence.");
                result = previous;
                return;
            }
            if (RenewalTerminal.Contains(previous.Step) && previous.Step != "recovery-required")
                throw new TaskServerConflictException("renewal-terminal", "Renewal operation already ended.");
            var oldIndex = Array.IndexOf(ProviderRenewalProtocol.Steps, previous.Step);
            var newIndex = Array.IndexOf(ProviderRenewalProtocol.Steps, request.Step);
            var terminalFailure = request.Step is "cancelled" or "failed" or "recovery-required";
            if (previous.Step == "recovery-required" && request.Step is not ("cancelled" or "verified"))
                throw new TaskServerConflictException("renewal-recovery-required",
                    "Recovery needs proof of the old or a new generation.");
            if (previous.Step != "recovery-required" && !terminalFailure && newIndex != oldIndex + 1)
                throw new TaskServerConflictException("renewal-step-order", "Renewal receipt skipped a required step.");
            if (previous.Step == "recovery-required" && request.Step == "cancelled" &&
                (request.ObservedGeneration != previous.ExpectedGeneration ||
                 !request.RealRequestSucceeded || !BothRenewalUnits(request.VerifiedUnits)))
                throw new ArgumentException("Recovery rollback needs both units and a real request on the prior generation.");
            if (request.Step == "cancelled" && oldIndex >= Array.IndexOf(ProviderRenewalProtocol.Steps, "preflight"))
                throw new TaskServerConflictException("renewal-recovery-required", "Installed generation needs explicit recovery.");
            if (request.Step == "failed" && oldIndex >= Array.IndexOf(ProviderRenewalProtocol.Steps, "preflight"))
                throw new TaskServerConflictException("renewal-recovery-required", "Installed generation needs explicit recovery.");
            if (UtcNow >= previous.Deadline && !terminalFailure)
                throw new TaskServerConflictException("renewal-expired", "Renewal deadline passed; record cancellation or recovery.");
            if (request.Step == "verified" &&
                (request.ObservedGeneration is null || request.ObservedGeneration == previous.ExpectedGeneration ||
                 request.EffectiveSource is null || !request.RealRequestSucceeded ||
                 !BothRenewalUnits(request.VerifiedUnits)))
                throw new ArgumentException("Verification needs a new generation, selected source, both units and a real request.");
            if (request.Step == "verified" && request.EffectiveSource !=
                (previous.Method is "R2" or "R3" ? "native-cli-store" : "environment-file"))
                throw new ArgumentException("Verification source does not match the selected renewal mode.");
            if (request.Step is "retired" or "complete" && !previous.RealRequestSucceeded)
                throw new TaskServerConflictException("renewal-unverified", "A real request must verify renewal before retirement.");
            result = previous with {
                Step = request.Step,
                ObservedGeneration = request.ObservedGeneration ?? previous.ObservedGeneration,
                EffectiveSource = request.EffectiveSource ?? previous.EffectiveSource,
                VerifiedUnits = request.VerifiedUnits.Count > 0 ? request.VerifiedUnits : previous.VerifiedUnits,
                RealRequestSucceeded = request.RealRequestSucceeded || previous.RealRequestSucceeded,
                EvidenceRefs = request.EvidenceRefs.Count > 0 ? request.EvidenceRefs : previous.EvidenceRefs,
                UpdatedAt = UtcNow,
            };
            await WriteRenewalAsync(connection, transaction, result, ct);
            await AuditAsync(connection, transaction, actorId, "provider-renewal." + request.Step,
                "credential", previous.CredentialId,
                JsonSerializer.Serialize(new { operationId, previous.HostId, result.ObservedGeneration }), ct);
        }, ct);
        return result!;
    }

    public async Task<ProviderRenewalReceiptDto?> GetProviderRenewalAsync(string operationId, CancellationToken ct)
    {
        RequireRenewalId(operationId);
        ProviderRenewalReceiptDto? receipt = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            receipt = await ReadRenewalAsync(connection, transaction,
                "SELECT payload_json FROM provider_renewal_operations WHERE operation_id = $id;",
                ct, ("$id", operationId));
            if (receipt is null || RenewalTerminal.Contains(receipt.Step) || receipt.Deadline > UtcNow)
                return;
            receipt = receipt with {
                Step = Array.IndexOf(ProviderRenewalProtocol.Steps, receipt.Step) >=
                    Array.IndexOf(ProviderRenewalProtocol.Steps, "preflight")
                    ? "recovery-required" : "cancelled",
                UpdatedAt = UtcNow,
            };
            await WriteRenewalAsync(connection, transaction, receipt, ct);
        }, ct);
        return receipt;
    }

    private static async Task<ProviderRenewalReceiptDto?> ReadRenewalAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        command.Transaction = transaction;
        var payload = await command.ExecuteScalarAsync(ct) as string;
        return payload is null ? null : JsonSerializer.Deserialize<ProviderRenewalReceiptDto>(payload, RenewalJson);
    }

    private static Task WriteRenewalAsync(
        SqliteConnection connection, SqliteTransaction transaction, ProviderRenewalReceiptDto receipt,
        CancellationToken ct)
        => ExecuteAsync(connection, """
            UPDATE provider_renewal_operations SET step = $step, payload_json = $payload
             WHERE operation_id = $id;
            """, ct, transaction, ("$step", receipt.Step),
            ("$payload", JsonSerializer.Serialize(receipt, RenewalJson)), ("$id", receipt.OperationId));

    private static void RequireRenewalId(string? value)
    {
        if (value is null || !IsRenewalId(value))
            throw new ArgumentException("Renewal identifier is invalid.");
    }

    private static bool IsRenewalId(string value)
        => RenewalIdentifier.IsMatch(value) &&
           !value.Contains("sk-ant", StringComparison.OrdinalIgnoreCase) &&
           !value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) &&
           !value.StartsWith("eyJ", StringComparison.Ordinal);

    private static bool BothRenewalUnits(IReadOnlyList<string> units)
        => units.Contains("agent-runner-review.service") &&
           (units.Contains("agent-host.service") || units.Contains("agent-runner.service"));
}
