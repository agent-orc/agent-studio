using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task RecordAttemptExecutorReceiptAsync(string runId,
        AttemptExecutorReceiptRequest receipt, CancellationToken ct)
    {
        RequireCredentialCanary();
        if (receipt.Executor is not ("container" or "process")
            || receipt.CleanupStatus is not ("running" or "verified" or "failed")
            || receipt.GrantHalfCores is < 0 or > 512
            || receipt.ImageDigest is { } digest &&
                !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)
            || receipt.CacheOutcome.Length > 80
            || !Regex.IsMatch(receipt.CacheOutcome, "^[a-z ,:-]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid executor receipt metadata.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await using var command = Command(connection, """
                SELECT l.lease_id, l.fence, l.runner_id, l.instance_id, l.status,
                       r.host_id, r.status
                  FROM leases l JOIN runners r ON r.id = l.runner_id
                 WHERE l.run_id = $run;
                """, transaction, ("$run", runId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)
                || reader.GetString(0) != receipt.LeaseId
                || reader.GetInt64(1) != receipt.Fence
                || reader.GetString(2) != receipt.RunnerId
                || reader.GetString(3) != receipt.InstanceId)
                throw new TaskServerConflictException("stale-fence", "Executor receipt does not match the attempt authority.");
            var leaseStatus = reader.GetString(4);
            await reader.CloseAsync();
            if (receipt.CleanupStatus == "running" && leaseStatus != "active")
                throw new TaskServerConflictException("stale-fence", "Only an active attempt can report a running executor.");
            await ExecuteAsync(connection, """
                INSERT INTO attempt_executor_receipts(run_id, lease_id, fence, executor,
                    image_digest, grant_half_cores, cache_outcome, cleanup_status, started_at, recorded_at)
                VALUES ($run, $lease, $fence, $executor, $image, $grant, $cache, $cleanup, $start, $now)
                ON CONFLICT(run_id) DO UPDATE SET
                    image_digest = excluded.image_digest,
                    grant_half_cores = excluded.grant_half_cores,
                    cache_outcome = excluded.cache_outcome,
                    cleanup_status = excluded.cleanup_status,
                    recorded_at = excluded.recorded_at
                WHERE attempt_executor_receipts.lease_id = excluded.lease_id
                  AND attempt_executor_receipts.fence = excluded.fence
                  AND attempt_executor_receipts.cleanup_status != 'verified'
                  AND (attempt_executor_receipts.cleanup_status = 'running'
                       OR excluded.cleanup_status != 'running');
                """, ct, transaction, ("$run", runId), ("$lease", receipt.LeaseId),
                ("$fence", receipt.Fence), ("$executor", receipt.Executor),
                ("$image", receipt.ImageDigest), ("$grant", receipt.GrantHalfCores),
                ("$cache", receipt.CacheOutcome), ("$cleanup", receipt.CleanupStatus),
                ("$start", Iso(receipt.StartedAt)), ("$now", Iso(UtcNow)));
        }, ct);
    }

    public async Task<AttemptVisibilitySnapshotDto> GetAttemptVisibilityAsync(CancellationToken ct)
    {
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT l.run_id, p.name, t.task_key, r.host_id, l.lease_id, l.fence,
                   l.status, l.acquired_at, l.expires_at, x.executor, x.image_digest,
                   x.grant_half_cores, x.cache_outcome, x.cleanup_status,
                   s.grant_half_cores, h.payload_json
              FROM leases l JOIN runs a ON a.id = l.run_id
              JOIN tasks t ON t.id = l.task_id JOIN projects p ON p.id = t.project_id
              JOIN runners r ON r.id = l.runner_id
              LEFT JOIN attempt_executor_receipts x ON x.run_id = l.run_id
              LEFT JOIN host_placement_advertisements h ON h.host_id = r.host_id
                  AND h.runner_id = l.runner_id
              LEFT JOIN placement_shadow_receipts s ON s.id = (
                  SELECT MAX(id) FROM placement_shadow_receipts
                   WHERE lease_id = l.lease_id AND fence = l.fence)
             ORDER BY l.acquired_at DESC LIMIT 200;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var attempts = new List<AttemptVisibilityDto>();
        while (await reader.ReadAsync(ct))
        {
            var leaseStatus = reader.GetString(6);
            var expiry = Parse(reader.GetString(8));
            var stale = leaseStatus == "active" && expiry <= now;
            var cleanup = reader.IsDBNull(13) ? "unreported" : reader.GetString(13);
            var occupancy = cleanup == "verified" ? "released"
                : cleanup == "failed" || stale || cleanup == "running" && leaseStatus != "active"
                    ? "orphan" : leaseStatus == "active" ? "occupied" : "released";
            var grant = reader.IsDBNull(11) ? reader.IsDBNull(14) ? (int?)null : reader.GetInt32(14)
                : reader.GetInt32(11);
            var advertised = reader.IsDBNull(15) ? null
                : JsonSerializer.Deserialize<HostPlacementAdvertisement>(reader.GetString(15));
            var executor = !reader.IsDBNull(9) ? reader.GetString(9)
                : advertised?.OperatingSystem == "windows" ? "process (advertised)" : "unreported";
            attempts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), "coding", executor,
                reader.GetString(4), reader.GetInt64(5), stale ? "stale" : leaseStatus,
                Parse(reader.GetString(7)), expiry,
                reader.IsDBNull(10) ? null : reader.GetString(10), grant,
                reader.IsDBNull(11) ? reader.IsDBNull(14) ? "unreported" : "placement shadow"
                    : "executor receipt",
                reader.IsDBNull(12) ? "unreported" : reader.GetString(12), cleanup,
                occupancy, reader.IsDBNull(9) ? advertised is null ? "Task Server lease"
                    : "Task Server lease + host advertisement" : "Task Server lease + host receipt"));
        }
        return new(now, attempts);
    }
}
