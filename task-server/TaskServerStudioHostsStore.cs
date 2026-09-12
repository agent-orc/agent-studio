using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio P1 "hosts" bundle (G1_Hosts): remote-host ("client") lifecycle and
/// management-command routes. In the OLD monolith a "client" was a
/// filesystem-backed dev-seat identity; here it is the same durable identity
/// as a Task Server execution host/runner (<c>runners.host_id</c>). These
/// methods are thin wrappers around (or close mirrors of) the existing
/// runner/capability/runtime-capacity/project-policy infrastructure - they
/// never re-derive host identity or claimability truth themselves.
/// </summary>
public sealed partial class TaskServerStore
{
    /// <summary>
    /// Creates every table this bundle owns. All table names are prefixed
    /// with <c>studio_host_</c>/<c>studio_provider_auth_</c> so they cannot
    /// collide with any other P1 group's tables. The caller is responsible
    /// for invoking this from the main migration method.
    /// </summary>
    internal async Task ApplyStudioHostsMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_host_defaults(
                host_id TEXT PRIMARY KEY,
                defaults_json TEXT NOT NULL,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_host_lifecycle(
                host_id TEXT PRIMARY KEY,
                retired_at TEXT,
                retired_reason TEXT,
                permanently_deleted_at TEXT,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_host_preflight_cache(
                host_id TEXT PRIMARY KEY,
                invalidated_at TEXT NOT NULL,
                generation INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_provider_auth_events(
                id TEXT PRIMARY KEY,
                host_id TEXT NOT NULL,
                provider TEXT NOT NULL,
                status TEXT NOT NULL,
                detail_json TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_provider_auth_events_host
                ON studio_provider_auth_events(host_id, created_at);
            CREATE TABLE IF NOT EXISTS studio_host_cli_updates(
                host_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                codex_target_version TEXT NOT NULL,
                claude_target_version TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT,
                detail TEXT
            );
            CREATE TABLE IF NOT EXISTS studio_host_cli_drift(
                host_id TEXT NOT NULL,
                cli_name TEXT NOT NULL,
                installed_version TEXT NOT NULL,
                target_version TEXT NOT NULL,
                first_detected_at TEXT NOT NULL,
                alarmed_at TEXT,
                PRIMARY KEY(host_id, cli_name)
            );
            CREATE TABLE IF NOT EXISTS studio_host_model_cli_alerts(
                host_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                cli_name TEXT NOT NULL,
                installed_version TEXT NOT NULL,
                minimum_version TEXT NOT NULL,
                alerted_at TEXT NOT NULL,
                PRIMARY KEY(host_id, model_id)
            );
            """, ct);
    }

    // ---------------------------------------------------------------
    // GET /api/v1/studio/clients
    // ---------------------------------------------------------------

    public async Task<IReadOnlyList<StudioClientSummaryDto>> ListStudioClientsAsync(CancellationToken ct)
    {
        var snapshots = await ListRunnerCapabilitySnapshotsAsync(ct);
        await using var connection = await OpenReadyAsync(ct);
        var lifecycleByHost = await ReadAllStudioHostLifecycleAsync(connection, ct);

        var result = new List<StudioClientSummaryDto>();
        foreach (var group in snapshots.GroupBy(item => item.HostId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var latest = group.OrderByDescending(item => item.LastSeenAt).First();
            lifecycleByHost.TryGetValue(group.Key, out var lifecycle);
            result.Add(new StudioClientSummaryDto(
                group.Key,
                group.Key,
                latest.Status,
                latest.HostAdmission.AdmissionState,
                latest.HostAdmission,
                latest.RuntimeCapacity,
                latest.Telemetry,
                group.Count(),
                latest.LastSeenAt,
                lifecycle?.RetiredAt,
                lifecycle?.RetiredReason,
                lifecycle?.PermanentlyDeletedAt));
        }
        return result;
    }

    // ---------------------------------------------------------------
    // GET/PUT /api/v1/studio/clients/{clientId}/defaults
    // ---------------------------------------------------------------

    public async Task<StudioHostDefaultsDto?> GetStudioHostDefaultsAsync(string hostId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        await using var connection = await OpenReadyAsync(ct);
        return await ReadStudioHostDefaultsAsync(connection, null, hostId.Trim(), ct);
    }

    public async Task<StudioHostDefaultsDto> UpdateStudioHostDefaultsAsync(
        string hostId, UpdateStudioHostDefaultsRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        if (request.ExpectedVersion < 0)
            throw new ArgumentException("Host defaults expectedVersion cannot be negative.");

        var normalizedHostId = hostId.Trim();
        StudioHostDefaultsDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadStudioHostDefaultsAsync(connection, transaction, normalizedHostId, ct);
            if (existing is null && request.ExpectedVersion != 0)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected host defaults version {request.ExpectedVersion}, but the host has no defaults yet.");
            if (existing is not null && existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected host defaults version {request.ExpectedVersion}, current version is {existing.Version}.");

            var now = UtcNow;
            var nextVersion = existing is null ? 1 : existing.Version + 1;
            var defaultsJson = JsonSerializer.Serialize(request.Defaults ?? new StudioHostDefaultsSettings());
            await ExecuteAsync(connection, """
                INSERT INTO studio_host_defaults(host_id, defaults_json, version, updated_at)
                VALUES ($host, $defaults, $version, $now)
                ON CONFLICT(host_id) DO UPDATE SET
                    defaults_json = excluded.defaults_json,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$host", normalizedHostId), ("$defaults", defaultsJson),
                ("$version", nextVersion), ("$now", Iso(now)));

            updated = new StudioHostDefaultsDto(
                normalizedHostId, request.Defaults ?? new StudioHostDefaultsSettings(), nextVersion, now);
            await AuditAsync(
                connection, transaction, actorId,
                existing is null ? "studio.host-defaults.created" : "studio.host-defaults.updated",
                "host", normalizedHostId,
                JsonSerializer.Serialize(new { request.Defaults, request.ExpectedVersion, updated.Version }),
                ct);
        }, ct);
        return updated!;
    }

