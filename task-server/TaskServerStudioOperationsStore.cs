using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// The fenced Runner-operation dispatch ledger. <c>studio_operations</c> is
/// both the claim queue a Runner polls and the durable projection every P2
/// GET route reads once an operation completes; there is no separate
/// per-feature results table. A studio operation carries its own lease and
/// fence, disjoint from <c>leases</c>/<c>fence_counters</c>, because it is
/// never a task run: it has no branch, no push, and it does not occupy a
/// board lane.
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioOperationsMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_operations(
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                project_id TEXT,
                task_id TEXT,
                status TEXT NOT NULL,
                request_json TEXT NOT NULL,
                result_json TEXT,
                error TEXT,
                trigger_tag TEXT,
                severity TEXT,
                topic TEXT,
                runner_id TEXT,
                lease_id TEXT,
                fence INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                claimed_at TEXT,
                completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS studio_operation_events(
                cursor INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL REFERENCES studio_operations(id),
                occurred_at TEXT NOT NULL,
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_operation_artifacts(
                id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL REFERENCES studio_operations(id),
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                content_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_operations_status_created
                ON studio_operations(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_studio_operations_project_kind_completed
                ON studio_operations(project_id, kind, completed_at);
            CREATE INDEX IF NOT EXISTS ix_studio_operation_events_operation
                ON studio_operation_events(operation_id, cursor);
            CREATE INDEX IF NOT EXISTS ix_studio_operation_artifacts_operation
                ON studio_operation_artifacts(operation_id);
            """, ct);
    }

    /// <summary>
    /// Creates a pending operation and returns it. Callers that expose a P2
    /// POST action route should return <see cref="StudioOperationAcceptedResponse"/>
    /// built from the result, never block for completion.
    /// </summary>
    public async Task<StudioOperationDto> CreateStudioOperationAsync(
        string kind,
        string? projectId,
        string? taskId,
        object request,
        string? trigger,
        string? severity,
        string? topic,
        CancellationToken ct)
    {
        RequireWritable();
        var id = $"sop_{Guid.NewGuid():N}";
        var now = Iso(UtcNow);
        var requestJson = JsonSerializer.Serialize(request);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_operations(
                id, kind, project_id, task_id, status, request_json, trigger_tag, severity, topic,
                fence, created_at, updated_at)
            VALUES ($id, $kind, $project, $task, $status, $request, $trigger, $severity, $topic, 0, $now, $now);
            """, ct,
            ("$id", id), ("$kind", kind), ("$project", projectId), ("$task", taskId),
            ("$status", StudioOperationStatuses.Pending), ("$request", requestJson),
            ("$trigger", trigger), ("$severity", severity), ("$topic", topic), ("$now", now));
        return (await GetStudioOperationAsync(id, ct))!;
    }

    public async Task<StudioOperationDto?> GetStudioOperationAsync(string operationId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, SelectOperationSql + " WHERE id = $id;", ("$id", operationId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadOperation(reader) : null;
    }

    public async Task<StudioOperationDto> RequireStudioOperationAsync(string operationId, CancellationToken ct)
        => await GetStudioOperationAsync(operationId, ct) ?? throw new KeyNotFoundException($"Studio operation '{operationId}' was not found.");

    /// <summary>
    /// Reads the durable projection for a P2 route: the most recent
    /// completed operations of one kind for a project, newest first,
    /// optionally filtered the same way the legacy "reports" endpoints were
    /// (severity, topic, trigger).
    /// </summary>
    public async Task<IReadOnlyList<StudioOperationDto>> ListCompletedStudioOperationsAsync(
        string kind, string? projectId, string? severity, string? topic, string? trigger, int limit, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var sql = SelectOperationSql + """
             WHERE kind = $kind AND status = $status
               AND ($project IS NULL OR project_id = $project)
               AND ($severity IS NULL OR severity = $severity)
               AND ($topic IS NULL OR topic = $topic)
               AND ($trigger IS NULL OR trigger_tag = $trigger)
             ORDER BY completed_at DESC
             LIMIT $limit;
            """;
        await using var command = Command(connection, sql,
            ("$kind", kind), ("$status", StudioOperationStatuses.Succeeded), ("$project", projectId),
            ("$severity", severity), ("$topic", topic), ("$trigger", trigger), ("$limit", Math.Clamp(limit, 1, 500)));
        var result = new List<StudioOperationDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadOperation(reader));
        return result;
    }

    public async Task<StudioOperationDto?> GetLatestCompletedStudioOperationAsync(
        string kind, string? projectId, CancellationToken ct)
    {
        var list = await ListCompletedStudioOperationsAsync(kind, projectId, null, null, null, 1, ct);
        return list.Count > 0 ? list[0] : null;
    }

    public async Task CancelStudioOperationAsync(string operationId, CancellationToken ct)
    {
        RequireWritable();
        await using var connection = await OpenReadyAsync(ct);
        var now = Iso(UtcNow);
        var updated = await ExecuteAsync(connection, """
            UPDATE studio_operations
               SET status = $status, updated_at = $now, completed_at = $now
             WHERE id = $id AND status NOT IN ($succeeded, $failed, $canceled);
            """, ct,
            ("$status", StudioOperationStatuses.Canceled), ("$now", now), ("$id", operationId),
            ("$succeeded", StudioOperationStatuses.Succeeded), ("$failed", StudioOperationStatuses.Failed),
            ("$canceled", StudioOperationStatuses.Canceled));
        if (updated == 0 && await GetStudioOperationAsync(operationId, ct) is null)
            throw new KeyNotFoundException($"Studio operation '{operationId}' was not found.");
    }

    public async Task<ClaimStudioOperationResponse> ClaimStudioOperationAsync(
        ClaimStudioOperationRequest request, CancellationToken ct)
    {
        RequireAdmission();
        StudioOperationDto? claimed = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var pendingId = await ScalarAsync(connection, """
                SELECT id FROM studio_operations WHERE status = $pending ORDER BY created_at LIMIT 1;
                """, ct, transaction, ("$pending", StudioOperationStatuses.Pending));
            if (pendingId is null) return;
            var id = (string)pendingId;
            var leaseId = $"sop_lse_{Guid.NewGuid():N}";
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                UPDATE studio_operations
                   SET status = $claimed, runner_id = $runner, lease_id = $lease, fence = fence + 1,
                       claimed_at = $now, updated_at = $now
                 WHERE id = $id AND status = $pending;
                """, ct, transaction,
                ("$claimed", StudioOperationStatuses.Claimed), ("$runner", request.RunnerId), ("$lease", leaseId),
                ("$now", now), ("$id", id), ("$pending", StudioOperationStatuses.Pending));
            await using var command = Command(connection, SelectOperationSql + " WHERE id = $id;", transaction, ("$id", id));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) claimed = ReadOperation(reader);
        }, ct);
        return claimed is null
            ? new ClaimStudioOperationResponse("empty", Message: "No pending studio operation is available.")
            : new ClaimStudioOperationResponse("claimed", claimed);
    }

    public async Task HeartbeatStudioOperationAsync(string operationId, StudioOperationLeaseRequest request, CancellationToken ct)
    {
        var operation = await RequireStudioOperationAsync(operationId, ct);
        ValidateStudioOperationLease(operation, request.RunnerId, request.LeaseId, request.Fence);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, "UPDATE studio_operations SET updated_at = $now WHERE id = $id;", ct,
            ("$now", Iso(UtcNow)), ("$id", operationId));
    }

    public async Task<StudioOperationEventDto> AppendStudioOperationEventAsync(
        string operationId, AppendStudioOperationEventRequest request, CancellationToken ct)
    {
        var operation = await RequireStudioOperationAsync(operationId, ct);
        ValidateStudioOperationLease(operation, request.RunnerId, request.LeaseId, request.Fence);
        StudioOperationEventDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO studio_operation_events(operation_id, occurred_at, kind, payload_json)
                VALUES ($operation, $now, $kind, $payload);
                """, ct, transaction,
                ("$operation", operationId), ("$now", now), ("$kind", request.Kind), ("$payload", request.PayloadJson));
            var cursor = Convert.ToInt64(await ScalarAsync(connection, "SELECT last_insert_rowid();", ct, transaction));
            result = new StudioOperationEventDto(cursor, operationId, Parse(now), request.Kind, request.PayloadJson);
        }, ct);
        return result!;
    }

    public async Task<StudioOperationArtifactDto> AppendStudioOperationArtifactAsync(
        string operationId, AppendStudioOperationArtifactRequest request, CancellationToken ct)
    {
        var operation = await RequireStudioOperationAsync(operationId, ct);
        ValidateStudioOperationLease(operation, request.RunnerId, request.LeaseId, request.Fence);
        var artifactId = $"sopa_{Guid.NewGuid():N}";
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_operation_artifacts(id, operation_id, name, media_type, content_json, created_at)
            VALUES ($id, $operation, $name, $media, $content, $now);
            """, ct,
            ("$id", artifactId), ("$operation", operationId), ("$name", request.Name), ("$media", request.MediaType),
            ("$content", request.ContentJson), ("$now", now));
        return new StudioOperationArtifactDto(artifactId, operationId, request.Name, request.MediaType, request.ContentJson, Parse(now));
    }

    public async Task<StudioOperationDto> CompleteStudioOperationAsync(
        string operationId, CompleteStudioOperationRequest request, CancellationToken ct)
    {
        var operation = await RequireStudioOperationAsync(operationId, ct);
        ValidateStudioOperationLease(operation, request.RunnerId, request.LeaseId, request.Fence);
        var status = request.Outcome switch
        {
            "succeeded" => StudioOperationStatuses.Succeeded,
            "failed" => StudioOperationStatuses.Failed,
            _ => throw new ArgumentException($"Unsupported studio operation outcome '{request.Outcome}'."),
        };
        await using var connection = await OpenReadyAsync(ct);
        var now = Iso(UtcNow);
        await ExecuteAsync(connection, """
            UPDATE studio_operations
               SET status = $status, result_json = $result, error = $error, updated_at = $now, completed_at = $now
             WHERE id = $id;
            """, ct,
            ("$status", status), ("$result", request.ResultJson), ("$error", request.ErrorMessage),
            ("$now", now), ("$id", operationId));
        return (await GetStudioOperationAsync(operationId, ct))!;
    }

    private static void ValidateStudioOperationLease(StudioOperationDto operation, string runnerId, string leaseId, long fence)
    {
        if (!string.Equals(operation.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(operation.LeaseId, leaseId, StringComparison.Ordinal)
            || operation.Fence != fence)
            throw new TaskServerConflictException("stale-fence", "Runner id, lease id, or fence does not match the studio operation's current authority.");
        if (StudioOperationStatuses.Terminal.Contains(operation.Status))
            throw new TaskServerConflictException("operation-already-terminal", $"Studio operation status is '{operation.Status}'.");
    }

    private const string SelectOperationSql = """
        SELECT id, kind, project_id, task_id, status, request_json, result_json, error, trigger_tag, severity, topic,
               runner_id, lease_id, fence, created_at, updated_at, claimed_at, completed_at
          FROM studio_operations
        """;

    private static StudioOperationDto ReadOperation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetInt64(13),
        Parse(reader.GetString(14)),
        Parse(reader.GetString(15)),
        reader.IsDBNull(16) ? null : Parse(reader.GetString(16)),
        reader.IsDBNull(17) ? null : Parse(reader.GetString(17)));
}
