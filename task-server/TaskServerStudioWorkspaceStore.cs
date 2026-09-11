using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio P1 "workspace" bundle (G9_Workspace): workspace-wide aggregates
/// (counts, screenshots, token spend) computed live across the whole Task
/// Server data root, plus the CRUD/extension-settings routes the existing
/// <c>workspaces</c> table did not yet have (rename, delete, reorder,
/// autonomy, orchestrator model, settings). None of these methods re-derive
/// project/task/run/runner truth - they only read the existing tables and
/// add narrow extension state of their own.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly TimeSpan WorkspaceMetricsCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Creates every table/column this bundle owns. <c>workspaces</c> itself
    /// is created elsewhere; this only adds the <c>rank</c> column (used by
    /// reorder) defensively, plus two brand new tables:
    /// <c>workspace_studio_settings</c> (per-workspace autonomy/orchestrator
    /// model/settings extension row) and
    /// <c>studio_workspace_metrics_cache</c> (the 60s TTL cache backing the
    /// <c>/cached</c> token routes). The caller is responsible for invoking
    /// this from the main migration method.
    /// </summary>
    internal async Task ApplyStudioWorkspaceMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, "workspaces", "rank", "INTEGER NOT NULL DEFAULT 0", ct);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS workspace_studio_settings(
                workspace_id TEXT PRIMARY KEY REFERENCES workspaces(id),
                autonomy_json TEXT,
                orchestrator_model TEXT,
                settings_json TEXT,
                version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_workspace_metrics_cache(
                cache_key TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                computed_at TEXT NOT NULL
            );
            """, ct);
    }

    // ---------------------------------------------------------------
    // PUT /api/v1/studio/workspaces/{id}
    // DELETE /api/v1/studio/workspaces/{id}
    // POST /api/v1/studio/workspaces/{id}/reorder
    // ---------------------------------------------------------------

    public async Task<WorkspaceDto?> UpdateWorkspaceAsync(
        string workspaceId, UpdateWorkspaceRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Workspace name is required.");
        WorkspaceDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadWorkspaceAsync(connection, transaction, workspaceId, ct);
            if (existing is null) return;
            if (existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected workspace version {request.ExpectedVersion}, current version is {existing.Version}.");

            var now = Iso(UtcNow);
            var name = request.Name.Trim();
            await ExecuteAsync(connection, """
                UPDATE workspaces SET name = $name, version = $version, updated_at = $updated
                 WHERE id = $id AND version = $expected;
                """, ct, transaction,
                ("$name", name), ("$version", existing.Version + 1), ("$updated", now),
                ("$id", workspaceId), ("$expected", request.ExpectedVersion));
            updated = existing with { Name = name, Version = existing.Version + 1, UpdatedAt = Parse(now) };
            await AuditAsync(connection, transaction, actorId, "workspace.updated", "workspace", workspaceId,
                JsonSerializer.Serialize(new { request.ExpectedVersion, updated.Version, name }), ct);
        }, ct);
        return updated;
    }

    public async Task<WorkspaceDeleteResponse?> DeleteWorkspaceAsync(
        string workspaceId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        WorkspaceDeleteResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadWorkspaceAsync(connection, transaction, workspaceId, ct);
            if (existing is null) return;

            var projectCount = Convert.ToInt64(
                await ScalarAsync(
                    connection, "SELECT count(*) FROM projects WHERE workspace_id = $id;",
                    ct, transaction, ("$id", workspaceId)) ?? 0L,
                CultureInfo.InvariantCulture);
            if (projectCount > 0)
                throw new TaskServerConflictException(
                    "workspace-not-empty",
                    $"Workspace '{workspaceId}' still has {projectCount} project(s) and cannot be deleted.");

            await ExecuteAsync(connection, """
                DELETE FROM workspace_studio_settings WHERE workspace_id = $id;
                DELETE FROM workspaces WHERE id = $id;
                """, ct, transaction, ("$id", workspaceId));
            result = new WorkspaceDeleteResponse(workspaceId, true);
            await AuditAsync(connection, transaction, actorId, "workspace.deleted", "workspace", workspaceId, "{}", ct);
        }, ct);
        return result;
    }

    public async Task<ReorderWorkspacesResponse> ReorderWorkspacesAsync(
        string workspaceId, ReorderWorkspacesRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var ids = request.WorkspaceIds ?? [];
        if (ids.Count == 0)
            throw new ArgumentException("At least one workspace id is required to reorder.");
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("Workspace ids in a reorder request must be unique.");

        List<WorkspaceRankDto>? ranks = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            foreach (var id in ids)
            {
                var exists = Convert.ToInt64(
                    await ScalarAsync(
                        connection, "SELECT count(*) FROM workspaces WHERE id = $id;",
                        ct, transaction, ("$id", id)) ?? 0L,
                    CultureInfo.InvariantCulture);
                if (exists == 0)
                    throw new ArgumentException($"Workspace '{id}' does not exist and cannot be included in a reorder.");
            }

            var now = Iso(UtcNow);
            ranks = [];
            for (var index = 0; index < ids.Count; index++)
            {
                await ExecuteAsync(connection, """
                    UPDATE workspaces SET rank = $rank, updated_at = $updated WHERE id = $id;
                    """, ct, transaction, ("$rank", (long)index), ("$updated", now), ("$id", ids[index]));
                ranks.Add(new WorkspaceRankDto(ids[index], index));
            }
            await AuditAsync(connection, transaction, actorId, "workspace.reordered", "workspace", workspaceId,
                JsonSerializer.Serialize(new { order = ids }), ct);
        }, ct);
        return new ReorderWorkspacesResponse(ranks!);
    }

    private static async Task<WorkspaceDto?> ReadWorkspaceAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string workspaceId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, name, version, created_at, updated_at FROM workspaces WHERE id = $id;
            """, transaction, ("$id", workspaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new WorkspaceDto(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            Parse(reader.GetString(3)), Parse(reader.GetString(4)));
    }

    // ---------------------------------------------------------------
    // PUT /api/v1/studio/workspaces/{workspaceId}/autonomy
    // PUT /api/v1/studio/workspaces/{workspaceId}/orchestrator-model
    // GET /api/v1/studio/workspaces/{workspaceId}/settings
    // ---------------------------------------------------------------

    public async Task<WorkspaceStudioSettingsDto> UpdateWorkspaceAutonomyAsync(
        string workspaceId, UpdateWorkspaceAutonomyRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await RequireWorkspaceExistsAsync(workspaceId, ct);
        var autonomyJson = JsonSerializer.Serialize(request.Autonomy ?? new WorkspaceAutonomySettings());
        WorkspaceStudioSettingsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadWorkspaceStudioSettingsRowAsync(connection, transaction, workspaceId, ct);
            ValidateSettingsVersion(existing?.Version ?? 0, request.ExpectedVersion);
            var now = Iso(UtcNow);
            var nextVersion = (existing?.Version ?? 0) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO workspace_studio_settings(workspace_id, autonomy_json, orchestrator_model, settings_json, version, updated_at)
                VALUES ($workspace, $autonomy, NULL, NULL, $version, $now)
                ON CONFLICT(workspace_id) DO UPDATE SET
                    autonomy_json = excluded.autonomy_json,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$workspace", workspaceId), ("$autonomy", autonomyJson), ("$version", nextVersion), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "workspace.autonomy.updated", "workspace", workspaceId,
                JsonSerializer.Serialize(new { request.Autonomy, request.ExpectedVersion, nextVersion }), ct);
            result = await ReadWorkspaceStudioSettingsAsync(connection, transaction, workspaceId, ct);
        }, ct);
        return result!;
    }

    public async Task<WorkspaceStudioSettingsDto> UpdateWorkspaceOrchestratorModelAsync(
        string workspaceId, UpdateWorkspaceOrchestratorModelRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await RequireWorkspaceExistsAsync(workspaceId, ct);
        WorkspaceStudioSettingsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadWorkspaceStudioSettingsRowAsync(connection, transaction, workspaceId, ct);
            ValidateSettingsVersion(existing?.Version ?? 0, request.ExpectedVersion);
            var now = Iso(UtcNow);
            var nextVersion = (existing?.Version ?? 0) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO workspace_studio_settings(workspace_id, autonomy_json, orchestrator_model, settings_json, version, updated_at)
                VALUES ($workspace, NULL, $model, NULL, $version, $now)
                ON CONFLICT(workspace_id) DO UPDATE SET
                    orchestrator_model = excluded.orchestrator_model,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$workspace", workspaceId), ("$model", request.OrchestratorModel), ("$version", nextVersion), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "workspace.orchestrator-model.updated", "workspace", workspaceId,
                JsonSerializer.Serialize(new { request.OrchestratorModel, request.ExpectedVersion, nextVersion }), ct);
            result = await ReadWorkspaceStudioSettingsAsync(connection, transaction, workspaceId, ct);
        }, ct);
        return result!;
    }

    public async Task<WorkspaceStudioSettingsDto> GetWorkspaceStudioSettingsAsync(string workspaceId, CancellationToken ct)
    {
        await RequireWorkspaceExistsAsync(workspaceId, ct);
        await using var connection = await OpenReadyAsync(ct);
        return await ReadWorkspaceStudioSettingsAsync(connection, null, workspaceId, ct);
    }

    private async Task RequireWorkspaceExistsAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("Workspace id is required.");
        await using var connection = await OpenReadyAsync(ct);
        var exists = Convert.ToInt64(
            await ScalarAsync(connection, "SELECT count(*) FROM workspaces WHERE id = $id;", ct, ("$id", workspaceId)) ?? 0L,
            CultureInfo.InvariantCulture);
        if (exists == 0) throw new KeyNotFoundException($"Workspace '{workspaceId}' was not found.");
    }

    private static void ValidateSettingsVersion(long currentVersion, long expectedVersion)
    {
        if (currentVersion != expectedVersion)
            throw new TaskServerConflictException(
                "resource-version-mismatch",
                $"Expected workspace settings version {expectedVersion}, current version is {currentVersion}.");
    }

    private static async Task<(long Version, DateTime UpdatedAt)?> ReadWorkspaceStudioSettingsRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string workspaceId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT version, updated_at FROM workspace_studio_settings WHERE workspace_id = $id;
            """, transaction, ("$id", workspaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), Parse(reader.GetString(1)));
    }

    private async Task<WorkspaceStudioSettingsDto> ReadWorkspaceStudioSettingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string workspaceId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT autonomy_json, orchestrator_model, settings_json, version, updated_at
              FROM workspace_studio_settings WHERE workspace_id = $id;
            """, transaction, ("$id", workspaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new WorkspaceStudioSettingsDto(workspaceId, null, null, null, 0, UtcNow);
        var autonomy = reader.IsDBNull(0)
            ? null
            : JsonSerializer.Deserialize<WorkspaceAutonomySettings>(reader.GetString(0));
        return new WorkspaceStudioSettingsDto(
            workspaceId,
            autonomy,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            Parse(reader.GetString(4)));
    }

    // ---------------------------------------------------------------
    // GET /api/v1/studio/workspace/summary
    // ---------------------------------------------------------------

    public async Task<WorkspaceSummaryDto> GetWorkspaceSummaryAsync(string? workspaceId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var filter = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;

        var projectCount = Convert.ToInt32(
            await ScalarAsync(connection, filter is null
                ? "SELECT count(*) FROM projects;"
                : "SELECT count(*) FROM projects WHERE workspace_id = $ws;",
                ct, filter is null ? [] : [("$ws", filter)]) ?? 0,
            CultureInfo.InvariantCulture);

        var taskCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = Command(connection, filter is null
            ? "SELECT state, count(*) FROM tasks GROUP BY state;"
            : "SELECT t.state, count(*) FROM tasks t JOIN projects p ON p.id = t.project_id WHERE p.workspace_id = $ws GROUP BY t.state;",
            filter is null ? [] : [("$ws", filter)]))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                taskCounts[reader.GetString(0)] = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        }

        var runCount = Convert.ToInt32(
            await ScalarAsync(connection, filter is null
                ? "SELECT count(*) FROM runs;"
                : "SELECT count(*) FROM runs r JOIN tasks t ON t.id = r.task_id JOIN projects p ON p.id = t.project_id WHERE p.workspace_id = $ws;",
                ct, filter is null ? [] : [("$ws", filter)]) ?? 0,
            CultureInfo.InvariantCulture);

        // Runners have no workspace association in the schema (a runner can
        // serve any project/workspace); the active-runner count is therefore
        // always global, regardless of the workspaceId filter.
        var activeRunnerCount = Convert.ToInt32(
            await ScalarAsync(connection, "SELECT count(*) FROM runners WHERE status = 'active';", ct) ?? 0,
            CultureInfo.InvariantCulture);

        return new WorkspaceSummaryDto(filter, projectCount, taskCounts, runCount, activeRunnerCount, UtcNow);
    }

    // ---------------------------------------------------------------
    // GET /api/v1/studio/workspace/screenshots
    // ---------------------------------------------------------------

    public async Task<WorkspaceScreenshotsResponse> GetWorkspaceScreenshotsAsync(
        string? workspaceId, int limit, CancellationToken ct)
    {
        var effectiveLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
        var filter = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, filter is null
            ? """
              SELECT a.id, a.run_id, t.id, p.id, a.name, a.media_type, a.size_bytes, a.created_at
                FROM artifacts a
                JOIN runs r ON r.id = a.run_id
                JOIN tasks t ON t.id = r.task_id
                JOIN projects p ON p.id = t.project_id
               WHERE a.media_type LIKE 'image/%'
               ORDER BY a.created_at DESC
               LIMIT $limit;
              """
            : """
              SELECT a.id, a.run_id, t.id, p.id, a.name, a.media_type, a.size_bytes, a.created_at
                FROM artifacts a
                JOIN runs r ON r.id = a.run_id
                JOIN tasks t ON t.id = r.task_id
                JOIN projects p ON p.id = t.project_id
               WHERE a.media_type LIKE 'image/%' AND p.workspace_id = $ws
               ORDER BY a.created_at DESC
               LIMIT $limit;
              """,
            filter is null ? [("$limit", (long)effectiveLimit)] : [("$ws", filter), ("$limit", (long)effectiveLimit)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkspaceScreenshotDto>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WorkspaceScreenshotDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6), Parse(reader.GetString(7))));
        }
        return new WorkspaceScreenshotsResponse(result);
    }

    // ---------------------------------------------------------------
    // GET /api/v1/studio/workspace/tokens/expensive-jobs (+ /cached)
    // GET /api/v1/studio/workspace/tokens/timeline (+ /cached)
    // ---------------------------------------------------------------

    public async Task<WorkspaceExpensiveJobsResponse> GetWorkspaceExpensiveJobsAsync(
        string? workspaceId, int limit, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ComputeExpensiveJobsAsync(connection, null, workspaceId, limit, ct);
    }

    public async Task<WorkspaceTokenTimelineResponse> GetWorkspaceTokenTimelineAsync(
        string? workspaceId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ComputeTokenTimelineAsync(connection, null, workspaceId, ct);
    }

    public async Task<WorkspaceExpensiveJobsResponse> GetWorkspaceExpensiveJobsCachedAsync(
        string? workspaceId, int limit, CancellationToken ct)
    {
        var effectiveLimit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 200);
        var cacheKey = $"expensive-jobs:{workspaceId ?? "*"}:{effectiveLimit}";
        return await GetOrRefreshCachedAsync(
            cacheKey,
            WorkspaceMetricsCacheTtl,
            (connection, transaction) => ComputeExpensiveJobsAsync(connection, transaction, workspaceId, effectiveLimit, ct),
            ct);
    }

    public async Task<WorkspaceTokenTimelineResponse> GetWorkspaceTokenTimelineCachedAsync(
        string? workspaceId, CancellationToken ct)
    {
        var cacheKey = $"timeline:{workspaceId ?? "*"}";
        return await GetOrRefreshCachedAsync(
            cacheKey,
            WorkspaceMetricsCacheTtl,
            (connection, transaction) => ComputeTokenTimelineAsync(connection, transaction, workspaceId, ct),
            ct);
    }

    /// <summary>
    /// Serves <paramref name="cacheKey"/> from <c>studio_workspace_metrics_cache</c>
    /// when a row exists and is younger than <paramref name="ttl"/>; otherwise
    /// recomputes via <paramref name="computeFunc"/>, persists the fresh
    /// payload, and returns it. This is the one shared code path both
    /// <c>/cached</c> routes call - each live route (<see cref="GetWorkspaceExpensiveJobsAsync"/>,
    /// <see cref="GetWorkspaceTokenTimelineAsync"/>) always recomputes and never
    /// touches this cache table, so cached and live are genuinely separate
    /// code paths rather than one aliasing the other. The recompute runs
    /// inside the same write transaction that persists the cache row, so
    /// <paramref name="computeFunc"/> is handed that transaction explicitly -
    /// SQLite refuses to run a transaction-less command against a connection
    /// that already has a pending local transaction.
    /// </summary>
    private async Task<T> GetOrRefreshCachedAsync<T>(
        string cacheKey,
        TimeSpan ttl,
        Func<SqliteConnection, SqliteTransaction?, Task<T>> computeFunc,
        CancellationToken ct)
    {
        await using (var connection = await OpenReadyAsync(ct))
        {
            await using var command = Command(connection, """
                SELECT payload_json, computed_at FROM studio_workspace_metrics_cache WHERE cache_key = $key;
                """, ("$key", cacheKey));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var payloadJson = reader.GetString(0);
                var computedAt = Parse(reader.GetString(1));
                if (UtcNow - computedAt < ttl)
                    return JsonSerializer.Deserialize<T>(payloadJson)!;
            }
        }

        T fresh = default!;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            fresh = await computeFunc(connection, transaction);
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO studio_workspace_metrics_cache(cache_key, payload_json, computed_at)
                VALUES ($key, $payload, $now)
                ON CONFLICT(cache_key) DO UPDATE SET
                    payload_json = excluded.payload_json,
                    computed_at = excluded.computed_at;
                """, ct, transaction,
                ("$key", cacheKey), ("$payload", JsonSerializer.Serialize(fresh)), ("$now", now));
        }, ct);
        return fresh;
    }

    private async Task<WorkspaceExpensiveJobsResponse> ComputeExpensiveJobsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? workspaceId, int limit, CancellationToken ct)
    {
        var effectiveLimit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 200);
        var filter = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
        await using var command = Command(connection, filter is null
            ? """
              SELECT oc.context_key, oc.kind, oc.project_id, oc.task_id,
                     COALESCE(SUM(t.input_tokens), 0) AS input_tokens,
                     COALESCE(SUM(t.output_tokens), 0) AS output_tokens,
                     COALESCE(SUM(t.cache_read_tokens), 0) AS cache_read_tokens,
                     COALESCE(SUM(t.cache_creation_tokens), 0) AS cache_creation_tokens,
                     MAX(t.created_at) AS last_used_at
                FROM orchestrator_context_turns t
                JOIN orchestrator_contexts oc ON oc.context_key = t.context_key
                JOIN projects p ON p.id = oc.project_id
               GROUP BY oc.context_key, oc.kind, oc.project_id, oc.task_id
               ORDER BY (input_tokens + output_tokens + cache_read_tokens + cache_creation_tokens) DESC
               LIMIT $limit;
              """
            : """
              SELECT oc.context_key, oc.kind, oc.project_id, oc.task_id,
                     COALESCE(SUM(t.input_tokens), 0) AS input_tokens,
                     COALESCE(SUM(t.output_tokens), 0) AS output_tokens,
                     COALESCE(SUM(t.cache_read_tokens), 0) AS cache_read_tokens,
                     COALESCE(SUM(t.cache_creation_tokens), 0) AS cache_creation_tokens,
                     MAX(t.created_at) AS last_used_at
                FROM orchestrator_context_turns t
                JOIN orchestrator_contexts oc ON oc.context_key = t.context_key
                JOIN projects p ON p.id = oc.project_id
               WHERE p.workspace_id = $ws
               GROUP BY oc.context_key, oc.kind, oc.project_id, oc.task_id
               ORDER BY (input_tokens + output_tokens + cache_read_tokens + cache_creation_tokens) DESC
               LIMIT $limit;
              """,
            transaction,
            filter is null
                ? [("$limit", (long)effectiveLimit)]
                : [("$ws", filter), ("$limit", (long)effectiveLimit)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var jobs = new List<WorkspaceExpensiveJobDto>();
        while (await reader.ReadAsync(ct))
        {
            var input = reader.GetInt64(4);
            var output = reader.GetInt64(5);
            var cacheRead = reader.GetInt64(6);
            var cacheCreation = reader.GetInt64(7);
            jobs.Add(new WorkspaceExpensiveJobDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                input, output, cacheRead, cacheCreation,
                input + output + cacheRead + cacheCreation,
                Parse(reader.GetString(8))));
        }
        return new WorkspaceExpensiveJobsResponse(jobs, UtcNow);
    }

    private async Task<WorkspaceTokenTimelineResponse> ComputeTokenTimelineAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? workspaceId, CancellationToken ct)
    {
        var filter = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
        await using var command = Command(connection, filter is null
            ? """
              SELECT substr(t.created_at, 1, 10) AS day,
                     COALESCE(SUM(t.input_tokens), 0),
                     COALESCE(SUM(t.output_tokens), 0),
                     COALESCE(SUM(t.cache_read_tokens), 0),
                     COALESCE(SUM(t.cache_creation_tokens), 0)
                FROM orchestrator_context_turns t
                JOIN orchestrator_contexts oc ON oc.context_key = t.context_key
                JOIN projects p ON p.id = oc.project_id
               GROUP BY day
               ORDER BY day;
              """
            : """
              SELECT substr(t.created_at, 1, 10) AS day,
                     COALESCE(SUM(t.input_tokens), 0),
                     COALESCE(SUM(t.output_tokens), 0),
                     COALESCE(SUM(t.cache_read_tokens), 0),
                     COALESCE(SUM(t.cache_creation_tokens), 0)
                FROM orchestrator_context_turns t
                JOIN orchestrator_contexts oc ON oc.context_key = t.context_key
                JOIN projects p ON p.id = oc.project_id
               WHERE p.workspace_id = $ws
               GROUP BY day
               ORDER BY day;
              """,
            transaction,
            filter is null ? [] : [("$ws", filter)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var buckets = new List<WorkspaceTokenTimelineBucketDto>();
        while (await reader.ReadAsync(ct))
        {
            var input = reader.GetInt64(1);
            var output = reader.GetInt64(2);
            var cacheRead = reader.GetInt64(3);
            var cacheCreation = reader.GetInt64(4);
            buckets.Add(new WorkspaceTokenTimelineBucketDto(
                reader.GetString(0), input, output, cacheRead, cacheCreation,
                input + output + cacheRead + cacheCreation));
        }
        return new WorkspaceTokenTimelineResponse(buckets, UtcNow);
    }
}
