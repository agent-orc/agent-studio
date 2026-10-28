using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<HostPlacementAdvertisement?> GetHostPlacementAsync(string hostId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadHostPlacementAsync(connection, null, hostId, ct);
    }

    public async Task<HostPlacementAdvertisement> UpsertHostPlacementAsync(
        string hostId, UpsertHostPlacementRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(request.RunnerId)
            || string.IsNullOrWhiteSpace(request.InstanceId) || request.ExpectedVersion < 0)
            throw new ArgumentException("Host, runner, instance and nonnegative expected version are required.");
        if (request.OperatingSystem is not ("linux" or "windows")
            || request.Architecture is not ("x64" or "arm64")
            || request.Roles.Count is < 1 or > 4
            || request.Roles.Any(role => role is not ("coding" or "review" or "gate" or "post"))
            || request.Executors.Count is < 1 or > 4
            || request.Executors.Any(executor => executor is not ("container" or "process"))
            || request.ToolchainDigests.Count > 32 || request.ProviderBindings.Count > 32
            || request.RepositoryIds.Count > 256 || request.WarmRepositoryIds.Count > 256
            || request.WarmRepositoryIds.Except(request.RepositoryIds, StringComparer.Ordinal).Any()
            || new[] { request.ToolchainDigests, request.ProviderBindings, request.RepositoryIds }
                .Any(items => items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 160)))
            throw new ArgumentException("Host placement advertisement has invalid or untested capabilities.");
        HostPlacementAdvertisement? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            var registeredHost = Convert.ToString(await ScalarAsync(connection,
                "SELECT host_id FROM runners WHERE id = $runner;", ct, transaction,
                ("$runner", request.RunnerId)), CultureInfo.InvariantCulture);
            if (registeredHost != hostId)
                throw new TaskServerConflictException("host-identity-mismatch", "Runner belongs to a different physical host.");
            foreach (var repositoryId in request.RepositoryIds)
            {
                var projectExists = await ScalarAsync(connection,
                    "SELECT 1 FROM projects WHERE id = $project;", ct, transaction,
                    ("$project", repositoryId));
                if (projectExists is null)
                    throw new ArgumentException("Advertised repository project was not found.");
                var hasRegisteredRepository = await ScalarAsync(connection,
                    "SELECT 1 FROM project_repositories WHERE project_id = $project LIMIT 1;",
                    ct, transaction, ("$project", repositoryId)) is not null;
                if (hasRegisteredRepository && await ScalarAsync(connection, """
                    SELECT 1 FROM project_repository_probes
                     WHERE project_id = $project AND runner_id = $runner AND admitted = 1;
                    """, ct, transaction, ("$project", repositoryId),
                    ("$runner", request.RunnerId)) is null)
                    throw new TaskServerConflictException("repository-proof-required",
                        "Registered repository access requires an admitted runner probe.");
            }
            var current = await ReadHostPlacementAsync(connection, transaction, hostId, ct);
            if ((current?.Version ?? 0) != request.ExpectedVersion)
                throw new TaskServerConflictException("resource-version-mismatch", "Host placement advertisement changed.");
            var now = UtcNow;
            updated = new HostPlacementAdvertisement(hostId, request.RunnerId, request.InstanceId,
                request.Roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.OperatingSystem, request.Architecture,
                request.Executors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.ToolchainDigests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.ProviderBindings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.RepositoryIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.WarmRepositoryIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.ExpectedVersion + 1, now);
            await ExecuteAsync(connection, """
                INSERT INTO host_placement_advertisements(host_id, runner_id, instance_id, payload_json, version, updated_at)
                VALUES ($host, $runner, $instance, $payload, $version, $now)
                ON CONFLICT(host_id) DO UPDATE SET
                    runner_id = excluded.runner_id, instance_id = excluded.instance_id,
                    payload_json = excluded.payload_json, version = excluded.version, updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$host", hostId), ("$runner", request.RunnerId), ("$instance", request.InstanceId),
                ("$payload", JsonSerializer.Serialize(updated)), ("$version", updated.Version),
                ("$now", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "host-placement.advertised", "host",
                hostId, JsonSerializer.Serialize(new { updated.Version, updated.Roles,
                    updated.OperatingSystem, updated.Architecture }), ct);
        }, ct);
        return updated!;
    }

    private static async Task<HostPlacementAdvertisement?> ReadHostPlacementAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string hostId, CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(connection,
            "SELECT payload_json FROM host_placement_advertisements WHERE host_id = $host;",
            ct, transaction, ("$host", hostId)), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(json)
            ? null : JsonSerializer.Deserialize<HostPlacementAdvertisement>(json);
    }

    private async Task<HostPlacementDecision> EvaluateHostPlacementAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string hostId, string runnerId, string instanceId, string repositoryId,
        IReadOnlyList<string> requirements, DateTime queuedAt,
        bool allocationAcknowledged, bool grantAvailable, CancellationToken ct)
    {
        var current = await ReadHostPlacementAsync(connection, transaction, hostId, ct);
        if (current?.RunnerId != runnerId || current?.InstanceId != instanceId) current = null;
        var warmWindow = TimeSpan.FromSeconds(Math.Clamp(
            _options.DynamicPlacementWarmPreferenceSeconds, 0, 300));
        var anotherWarm = false;
        await using var command = Command(connection, """
            SELECT h.payload_json, r.runtime_capacity_applied_version, c.allocation_version,
                   c.max_parallelism
              FROM host_placement_advertisements h
              JOIN runners r ON r.id = h.runner_id AND r.instance_id = h.instance_id
              JOIN runtime_capacity_settings c ON c.host_id = h.host_id
             WHERE h.host_id <> $host AND r.status = 'active'
             LIMIT 100;
            """, transaction, ("$host", hostId));
        var otherHosts = new List<(HostPlacementAdvertisement Host, bool Acknowledged, int Ceiling)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var other = JsonSerializer.Deserialize<HostPlacementAdvertisement>(reader.GetString(0));
                if (other is not null)
                    otherHosts.Add((other,
                        !reader.IsDBNull(1) && reader.GetInt64(1) == reader.GetInt64(2),
                        reader.GetInt32(3)));
            }
        }
        foreach (var other in otherHosts)
        {
            if (!other.Host.WarmRepositoryIds.Contains(repositoryId, StringComparer.Ordinal)) continue;
            var occupied = await CountOccupiedHostGrantsAsync(connection, transaction, other.Host.HostId, ct);
            var verdict = HostPlacementPolicy.Decide(other.Host, "coding", repositoryId,
                requirements, queuedAt, UtcNow, TimeSpan.Zero, false,
                other.Acknowledged, occupied < other.Ceiling);
            if (verdict.Eligible) { anotherWarm = true; break; }
        }
        return HostPlacementPolicy.Decide(current, "coding", repositoryId,
            requirements, queuedAt, UtcNow, warmWindow, anotherWarm,
            allocationAcknowledged, grantAvailable);
    }

    public async Task<IReadOnlyList<PlacementShadowReceipt>> ListPlacementShadowReceiptsAsync(
        string? taskId, int limit, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, task_id, runner_id, host_id, actual_status, shadow_status,
                   matched_json, rejected_json, allocation_version, grant_half_cores,
                   lease_id, fence, authority_epoch, observed_at
              FROM placement_shadow_receipts
             WHERE $task IS NULL OR task_id = $task
             ORDER BY id DESC LIMIT $limit;
            """, ("$task", taskId), ("$limit", Math.Clamp(limit, 1, 500)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<PlacementShadowReceipt>();
        while (await reader.ReadAsync(ct))
            result.Add(new PlacementShadowReceipt(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [],
                reader.GetInt64(8), reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetInt64(12),
                Parse(reader.GetString(13))));
        return result;
    }

    private static Task RecordPlacementShadowAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId,
        string runnerId, string hostId, string actualStatus, HostPlacementDecision shadow,
        long allocationVersion, int grantHalfCores, string? leaseId, long? fence,
        DateTime now, CancellationToken ct)
        => ExecuteAsync(connection, """
            INSERT INTO placement_shadow_receipts(task_id, runner_id, host_id, actual_status,
                shadow_status, matched_json, rejected_json, allocation_version, grant_half_cores,
                lease_id, fence, authority_epoch, observed_at)
            VALUES ($task, $runner, $host, $actual, $shadow, $matched, $rejected,
                $allocation, $grant, $lease, $fence, $epoch, $now);
            """, ct, transaction, ("$task", taskId), ("$runner", runnerId), ("$host", hostId),
            ("$actual", actualStatus), ("$shadow", shadow.Status),
            ("$matched", JsonSerializer.Serialize(shadow.Matched)),
            ("$rejected", JsonSerializer.Serialize(shadow.Rejected)),
            ("$allocation", allocationVersion), ("$grant", grantHalfCores),
            ("$lease", leaseId), ("$fence", fence), ("$epoch", fence ?? 0),
            ("$now", Iso(now)));
}
