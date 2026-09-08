using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<ResultFinalizationDto> FinalizeResultAsync(
        string runId,
        ResultFinalizationRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (request.Attempt <= 0)
            throw new ArgumentException("Result-finalization attempt must be positive.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Result-finalization idempotency key is required.");

        ResultFinalizationDto? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                        ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(
                lease,
                request.RunnerId,
                request.InstanceId,
                request.LeaseId,
                request.Fence);
            await EnsureOutboxLeaseCurrentAsync(
                connection,
                transaction,
                lease,
                request.RunnerId,
                request.InstanceId,
                request.LeaseId,
                ct);

            var existing = await ReadResultFinalizationAsync(
                connection,
                transaction,
                runId,
                ct);
            if (existing is { Status: ResultFinalizationStatus.Ready or ResultFinalizationStatus.Degraded })
            {
                response = existing;
                return;
            }
            if (existing is not null && request.Attempt <= existing.Attempt)
            {
                response = existing;
                return;
            }
            var expectedAttempt = (existing?.Attempt ?? 0) + 1;
            if (request.Attempt != expectedAttempt)
            {
                throw new TaskServerConflictException(
                    "result-finalization-attempt-gap",
                    $"Expected Result-finalization attempt {expectedAttempt}, received {request.Attempt}.");
            }

            var maxAttempts = Math.Clamp(_options.ResultFinalizationMaxAttempts, 1, 10);
            var (task, run) = await ReadResultSummarySubjectAsync(
                connection,
                transaction,
                runId,
                ct);
            var events = await ReadResultSummaryEventsAsync(
                connection,
                transaction,
                runId,
                ct);
            var artifacts = await ReadResultSummaryArtifactsAsync(
                connection,
                transaction,
                runId,
                ct);

            ResultSummaryGeneration generated;
            try
            {
                generated = await _resultSummaries.GenerateAsync(
                    new ResultSummaryContext(task, run, events, artifacts),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                generated = ResultSummaryGeneration.Failure(exception.Message);
            }

            var now = UtcNow;
            string? artifactId = null;
            string? artifactSha256 = null;
            string? error = null;
            ResultFinalizationStatus status;
            if (generated.Succeeded && !string.IsNullOrWhiteSpace(generated.Markdown))
            {
                status = ResultFinalizationStatus.Ready;
                artifactId = DeterministicId("art", $"{runId}:status.md");
                var content = Encoding.UTF8.GetBytes(generated.Markdown);
                artifactSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                await ExecuteAsync(connection, """
                    INSERT INTO artifacts(
                        id, run_id, name, media_type, sha256, content, size_bytes,
                        idempotency_key, fence, created_at, sequence)
                    VALUES (
                        $id, $run, 'status.md', 'text/markdown', $sha, $content, $size,
                        $key, $fence, $now, NULL)
                    ON CONFLICT(id) DO UPDATE SET
                        sha256 = excluded.sha256,
                        content = excluded.content,
                        size_bytes = excluded.size_bytes,
                        created_at = excluded.created_at;
                    """, ct, transaction,
                    ("$id", artifactId),
                    ("$run", runId),
                    ("$sha", artifactSha256),
                    ("$content", content),
                    ("$size", content.LongLength),
                    ("$key", $"result-finalization:{runId}:status.md"),
                    ("$fence", request.Fence),
                    ("$now", Iso(now)));
            }
            else
            {
                error = LimitError(generated.Error);
                status = request.Attempt >= maxAttempts
                    ? ResultFinalizationStatus.Degraded
                    : ResultFinalizationStatus.Retryable;
            }

            await ExecuteAsync(connection, """
                INSERT INTO result_finalizations(
                    run_id, status, attempt_count, max_attempts, artifact_id,
                    artifact_sha256, error, last_idempotency_key, updated_at)
                VALUES (
                    $run, $status, $attempt, $max, $artifact, $sha, $error, $key, $now)
                ON CONFLICT(run_id) DO UPDATE SET
                    status = excluded.status,
                    attempt_count = excluded.attempt_count,
                    max_attempts = excluded.max_attempts,
                    artifact_id = excluded.artifact_id,
                    artifact_sha256 = excluded.artifact_sha256,
                    error = excluded.error,
                    last_idempotency_key = excluded.last_idempotency_key,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$run", runId),
                ("$status", status.ToString()),
                ("$attempt", request.Attempt),
                ("$max", maxAttempts),
                ("$artifact", artifactId),
                ("$sha", artifactSha256),
                ("$error", error),
                ("$key", request.IdempotencyKey),
                ("$now", Iso(now)));

            var eventKind = status switch
            {
                ResultFinalizationStatus.Ready => LifecycleEventKinds.ResultFinalizationReady,
                ResultFinalizationStatus.Degraded => LifecycleEventKinds.ResultFinalizationDegraded,
                _ => LifecycleEventKinds.ResultFinalizationRetryable,
            };
            await ExecuteAsync(connection, """
                INSERT INTO events(
                    event_id, run_id, task_id, kind, payload_json,
                    idempotency_key, fence, occurred_at)
                VALUES (
                    $event, $run, $task, $kind, $payload,
                    $key, $fence, $now)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """, ct, transaction,
                ("$event", DeterministicId("evt", request.IdempotencyKey)),
                ("$run", runId),
                ("$task", task.TaskId),
                ("$kind", eventKind),
                ("$payload", JsonSerializer.Serialize(new
                {
                    status = status.ToString(),
                    attempt = request.Attempt,
                    maxAttempts,
                    artifactId,
                    artifactSha256,
                    error,
                })),
                ("$key", $"result-finalization-event:{request.IdempotencyKey}"),
                ("$fence", request.Fence),
                ("$now", Iso(now)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                status == ResultFinalizationStatus.Ready
                    ? "result-finalization.ready"
                    : status == ResultFinalizationStatus.Degraded
                        ? "result-finalization.degraded"
                        : "result-finalization.retryable",
                "run",
                runId,
                JsonSerializer.Serialize(new
                {
                    status = status.ToString(),
                    attempt = request.Attempt,
                    maxAttempts,
                    artifactId,
                    artifactSha256,
                    error,
                }),
                ct);

            response = new ResultFinalizationDto(
                runId,
                status,
                request.Attempt,
                maxAttempts,
                artifactId,
                artifactSha256,
                error,
                now);
        }, ct);
        return response!;
    }

    private static async Task<ResultFinalizationDto?> ReadResultFinalizationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run_id, status, attempt_count, max_attempts, artifact_id,
                   artifact_sha256, error, updated_at
              FROM result_finalizations
             WHERE run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadResultFinalization(reader);
    }

    private static ResultFinalizationDto ReadResultFinalization(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Enum.TryParse<ResultFinalizationStatus>(reader.GetString(1), true, out var status)
                ? status
                : ResultFinalizationStatus.None,
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            Parse(reader.GetString(7)));

    private static async Task<(TaskDto Task, RunDto Run)> ReadResultSummarySubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT t.id, t.project_id, t.task_key, t.title, t.state, t.version,
                   t.created_at, t.updated_at, t.body, t.archive_state, t.archived_at,
                   r.id, r.task_id, r.status, r.runner_id, r.fence,
                   r.created_at, r.started_at, r.finished_at
              FROM runs r
              JOIN tasks t ON t.id = r.task_id
             WHERE r.id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Run was not found.");
        var task = ReadTask(reader);
        var run = new RunDto(
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15),
            Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : Parse(reader.GetString(18)));
        return (task, run);
    }

    private static async Task<IReadOnlyList<EventDto>> ReadResultSummaryEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT cursor, event_id, run_id, task_id, kind, payload_json,
                   idempotency_key, fence, occurred_at, sequence
              FROM events
             WHERE run_id = $run
             ORDER BY cursor;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<EventDto>();
        while (await reader.ReadAsync(ct)) events.Add(ReadEvent(reader));
        return events;
    }

    private static async Task<IReadOnlyList<ArtifactDto>> ReadResultSummaryArtifactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, run_id, name, media_type, sha256, size_bytes,
                   idempotency_key, fence, created_at, sequence
              FROM artifacts
             WHERE run_id = $run
             ORDER BY created_at, id;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var artifacts = new List<ArtifactDto>();
        while (await reader.ReadAsync(ct)) artifacts.Add(ReadArtifact(reader));
        return artifacts;
    }

    private static string LimitError(string? error)
    {
        var value = string.IsNullOrWhiteSpace(error)
            ? "Result summary generation returned no document."
            : error.Trim();
        return value.Length <= 1000 ? value : value[..1000];
    }
}
