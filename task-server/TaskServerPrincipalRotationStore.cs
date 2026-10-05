using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private sealed record RotationRow(
        PrincipalRotationReceipt Receipt,
        string[] PreviousIds,
        int OverlapSeconds,
        DateTime? DeliveredAt,
        DateTime? RecoveryClosedAt);

    private async Task<IssuedPrincipalCredential> BeginPrincipalRotationAsync(
        string principalId, RotatePrincipalRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var operationId = RequireIdentifier(request.OperationId, "Operation id");
        var overlap = request.OverlapSeconds ?? _options.PrincipalRotationOverlapSeconds;
        if (overlap < 1 || overlap > _options.MaximumPrincipalRotationOverlapSeconds)
            throw new ArgumentException(
                $"OverlapSeconds must be between 1 and {_options.MaximumPrincipalRotationOverlapSeconds}. Use revoke for immediate interruption.");
        var consumers = request.Consumers?.OrderBy(item => item.ConsumerId, StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentException("At least one rotation consumer is required.");
        if (consumers.Length == 0 || consumers.Length > 32
            || consumers.Select(item => item.ConsumerId).Distinct(StringComparer.Ordinal).Count() != consumers.Length)
            throw new ArgumentException("Rotation consumers must be unique and numbered between 1 and 32.");
        foreach (var consumer in consumers)
            _ = RequireIdentifier(consumer.ConsumerId, "Consumer id");
        var deliveryConsumerId = request.DeliveryConsumerId
            ?? (consumers.Length == 1 ? consumers[0].ConsumerId : null);
        if (deliveryConsumerId is null || !consumers.Any(item => item.ConsumerId == deliveryConsumerId))
            throw new ArgumentException("Select a declared delivery consumer for a shared principal.");
        if (consumers.Length > 1 && actorId == principalId)
            throw new TaskServerConflictException("rotation-independent-manager-required",
                "A shared principal requires a separate management principal for rotation delivery.");

        PrincipalDto? principal = null;
        PrincipalRotationReceipt? receipt = null;
        string? credential = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            principal = await ReadPrincipalAsync(connection, transaction, principalId, ct)
                ?? throw new KeyNotFoundException("Principal was not found.");
            if (principal.RevokedAt is not null)
                throw new InvalidOperationException("A revoked principal cannot be rotated.");
            if (consumers.Any(item => !principal.Scopes.Contains(item.RequiredScope)))
                throw new ArgumentException("A consumer requires a scope that the principal does not hold.");
            if (principal.RunnerId is not null && consumers.Any(item => item.ConsumerId != principal.RunnerId))
                throw new ArgumentException("A Runner rotation consumer must match the bound Runner id.");

            var prior = await ReadRotationAsync(connection, transaction, operationId, ct);
            if (prior is not null)
            {
                if (prior.Receipt.PrincipalId != principalId
                    || prior.OverlapSeconds != overlap
                    || JsonSerializer.Serialize(prior.Receipt.Consumers) != JsonSerializer.Serialize(consumers))
                    throw new TaskServerConflictException("rotation-idempotency-conflict",
                        "The rotation operation id is already bound to different parameters.");
                if (prior.Receipt.ActorId != actorId)
                    throw new TaskServerConflictException("rotation-issuer-required",
                        "Only the issuing management principal can replay protected delivery.");
                receipt = prior.Receipt;
                if (!(prior.Receipt.DeliveredConsumers ?? []).Contains(deliveryConsumerId)
                    && UtcNow < prior.Receipt.PreviousCredentialValidUntil
                    && prior.Receipt.State is "issued" or "delivered" or "awaiting-consumers")
                {
                    var replay = DeriveRotationCredential(principalId, operationId, createKey: false);
                    if (!TryCredentialId(replay, out var replayId)
                        || replayId != prior.Receipt.CredentialGeneration)
                        throw new TaskServerConflictException("rotation-delivery-key-mismatch",
                            "The host rotation delivery key does not match this operation.");
                    credential = replay;
                }
                return;
            }

            var activeId = await ScalarAsync(connection, """
                SELECT operation_id FROM principal_rotations
                 WHERE principal_id = $id AND retired_at IS NULL
                   AND recovery_closed_at IS NULL AND revoked_at IS NULL
                 ORDER BY issued_at DESC LIMIT 1;
                """, ct, transaction, ("$id", principalId)) as string;
            if (activeId is not null)
            {
                var active = (await ReadRotationAsync(connection, transaction, activeId, ct))!;
                if (UtcNow < active.Receipt.PreviousCredentialValidUntil)
                    throw new TaskServerConflictException("rotation-in-progress",
                        "The principal already has a rotation in progress. Resume that operation.");
                if (!actorId.StartsWith("recovery:", StringComparison.Ordinal))
                    throw new TaskServerConflictException("recovery-identity-required",
                        "The previous rotation deadline passed. Use a separately enrolled recovery management principal.");
                await ExecuteAsync(connection, """
                    UPDATE principal_rotations SET recovery_closed_at = $now WHERE operation_id = $operation;
                    """, ct, transaction, ("$now", Iso(UtcNow)), ("$operation", activeId));
            }

            var now = UtcNow;
            var deadline = now.AddSeconds(overlap);
            var oldIds = new List<string>();
            await using (var command = Command(connection, """
                SELECT credential_id FROM principal_credentials
                 WHERE principal_id = $id AND revoked_at IS NULL
                   AND (expires_at IS NULL OR expires_at > $now)
                 ORDER BY created_at DESC, credential_id DESC;
                """, transaction, ("$id", principalId), ("$now", Iso(now))))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) oldIds.Add(reader.GetString(0));

            credential = DeriveRotationCredential(principalId, operationId, createKey: true);
            if (!TryCredentialId(credential, out var credentialId))
                throw new InvalidOperationException("Generated credential is malformed.");
            await ExecuteAsync(connection, """
                UPDATE principal_credentials SET expires_at = $deadline
                 WHERE principal_id = $id AND revoked_at IS NULL
                   AND (expires_at IS NULL OR expires_at > $deadline);
                """, ct, transaction, ("$deadline", Iso(deadline)), ("$id", principalId));
            await InsertCredentialAsync(connection, transaction, principalId, credential, now, ct);
            await ExecuteAsync(connection, """
                INSERT INTO principal_rotations(operation_id, principal_id, credential_id,
                    previous_ids_json, previous_generation, consumers_json, acknowledged_json,
                    acknowledged_at_json, actor_id, overlap_seconds,
                    issued_at, deadline_at, delivered_at, retired_at, recovery_closed_at)
                VALUES ($operation, $principal, $credential, $previous, $previous_generation,
                    $consumers, '[]', '{}', $actor,
                    $overlap, $now, $deadline, NULL, NULL, NULL);
                """, ct, transaction,
                ("$operation", operationId), ("$principal", principalId), ("$credential", credentialId),
                ("$previous", JsonSerializer.Serialize(oldIds)),
                ("$previous_generation", oldIds.FirstOrDefault()),
                ("$consumers", JsonSerializer.Serialize(consumers)),
                ("$actor", actorId),
                ("$overlap", overlap), ("$now", Iso(now)), ("$deadline", Iso(deadline)));
            receipt = new PrincipalRotationReceipt(operationId, principalId, credentialId,
                "issued", now, deadline, consumers, [],
                ActorId: actorId, PreviousCredentialGeneration: oldIds.FirstOrDefault(),
                AcknowledgedAt: new Dictionary<string, DateTime>(), DeliveredConsumers: []);
            await AuditAsync(connection, transaction, actorId, "principal.rotation-issued", "principal",
                principalId, JsonSerializer.Serialize(new { operationId, credentialId, overlapSeconds = overlap }), ct);
        }, ct);
        return new IssuedPrincipalCredential(principal!, credential, receipt!.IssuedAt,
            receipt.PreviousCredentialValidUntil, receipt,
            credential is not null
                ? DeriveConsumerProof(principalId, operationId, deliveryConsumerId) : null);
    }

    public async Task<PrincipalRotationReceipt?> GetPrincipalRotationAsync(
        string principalId, string operationId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var row = await ReadRotationAsync(connection, null, operationId, ct);
        return row?.Receipt.PrincipalId == principalId ? row.Receipt : null;
    }

    public async Task<PrincipalRotationReceipt> InspectPrincipalRotationConsumerAsync(
        string operationId, TaskServerPrincipal actor, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var row = await ReadRotationAsync(connection, null, operationId, ct)
            ?? throw new KeyNotFoundException("Rotation was not found.");
        RequireRotationBearer(row, actor);
        return row.Receipt;
    }

    public async Task<PrincipalRotationReceipt> MarkPrincipalRotationDeliveredAsync(
        string operationId, TaskServerPrincipal actor, string? consumerId, string? consumerProof, CancellationToken ct)
    {
        PrincipalRotationReceipt? receipt = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadRotationAsync(connection, transaction, operationId, ct)
                ?? throw new KeyNotFoundException("Rotation was not found.");
            RequireRotationBearer(row, actor);
            var consumer = RequireRotationConsumer(row, consumerId, consumerProof);
            var delivered = (row.Receipt.DeliveredConsumers ?? []).ToHashSet(StringComparer.Ordinal);
            if (delivered.Contains(consumer.ConsumerId) || row.Receipt.RetiredAt is not null)
            {
                receipt = row.Receipt;
                return;
            }
            if (UtcNow >= row.Receipt.PreviousCredentialValidUntil)
                throw new TaskServerConflictException("rotation-recovery-required", "Rotation deadline passed.");
            delivered.Add(consumer.ConsumerId);
            await ExecuteAsync(connection, """
                UPDATE principal_rotations SET delivered_at = COALESCE(delivered_at, $now),
                    delivered_consumers_json = $delivered
                 WHERE operation_id = $operation;
                """, ct, transaction, ("$now", Iso(UtcNow)),
                ("$delivered", JsonSerializer.Serialize(delivered.Order(StringComparer.Ordinal))),
                ("$operation", operationId));
            receipt = (await ReadRotationAsync(connection, transaction, operationId, ct))!.Receipt;
        }, ct);
        return receipt!;
    }

    public async Task RecordPrincipalScopeProofAsync(
        TaskServerPrincipal actor, string scope, string? declaredConsumerId,
        string? consumerProof, CancellationToken ct)
    {
        if (actor.CredentialId is null || actor.RotationOperationId is null) return;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadRotationAsync(connection, transaction, actor.RotationOperationId, ct);
            if (row is null || row.Receipt.RetiredAt is not null || row.RecoveryClosedAt is not null)
                return;
            PrincipalRotationConsumer consumer;
            try { consumer = RequireRotationConsumer(row, declaredConsumerId, consumerProof); }
            catch (TaskServerConflictException) { return; }
            if (consumer.RequiredScope != scope) return;
            await ExecuteAsync(connection, """
                INSERT INTO principal_rotation_proofs(credential_id, consumer_id, scope, observed_at)
                VALUES ($credential, $consumer, $scope, $now)
                ON CONFLICT(credential_id, consumer_id, scope)
                DO UPDATE SET observed_at = excluded.observed_at;
                """, ct, transaction, ("$credential", actor.CredentialId),
                ("$consumer", consumer.ConsumerId),
                ("$scope", scope), ("$now", Iso(UtcNow)));
        }, ct);
    }

    public async Task<PrincipalRotationReceipt> AcknowledgePrincipalRotationAsync(
        string operationId, PrincipalRotationAcknowledgement request,
        TaskServerPrincipal actor, string? consumerProof, CancellationToken ct)
    {
        PrincipalRotationReceipt? receipt = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var row = await ReadRotationAsync(connection, transaction, operationId, ct)
                ?? throw new KeyNotFoundException("Rotation was not found.");
            RequireRotationBearer(row, actor);
            var consumer = RequireRotationConsumer(row, request.ConsumerId, consumerProof);
            if (row.Receipt.RetiredAt is not null)
            {
                receipt = row.Receipt;
                return;
            }
            if (UtcNow >= row.Receipt.PreviousCredentialValidUntil)
                throw new TaskServerConflictException("rotation-recovery-required", "Rotation deadline passed.");
            if (!(row.Receipt.DeliveredConsumers ?? []).Contains(consumer.ConsumerId))
                throw new TaskServerConflictException("rotation-delivery-required", "Delivery has not been acknowledged.");
            if (!actor.Scopes.Contains(consumer.RequiredScope))
                throw new ArgumentException("Consumer lacks its required scope.");
            var proof = await ScalarAsync(connection, """
                SELECT 1 FROM principal_rotation_proofs
                 WHERE credential_id = $credential AND consumer_id = $consumer AND scope = $scope;
                """, ct, transaction, ("$credential", actor.CredentialId),
                ("$consumer", consumer.ConsumerId),
                ("$scope", consumer.RequiredScope));
            if (proof is null)
                throw new TaskServerConflictException("rotation-scope-proof-required",
                    "The new credential must complete a scoped operation before acknowledgement.");
            var acknowledged = row.Receipt.AcknowledgedConsumers.ToHashSet(StringComparer.Ordinal);
            acknowledged.Add(consumer.ConsumerId);
            var acknowledgedAt = new Dictionary<string, DateTime>(
                row.Receipt.AcknowledgedAt ?? new Dictionary<string, DateTime>(), StringComparer.Ordinal);
            acknowledgedAt.TryAdd(consumer.ConsumerId, UtcNow);
            var retired = acknowledged.Count == row.Receipt.Consumers.Count;
            await ExecuteAsync(connection, """
                UPDATE principal_rotations SET acknowledged_json = $acknowledged,
                    acknowledged_at_json = $acknowledged_at,
                    retired_at = CASE WHEN $retired = 1 THEN $now ELSE retired_at END
                 WHERE operation_id = $operation;
                """, ct, transaction,
                ("$acknowledged", JsonSerializer.Serialize(acknowledged.Order(StringComparer.Ordinal))),
                ("$acknowledged_at", JsonSerializer.Serialize(acknowledgedAt)),
                ("$retired", retired ? 1 : 0), ("$now", Iso(UtcNow)), ("$operation", operationId));
            if (retired)
                foreach (var oldId in row.PreviousIds)
                    await ExecuteAsync(connection, """
                        UPDATE principal_credentials SET revoked_at = COALESCE(revoked_at, $now)
                         WHERE credential_id = $id;
                        """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", oldId));
            receipt = (await ReadRotationAsync(connection, transaction, operationId, ct))!.Receipt;
        }, ct);
        return receipt!;
    }

    private static void RequireRotationBearer(RotationRow row, TaskServerPrincipal actor)
    {
        if (actor.PrincipalId != row.Receipt.PrincipalId
            || actor.CredentialId != row.Receipt.CredentialGeneration)
            throw new TaskServerConflictException("rotation-generation-mismatch",
                "The acknowledgement must use the newly issued credential.");
    }

    private PrincipalRotationConsumer RequireRotationConsumer(
        RotationRow row, string? consumerId, string? proof)
    {
        var selected = consumerId ?? (row.Receipt.Consumers.Count == 1
            ? row.Receipt.Consumers[0].ConsumerId : null);
        var consumer = row.Receipt.Consumers.SingleOrDefault(item => item.ConsumerId == selected);
        if (consumer is null)
            throw new TaskServerConflictException("rotation-consumer-required",
                "A declared rotation consumer is required.");
        if (row.Receipt.Consumers.Count > 1)
        {
            var expected = DeriveConsumerProof(row.Receipt.PrincipalId, row.Receipt.OperationId, consumer.ConsumerId);
            var supplied = proof ?? string.Empty;
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected)))
                throw new TaskServerConflictException("rotation-consumer-proof-required",
                    "The selected consumer requires its protected delivery proof.");
        }
        return consumer;
    }

    private string DeriveConsumerProof(string principalId, string operationId, string consumerId)
    {
        var key = ReadRotationDeliveryKey(create: false);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key,
                Encoding.UTF8.GetBytes("consumer:" + principalId + "\0" + operationId + "\0" + consumerId)))
                .ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // The delivery seed belongs to the Task Server host, outside the command
    // database. A committed receipt can reproduce its one bearer after a lost
    // HTTP response; replay stops for each consumer at its delivery or the deadline.
    private string DeriveRotationCredential(string principalId, string operationId, bool createKey)
    {
        var key = ReadRotationDeliveryKey(createKey);
        try
        {
            var context = principalId + "\0" + operationId;
            var id = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("id:" + context));
            var secret = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("secret:" + context));
            return $"ats_{Convert.ToHexString(id)[..32].ToLowerInvariant()}.{Convert.ToHexString(secret).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] ReadRotationDeliveryKey(bool create)
    {
        var path = Path.Combine(DataDirectory, "principal-rotation-delivery.key");
        if (!File.Exists(path) && create)
        {
            var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    stream.Write(generated);
                    stream.Flush(flushToDisk: true);
                }
                try { File.Move(staged, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
                if (File.Exists(staged)) File.Delete(staged);
            }
        }
        if (!File.Exists(path))
            throw new TaskServerConflictException("rotation-delivery-key-unavailable",
                "The host rotation delivery key is unavailable; restore its protected backup before retrying.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("The host rotation delivery key must be a private regular file.");
        var key = File.ReadAllBytes(path);
        if (key.Length != 32)
            throw new InvalidOperationException("The host rotation delivery key is invalid.");
        return key;
    }

    private async Task<RotationRow?> ReadRotationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string operationId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT operation_id, principal_id, credential_id, previous_ids_json,
                   consumers_json, acknowledged_json, overlap_seconds, issued_at,
                   deadline_at, delivered_at, retired_at, recovery_closed_at, revoked_at,
                   actor_id, previous_generation, acknowledged_at_json, delivered_consumers_json
              FROM principal_rotations WHERE operation_id = $operation;
            """, transaction, ("$operation", operationId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var deadline = Parse(reader.GetString(8));
        DateTime? delivered = reader.IsDBNull(9) ? null : Parse(reader.GetString(9));
        DateTime? retired = reader.IsDBNull(10) ? null : Parse(reader.GetString(10));
        DateTime? recovered = reader.IsDBNull(11) ? null : Parse(reader.GetString(11));
        var acknowledged = JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [];
        var state = !reader.IsDBNull(12) ? "revoked"
            : retired is not null ? "retired"
            : recovered is not null ? "superseded-in-recovery"
            : UtcNow >= deadline ? "recovery-required"
            : acknowledged.Length > 0 ? "awaiting-consumers"
            : delivered is not null ? "delivered" : "issued";
        return new RotationRow(new PrincipalRotationReceipt(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), state,
            Parse(reader.GetString(7)), deadline,
            JsonSerializer.Deserialize<PrincipalRotationConsumer[]>(reader.GetString(4)) ?? [],
            acknowledged, retired, delivered, reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            JsonSerializer.Deserialize<Dictionary<string, DateTime>>(reader.GetString(15)) ??
                new Dictionary<string, DateTime>(),
            JsonSerializer.Deserialize<string[]>(reader.GetString(16)) ?? []),
            JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
            reader.GetInt32(6), delivered, recovered);
    }
}
