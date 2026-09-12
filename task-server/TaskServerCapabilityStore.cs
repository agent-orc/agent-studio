using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private const int CapabilityFailureThreshold = 2;
    private const int CapabilityBaseCooldownSeconds = 120;
    private static readonly HashSet<string> WholeHostCapabilities = new(StringComparer.Ordinal)
    {
        CapabilityProtocol.Disk,
        CapabilityProtocol.LeaseAuthority,
        CapabilityProtocol.HostNetwork,
        CapabilityProtocol.RepositoryFileSystem,
        CapabilityProtocol.TaskServerAuthority,
    };

    public async Task<RunnerCapabilitySnapshotDto> AdvertiseCapabilitiesAsync(
        CapabilityAdvertisementRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (request.SchemaVersion != CapabilityProtocol.CurrentSchemaVersion)
            throw new ArgumentException(
                $"Capability schema {request.SchemaVersion} is unsupported; expected {CapabilityProtocol.CurrentSchemaVersion}.");
        if (request.FreshForSeconds is < 30 or > 900)
            throw new ArgumentException("Capability freshness must be between 30 and 900 seconds.");
        if (request.Generation <= 0 || request.Capabilities.Count == 0)
            throw new ArgumentException("Capability generation and at least one capability are required.");

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var runner = await ReadCapabilityRunnerAsync(
                connection, transaction, request.RunnerId, request.InstanceId, ct);
            var generationValue = await ScalarAsync(
                    connection,
                    "SELECT MAX(generation) FROM runner_capabilities WHERE runner_id = $runner;",
                    ct,
                    transaction,
                    ("$runner", request.RunnerId));
            var currentGeneration = generationValue is null or DBNull
                ? 0L
                : Convert.ToInt64(generationValue, CultureInfo.InvariantCulture);
            if (request.Generation < currentGeneration)
                throw new TaskServerConflictException(
                    "stale-capability-advertisement",
                    $"Capability generation {request.Generation} is older than {currentGeneration}.");
            var advertisedAt = request.AdvertisedAt.ToUniversalTime();
            if (advertisedAt > UtcNow.AddMinutes(2))
                throw new ArgumentException("Capability advertisement time is too far in the future.");
            var freshUntil = advertisedAt.AddSeconds(request.FreshForSeconds);
            var now = Iso(UtcNow);
            foreach (var capability in request.Capabilities)
            {
                var key = NormalizeCapability(capability.Key);
                if (key.Length == 0 || string.IsNullOrWhiteSpace(capability.Category))
                    throw new ArgumentException("Capability key and category are required.");
                var advertisedStatus = capability.Status.Trim().ToLowerInvariant();
                var tracksProbeHistory = key.StartsWith("provider-auth:", StringComparison.Ordinal);
                var positiveRecovery = tracksProbeHistory
                                       && advertisedStatus == ProviderAuthProbeStatuses.Ready
                                       && capability.Signal is ProviderAuthProbeSignals.Ok
                                           or ProviderAuthProbeSignals.CredentialsExpiring;
                var previous = tracksProbeHistory
                    ? await ReadCapabilityRowAsync(connection, transaction, request.RunnerId, key, ct)
                    : null;
                var probeHistory = previous?.RecoveryHistory ?? [];
                if (previous is not null
                    && !string.Equals(previous.AdvertisedStatus, advertisedStatus, StringComparison.Ordinal))
                {
                    probeHistory = AppendHistory(
                        probeHistory,
                        new CapabilityRecoveryEventDto(
                            advertisedAt,
                            previous.AdvertisedStatus,
                            advertisedStatus,
                            $"Provider authentication probe changed from {previous.AdvertisedStatus} to {advertisedStatus}."));
                }
                if (previous is not null
                    && positiveRecovery
                    && previous.HealthState != CapabilityHealthStates.Healthy)
                {
                    probeHistory = AppendHistory(
                        probeHistory,
                        new CapabilityRecoveryEventDto(
                            advertisedAt,
                            previous.HealthState,
                            CapabilityHealthStates.Healthy,
                            "Provider authentication probe confirmed recovery."));
                }
                await ExecuteAsync(connection, """
                    INSERT INTO runner_capabilities(
                        runner_id, capability_key, category, schema_version,
                        advertised_status, health_state, reason, version,
                        identity_value, detail, signal, credential_expires_at,
                        limited_until, credential_modified_at, advertised_at, fresh_until,
                        generation, recovery_history_json, updated_at)
                    VALUES (
                        $runner, $key, $category, $schema, $status, 'healthy',
                        NULL, $version, $identity, $detail, $signal, $expires,
                        $limited, $credential_modified, $advertised,
                        $fresh, $generation, $history, $updated)
                    ON CONFLICT(runner_id, capability_key) DO UPDATE SET
                        category = excluded.category,
                        schema_version = excluded.schema_version,
                        advertised_status = excluded.advertised_status,
                        health_state = CASE WHEN $positive_recovery = 1 THEN 'healthy' ELSE runner_capabilities.health_state END,
                        reason = CASE WHEN $positive_recovery = 1 THEN NULL ELSE runner_capabilities.reason END,
                        first_failure_at = CASE WHEN $positive_recovery = 1 THEN NULL ELSE runner_capabilities.first_failure_at END,
                        last_failure_at = CASE WHEN $positive_recovery = 1 THEN NULL ELSE runner_capabilities.last_failure_at END,
                        cooldown_until = CASE WHEN $positive_recovery = 1 THEN NULL ELSE runner_capabilities.cooldown_until END,
                        canary_claim_id = CASE WHEN $positive_recovery = 1 THEN NULL ELSE runner_capabilities.canary_claim_id END,
                        consecutive_failures = CASE WHEN $positive_recovery = 1 THEN 0 ELSE runner_capabilities.consecutive_failures END,
                        version = excluded.version,
                        identity_value = excluded.identity_value,
                        detail = excluded.detail,
                        signal = excluded.signal,
                        credential_expires_at = excluded.credential_expires_at,
                        limited_until = excluded.limited_until,
                        credential_modified_at = excluded.credential_modified_at,
                        advertised_at = excluded.advertised_at,
                        fresh_until = excluded.fresh_until,
                        generation = excluded.generation,
                        recovery_history_json = CASE
                            WHEN $tracks_history = 1 THEN excluded.recovery_history_json
                            ELSE runner_capabilities.recovery_history_json
                        END,
                        updated_at = excluded.updated_at;
                    """, ct, transaction,
                    ("$runner", request.RunnerId),
                    ("$key", key),
                    ("$category", capability.Category.Trim().ToLowerInvariant()),
                    ("$schema", request.SchemaVersion),
                    ("$status", advertisedStatus),
                    ("$version", capability.Version),
                    ("$identity", capability.Identity),
                    ("$detail", capability.Detail),
                    ("$signal", capability.Signal),
                    ("$expires", capability.ExpiresAt is null ? null : Iso(capability.ExpiresAt.Value.ToUniversalTime())),
                    ("$limited", capability.LimitedUntil is null ? null : Iso(capability.LimitedUntil.Value.ToUniversalTime())),
                    ("$credential_modified", capability.CredentialModifiedAt is null ? null : Iso(capability.CredentialModifiedAt.Value.ToUniversalTime())),
                    ("$advertised", Iso(advertisedAt)),
                    ("$fresh", Iso(freshUntil)),
                    ("$generation", request.Generation),
                    ("$history", JsonSerializer.Serialize(probeHistory)),
                    ("$tracks_history", tracksProbeHistory ? 1 : 0),
                    ("$positive_recovery", positiveRecovery ? 1 : 0),
                    ("$updated", now));
            }
            if (request.Telemetry is not null)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO runner_telemetry_latest(runner_id, payload_json, observed_at)
                    VALUES ($runner, $payload, $observed)
                    ON CONFLICT(runner_id) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        observed_at = excluded.observed_at;
                    """, ct, transaction,
                    ("$runner", request.RunnerId),
                    ("$payload", JsonSerializer.Serialize(request.Telemetry)),
                    ("$observed", Iso(request.Telemetry.ObservedAt.ToUniversalTime())));
            }
            await ExecuteAsync(
                connection,
                "UPDATE runners SET last_seen_at = $now WHERE id = $runner;",
                ct,
                transaction,
                ("$now", now),
                ("$runner", request.RunnerId));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "runner.capabilities-advertised",
                "runner",
                request.RunnerId,
                JsonSerializer.Serialize(new
                {
                    runner.HostId,
                    request.SchemaVersion,
                    request.Generation,
                    advertisedAt,
                    freshUntil,
                    capabilities = request.Capabilities.Select(item => item.Key),
                    telemetryAt = request.Telemetry?.ObservedAt,
                }),
                ct);
        }, ct);
        await EvaluateHostCliPolicyAsync(request.RunnerId, actorId, ct);
        return (await ListRunnerCapabilitySnapshotsAsync(ct))
            .Single(item => string.Equals(item.RunnerId, request.RunnerId, StringComparison.Ordinal));
    }

    public async Task<CapabilityFailureResponse> ReportCapabilityFailureAsync(
        CapabilityFailureRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.Classification)
            || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Capability failure classification, reason, and idempotency key are required.");

        CapabilityFailureResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var runner = await ReadCapabilityRunnerAsync(
                connection, transaction, request.RunnerId, request.InstanceId, ct);
            var payloadHash = HashJson(request);
            await using (var replayCommand = Command(connection, """
                SELECT payload_sha256, response_json
                  FROM capability_failure_deliveries
                 WHERE runner_id = $runner AND idempotency_key = $key;
                """, transaction, ("$runner", request.RunnerId), ("$key", request.IdempotencyKey)))
            await using (var replayReader = await replayCommand.ExecuteReaderAsync(ct))
            {
                if (await replayReader.ReadAsync(ct))
                {
                    if (!string.Equals(replayReader.GetString(0), payloadHash, StringComparison.Ordinal))
                        throw new TaskServerConflictException(
                            "idempotency-conflict",
                            "Capability failure idempotency key is bound to another payload.");
                    response = JsonSerializer.Deserialize<CapabilityFailureResponse>(replayReader.GetString(1))
                        ?? throw new InvalidDataException("Stored capability failure response is invalid.");
                    return;
                }
            }

            var capability = await ReadCapabilityRowAsync(
                connection, transaction, request.RunnerId, NormalizeCapability(request.CapabilityKey), ct)
                ?? throw new KeyNotFoundException("Advertised capability was not found.");
            var occurredAt = request.OccurredAt.ToUniversalTime();
            if (occurredAt > UtcNow.AddMinutes(2))
                throw new ArgumentException("Capability failure time is too far in the future.");
            if (occurredAt < capability.AdvertisedAt)
                throw new TaskServerConflictException(
                    "stale-capability-failure",
                    "Capability failure predates the current advertisement.");
            if (capability.LastFailureAt is not null && occurredAt < capability.LastFailureAt)
                throw new TaskServerConflictException(
                    "stale-capability-failure",
                    "Capability failure is older than the current failure state.");
            if (capability.CanaryClaimId is not null
                && request.ClaimId is not null
                && !string.Equals(capability.CanaryClaimId, request.ClaimId, StringComparison.Ordinal))
                throw new TaskServerConflictException(
                    "stale-capability-canary",
                    "Capability failure does not belong to the active half-open canary.");
            await ValidateCapabilityClaimCorrelationAsync(
                connection,
                transaction,
                request,
                ct);

            var wholeHost = WholeHostCapabilities.Contains(capability.Key);
            var failures = capability.ConsecutiveFailures + 1;
            var nextState = capability.HealthState switch
            {
                CapabilityHealthStates.HalfOpen => CapabilityHealthStates.Draining,
                CapabilityHealthStates.Draining => CapabilityHealthStates.Draining,
                _ when wholeHost || failures >= CapabilityFailureThreshold => CapabilityHealthStates.Draining,
                _ => CapabilityHealthStates.Suspect,
            };
            DateTime? cooldownUntil = nextState == CapabilityHealthStates.Draining
                ? UtcNow.AddSeconds(CapabilityBaseCooldownSeconds * (1 << Math.Min(Math.Max(0, failures - 2), 4)))
                : null;
            var history = AppendHistory(
                capability.RecoveryHistory,
                new CapabilityRecoveryEventDto(
                    UtcNow,
                    capability.HealthState,
                    nextState,
                    request.Reason,
                    request.ClaimId));
            await ExecuteAsync(connection, """
                UPDATE runner_capabilities
                   SET health_state = $state,
                       reason = $reason,
                       first_failure_at = COALESCE(first_failure_at, $occurred),
                       last_failure_at = $occurred,
                       cooldown_until = $cooldown,
                       canary_claim_id = NULL,
                       consecutive_failures = $failures,
                       recovery_history_json = $history,
                       updated_at = $updated
                 WHERE runner_id = $runner AND capability_key = $capability;
                """, ct, transaction,
                ("$state", nextState),
                ("$reason", $"{request.Classification}: {request.Reason}"),
                ("$occurred", Iso(occurredAt)),
                ("$cooldown", cooldownUntil is null ? null : Iso(cooldownUntil.Value)),
                ("$failures", failures),
                ("$history", JsonSerializer.Serialize(history)),
                ("$updated", Iso(UtcNow)),
                ("$runner", request.RunnerId),
                ("$capability", capability.Key));
            if (wholeHost)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO host_admission(
                        host_id, automatic_drain_reason, automatic_drain_at, updated_at)
                    VALUES ($host, $reason, $now, $now)
                    ON CONFLICT(host_id) DO UPDATE SET
                        automatic_drain_reason = excluded.automatic_drain_reason,
                        automatic_drain_at = excluded.automatic_drain_at,
                        updated_at = excluded.updated_at;
                    """, ct, transaction,
                    ("$host", runner.HostId),
                    ("$reason", $"{capability.Key}: {request.Classification}: {request.Reason}"),
                    ("$now", Iso(UtcNow)));
            }
            response = new CapabilityFailureResponse(
                "accepted",
                capability.Key,
                nextState,
                cooldownUntil,
                wholeHost);
            await ExecuteAsync(connection, """
                INSERT INTO capability_failure_deliveries(
                    runner_id, idempotency_key, payload_sha256, response_json, received_at)
                VALUES ($runner, $key, $hash, $response, $now);
                """, ct, transaction,
                ("$runner", request.RunnerId),
                ("$key", request.IdempotencyKey),
                ("$hash", payloadHash),
                ("$response", JsonSerializer.Serialize(response)),
                ("$now", Iso(UtcNow)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                wholeHost ? "host.automatic-drain" : "runner.capability-failed",
                wholeHost ? "host" : "runner-capability",
                wholeHost ? runner.HostId : $"{request.RunnerId}/{capability.Key}",
                JsonSerializer.Serialize(new
                {
                    capability = capability.Key,
                    nextState,
                    request.Classification,
                    request.Reason,
                    request.ClaimKind,
                    request.ClaimId,
                    request.Fence,
                    cooldownUntil,
                    wholeHost,
                }),
                ct);
        }, ct);
        return response!;
    }

    public async Task<IReadOnlyList<RunnerCapabilitySnapshotDto>> ListRunnerCapabilitySnapshotsAsync(
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var runners = new List<CapabilityRunner>();
        await using (var command = Command(connection, """
            SELECT id, name, host_id, instance_id, runner_version, protocol_version,
                   status, registered_at, last_seen_at, effective_max_parallelism,
                   runtime_capacity_applied_at, runtime_capacity_applied_version,
                   role_max_parallelism
              FROM runners
             ORDER BY host_id, name, id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                runners.Add(new CapabilityRunner(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    Parse(reader.GetString(7)),
                    Parse(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }

        var result = new List<RunnerCapabilitySnapshotDto>();
        foreach (var runner in runners)
        {
            var hostAdmission = await ReadHostAdmissionAsync(connection, null, runner.HostId, ct);
            var runtimeCapacity = await ReadRuntimeCapacitySettingsAsync(
                connection,
                null,
                runner.HostId,
                ct);
            var projectPolicy = await ReadHostProjectPolicyAsync(
                connection,
                null,
                runner.HostId,
                ct);
            var capabilities = new List<CapabilityHealthDto>();
            await using (var command = Command(connection, """
                SELECT capability_key, category, advertised_status, health_state,
                       reason, advertised_at, fresh_until, first_failure_at,
                       last_failure_at, cooldown_until, canary_claim_id,
                       consecutive_failures, version, identity_value, detail,
                       recovery_history_json, signal, credential_expires_at,
                       limited_until, credential_modified_at
                  FROM runner_capabilities
                 WHERE runner_id = $runner
                 ORDER BY category, capability_key;
                """, ("$runner", runner.Id)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var key = reader.GetString(0);
                    var freshUntil = Parse(reader.GetString(6));
                    capabilities.Add(new CapabilityHealthDto(
                        key,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        Parse(reader.GetString(5)),
                        freshUntil,
                        freshUntil > UtcNow,
                        reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
                        reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
                        reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.GetInt32(11),
                        reader.IsDBNull(12) ? null : reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13),
                        reader.IsDBNull(14) ? null : reader.GetString(14),
                        await AffectedClaimsAsync(connection, runner.Id, key, ct),
                        DeserializeHistory(reader.GetString(15)),
                        reader.IsDBNull(16) ? null : reader.GetString(16),
                        reader.IsDBNull(17) ? null : Parse(reader.GetString(17)),
                        reader.IsDBNull(18) ? null : Parse(reader.GetString(18)),
                        reader.IsDBNull(19) ? null : Parse(reader.GetString(19))));
                }
            }
            HostTelemetrySnapshotDto? telemetry = null;
            await using (var telemetryCommand = Command(connection, """
                SELECT payload_json FROM runner_telemetry_latest WHERE runner_id = $runner;
                """, ("$runner", runner.Id)))
            {
                var json = Convert.ToString(await telemetryCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(json))
                    telemetry = JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(json);
            }
            DateTime? restartedAt = null;
            var reviewsLost = 0;
            await using (var restartCommand = Command(connection, """
                SELECT restarted_at, reviews_lost
                  FROM runner_review_restarts
                 WHERE runner_id = $runner
                   AND instance_id = $instance;
                """, ("$runner", runner.Id), ("$instance", runner.InstanceId)))
            await using (var restartReader = await restartCommand.ExecuteReaderAsync(ct))
            {
                if (await restartReader.ReadAsync(ct))
                {
                    var observedRestart = Parse(restartReader.GetString(0));
                    if (observedRestart >= UtcNow.AddHours(-24))
                    {
                        restartedAt = observedRestart;
                        reviewsLost = restartReader.GetInt32(1);
                    }
                }
            }
            result.Add(new RunnerCapabilitySnapshotDto(
                runner.Id,
                runner.Name,
                runner.HostId,
                runner.InstanceId,
                runner.RunnerVersion,
                runner.ProtocolVersion,
                runner.Status,
                runner.RegisteredAt,
                runner.LastSeenAt,
                hostAdmission,
                capabilities,
                telemetry,
                runtimeCapacity,
                runner.EffectiveMaxParallelism,
                runner.RuntimeCapacityAppliedAt,
                runner.RuntimeCapacityAppliedVersion,
                projectPolicy,
                runner.RoleMaxParallelism,
                RestartedAt: restartedAt,
                ReviewsLost: reviewsLost,
                InstalledClis: InstalledCliProjection.FromCapabilities(
                    capabilities,
                    _options.CodexCliTargetVersion,
                    _options.ClaudeCliTargetVersion),
                CliUpdate: await ReadHostCliUpdateAsync(connection, null, runner.HostId, ct)));
        }
        return result;
    }

    public async Task<RemoteHostAdmissionDto> RequestOperatorHostDrainAsync(
        string hostId,
        OperatorHostDrainRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Host id and operator drain reason are required.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var exists = Convert.ToInt32(
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM runners WHERE host_id = $host;",
                    ct,
                    transaction,
                    ("$host", hostId)) ?? 0,
                CultureInfo.InvariantCulture);
            if (exists == 0) throw new KeyNotFoundException("Host was not found.");
            await ExecuteAsync(connection, """
                INSERT INTO host_admission(
                    host_id, operator_drain_reason, operator_drain_at, updated_at)
                VALUES ($host, $reason, $now, $now)
                ON CONFLICT(host_id) DO UPDATE SET
                    operator_drain_reason = excluded.operator_drain_reason,
                    operator_drain_at = excluded.operator_drain_at,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$host", hostId),
                ("$reason", request.Reason.Trim()),
                ("$now", Iso(UtcNow)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "host.operator-drain-requested",
                "host",
                hostId,
                JsonSerializer.Serialize(new { request.Reason }),
                ct);
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        return await ReadHostAdmissionAsync(read, null, hostId, ct);
    }

    public async Task<RemoteHostAdmissionDto> ClearAutomaticHostDrainAsync(
        string hostId,
        ClearAutomaticHostDrainRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Host id and automatic drain recovery reason are required.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var changed = await ExecuteAsync(connection, """
                UPDATE host_admission
                   SET automatic_drain_reason = NULL,
                       automatic_drain_at = NULL,
                       updated_at = $now
                 WHERE host_id = $host AND automatic_drain_at IS NOT NULL;
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$host", hostId));
            if (changed == 0)
                throw new TaskServerConflictException(
                    "host-not-automatically-drained",
                    "The host has no automatic capability drain to clear.");
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "host.automatic-drain-cleared",
                "host",
                hostId,
                JsonSerializer.Serialize(new { request.Reason }),
                ct);
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        return await ReadHostAdmissionAsync(read, null, hostId, ct);
    }

    private async Task<CapabilityAdmission> EvaluateCapabilityAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string hostId,
        IReadOnlyList<string>? requested,
        CancellationToken ct)
    {
        var host = await ReadHostAdmissionAsync(connection, transaction, hostId, ct);
        if (host.AutomaticDrainAt is not null)
            return CapabilityAdmission.Blocked(
                $"Host '{hostId}' is under automatic whole-host drain: {host.AutomaticDrainReason}.");
        if (host.OperatorDrainAt is not null)
            return CapabilityAdmission.Blocked(
                $"Host '{hostId}' is under operator-requested whole-host drain: {host.OperatorDrainReason}.");

        var required = (requested ?? [])
            .Select(NormalizeCapability)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var canaries = new List<string>();
        foreach (var key in required)
        {
            var capability = await ReadCapabilityRowAsync(connection, transaction, runnerId, key, ct);
            if (capability is null)
                return CapabilityAdmission.Blocked($"Required capability '{key}' was not advertised.");
            if (capability.FreshUntil <= UtcNow)
                return CapabilityAdmission.Blocked(
                    $"Required capability '{key}' is stale since {capability.FreshUntil:O}.");
            if (!string.Equals(capability.AdvertisedStatus, "ready", StringComparison.Ordinal))
                return CapabilityAdmission.Blocked(
                    $"Required capability '{key}' is advertised as {capability.AdvertisedStatus}.");
            if (capability.HealthState == CapabilityHealthStates.Draining)
            {
                if (capability.CooldownUntil is null || capability.CooldownUntil > UtcNow)
                    return CapabilityAdmission.Blocked(
                        $"Required capability '{key}' is draining until {capability.CooldownUntil:O}.");
                var history = AppendHistory(
                    capability.RecoveryHistory,
                    new CapabilityRecoveryEventDto(
                        UtcNow,
                        CapabilityHealthStates.Draining,
                        CapabilityHealthStates.HalfOpen,
                        "cooldown elapsed; one canary may be admitted"));
                await ExecuteAsync(connection, """
                    UPDATE runner_capabilities
                       SET health_state = 'half-open',
                           recovery_history_json = $history,
                           updated_at = $now
                     WHERE runner_id = $runner AND capability_key = $key;
                    """, ct, transaction,
                    ("$history", JsonSerializer.Serialize(history)),
                    ("$now", Iso(UtcNow)),
                    ("$runner", runnerId),
                    ("$key", key));
                capability = capability with
                {
                    HealthState = CapabilityHealthStates.HalfOpen,
                    RecoveryHistory = history,
                };
            }
            if (capability.HealthState == CapabilityHealthStates.HalfOpen)
            {
                if (!string.IsNullOrWhiteSpace(capability.CanaryClaimId))
                    return CapabilityAdmission.Blocked(
                        $"Required capability '{key}' already has canary claim '{capability.CanaryClaimId}'.");
                canaries.Add(key);
            }
        }
        return new CapabilityAdmission(true, null, required, canaries);
    }

    private static async Task ValidateCapabilityClaimCorrelationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityFailureRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimKind)
            && string.IsNullOrWhiteSpace(request.ClaimId)
            && request.Fence is null)
            return;
        if (string.IsNullOrWhiteSpace(request.ClaimKind)
            || string.IsNullOrWhiteSpace(request.ClaimId)
            || request.Fence is null)
            throw new ArgumentException(
                "Capability failure claim kind, claim id, and fence must be supplied together.");

        var review = string.Equals(request.ClaimKind, "review", StringComparison.OrdinalIgnoreCase);
        var sql = review
            ? """
              SELECT executor_id, fence, status, required_capabilities_json
                FROM review_attempts
               WHERE id = $claim;
              """
            : """
              SELECT runner_id, fence, status, required_capabilities_json
                FROM runs
               WHERE id = $claim;
              """;
        await using var command = Command(
            connection,
            sql,
            transaction,
            ("$claim", request.ClaimId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new TaskServerConflictException(
                "stale-capability-claim",
                "Capability failure claim was not found.");
        var owner = reader.IsDBNull(0) ? null : reader.GetString(0);
        var fence = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var status = reader.GetString(2);
        var required = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        var current = review
            ? status is "leased" or "process-unknown"
            : status is "running" or "process-unknown";
        if (!string.Equals(owner, request.RunnerId, StringComparison.Ordinal)
            || fence != request.Fence
            || !current)
            throw new TaskServerConflictException(
                "stale-capability-claim",
                "Capability failure is not bound to the current active claim authority.");
        if (!required.Contains(NormalizeCapability(request.CapabilityKey), StringComparer.Ordinal))
            throw new TaskServerConflictException(
                "capability-not-required-by-claim",
                "Capability failure is not correlated with the active claim's required capability set.");
    }

    private async Task ReserveCanariesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        IReadOnlyList<string> capabilities,
        string claimId,
        CancellationToken ct)
    {
        foreach (var key in capabilities)
        {
            var updated = await ExecuteAsync(connection, """
                UPDATE runner_capabilities
                   SET canary_claim_id = $claim, updated_at = $now
                 WHERE runner_id = $runner
                   AND capability_key = $key
                   AND health_state = 'half-open'
                   AND canary_claim_id IS NULL;
                """, ct, transaction,
                ("$claim", claimId),
                ("$now", Iso(UtcNow)),
                ("$runner", runnerId),
                ("$key", key));
            if (updated != 1)
                throw new TaskServerConflictException(
                    "capability-canary-race",
                    $"Capability '{key}' canary was claimed concurrently.");
        }
    }

    private async Task ResolveCanarySuccessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string claimId,
        string reason,
        CancellationToken ct)
    {
        var rows = new List<CapabilityRow>();
        await using (var command = Command(connection, """
            SELECT capability_key, category, advertised_status, health_state,
                   reason, advertised_at, fresh_until, first_failure_at,
                   last_failure_at, cooldown_until, canary_claim_id,
                   consecutive_failures, recovery_history_json, signal,
                   credential_expires_at, limited_until, credential_modified_at
              FROM runner_capabilities
             WHERE runner_id = $runner AND canary_claim_id = $claim;
            """, transaction, ("$runner", runnerId), ("$claim", claimId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) rows.Add(ReadCapabilityRow(reader));
        }
        foreach (var row in rows)
        {
            var history = AppendHistory(
                row.RecoveryHistory,
                new CapabilityRecoveryEventDto(
                    UtcNow,
                    CapabilityHealthStates.HalfOpen,
                    CapabilityHealthStates.Healthy,
                    reason,
                    claimId));
            await ExecuteAsync(connection, """
                UPDATE runner_capabilities
                   SET health_state = 'healthy',
                       reason = NULL,
                       first_failure_at = NULL,
                       last_failure_at = NULL,
                       cooldown_until = NULL,
                       canary_claim_id = NULL,
                       consecutive_failures = 0,
                       recovery_history_json = $history,
                       updated_at = $now
                 WHERE runner_id = $runner AND capability_key = $key
                   AND canary_claim_id = $claim;
                """, ct, transaction,
                ("$history", JsonSerializer.Serialize(history)),
                ("$now", Iso(UtcNow)),
                ("$runner", runnerId),
                ("$key", row.Key),
                ("$claim", claimId));
        }
    }

    private async Task<CapabilityRunner> ReadCapabilityRunnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string instanceId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, name, host_id, instance_id, runner_version, protocol_version,
                   status, registered_at, last_seen_at,
                   effective_max_parallelism, runtime_capacity_applied_at,
                   runtime_capacity_applied_version, role_max_parallelism
              FROM runners WHERE id = $runner;
            """, transaction, ("$runner", runnerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException("Runner was not found.");
        var runner = new CapabilityRunner(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            Parse(reader.GetString(7)),
            Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12));
        if (!string.Equals(runner.InstanceId, instanceId, StringComparison.Ordinal))
            throw new TaskServerConflictException(
                "runner-instance-mismatch",
                "Runner instance does not own this capability advertisement.");
        if (!string.Equals(runner.Status, "active", StringComparison.Ordinal))
            throw new TaskServerConflictException(
                "runner-not-active",
                $"Runner status is '{runner.Status}'.");
        return runner;
    }

    private async Task<CapabilityRow?> ReadCapabilityRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runnerId,
        string key,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT capability_key, category, advertised_status, health_state,
                   reason, advertised_at, fresh_until, first_failure_at,
                   last_failure_at, cooldown_until, canary_claim_id,
                   consecutive_failures, recovery_history_json, signal,
                   credential_expires_at, limited_until, credential_modified_at
              FROM runner_capabilities
             WHERE runner_id = $runner AND capability_key = $key;
            """, transaction, ("$runner", runnerId), ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCapabilityRow(reader) : null;
    }

    private static CapabilityRow ReadCapabilityRow(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Parse(reader.GetString(5)),
            Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11),
            DeserializeHistory(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : Parse(reader.GetString(16)));

    private async Task<RemoteHostAdmissionDto> ReadHostAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT automatic_drain_reason, automatic_drain_at,
                   operator_drain_reason, operator_drain_at
              FROM host_admission WHERE host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new RemoteHostAdmissionDto(hostId, "open", null, null, null, null);
        DateTime? automaticAt = reader.IsDBNull(1) ? null : Parse(reader.GetString(1));
        DateTime? operatorAt = reader.IsDBNull(3) ? null : Parse(reader.GetString(3));
        return new RemoteHostAdmissionDto(
            hostId,
            automaticAt is not null ? "automatic-draining" : operatorAt is not null ? "operator-draining" : "open",
            reader.IsDBNull(0) ? null : reader.GetString(0),
            automaticAt,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            operatorAt);
    }

    private async Task<IReadOnlyList<string>> AffectedClaimsAsync(
        SqliteConnection connection,
        string runnerId,
        string capability,
        CancellationToken ct)
    {
        var affected = new List<string>();
        await using (var command = Command(connection, """
            SELECT id, required_capabilities_json
              FROM runs
             WHERE runner_id = $runner AND status IN ('running', 'process-unknown');
            """, ("$runner", runnerId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var required = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
                if (required.Contains(capability, StringComparer.Ordinal)) affected.Add($"run:{reader.GetString(0)}");
            }
        }
        await using (var command = Command(connection, """
            SELECT id, required_capabilities_json
              FROM review_attempts
             WHERE executor_id = $runner AND status IN ('leased', 'process-unknown');
            """, ("$runner", runnerId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var required = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
                if (required.Contains(capability, StringComparer.Ordinal)) affected.Add($"review:{reader.GetString(0)}");
            }
        }
        return affected;
    }

    private static string NormalizeCapability(string value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static IReadOnlyList<CapabilityRecoveryEventDto> DeserializeHistory(string json)
        => JsonSerializer.Deserialize<List<CapabilityRecoveryEventDto>>(json) ?? [];

    private static IReadOnlyList<CapabilityRecoveryEventDto> AppendHistory(
        IReadOnlyList<CapabilityRecoveryEventDto> history,
        CapabilityRecoveryEventDto item)
        => history.Append(item).TakeLast(20).ToArray();

    private sealed record CapabilityAdmission(
        bool Eligible,
        string? Message,
        IReadOnlyList<string> Required,
        IReadOnlyList<string> Canaries)
    {
        public static CapabilityAdmission Blocked(string message)
            => new(false, message, [], []);
    }

    private sealed record CapabilityRunner(
        string Id,
        string Name,
        string HostId,
        string InstanceId,
        string RunnerVersion,
        int ProtocolVersion,
        string Status,
        DateTime RegisteredAt,
        DateTime LastSeenAt,
        int? EffectiveMaxParallelism = null,
        DateTime? RuntimeCapacityAppliedAt = null,
        long? RuntimeCapacityAppliedVersion = null,
        int? RoleMaxParallelism = null);

    private sealed record CapabilityRow(
        string Key,
        string Category,
        string AdvertisedStatus,
        string HealthState,
        string? Reason,
        DateTime AdvertisedAt,
        DateTime FreshUntil,
        DateTime? FirstFailureAt,
        DateTime? LastFailureAt,
        DateTime? CooldownUntil,
        string? CanaryClaimId,
        int ConsecutiveFailures,
        IReadOnlyList<CapabilityRecoveryEventDto> RecoveryHistory,
        string? Signal,
        DateTime? ExpiresAt,
        DateTime? LimitedUntil,
        DateTime? CredentialModifiedAt);
}

internal static class ProviderAuthProbeStatuses
{
    public const string Ready = "ready";
}

internal static class ProviderAuthProbeSignals
{
    public const string Ok = "ok";
    public const string CredentialsExpiring = "credentials-expiring";
}
