using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions CredentialRegistryJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex UnsafeCredentialMetadata = new(
        @"(?i)(?:bearer\s+\S+|\b(?:sk|ghp|gho|github_pat)_[A-Za-z0-9_-]+|[A-Za-z0-9_-]{30,}\.[A-Za-z0-9_-]{30,}|://[^/\s]+@|[?&#][^\s]+)",
        RegexOptions.Compiled);

    public async Task<CredentialRegistryRecordDto> UpsertCredentialRegistryAsync(
        CredentialRegistryObservationRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateCredentialMetadata(request);
        var record = request.Record;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            string? generation = null;
            string? instance = null;
            DateTime? observedAt = null;
            await using (var command = Command(connection, """
                SELECT generation, source_instance_id, observed_at
                  FROM credential_registry
                 WHERE installation_id = $installation AND host_id = $host AND credential_id = $credential;
                """, ("$installation", record.InstallationId), ("$host", record.HostId),
                ("$credential", record.CredentialId)))
            {
                command.Transaction = transaction;
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    generation = reader.GetString(0);
                    instance = reader.GetString(1);
                    observedAt = Parse(reader.GetString(2));
                }
            }
            var hasRegisteredSource = false;
            var sourceIsCurrent = false;
            var previousSourceIsCurrent = false;
            await using (var command = Command(connection, """
                SELECT instance_id, status FROM runners WHERE host_id = $host;
                """, ("$host", record.HostId)))
            {
                command.Transaction = transaction;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    hasRegisteredSource = true;
                    if (!string.Equals(reader.GetString(1), "active", StringComparison.Ordinal))
                        continue;
                    sourceIsCurrent |= string.Equals(reader.GetString(0), request.SourceInstanceId, StringComparison.Ordinal);
                    previousSourceIsCurrent |= string.Equals(reader.GetString(0), instance, StringComparison.Ordinal);
                }
            }
            if (hasRegisteredSource && !sourceIsCurrent)
                throw new TaskServerConflictException("stale-credential-instance", "Source instance is not current for this host.");
            await using (var command = Command(connection, """
                SELECT 1 FROM credential_registry_retired_sources
                 WHERE installation_id = $installation AND host_id = $host
                   AND credential_id = $credential AND source_instance_id = $instance;
                """, ("$installation", record.InstallationId), ("$host", record.HostId),
                ("$credential", record.CredentialId), ("$instance", request.SourceInstanceId)))
            {
                command.Transaction = transaction;
                if (await command.ExecuteScalarAsync(ct) is not null)
                    throw new TaskServerConflictException("stale-credential-instance", "A retired source instance cannot regain this credential.");
            }
            if (generation is null)
            {
                if (request.ExpectedGeneration is not null || record.Supersedes is not null)
                    throw new TaskServerConflictException("stale-credential-generation", "Credential generation has no predecessor in this registry.");
            }
            else if (!string.Equals(request.ExpectedGeneration, generation, StringComparison.Ordinal))
                throw new TaskServerConflictException("stale-credential-generation", "Credential generation changed before the observation was stored.");
            else if (!string.Equals(instance, request.SourceInstanceId, StringComparison.Ordinal) &&
                (!sourceIsCurrent || previousSourceIsCurrent))
                throw new TaskServerConflictException("stale-credential-instance", "Current host registration has not transferred this credential source.");
            else if (request.ObservedAt <= observedAt)
                throw new TaskServerConflictException("stale-credential-observation", "A newer credential observation is already stored.");
            // A restarted daemon keeps the native store, so an authorized
            // handoff may refresh the unchanged generation.
            else if (!string.Equals(record.Generation, generation, StringComparison.Ordinal) &&
                !string.Equals(record.Supersedes, generation, StringComparison.Ordinal))
                throw new TaskServerConflictException("stale-credential-generation", "New credential generation must supersede the current one.");

            if (instance is not null && !string.Equals(instance, request.SourceInstanceId, StringComparison.Ordinal))
                await ExecuteAsync(connection, """
                    INSERT INTO credential_registry_retired_sources(
                        installation_id, host_id, credential_id, source_instance_id,
                        retired_generation, retired_at)
                    VALUES ($installation, $host, $credential, $instance, $generation, $retired);
                    """, ct, transaction,
                    ("$installation", record.InstallationId), ("$host", record.HostId),
                    ("$credential", record.CredentialId), ("$instance", instance),
                    ("$generation", generation), ("$retired", Iso(UtcNow)));
            await ExecuteAsync(connection, """
                INSERT INTO credential_registry(
                    installation_id, host_id, credential_id, generation,
                    source_instance_id, observed_at, payload_json, updated_at)
                VALUES ($installation, $host, $credential, $generation,
                    $instance, $observed, $payload, $updated)
                ON CONFLICT(installation_id, host_id, credential_id) DO UPDATE SET
                    generation = excluded.generation,
                    source_instance_id = excluded.source_instance_id,
                    observed_at = excluded.observed_at,
                    payload_json = excluded.payload_json,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$installation", record.InstallationId), ("$host", record.HostId),
                ("$credential", record.CredentialId), ("$generation", record.Generation),
                ("$instance", request.SourceInstanceId), ("$observed", Iso(request.ObservedAt)),
                ("$payload", JsonSerializer.Serialize(record, CredentialRegistryJson)),
                ("$updated", Iso(UtcNow)));
            await AuditAsync(connection, transaction, actorId, "credential.metadata-observed",
                "credential", record.CredentialId,
                JsonSerializer.Serialize(new { record.InstallationId, record.HostId, record.Generation }), ct);
        }, ct);
        return record;
    }

    public async Task<IReadOnlyList<CredentialRegistryRecordDto>> ListCredentialRegistryAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var result = new List<CredentialRegistryRecordDto>();
        await using var command = Command(connection, """
            SELECT payload_json FROM credential_registry
             ORDER BY installation_id, host_id, credential_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(JsonSerializer.Deserialize<CredentialRegistryRecordDto>(reader.GetString(0), CredentialRegistryJson)!);
        return result;
    }

    public async Task<IReadOnlyList<CredentialViewDto>> ListCredentialViewsAsync(CancellationToken ct)
        => (await ListCredentialRegistryAsync(ct))
            .Select(record => CredentialViewPolicy.Project(record, UtcNow))
            .ToArray();

    private void ValidateCredentialMetadata(CredentialRegistryObservationRequest request)
    {
        var record = request.Record ?? throw new ArgumentException("Credential metadata is required.");
        static void Required(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
                value.Any(char.IsControl))
                throw new ArgumentException($"Credential metadata {name} must be a bounded identifier.");
        }
        Required(record.InstallationId, "installationId");
        Required(record.HostId, "hostId");
        Required(record.CredentialId, "credentialId");
        Required(record.Provider, "provider");
        Required(record.Owner, "owner");
        Required(record.RecoveryOwner, "recoveryOwner");
        Required(record.Generation, "generation");
        Required(record.RenewalMethod, "renewalMethod");
        Required(request.SourceInstanceId, "sourceInstanceId");
        if (record.Locator is null || record.UnknownReasons is null || record.Scopes is null ||
            record.Bindings is null || record.EvidenceRefs is null ||
            record.Bindings.Any(binding => binding is null))
            throw new ArgumentException("Credential metadata collections and locator are required.");
        if (string.IsNullOrWhiteSpace(record.Locator.LocalRef))
            throw new ArgumentException("Credential locator localRef is required.");
        foreach (var binding in record.Bindings)
        {
            Required(binding.ServiceId, "binding.serviceId");
            Required(binding.Purpose, "binding.purpose");
            Required(binding.SourceRef, "binding.sourceRef");
        }
        foreach (var scope in record.Scopes) Required(scope, "scope");
        if (record.SchemaVersion != CredentialRegistryProtocol.SchemaVersion ||
            !CredentialRegistryProtocol.Kinds.Contains(record.Kind) ||
            !string.Equals(record.RenewalMethod,
                CredentialRegistryProtocol.RenewalMethods.GetValueOrDefault(record.Kind),
                StringComparison.Ordinal) ||
            !CredentialRegistryProtocol.Adapters.Contains(record.Locator.Adapter) ||
            record.Locator.EffectiveSource is not ("active" or "shadowed" or "absent" or "unknown") ||
            record.ExpiryKnowledge is not ("issuer" or "operator" or "none" or "unknown") ||
            record.LastOutcome is not ("healthy" or "credential_invalid" or "provider_incident" or
                "quota_exhausted" or "network_failure" or "indeterminate" or "not_verified"))
            throw new ArgumentException("Credential metadata contains an unsupported kind, source or outcome.");
        var dates = new (string Name, DateTime? Value)[]
        {
            ("createdAt", record.CreatedAt), ("discoveredAt", record.DiscoveredAt),
            ("lastVerifiedAt", record.LastVerifiedAt), ("lastRenewedAt", record.LastRenewedAt),
            ("expiresAt", record.ExpiresAt), ("rotationDueAt", record.RotationDueAt),
            ("accessTokenExpiresAt", record.AccessTokenExpiresAt),
            ("lastRealSuccessAt", record.LastRealSuccessAt), ("nextProbeAt", record.NextProbeAt),
        };
        if (dates.Any(date => date.Value is null &&
            (!record.UnknownReasons.TryGetValue(date.Name, out var reason) || string.IsNullOrWhiteSpace(reason))))
            throw new ArgumentException("Unknown credential dates require explicit reasons.");
        if (dates.Any(date => date.Value is { } known && known.Kind != DateTimeKind.Utc))
            throw new ArgumentException("Known credential dates must be UTC.");
        if (record.Kind is "claude_native_login" or "codex_chatgpt_login" &&
            record.AccessTokenExpiresAt is not null && record.ExpiresAt == record.AccessTokenExpiresAt)
            throw new ArgumentException("Access-token expiry cannot be used as refreshable-session expiry.");
        if (record.ExpiryKnowledge == "none" &&
            record.Kind is not ("github_deploy_key" or "wireguard_peer" or "administration_ssh_key"))
            throw new ArgumentException("Expiry knowledge 'none' needs a credential-kind guarantee.");
        if (record.ExpiresAt is not null && record.ExpiryKnowledge is not ("issuer" or "operator"))
            throw new ArgumentException("Known expiry requires issuer or operator provenance.");
        if (request.ObservedAt.Kind != DateTimeKind.Utc || request.ObservedAt > UtcNow.AddMinutes(2))
            throw new ArgumentException("Credential observation time must be UTC and current.");
        if (record.Scopes.Count > 64 || record.Bindings.Count > 128 || record.EvidenceRefs.Count > 32)
            throw new ArgumentException("Credential metadata collection exceeds its bound.");
        if (record.Locator.LocalRef.Length > 256 ||
            record.Locator.LocalRef.Contains("://", StringComparison.Ordinal) ||
            record.Locator.LocalRef.IndexOfAny(['?', '#', '@']) >= 0 ||
            record.EvidenceRefs.Any(reference => reference.Length > 128 ||
                !reference.StartsWith("evidence:", StringComparison.Ordinal) ||
                reference.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (':' or '-' or '_' or '.'))))
            throw new ArgumentException("Credential locator and evidence must be safe local references.");
        if (record.Generation.Length == 64 && record.Generation.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Credential generation cannot be a token hash.");
        if (record.GitHubKeyId is <= 0 ||
            record.GitHubKeyId is not null && record.Kind != "github_deploy_key" ||
            record.ProvisioningCredentialId is not null && record.Kind != "github_deploy_key" ||
            record.ProvisioningCredentialId is not null &&
                (record.ProvisioningCredentialId.Length > 128 || record.ProvisioningCredentialId.Any(char.IsControl)) ||
            record.RepositoryPurpose is not null &&
                (record.Kind is not ("github_deploy_key" or "github_https_token" or "github_provisioning_oauth")
                 || record.RepositoryPurpose is not ("product" or "workspace")) ||
            record.RepositoryWriteGrant is not null &&
                record.Kind is not ("github_deploy_key" or "github_https_token") ||
            record.TokenSubtype is not null &&
                (record.Kind is not ("github_https_token" or "github_provisioning_oauth")
                 || record.TokenSubtype is not ("fine-grained-pat" or "classic-pat" or "oauth-app" or "unknown")))
            throw new ArgumentException("GitHub credential metadata has an invalid kind or identifier.");
        var serialized = JsonSerializer.Serialize(request, CredentialRegistryJson);
        if (serialized.Length > 32_768 || UnsafeCredentialMetadata.IsMatch(serialized))
            throw new ArgumentException("Credential metadata contains a value or unsafe reference.");
    }
}
