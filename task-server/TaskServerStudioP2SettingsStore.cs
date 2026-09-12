using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Global CLI/quota settings, the crash-recovery operator queue, and watch
/// paths (Group G4 of the P2 "operations and insight" bundle). None of these
/// routes are project-scoped:
/// <list type="bullet">
///   <item>CLI/quota settings are one singleton row (<c>id = 1</c>) in
///   <c>studio_cli_settings</c>; each mutation touches exactly one column
///   via <c>INSERT ... ON CONFLICT DO UPDATE</c> and returns the full row.</item>
///   <item>Crash recovery is a standalone durable queue
///   (<c>studio_crash_recovery_pending</c>) rather than a join against
///   <c>runner_reconciliation_actions</c>: that table is an append-only log
///   of actions the invariant reconciliation sweep already took, with no
///   "awaiting operator decision" status column, so it does not carry the
///   pending/committed/dismissed lifecycle this bundle needs.</item>
///   <item>Watch paths (<c>studio_watch_paths</c>) name directories the
///   standalone runner should observe; <c>project_id</c> is an optional
///   reference, not a scoping key.</item>
/// </list>
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioP2SettingsMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_cli_settings(
                id INTEGER PRIMARY KEY CHECK (id = 1),
                economy_mode INTEGER NOT NULL DEFAULT 0,
                quota_caps_json TEXT,
                quota_model_routes_json TEXT,
                quota_wait_policy_json TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_crash_recovery_pending(
                id TEXT PRIMARY KEY,
                project_id TEXT,
                task_id TEXT,
                detected_at TEXT NOT NULL,
                detail_json TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending'
            );
            CREATE INDEX IF NOT EXISTS ix_studio_crash_recovery_pending_status_detected
                ON studio_crash_recovery_pending(status, detected_at);
            CREATE TABLE IF NOT EXISTS studio_watch_paths(
                name TEXT PRIMARY KEY,
                project_id TEXT,
                pattern TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """, ct);
    }

    // ---- Global CLI / quota settings -------------------------------------

    public async Task<CliSettingsDto> SetEconomyModeAsync(
        SetEconomyModeRequest request, string actorId, CancellationToken ct)
        => await UpsertCliSettingAsync(
            """
            INSERT INTO studio_cli_settings(id, economy_mode, quota_caps_json, quota_model_routes_json, quota_wait_policy_json, updated_at)
            VALUES (1, $value, NULL, NULL, NULL, $now)
            ON CONFLICT(id) DO UPDATE SET economy_mode = excluded.economy_mode, updated_at = excluded.updated_at;
            """,
            request.Enabled ? 1L : 0L,
            actorId,
            "studio.cli.economy-mode.updated",
            new { request.Enabled },
            ct);

    public async Task<CliSettingsDto> SetQuotaCapsAsync(
        SetQuotaCapsRequest request, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CapsJson)) throw new ArgumentException("Quota caps JSON is required.");
        return await UpsertCliSettingAsync(
            """
            INSERT INTO studio_cli_settings(id, economy_mode, quota_caps_json, quota_model_routes_json, quota_wait_policy_json, updated_at)
            VALUES (1, 0, $value, NULL, NULL, $now)
            ON CONFLICT(id) DO UPDATE SET quota_caps_json = excluded.quota_caps_json, updated_at = excluded.updated_at;
            """,
            request.CapsJson,
            actorId,
            "studio.cli.quota-caps.updated",
            new { request.CapsJson },
            ct);
    }

    public async Task<CliSettingsDto> SetQuotaModelRoutesAsync(
        SetQuotaModelRoutesRequest request, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelRoutesJson))
            throw new ArgumentException("Quota model routes JSON is required.");
        return await UpsertCliSettingAsync(
            """
            INSERT INTO studio_cli_settings(id, economy_mode, quota_caps_json, quota_model_routes_json, quota_wait_policy_json, updated_at)
            VALUES (1, 0, NULL, $value, NULL, $now)
            ON CONFLICT(id) DO UPDATE SET quota_model_routes_json = excluded.quota_model_routes_json, updated_at = excluded.updated_at;
            """,
            request.ModelRoutesJson,
            actorId,
            "studio.cli.quota-model-routes.updated",
            new { request.ModelRoutesJson },
            ct);
    }

    public async Task<CliSettingsDto> SetQuotaWaitPolicyAsync(
        SetCliQuotaWaitPolicyRequest request, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.WaitPolicyJson))
            throw new ArgumentException("Quota wait policy JSON is required.");
        return await UpsertCliSettingAsync(
            """
            INSERT INTO studio_cli_settings(id, economy_mode, quota_caps_json, quota_model_routes_json, quota_wait_policy_json, updated_at)
            VALUES (1, 0, NULL, NULL, $value, $now)
            ON CONFLICT(id) DO UPDATE SET quota_wait_policy_json = excluded.quota_wait_policy_json, updated_at = excluded.updated_at;
            """,
            request.WaitPolicyJson,
            actorId,
            "studio.cli.quota-wait-policy.updated",
            new { request.WaitPolicyJson },
            ct);
    }

    private async Task<CliSettingsDto> UpsertCliSettingAsync(
        string upsertSql,
        object value,
        string actorId,
        string auditAction,
        object auditDetail,
        CancellationToken ct)
    {
        RequireWritable();
        CliSettingsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, upsertSql, ct, transaction, ("$value", value), ("$now", now));
            result = await ReadCliSettingsAsync(connection, transaction, ct);
            await AuditAsync(connection, transaction, actorId, auditAction, "cli-settings", "singleton",
                JsonSerializer.Serialize(auditDetail), ct);
        }, ct);
        return result!;
    }

    private static async Task<CliSettingsDto> ReadCliSettingsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT economy_mode, quota_caps_json, quota_model_routes_json, quota_wait_policy_json, updated_at
              FROM studio_cli_settings WHERE id = 1;
            """, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The singleton CLI settings row was not created by the upsert.");
        return new CliSettingsDto(
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Parse(reader.GetString(4)));
    }

    // ---- Crash recovery ---------------------------------------------------

    public async Task<IReadOnlyList<CrashRecoveryPendingItemDto>> ListCrashRecoveryPendingAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_id, detected_at, detail_json, status
              FROM studio_crash_recovery_pending
             WHERE status = $status
             ORDER BY detected_at DESC;
            """, ("$status", CrashRecoveryPendingStatuses.Pending));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CrashRecoveryPendingItemDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadCrashRecoveryPendingItem(reader));
        return result;
    }

    public async Task<CrashRecoveryPendingItemDto> CommitCrashRecoveryPendingAsync(
        string id, string actorId, CancellationToken ct)
        => await TransitionCrashRecoveryPendingAsync(
            id, CrashRecoveryPendingStatuses.Committed, actorId, "studio.crash-recovery.committed", ct);

    public async Task<CrashRecoveryPendingItemDto> DismissCrashRecoveryPendingAsync(
        string id, string actorId, CancellationToken ct)
        => await TransitionCrashRecoveryPendingAsync(
            id, CrashRecoveryPendingStatuses.Dismissed, actorId, "studio.crash-recovery.dismissed", ct);

    private async Task<CrashRecoveryPendingItemDto> TransitionCrashRecoveryPendingAsync(
        string id, string newStatus, string actorId, string auditAction, CancellationToken ct)
    {
        RequireWritable();
        CrashRecoveryPendingItemDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            CrashRecoveryPendingItemDto? existing;
            await using (var command = Command(connection, """
                SELECT id, project_id, task_id, detected_at, detail_json, status
                  FROM studio_crash_recovery_pending WHERE id = $id;
                """, transaction, ("$id", id)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                existing = await reader.ReadAsync(ct) ? ReadCrashRecoveryPendingItem(reader) : null;
            if (existing is null)
                throw new KeyNotFoundException($"Crash recovery item '{id}' was not found.");
            if (!string.Equals(existing.Status, CrashRecoveryPendingStatuses.Pending, StringComparison.Ordinal))
                throw new TaskServerConflictException(
                    "crash-recovery-not-pending",
                    $"Crash recovery item '{id}' is '{existing.Status}', not 'pending'.");
            await ExecuteAsync(connection, """
                UPDATE studio_crash_recovery_pending SET status = $status WHERE id = $id;
                """, ct, transaction, ("$status", newStatus), ("$id", id));
            result = existing with { Status = newStatus };
            await AuditAsync(connection, transaction, actorId, auditAction, "crash-recovery-pending", id,
                JsonSerializer.Serialize(new { newStatus }), ct);
        }, ct);
        return result!;
    }

    private static CrashRecoveryPendingItemDto ReadCrashRecoveryPendingItem(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5));

    /// <summary>
    /// Test/operational seam: enqueue a pending crash-recovery item directly,
    /// mirroring how a boot-time sweep (outside this bundle's scope) would
    /// insert evidence for an operator to later commit or dismiss.
    /// </summary>
    internal async Task<CrashRecoveryPendingItemDto> EnqueueCrashRecoveryPendingAsync(
        string? projectId, string? taskId, string detailJson, CancellationToken ct)
    {
        RequireWritable();
        var id = $"crp_{Guid.NewGuid():N}";
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_crash_recovery_pending(id, project_id, task_id, detected_at, detail_json, status)
                VALUES ($id, $project, $task, $detected, $detail, $status);
                """, ct, transaction,
                ("$id", id), ("$project", projectId), ("$task", taskId), ("$detected", Iso(now)),
                ("$detail", detailJson), ("$status", CrashRecoveryPendingStatuses.Pending));
        }, ct);
        return new CrashRecoveryPendingItemDto(id, projectId, taskId, now, detailJson, CrashRecoveryPendingStatuses.Pending);
    }

    // ---- Watch paths --------------------------------------------------------

    public async Task<WatchPathDto> CreateWatchPathAsync(
        CreateWatchPathRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Watch path name is required.");
        if (string.IsNullOrWhiteSpace(request.Pattern)) throw new ArgumentException("Watch path pattern is required.");
        var name = request.Name.Trim();
        var now = UtcNow;
        WatchPathDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ScalarAsync(
                connection, "SELECT 1 FROM studio_watch_paths WHERE name = $name;", ct, transaction, ("$name", name));
            if (existing is not null)
                throw new TaskServerConflictException("watch-path-exists", $"Watch path '{name}' already exists.");
            await ExecuteAsync(connection, """
                INSERT INTO studio_watch_paths(name, project_id, pattern, created_at)
                VALUES ($name, $project, $pattern, $now);
                """, ct, transaction,
                ("$name", name), ("$project", request.ProjectId), ("$pattern", request.Pattern), ("$now", Iso(now)));
            result = new WatchPathDto(name, request.ProjectId, request.Pattern, now);
            await AuditAsync(connection, transaction, actorId, "studio.watch-path.created", "watch-path", name,
                JsonSerializer.Serialize(new { request.ProjectId, request.Pattern }), ct);
        }, ct);
        return result!;
    }

    public async Task DeleteWatchPathAsync(string name, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var affected = await ExecuteAsync(connection,
                "DELETE FROM studio_watch_paths WHERE name = $name;", ct, transaction, ("$name", name));
            if (affected == 0) throw new KeyNotFoundException($"Watch path '{name}' was not found.");
            await AuditAsync(connection, transaction, actorId, "studio.watch-path.deleted", "watch-path", name,
                "{}", ct);
        }, ct);
    }
}