    private static async Task<StudioHostDefaultsDto?> ReadStudioHostDefaultsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string hostId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT defaults_json, version, updated_at FROM studio_host_defaults WHERE host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var defaults = JsonSerializer.Deserialize<StudioHostDefaultsSettings>(reader.GetString(0))
            ?? new StudioHostDefaultsSettings();
        return new StudioHostDefaultsDto(hostId, defaults, reader.GetInt64(1), Parse(reader.GetString(2)));
    }

    // ---------------------------------------------------------------
    // POST /api/v1/studio/clients/{clientId}/revive
    //
    // The route list pairs "drain" with "revive". Drain is delegated to the
    // existing RequestOperatorHostDrainAsync, which sets *operator* drain
    // state. The existing ClearAutomaticHostDrainAsync only ever clears
    // *automatic* drain state and throws "host-not-automatically-drained"
    // when there is none - calling it here would make every revive after a
    // drain fail, violating the idempotency requirement that drain/revive
    // round-trip safely. This method closely mirrors
    // ClearAutomaticHostDrainAsync's read-modify idiom but clears both
    // operator and automatic whole-host drain, and is a no-op (not an
    // error) when the host is already open. See the final report for the
    // full explanation of this deliberate deviation from the suggested
    // wiring.
    // ---------------------------------------------------------------

    public async Task<RemoteHostAdmissionDto> ReviveStudioHostAsync(
        string hostId, string? reason, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        var normalizedHostId = hostId.Trim();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var exists = Convert.ToInt32(
                await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM runners WHERE host_id = $host;",
                    ct, transaction, ("$host", normalizedHostId)) ?? 0,
                CultureInfo.InvariantCulture);
            if (exists == 0) throw new KeyNotFoundException("Host was not found.");

            var changed = await ExecuteAsync(connection, """
                UPDATE host_admission
                   SET operator_drain_reason = NULL,
                       operator_drain_at = NULL,
                       automatic_drain_reason = NULL,
                       automatic_drain_at = NULL,
                       updated_at = $now
                 WHERE host_id = $host
                   AND (operator_drain_at IS NOT NULL OR automatic_drain_at IS NOT NULL);
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$host", normalizedHostId));
            if (changed > 0)
                await AuditAsync(
                    connection, transaction, actorId, "studio.host.revived", "host", normalizedHostId,
                    JsonSerializer.Serialize(new { reason }), ct);
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        return await ReadHostAdmissionAsync(read, null, normalizedHostId, ct);
    }

    // ---------------------------------------------------------------
    // POST /api/v1/studio/clients/{clientId}/retire
    // DELETE /api/v1/studio/clients/{clientId}/permanent
    // ---------------------------------------------------------------

    public async Task<StudioHostLifecycleDto?> GetStudioHostLifecycleAsync(string hostId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        await using var connection = await OpenReadyAsync(ct);
        return await ReadStudioHostLifecycleAsync(connection, null, hostId.Trim(), ct);
    }

    public async Task<StudioHostLifecycleDto> RetireStudioHostAsync(
        string hostId, string? reason, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        var normalizedHostId = hostId.Trim();
        StudioHostLifecycleDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadStudioHostLifecycleAsync(connection, transaction, normalizedHostId, ct);
            if (existing?.RetiredAt is not null)
            {
                // Idempotent: already retired, keep the original tombstone.
                result = existing;
                return;
            }

            var now = UtcNow;
            var version = (existing?.Version ?? 0) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO studio_host_lifecycle(
                    host_id, retired_at, retired_reason, permanently_deleted_at, version, updated_at)
                VALUES ($host, $retiredAt, $reason, $deleted, $version, $now)
                ON CONFLICT(host_id) DO UPDATE SET
                    retired_at = excluded.retired_at,
                    retired_reason = excluded.retired_reason,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$host", normalizedHostId), ("$retiredAt", Iso(now)), ("$reason", reason),
                ("$deleted", existing?.PermanentlyDeletedAt is null ? null : Iso(existing.PermanentlyDeletedAt.Value)),
                ("$version", version), ("$now", Iso(now)));

            result = new StudioHostLifecycleDto(
                normalizedHostId, now, reason, existing?.PermanentlyDeletedAt, version, now);
            await AuditAsync(
                connection, transaction, actorId, "studio.host.retired", "host", normalizedHostId,
                JsonSerializer.Serialize(new { reason }), ct);
        }, ct);
        return result!;
    }

    public async Task<StudioHostLifecycleDto> PermanentlyDeleteStudioHostAsync(
        string hostId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        var normalizedHostId = hostId.Trim();
        StudioHostLifecycleDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadStudioHostLifecycleAsync(connection, transaction, normalizedHostId, ct);
            if (existing?.PermanentlyDeletedAt is not null)
            {
                // Idempotent: already tombstoned.
                result = existing;
                return;
            }

            var now = UtcNow;
            var retiredAt = existing?.RetiredAt ?? now;
            var retiredReason = existing?.RetiredReason ?? "permanently-deleted";
            var version = (existing?.Version ?? 0) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO studio_host_lifecycle(
                    host_id, retired_at, retired_reason, permanently_deleted_at, version, updated_at)
                VALUES ($host, $retiredAt, $reason, $deleted, $version, $now)
                ON CONFLICT(host_id) DO UPDATE SET
                    retired_at = excluded.retired_at,
                    retired_reason = excluded.retired_reason,
                    permanently_deleted_at = excluded.permanently_deleted_at,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$host", normalizedHostId), ("$retiredAt", Iso(retiredAt)), ("$reason", retiredReason),
                ("$deleted", Iso(now)), ("$version", version), ("$now", Iso(now)));

            // A soft, durable tombstone only: runners/runner_capabilities rows
            // for this host are never touched, preserving audit/history.
            result = new StudioHostLifecycleDto(normalizedHostId, retiredAt, retiredReason, now, version, now);
            await AuditAsync(
                connection, transaction, actorId, "studio.host.permanently-deleted", "host", normalizedHostId,
                "{}", ct);
        }, ct);
        return result!;
    }

    private static async Task<StudioHostLifecycleDto?> ReadStudioHostLifecycleAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string hostId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT retired_at, retired_reason, permanently_deleted_at, version, updated_at
              FROM studio_host_lifecycle WHERE host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StudioHostLifecycleDto(
            hostId,
            reader.IsDBNull(0) ? null : Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
            reader.GetInt64(3),
            Parse(reader.GetString(4)));
    }

    private static async Task<Dictionary<string, StudioHostLifecycleDto>> ReadAllStudioHostLifecycleAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, StudioHostLifecycleDto>(StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT host_id, retired_at, retired_reason, permanently_deleted_at, version, updated_at
              FROM studio_host_lifecycle;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var hostId = reader.GetString(0);
            result[hostId] = new StudioHostLifecycleDto(
                hostId,
                reader.IsDBNull(1) ? null : Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                reader.GetInt64(4),
                Parse(reader.GetString(5)));
        }
        return result;
    }

    // ---------------------------------------------------------------
    // POST /api/v1/studio/clients/{clientId}/runner-project-preflights/invalidate
    // ---------------------------------------------------------------

    public async Task<StudioPreflightInvalidateResponse> InvalidateStudioHostPreflightCacheAsync(
        string hostId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        var normalizedHostId = hostId.Trim();
        long generation = 0;
        var invalidatedAt = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO studio_host_preflight_cache(host_id, invalidated_at, generation)
                VALUES ($host, $now, 1)
                ON CONFLICT(host_id) DO UPDATE SET
                    invalidated_at = excluded.invalidated_at,
                    generation = studio_host_preflight_cache.generation + 1;
                """, ct, transaction, ("$host", normalizedHostId), ("$now", Iso(now)));
            generation = Convert.ToInt64(
                await ScalarAsync(
                    connection, "SELECT generation FROM studio_host_preflight_cache WHERE host_id = $host;",
                    ct, transaction, ("$host", normalizedHostId)) ?? 0,
                CultureInfo.InvariantCulture);
            invalidatedAt = now;
            await AuditAsync(
                connection, transaction, actorId, "studio.host.preflight-cache-invalidated",
                "host", normalizedHostId, JsonSerializer.Serialize(new { generation }), ct);
        }, ct);
        return new StudioPreflightInvalidateResponse(normalizedHostId, generation, invalidatedAt);
    }

    // ---------------------------------------------------------------
    // GET /api/v1/studio/clients/{clientId}/telemetry
    // ---------------------------------------------------------------

    public async Task<HostTelemetrySnapshotDto?> GetStudioHostTelemetryAsync(string hostId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        var normalizedHostId = hostId.Trim();
        await using var connection = await OpenReadyAsync(ct);
        var runnerId = Convert.ToString(
            await ScalarAsync(connection, """
                SELECT id FROM runners WHERE host_id = $host ORDER BY last_seen_at DESC LIMIT 1;
                """, ct, ("$host", normalizedHostId)),
            CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(runnerId)) return null;

        var json = Convert.ToString(
            await ScalarAsync(
                connection, "SELECT payload_json FROM runner_telemetry_latest WHERE runner_id = $runner;",
                ct, ("$runner", runnerId)),
            CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(json);
    }

    // ---------------------------------------------------------------
    // POST /api/v1/management/commands
    // ---------------------------------------------------------------

    public async Task<StudioManagementCommandResponse> DispatchStudioManagementCommandAsync(
        StudioManagementCommandRequest request, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            throw new ArgumentException("A management command name is required.");
        var arguments = string.IsNullOrWhiteSpace(request.ArgumentsJson)
            ? new StudioManagementCommandArguments()
            : JsonSerializer.Deserialize<StudioManagementCommandArguments>(request.ArgumentsJson)
                ?? new StudioManagementCommandArguments();
        var hostId = arguments.HostId;

        switch (request.Command)
        {
            case StudioManagementCommands.HostsDrain:
            {
                if (string.IsNullOrWhiteSpace(hostId))
                    throw new ArgumentException("hostId argument is required for hosts.drain.");
                var admission = await RequestOperatorHostDrainAsync(
                    hostId, new OperatorHostDrainRequest(arguments.Reason ?? "management-command"), actorId, ct);
                return new StudioManagementCommandResponse(
                    request.Command, "ok", JsonSerializer.Serialize(admission));
            }
            case StudioManagementCommands.HostsRevive:
            {
                if (string.IsNullOrWhiteSpace(hostId))
                    throw new ArgumentException("hostId argument is required for hosts.revive.");
                var admission = await ReviveStudioHostAsync(hostId, arguments.Reason, actorId, ct);
                return new StudioManagementCommandResponse(
                    request.Command, "ok", JsonSerializer.Serialize(admission));
            }
            case StudioManagementCommands.HostsRetire:
            {
                if (string.IsNullOrWhiteSpace(hostId))
                    throw new ArgumentException("hostId argument is required for hosts.retire.");
                var lifecycle = await RetireStudioHostAsync(hostId, arguments.Reason, actorId, ct);
                return new StudioManagementCommandResponse(
                    request.Command, "ok", JsonSerializer.Serialize(lifecycle));
            }
            case StudioManagementCommands.HostsPermanentDelete:
            {
                if (string.IsNullOrWhiteSpace(hostId))
                    throw new ArgumentException("hostId argument is required for hosts.permanent-delete.");
                var lifecycle = await PermanentlyDeleteStudioHostAsync(hostId, actorId, ct);
                return new StudioManagementCommandResponse(
                    request.Command, "ok", JsonSerializer.Serialize(lifecycle));
            }
            default:
                // Endpoint-level validation against StudioManagementCommands.Known
                // already rejects unrecognized commands before reaching the
                // store; this is defensive in case the store is ever called
                // directly from another host process.
                throw new ArgumentException($"Unknown management command '{request.Command}'.");
        }
    }

    // ---------------------------------------------------------------
    // POST /api/v1/management/remote-hosts/provider-auth
    // ---------------------------------------------------------------

    public async Task<StudioProviderAuthEventDto> RecordStudioProviderAuthEventAsync(
        StudioProviderAuthEventRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.HostId)
            || string.IsNullOrWhiteSpace(request.Provider)
            || string.IsNullOrWhiteSpace(request.Status))
            throw new ArgumentException("Host id, provider, and status are required.");

        var id = StableOrGeneratedId(null, "pae");
        var hostId = request.HostId.Trim();
        var provider = request.Provider.Trim().ToLowerInvariant();
        var status = request.Status.Trim().ToLowerInvariant();
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_provider_auth_events(id, host_id, provider, status, detail_json, created_at)
                VALUES ($id, $host, $provider, $status, $detail, $now);
                """, ct, transaction,
                ("$id", id), ("$host", hostId), ("$provider", provider),
                ("$status", status), ("$detail", request.DetailJson), ("$now", Iso(now)));
            await AuditAsync(
                connection, transaction, actorId, "studio.provider-auth.recorded", "host", hostId,
                JsonSerializer.Serialize(new { provider, status }), ct);
        }, ct);
        return new StudioProviderAuthEventDto(id, hostId, provider, status, request.DetailJson, now);
    }
}
