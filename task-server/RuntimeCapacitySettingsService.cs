using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Application boundary for centrally managed execution-host capacity.
/// Capacity is host-scoped: every project claimed on a host consumes the same
/// ceiling, while project identity remains an admission/query dimension only.
/// </summary>
public sealed class RuntimeCapacitySettingsService(TaskServerStore store)
{
    public Task<RuntimeCapacitySettingsDto?> GetAsync(
        string hostId,
        CancellationToken cancellationToken = default)
        => store.GetRuntimeCapacitySettingsAsync(hostId, cancellationToken);

    public Task<RuntimeCapacitySettingsDto> UpdateAsync(
        string hostId,
        UpdateRuntimeCapacitySettingsRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
        => store.UpdateRuntimeCapacitySettingsAsync(
            hostId,
            request,
            actorId,
            cancellationToken);
}

public sealed partial class TaskServerStore
{
    public async Task<RuntimeCapacitySettingsDto?> GetRuntimeCapacitySettingsAsync(
        string hostId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        await using var connection = await OpenReadyAsync(ct);
        return await ReadRuntimeCapacitySettingsAsync(
            connection,
            null,
            hostId.Trim(),
            ct);
    }

    public async Task<RuntimeCapacitySettingsDto> UpdateRuntimeCapacitySettingsAsync(
        string hostId,
        UpdateRuntimeCapacitySettingsRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        ValidateRuntimeCapacity(request);
        var normalizedHostId = hostId.Trim();
        RuntimeCapacitySettingsDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadRuntimeCapacitySettingsAsync(
                connection,
                transaction,
                normalizedHostId,
                ct);
            if (existing is null)
            {
                if (request.ExpectedVersion != 0)
                    throw new TaskServerConflictException(
                        "resource-version-mismatch",
                        $"Expected runtime capacity version {request.ExpectedVersion}, but the host has no central policy.");
                var createdAt = UtcNow;
                updated = new RuntimeCapacitySettingsDto(
                    normalizedHostId,
                    request.MaxParallelism,
                    request.TargetLoadPercent,
                    NormalizeRampStrategy(request.RampStrategy),
                    1,
                    createdAt,
                    1);
                await ExecuteAsync(connection, """
                    INSERT INTO runtime_capacity_settings(
                        host_id, max_parallelism, target_load_percent, ramp_strategy,
                        version, updated_at)
                    VALUES ($host, $max, $target, $ramp, 1, $now);
                    """, ct, transaction,
                    ("$host", normalizedHostId),
                    ("$max", request.MaxParallelism),
                    ("$target", request.TargetLoadPercent),
                    ("$ramp", NormalizeRampStrategy(request.RampStrategy)),
                    ("$now", Iso(createdAt)));
                await AuditAsync(
                    connection,
                    transaction,
                    actorId,
                    "runtime-capacity.created",
                    "host",
                    normalizedHostId,
                    JsonSerializer.Serialize(new
                    {
                        request.MaxParallelism,
                        request.TargetLoadPercent,
                        rampStrategy = NormalizeRampStrategy(request.RampStrategy),
                        request.ExpectedVersion,
                        updated.Version,
                    }),
                    ct);
                return;
            }
            if (existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected runtime capacity version {request.ExpectedVersion}, current version is {existing.Version}.");

            var now = UtcNow;
            var affected = await ExecuteAsync(connection, """
                UPDATE runtime_capacity_settings
                   SET max_parallelism = $max,
                       target_load_percent = $target,
                       ramp_strategy = $ramp,
                       version = version + 1,
                       allocation_version = allocation_version + 1,
                       updated_at = $now
                 WHERE host_id = $host AND version = $expected;
                """, ct, transaction,
                ("$max", request.MaxParallelism),
                ("$target", request.TargetLoadPercent),
                ("$ramp", NormalizeRampStrategy(request.RampStrategy)),
                ("$now", Iso(now)),
                ("$host", normalizedHostId),
                ("$expected", request.ExpectedVersion));
            if (affected != 1)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    "Runtime capacity changed since it was read.");

            updated = new RuntimeCapacitySettingsDto(
                normalizedHostId,
                request.MaxParallelism,
                request.TargetLoadPercent,
                NormalizeRampStrategy(request.RampStrategy),
                request.ExpectedVersion + 1,
                now,
                existing.AllocationVersion + 1);
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "runtime-capacity.updated",
                "host",
                normalizedHostId,
                JsonSerializer.Serialize(new
                {
                    request.MaxParallelism,
                    request.TargetLoadPercent,
                    rampStrategy = NormalizeRampStrategy(request.RampStrategy),
                    request.ExpectedVersion,
                    updated.Version,
                }),
                ct);
        }, ct);
        return updated!;
    }

    private static async Task<RuntimeCapacitySettingsDto?> ReadRuntimeCapacitySettingsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT host_id, max_parallelism, target_load_percent, ramp_strategy,
                   version, updated_at, allocation_version
              FROM runtime_capacity_settings
             WHERE host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new RuntimeCapacitySettingsDto(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                Parse(reader.GetString(5)),
                reader.GetInt64(6))
            : null;
    }

    private static async Task<int> CountOccupiedHostSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hostId,
        CancellationToken ct)
        => Convert.ToInt32(
            // One host budget (AGT-3171 D5): live post-work claims share the
            // grant with coding leases.
            await ScalarAsync(connection, """
                SELECT (SELECT COUNT(*)
                          FROM leases l
                          JOIN runners r ON r.id = l.runner_id
                         WHERE r.host_id = $host
                           AND l.status IN ('active', 'process-unknown'))
                     + (SELECT COUNT(*)
                          FROM post_work_attempts
                         WHERE host_id = $host AND state = 'live');
                """, ct, transaction, ("$host", hostId)) ?? 0,
            CultureInfo.InvariantCulture);

    private static void ValidateRuntimeCapacity(
        UpdateRuntimeCapacitySettingsRequest request)
    {
        if (request.MaxParallelism is < 1 or > 256)
            throw new ArgumentException("Runtime capacity maxParallelism must be between 1 and 256.");
        if (request.TargetLoadPercent is < 50 or > 95)
            throw new ArgumentException("Runtime capacity targetLoadPercent must be between 50 and 95.");
        if (request.ExpectedVersion < 0)
            throw new ArgumentException("Runtime capacity expectedVersion cannot be negative.");
        _ = NormalizeRampStrategy(request.RampStrategy);
    }

    private static string NormalizeRampStrategy(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "conservative" => "conservative",
            "balanced" => "balanced",
            "aggressive" => "aggressive",
            _ => throw new ArgumentException(
                "Runtime capacity rampStrategy must be conservative, balanced, or aggressive."),
        };
}

internal static class RuntimeCapacityAdoptionPolicy
{
    public static RuntimeCapacityAdoptionDecision Decide(
        RuntimeCapacitySettingsDto desired,
        int? reportedMaxParallelism,
        long? reportedVersion,
        long? previouslyAppliedVersion)
    {
        var allocationVersion = desired.AllocationVersion > 0
            ? desired.AllocationVersion : desired.Version;
        var confirmsDesired = reportedVersion == allocationVersion
                              && reportedMaxParallelism == desired.MaxParallelism;
        return new RuntimeCapacityAdoptionDecision(
            confirmsDesired,
            confirmsDesired && previouslyAppliedVersion != allocationVersion);
    }
}

internal readonly record struct RuntimeCapacityAdoptionDecision(
    bool ConfirmsDesired,
    bool EmitAudit);
