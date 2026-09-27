using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<ProjectPlacementDto?> GetProjectPlacementAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadProjectPlacementAsync(connection, null, projectId, ct);
    }

    public async Task<IReadOnlyList<ProjectPlacementAdmissionDto>> ListProjectPlacementAdmissionsAsync(
        string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT runner_id, reason, observed_at
              FROM project_placement_admissions
             WHERE project_id = $project
             ORDER BY observed_at DESC, runner_id;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ProjectPlacementAdmissionDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new ProjectPlacementAdmissionDto(
                projectId, reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2))));
        return result;
    }

    public async Task<ProjectPlacementDto> UpdateProjectPlacementAsync(
        string projectId,
        UpdateProjectPlacementRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (request.ExpectedVersion < 0 || request.MaxParallelism is < 1 or > 256)
            throw new ArgumentException("Placement expectedVersion must be nonnegative and maxParallelism must be between 1 and 256.");
        if (request.RequiredCapabilities?.Count > 16)
            throw new ArgumentException("Placement may require at most 16 capabilities.");
        var required = (request.RequiredCapabilities ?? [])
            .Select(NormalizeCapability)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (required.Any(key => key.Length is < 3 or > 80 || !key.Contains(':')
                                || key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ':' and not '-' and not '.')))
            throw new ArgumentException("Placement capability keys must use a bounded category:name form.");
        var pin = string.IsNullOrWhiteSpace(request.PinnedRunnerId)
            ? null : request.PinnedRunnerId.Trim();
        ProjectPlacementDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var projectExists = await ScalarAsync(connection,
                "SELECT 1 FROM projects WHERE id = $project;", ct, transaction, ("$project", projectId));
            if (projectExists is null)
                throw new KeyNotFoundException("Project was not found.");
            if (pin is not null)
            {
                var registeredCapabilities = Convert.ToString(await ScalarAsync(connection, """
                    SELECT capabilities_json FROM runners
                     WHERE id = $runner AND status = 'active';
                    """, ct, transaction, ("$runner", pin)), CultureInfo.InvariantCulture);
                if (registeredCapabilities is null
                    || !(JsonSerializer.Deserialize<string[]>(registeredCapabilities) ?? [])
                        .Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal))
                    throw new ArgumentException("Pinned runner must be an active coding registration.");
            }
            var existing = await ReadProjectPlacementAsync(connection, transaction, projectId, ct);
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected project placement version {request.ExpectedVersion}, current version is {currentVersion}.");
            var version = currentVersion + 1;
            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO project_placements(project_id, required_capabilities_json, pinned_runner_id,
                                               max_parallelism, version, updated_at)
                VALUES ($project, $required, $pin, $parallelism, $version, $now)
                ON CONFLICT(project_id) DO UPDATE SET
                    required_capabilities_json = excluded.required_capabilities_json,
                    pinned_runner_id = excluded.pinned_runner_id,
                    max_parallelism = excluded.max_parallelism,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                DELETE FROM project_placement_admissions WHERE project_id = $project;
                """, ct, transaction,
                ("$project", projectId), ("$required", JsonSerializer.Serialize(required)),
                ("$pin", pin), ("$parallelism", request.MaxParallelism),
                ("$version", version), ("$now", Iso(now)));
            updated = new ProjectPlacementDto(projectId, required, pin, request.MaxParallelism, version, now);
            await AuditAsync(connection, transaction, actorId, "project-placement.updated", "project",
                projectId, JsonSerializer.Serialize(updated), ct);
        }, ct);
        return updated!;
    }

    private static async Task<ProjectPlacementDto?> ReadProjectPlacementAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT required_capabilities_json, pinned_runner_id, max_parallelism, version, updated_at
              FROM project_placements WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ProjectPlacementDto(
            projectId,
            JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? [],
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            Parse(reader.GetString(4)));
    }

    private async Task RecordProjectPlacementAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string runnerId,
        string reason,
        CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO project_placement_admissions(project_id, runner_id, reason, observed_at)
            VALUES ($project, $runner, $reason, $now)
            ON CONFLICT(project_id, runner_id) DO UPDATE SET
                reason = excluded.reason,
                observed_at = excluded.observed_at;
            """, ct, transaction,
            ("$project", projectId), ("$runner", runnerId),
            ("$reason", reason), ("$now", Iso(UtcNow)));

    private static async Task<int> CountProjectLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken ct)
        => Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM leases l
              JOIN tasks t ON t.id = l.task_id
             WHERE t.project_id = $project
               AND l.status IN ('active', 'process-unknown');
            """, ct, transaction, ("$project", projectId)) ?? 0, CultureInfo.InvariantCulture);
}

internal static class ProjectPlacementAdmissionPolicy
{
    public static string? Refusal(
        ProjectPlacementDto placement,
        string runnerId,
        int occupiedProjectSlots,
        int effectiveProjectParallelism,
        bool capacityAcknowledged,
        double? cpuPercent,
        int targetLoadPercent)
    {
        if (placement.PinnedRunnerId is not null
            && !string.Equals(placement.PinnedRunnerId, runnerId, StringComparison.Ordinal))
            return "pinned-runner-mismatch";
        if (occupiedProjectSlots >= effectiveProjectParallelism)
            return "project-concurrency-full";
        if (!capacityAcknowledged)
            return "capacity-version-unacknowledged";
        if (cpuPercent is >= 0 and <= 100 && cpuPercent > targetLoadPercent)
            return "host-load-above-target";
        return null;
    }
}
