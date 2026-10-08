using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static string ServiceCohort(string provider) => $"service:{provider}";
    private async Task<ProviderBindingScope> BindingScopeAsync(SqliteConnection connection,
        SqliteTransaction transaction, string runnerId, string provider,
        string? source, string? generation, CancellationToken ct)
    {
        var host = Convert.ToString(await ScalarAsync(connection,
            "SELECT host_id FROM runners WHERE id = $runner;", ct, transaction,
            ("$runner", runnerId))) ?? runnerId;
        source ??= Convert.ToString(await ScalarAsync(connection, """
            SELECT effective_source FROM runner_capabilities
             WHERE runner_id = $runner AND capability_key = $key;
            """, ct, transaction, ("$runner", runnerId),
            ("$key", CapabilityProtocol.ProviderAuthentication(provider))));
        generation ??= Convert.ToString(await ScalarAsync(connection, """
            SELECT credential_generation FROM runner_capabilities
             WHERE runner_id = $runner AND capability_key = $key;
            """, ct, transaction, ("$runner", runnerId),
            ("$key", CapabilityProtocol.ProviderAuthentication(provider))));
        var matches = new List<CredentialRegistryRecordDto>();
        await using (var command = Command(connection, """
            SELECT payload_json FROM credential_registry WHERE host_id = $host;
            """, transaction, ("$host", host)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var record = System.Text.Json.JsonSerializer.Deserialize<CredentialRegistryRecordDto>(
                    reader.GetString(0), CredentialRegistryJson);
                if (record is not null && record.Provider == provider &&
                    record.Locator.EffectiveSource == source &&
                    (generation is null || record.Generation == generation))
                    matches.Add(record);
            }
        }
        if (matches.Count == 1)
        {
            var fallback = $"binding:{host}:{provider}";
            var key = await ReadProviderCircuitAsync(connection, transaction, fallback, ct) is null
                ? $"binding:{host}:{matches[0].CredentialId}"
                : fallback;
            var runbook = matches[0].RunbookId;
            if (string.IsNullOrWhiteSpace(runbook) || runbook == matches[0].RenewalMethod)
                runbook = "docs/operations/setup/cli-relogin-runbook.md";
            return new(key, runbook);
        }
        // An undiscovered source has no account identity. Keep the hold on the
        // host/provider pair across a missing-source repair until I1 reports it.
        return new($"binding:{host}:{provider}",
            provider is "claude" or "codex"
                ? "docs/operations/setup/cli-relogin-runbook.md" : null);
    }

    // A circuit is written in the same transaction as the advertisement. The
    // Task Server database, rather than a daemon timer, owns retries and the
    // one outstanding real request across hosts.
    private async Task ObserveProviderFleetAsync(SqliteConnection connection,
        SqliteTransaction transaction, CapabilityAdvertisementRequest request,
        AdvertisedCapabilityDto capability, CancellationToken ct)
    {
        if (request.CredentialHealthVersion != 2 ||
            !capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal)) return;
        var provider = NormalizeCapability(capability.Key)["provider-auth:".Length..];
        var outcome = capability.HealthOutcome;
        if (outcome is null) return;
        var service = ServiceCohort(provider);
        var bindingScope = await BindingScopeAsync(connection, transaction,
            request.RunnerId, provider, capability.EffectiveSource,
            capability.CredentialGeneration, ct);
        var binding = bindingScope.Key;
        var observed = capability.CredentialObservedAt?.ToUniversalTime() ?? request.AdvertisedAt.ToUniversalTime();
        var signature = capability.EvidenceExcerpt is
            "unauthorized" or "unauthorized:service-account-shaped"
            ? capability.EvidenceExcerpt : null;
        if (outcome == "healthy")
        {
            // A cached status, ordinary run, or another host cannot clear a
            // hold. The permit owner must advertise a later real success for
            // the binding captured when the permit was issued.
            var released = false;
            foreach (var key in new[] { service, binding })
                released |= await ExecuteAsync(connection, """
                    DELETE FROM provider_health_circuits
                     WHERE cohort_key = $key AND canary_runner_id = $runner
                       AND canary_instance_id = $instance
                       AND canary_started_at < $success
                       AND canary_until >= $success
                       AND last_evidence_at < $success
                       AND (canary_generation IS NULL OR canary_generation = $generation)
                       AND (canary_source IS NULL OR canary_source = $source);
                    """, ct, transaction,
                    ("$key", key), ("$runner", request.RunnerId), ("$instance", request.InstanceId),
                    ("$success", capability.LastRealSuccessAt is { } success &&
                        success.ToUniversalTime() <= request.AdvertisedAt.ToUniversalTime()
                            ? Iso(success.ToUniversalTime()) : ""),
                    ("$generation", capability.CredentialGeneration),
                    ("$source", capability.EffectiveSource)) > 0;
            if (released)
            {
                var remaining = await ReadProviderCircuitAsync(connection, transaction, service, ct)
                    ?? await ReadProviderCircuitAsync(connection, transaction, binding, ct);
                if (remaining is null)
                    await ExecuteAsync(connection, """
                        UPDATE runner_capabilities
                           SET health_state = 'healthy', reason = NULL,
                               first_failure_at = NULL, last_failure_at = NULL,
                               cooldown_until = NULL, canary_claim_id = NULL,
                               consecutive_failures = 0
                         WHERE runner_id = $runner AND capability_key = $key;
                        """, ct, transaction, ("$runner", request.RunnerId),
                        ("$key", capability.Key));
            }
            await ExecuteAsync(connection, """
                UPDATE provider_health_items SET closed_at = $now
                 WHERE cohort_key = $key AND credential_generation = $generation
                   AND closed_at IS NULL
                   AND NOT EXISTS(SELECT 1 FROM provider_health_circuits WHERE cohort_key = $key);
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$key", binding),
                ("$generation", capability.CredentialGeneration ?? "unknown"));
            return;
        }
        if (outcome is "quota_exhausted" or "network_failure") return;
        if (outcome is not ("credential_invalid" or "provider_incident" or "indeterminate")) return;
        var keyForOutcome = outcome == "provider_incident" ? service : binding;
        var current = await ReadProviderCircuitAsync(connection, transaction, keyForOutcome, ct);
        if (outcome == "indeterminate" && current is not null &&
            UtcNow - current.OpenedAt >= TimeSpan.FromMinutes(15))
            await InsertProviderItemAsync(connection, transaction, binding,
                capability.CredentialGeneration, "diagnosis", bindingScope.RunbookId, ct);
        if (current is not null && observed <= current.LastEvidenceAt) return;
        var isFailedCanary = current is not null
            && current.CanaryRunnerId == request.RunnerId
            && current.CanaryInstanceId == request.InstanceId
            && current.CanaryStartedAt < observed;
        var retryCount = isFailedCanary ? Math.Min(current!.RetryCount + 1, 3) : current?.RetryCount ?? 0;
        var retry = UtcNow.AddSeconds(ProviderFleetRetrySeconds(keyForOutcome, retryCount));
        if (current is null)
            await ExecuteAsync(connection, """
                INSERT INTO provider_health_circuits(cohort_key, provider, outcome,
                    credential_generation, opened_at, last_evidence_at, evidence_id, evidence_signature,
                    retry_count, next_retry_at)
                VALUES ($key, $provider, $outcome, $generation, $now, $observed,
                    $evidence, $signature, 0, $retry);
                """, ct, transaction, ("$key", keyForOutcome), ("$provider", provider),
                ("$outcome", outcome), ("$generation", outcome == "provider_incident" ? null : capability.CredentialGeneration),
                ("$now", Iso(UtcNow)), ("$observed", Iso(observed)),
                ("$evidence", capability.EvidenceId),
                ("$signature", signature),
                ("$retry", Iso(retry)));
        else
            await ExecuteAsync(connection, """
                UPDATE provider_health_circuits
                   SET outcome = $outcome, credential_generation = $generation,
                       last_evidence_at = $observed, evidence_id = $evidence,
                       evidence_signature = $signature,
                       retry_count = $count,
                       next_retry_at = CASE WHEN $failed = 1 THEN $retry ELSE next_retry_at END,
                       canary_runner_id = CASE WHEN $failed = 1 THEN NULL ELSE canary_runner_id END,
                       canary_instance_id = CASE WHEN $failed = 1 THEN NULL ELSE canary_instance_id END,
                       canary_generation = CASE WHEN $failed = 1 THEN NULL ELSE canary_generation END,
                       canary_source = CASE WHEN $failed = 1 THEN NULL ELSE canary_source END,
                       canary_started_at = CASE WHEN $failed = 1 THEN NULL ELSE canary_started_at END,
                       canary_until = CASE WHEN $failed = 1 THEN NULL ELSE canary_until END
                 WHERE cohort_key = $key;
                """, ct, transaction, ("$key", keyForOutcome), ("$outcome", outcome),
                ("$generation", outcome == "provider_incident" ? null : capability.CredentialGeneration), ("$observed", Iso(observed)),
                ("$evidence", capability.EvidenceId), ("$signature", signature), ("$count", retryCount),
                ("$failed", isFailedCanary ? 1 : 0),
                ("$retry", Iso(retry)));
        if (outcome == "credential_invalid")
            await InsertProviderItemAsync(connection, transaction, binding,
                capability.CredentialGeneration, "renewal", bindingScope.RunbookId, ct);
    }

    private static int ProviderFleetRetrySeconds(string key, int retryCount)
    {
        var baseSeconds = retryCount switch { 0 => 60, 1 => 120, _ => 300 };
        // Stable small jitter keeps restarts from moving the deadline.
        var jitter = (int)(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))[0] % 11) - 5;
        return baseSeconds + jitter;
    }

    private async Task InsertProviderItemAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cohort, string? generation, string kind, string? runbook, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            INSERT INTO provider_health_items(cohort_key, credential_generation, kind,
                runbook_id, opened_at)
            VALUES ($cohort, $generation, $kind, $runbook, $now)
            ON CONFLICT(cohort_key, credential_generation, kind) DO NOTHING;
            """, ct, transaction, ("$cohort", cohort),
            ("$generation", generation ?? "unknown"), ("$kind", kind),
            ("$runbook", kind == "renewal" ? runbook : null), ("$now", Iso(UtcNow)));
    }

    private async Task<ProviderCircuit?> ReadProviderCircuitAsync(SqliteConnection connection,
        SqliteTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT outcome, credential_generation, opened_at, last_evidence_at,
                   retry_count, next_retry_at, canary_runner_id, canary_instance_id,
                   canary_started_at, canary_until
              FROM provider_health_circuits WHERE cohort_key = $key;
            """, transaction, ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ProviderCircuit(key, reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1), Parse(reader.GetString(2)),
            Parse(reader.GetString(3)), reader.GetInt32(4),
            reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)));
    }

    public async Task<ProviderCanaryPermitDto> ReserveProviderCanaryAsync(
        string runnerId, string instanceId, string provider, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(provider) || provider.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Provider is invalid.");
        provider = provider.ToLowerInvariant();
        ProviderCanaryPermitDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ReadCapabilityRunnerAsync(connection, transaction, runnerId, instanceId, ct);
            var key = CapabilityProtocol.ProviderAuthentication(provider);
            if (await ReadCapabilityRowAsync(connection, transaction, runnerId, key, ct) is null)
                throw new KeyNotFoundException("Provider authentication capability was not advertised.");
            var binding = await BindingScopeAsync(connection, transaction,
                runnerId, provider, null, null, ct);
            var circuits = new List<ProviderCircuit>();
            foreach (var cohort in new[] { ServiceCohort(provider), binding.Key })
            {
                var circuit = await ReadProviderCircuitAsync(connection, transaction, cohort, ct);
                if (circuit is not null) circuits.Add(circuit);
            }
            foreach (var circuit in circuits)
            {
                if (circuit.CanaryRunnerId is not null && circuit.CanaryUntil > UtcNow)
                {
                    if (circuit.CanaryRunnerId == runnerId && circuit.CanaryInstanceId == instanceId)
                        continue;
                    result = new(false, circuit.NextRetryAt, "canary-in-flight");
                    return;
                }
                if (circuit.NextRetryAt is null || circuit.NextRetryAt > UtcNow)
                {
                    result = new(false, circuit.NextRetryAt, "cohort-held");
                    return;
                }
            }
            foreach (var circuit in circuits)
            {
                if (circuit.CanaryRunnerId == runnerId && circuit.CanaryInstanceId == instanceId
                    && circuit.CanaryUntil > UtcNow) continue;
                await ExecuteAsync(connection, """
                    UPDATE provider_health_circuits
                       SET canary_runner_id = $runner, canary_instance_id = $instance,
                           canary_generation = (SELECT credential_generation FROM runner_capabilities
                               WHERE runner_id = $runner AND capability_key = $key),
                           canary_source = (SELECT effective_source FROM runner_capabilities
                               WHERE runner_id = $runner AND capability_key = $key),
                           canary_started_at = $now, canary_until = $until
                     WHERE cohort_key = $cohort;
                    """, ct, transaction, ("$runner", runnerId), ("$instance", instanceId),
                    ("$now", Iso(UtcNow)), ("$until", Iso(UtcNow.AddSeconds(45))),
                    ("$cohort", circuit.Key), ("$key", key));
            }
            result = new(true, null, circuits.Count == 0 ? "no-hold" : "canary");
        }, ct);
        return result!;
    }

    public async Task<IReadOnlyList<ProviderHealthItemDto>> ListProviderHealthItemsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var items = new List<ProviderHealthItemDto>();
        await using var command = Command(connection, """
            SELECT cohort_key, credential_generation, kind, runbook_id, opened_at, closed_at
              FROM provider_health_items ORDER BY opened_at, cohort_key;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5))));
        return items;
    }

    private sealed record ProviderCircuit(string Key, string Outcome, string? Generation,
        DateTime OpenedAt, DateTime LastEvidenceAt, int RetryCount, DateTime? NextRetryAt,
        string? CanaryRunnerId, string? CanaryInstanceId, DateTime? CanaryStartedAt,
        DateTime? CanaryUntil);
    private sealed record ProviderBindingScope(string Key, string? RunbookId);
}
