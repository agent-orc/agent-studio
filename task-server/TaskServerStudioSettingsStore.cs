using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Durable per-project Studio settings, urls, and ownership mappings for the
/// P2 bundle. Every mutation is a single upsert against
/// <c>studio_project_settings</c> (one row per project) or its small sibling
/// tables; there is no disk-backed project-settings file in the standalone
/// Task Server.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<ProjectDto> UpdateProjectAsync(string projectIdentity, UpdateProjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (string.IsNullOrWhiteSpace(request.Name)) return project;
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection,
            "UPDATE projects SET name = $name, version = version + 1, updated_at = $now WHERE id = $id;",
            ct, ("$name", request.Name.Trim()), ("$now", Iso(now)), ("$id", project.ProjectId));
        return project with { Name = request.Name.Trim(), Version = project.Version + 1, UpdatedAt = now };
    }

    public async Task DeleteProjectAsync(string projectIdentity, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var taskCount = Convert.ToInt64(await ScalarAsync(
            connection, "SELECT count(*) FROM tasks WHERE project_id = $project;", ct, ("$project", project.ProjectId)));
        if (taskCount > 0)
            throw new TaskServerConflictException("project-has-tasks", "A project with tasks cannot be deleted; archive its tasks first.");
        await ExecuteAsync(connection, """
            DELETE FROM orchestrator_context_turns WHERE context_key IN (
                SELECT context_key FROM orchestrator_contexts WHERE project_id = $project);
            DELETE FROM orchestrator_contexts WHERE project_id = $project;
            DELETE FROM flow_definitions WHERE project_id = $project;
            DELETE FROM host_allowed_projects WHERE project_id = $project;
            DELETE FROM studio_project_settings WHERE project_id = $project;
            DELETE FROM studio_project_urls WHERE project_id = $project;
            DELETE FROM studio_ownership_mappings WHERE project_id = $project;
            DELETE FROM studio_operations WHERE project_id = $project;
            DELETE FROM studio_architecture_elements WHERE project_id = $project;
            DELETE FROM studio_schedules WHERE project_id = $project;
            DELETE FROM studio_watch_paths WHERE project_id = $project;
            DELETE FROM projects WHERE id = $project;
            """, ct, ("$project", project.ProjectId));
    }

    public Task UpdateAutoCommitAsync(string project, bool value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "auto_commit", value ? 1L : 0L, actor, ct);
    public Task UpdateAutoPushStrategyAsync(string project, string value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "auto_push_strategy", value, actor, ct);
    public Task UpdateCliContextModeAsync(string project, string value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "cli_context_mode", value, actor, ct);
    public Task UpdateCliModeAsync(string project, string value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "cli_mode", value, actor, ct);
    public Task UpdateCrashRecoveryEnabledAsync(string project, bool value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "crash_recovery_enabled", value ? 1L : 0L, actor, ct);
    public Task UpdateLaneSortStrategyAsync(string project, string value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "lane_sort_strategy", value, actor, ct);
    public Task UpdateMaxParallelismAsync(string project, int value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "max_parallelism", (long)value, actor, ct);
    public Task UpdateOrchestratorModelAsync(string project, string value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "orchestrator_model", value, actor, ct);
    public Task UpdateQuotaWaitPolicyAsync(string project, string value, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "quota_wait_policy", value, actor, ct);
    public Task UpdatePublishAutomationAsync(string project, PublishAutomationRequest request, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "publish_automation_json", JsonSerializer.Serialize(request), actor, ct);
    public Task SetPickupPausedAsync(string project, bool paused, string actor, CancellationToken ct) =>
        UpsertProjectSettingColumnAsync(project, "pickup_paused", paused ? 1L : 0L, actor, ct);

    public async Task<StudioProjectSettingsDto> GetStudioProjectSettingsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT auto_commit, auto_push_strategy, cli_context_mode, cli_mode, crash_recovery_enabled,
                   lane_sort_strategy, max_parallelism, orchestrator_model, quota_wait_policy, pickup_paused, updated_at
              FROM studio_project_settings WHERE project_id = $project;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new StudioProjectSettingsDto(null, null, null, null, null, null, null, null, null, false, null);
        return new StudioProjectSettingsDto(
            reader.IsDBNull(0) ? null : reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4) != 0,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9) != 0,
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)));
    }

    private async Task UpsertProjectSettingColumnAsync(
        string projectIdentity, string column, object value, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, $"""
            INSERT INTO studio_project_settings(project_id, {column}, updated_at, updated_by)
            VALUES ($project, $value, $now, $actor)
            ON CONFLICT(project_id) DO UPDATE SET
                {column} = excluded.{column}, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
            """, ct, ("$project", project.ProjectId), ("$value", value), ("$now", now), ("$actor", actorId));
    }

    // --- Urls ----------------------------------------------------------------

    public async Task<StudioProjectUrlDto> AddProjectUrlAsync(
        string projectIdentity, CreateProjectUrlRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("A url is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = $"surl_{Guid.NewGuid():N}";
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_project_urls(id, project_id, url, label, created_at, updated_at)
            VALUES ($id, $project, $url, $label, $now, $now);
            """, ct, ("$id", id), ("$project", project.ProjectId), ("$url", request.Url.Trim()),
            ("$label", request.Label), ("$now", Iso(now)));
        return new StudioProjectUrlDto(id, project.ProjectId, request.Url.Trim(), request.Label, now, now);
    }

    public async Task<StudioProjectUrlDto> UpdateProjectUrlAsync(
        string projectIdentity, string urlId, UpdateProjectUrlRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        var rows = await ExecuteAsync(connection, """
            UPDATE studio_project_urls SET url = $url, label = $label, updated_at = $now
             WHERE id = $id AND project_id = $project;
            """, ct, ("$url", request.Url.Trim()), ("$label", request.Label), ("$now", Iso(now)),
            ("$id", urlId), ("$project", project.ProjectId));
        if (rows == 0) throw new KeyNotFoundException("Project url was not found.");
        return new StudioProjectUrlDto(urlId, project.ProjectId, request.Url.Trim(), request.Label, now, now);
    }

    public async Task DeleteProjectUrlAsync(string projectIdentity, string urlId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, "DELETE FROM studio_project_urls WHERE id = $id AND project_id = $project;",
            ct, ("$id", urlId), ("$project", project.ProjectId));
    }

    // --- Ownership mappings ----------------------------------------------------

    public async Task<StudioOwnershipMappingDto> UpsertOwnershipMappingAsync(
        string projectIdentity, string mappingId, UpdateOwnershipMappingRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_ownership_mappings(id, project_id, pattern, owner, updated_at)
            VALUES ($id, $project, $pattern, $owner, $now)
            ON CONFLICT(id) DO UPDATE SET pattern = excluded.pattern, owner = excluded.owner, updated_at = excluded.updated_at;
            """, ct, ("$id", mappingId), ("$project", project.ProjectId), ("$pattern", request.Pattern),
            ("$owner", request.Owner), ("$now", Iso(now)));
        return new StudioOwnershipMappingDto(mappingId, project.ProjectId, request.Pattern, request.Owner, now);
    }

    public async Task<IReadOnlyList<StudioOwnershipMappingDto>> ListOwnershipMappingsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT id, project_id, pattern, owner, updated_at FROM studio_ownership_mappings WHERE project_id = $project ORDER BY pattern;",
            ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioOwnershipMappingDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new StudioOwnershipMappingDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4))));
        return result;
    }

    /// <summary>
    /// The Task-Server-authority half of the legacy project snapshot: project
    /// identity, lane counts, and durable settings. The concept dossier
    /// requires splitting this mixed contract before cutover; working-tree
    /// and other checkout facts stay with the dev-seat connector and are not
    /// duplicated here.
    /// </summary>
    public async Task<StudioProjectSnapshotResponse> GetStudioProjectSnapshotAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT state, count(*) FROM tasks WHERE project_id = $project GROUP BY state;", ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        var settings = await GetStudioProjectSettingsAsync(projectIdentity, ct);
        return new StudioProjectSnapshotResponse(project.ProjectId, project.Name, counts, settings, UtcNow);
    }
}
