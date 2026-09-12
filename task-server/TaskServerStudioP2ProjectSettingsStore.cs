using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backing store for the Studio P2 "project settings, project CRUD,
/// ownership mappings, project URLs" bundle. The nine simple per-project
/// toggles live as columns on a single <c>studio_project_settings</c> row
/// per project (read-modify-write via upsert), while ownership mappings and
/// project URLs are small owned child tables keyed by project id. Project
/// rename/delete operate directly on the existing <c>projects</c> table.
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioP2ProjectSettingsMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_project_settings(
                project_id TEXT PRIMARY KEY REFERENCES projects(id),
                auto_commit INTEGER,
                auto_push_strategy TEXT,
                cli_context_mode TEXT,
                cli_mode TEXT,
                crash_recovery_enabled INTEGER,
                lane_sort_strategy TEXT,
                max_parallelism INTEGER,
                orchestrator_model TEXT,
                quota_wait_policy_json TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_project_ownership_mappings(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                pattern TEXT NOT NULL,
                owner TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_project_ownership_mappings_project
                ON studio_project_ownership_mappings(project_id);
            CREATE TABLE IF NOT EXISTS studio_project_urls(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                url TEXT NOT NULL,
                label TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_project_urls_project
                ON studio_project_urls(project_id);
            """, ct);
    }

    public async Task<StudioProjectSettingsDto> SetAutoCommitAsync(
        string projectIdentity, SetAutoCommitRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "auto_commit", request.Enabled ? 1 : 0,
            actorId, "project-settings.auto-commit.updated", ct);

    public async Task<StudioProjectSettingsDto> SetAutoPushStrategyAsync(
        string projectIdentity, SetAutoPushStrategyRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "auto_push_strategy", RequireNonEmpty(request.Strategy, "Auto push strategy"),
            actorId, "project-settings.auto-push-strategy.updated", ct);

    public async Task<StudioProjectSettingsDto> SetCliContextModeAsync(
        string projectIdentity, SetCliContextModeRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "cli_context_mode", RequireNonEmpty(request.Mode, "CLI context mode"),
            actorId, "project-settings.cli-context-mode.updated", ct);

    public async Task<StudioProjectSettingsDto> SetCliModeAsync(
        string projectIdentity, SetCliModeRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "cli_mode", RequireNonEmpty(request.Mode, "CLI mode"),
            actorId, "project-settings.cli-mode.updated", ct);

    public async Task<StudioProjectSettingsDto> SetCrashRecoveryAsync(
        string projectIdentity, SetCrashRecoveryRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "crash_recovery_enabled", request.Enabled ? 1 : 0,
            actorId, "project-settings.crash-recovery.updated", ct);

    public async Task<StudioProjectSettingsDto> SetLaneSortStrategyAsync(
        string projectIdentity, SetLaneSortStrategyRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "lane_sort_strategy", RequireNonEmpty(request.Strategy, "Lane sort strategy"),
            actorId, "project-settings.lane-sort-strategy.updated", ct);

    public async Task<StudioProjectSettingsDto> SetMaxParallelismAsync(
        string projectIdentity, SetMaxParallelismRequest request, string actorId, CancellationToken ct)
    {
        if (request.MaxParallelism < 1)
            throw new ArgumentException("Max parallelism must be at least 1.");
        return await UpsertProjectSettingAsync(
            projectIdentity, "max_parallelism", request.MaxParallelism,
            actorId, "project-settings.max-parallelism.updated", ct);
    }

    public async Task<StudioProjectSettingsDto> SetOrchestratorModelAsync(
        string projectIdentity, SetOrchestratorModelRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "orchestrator_model", RequireNonEmpty(request.Model, "Orchestrator model"),
            actorId, "project-settings.orchestrator-model.updated", ct);

    public async Task<StudioProjectSettingsDto> SetQuotaWaitPolicyAsync(
        string projectIdentity, SetQuotaWaitPolicyRequest request, string actorId, CancellationToken ct)
        => await UpsertProjectSettingAsync(
            projectIdentity, "quota_wait_policy_json", RequireNonEmpty(request.PolicyJson, "Quota wait policy"),
            actorId, "project-settings.quota-wait-policy.updated", ct);

    private static string RequireNonEmpty(string? value, string label)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{label} is required.") : value.Trim();

    private async Task<StudioProjectSettingsDto> UpsertProjectSettingAsync(
        string projectIdentity,
        string column,
        object value,
        string actorId,
        string auditAction,
        CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        StudioProjectSettingsDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, $"""
                INSERT INTO studio_project_settings(project_id, {column}, updated_at)
                VALUES ($project, $value, $now)
                ON CONFLICT(project_id) DO UPDATE SET
                    {column} = excluded.{column},
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$project", project.ProjectId), ("$value", value), ("$now", now));
            await AuditAsync(connection, transaction, actorId, auditAction, "project-settings", project.ProjectId,
                JsonSerializer.Serialize(new { column }), ct);
            result = await ReadProjectSettingsAsync(connection, transaction, project.ProjectId, ct);
        }, ct);
        return result!;
    }

    private static async Task<StudioProjectSettingsDto> ReadProjectSettingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT project_id, auto_commit, auto_push_strategy, cli_context_mode, cli_mode,
                   crash_recovery_enabled, lane_sort_strategy, max_parallelism, orchestrator_model,
                   quota_wait_policy_json, updated_at
              FROM studio_project_settings
             WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new StudioProjectSettingsDto(
                projectId, false, "manual", "full", "interactive", false, "manual", 1, "default", null, DateTime.UtcNow);
        }
        return new StudioProjectSettingsDto(
            reader.GetString(0),
            !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
            reader.IsDBNull(2) ? "manual" : reader.GetString(2),
            reader.IsDBNull(3) ? "full" : reader.GetString(3),
            reader.IsDBNull(4) ? "interactive" : reader.GetString(4),
            !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? "manual" : reader.GetString(6),
            reader.IsDBNull(7) ? 1 : (int)reader.GetInt64(7),
            reader.IsDBNull(8) ? "default" : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            Parse(reader.GetString(10)));
    }

    public async Task<ProjectDto> UpdateProjectAsync(
        string projectIdentity, UpdateStudioProjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var name = string.IsNullOrWhiteSpace(request.Name) ? project.Name : request.Name.Trim();
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                UPDATE projects
                   SET name = $name, version = version + 1, updated_at = $now
                 WHERE id = $id;
                """, ct, transaction, ("$name", name), ("$now", now), ("$id", project.ProjectId));
            await AuditAsync(connection, transaction, actorId, "project.updated", "project", project.ProjectId,
                JsonSerializer.Serialize(new { name }), ct);
        }, ct);
        return project with { Name = name, Version = project.Version + 1, UpdatedAt = Parse(now) };
    }

    public async Task DeleteProjectAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var taskCount = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT COUNT(*) FROM tasks WHERE project_id = $project;",
                ct, transaction, ("$project", project.ProjectId)));
            if (taskCount > 0)
                throw new TaskServerConflictException(
                    "project-has-tasks", "A project with tasks cannot be deleted; delete or archive its tasks first.");
            // The project row is referenced by foreign keys from its
            // orchestrator project-chat context, its flow definition, and
            // this group's own settings/ownership-mapping/URL tables; all
            // must be cleared before the project row itself can be deleted.
            await ExecuteAsync(connection, """
                DELETE FROM orchestrator_context_turns WHERE context_key IN (
                    SELECT context_key FROM orchestrator_contexts WHERE project_id = $project);
                DELETE FROM orchestrator_contexts WHERE project_id = $project;
                DELETE FROM flow_definitions WHERE project_id = $project;
                DELETE FROM studio_project_settings WHERE project_id = $project;
                DELETE FROM studio_project_ownership_mappings WHERE project_id = $project;
                DELETE FROM studio_project_urls WHERE project_id = $project;
                DELETE FROM projects WHERE id = $project;
                """, ct, transaction, ("$project", project.ProjectId));
            await AuditAsync(connection, transaction, actorId, "project.deleted", "project", project.ProjectId, "{}", ct);
        }, ct);
    }

    public async Task<ProjectOwnershipMappingDto> UpsertProjectOwnershipMappingAsync(
        string projectIdentity,
        string mappingId,
        UpsertProjectOwnershipMappingRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(mappingId)) throw new ArgumentException("Mapping id is required.");
        if (string.IsNullOrWhiteSpace(request.Pattern) || string.IsNullOrWhiteSpace(request.Owner))
            throw new ArgumentException("Ownership mapping pattern and owner are required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = mappingId.Trim();
        var pattern = request.Pattern.Trim();
        var owner = request.Owner.Trim();
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_project_ownership_mappings(id, project_id, pattern, owner, updated_at)
                VALUES ($id, $project, $pattern, $owner, $now)
                ON CONFLICT(id) DO UPDATE SET
                    project_id = excluded.project_id,
                    pattern = excluded.pattern,
                    owner = excluded.owner,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$pattern", pattern), ("$owner", owner), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "project.ownership-mapping.upserted",
                "project-ownership-mapping", id,
                JsonSerializer.Serialize(new { project.ProjectId, pattern, owner }), ct);
        }, ct);
        return new ProjectOwnershipMappingDto(id, project.ProjectId, pattern, owner, Parse(now));
    }

    public async Task<ProjectUrlDto> CreateProjectUrlAsync(
        string projectIdentity, CreateProjectUrlRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("Project URL is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = $"purl_{Guid.NewGuid():N}";
        var url = request.Url.Trim();
        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_project_urls(id, project_id, url, label, created_at, updated_at)
                VALUES ($id, $project, $url, $label, $now, $now);
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$url", url), ("$label", label), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "project.url.created", "project-url", id,
                JsonSerializer.Serialize(new { project.ProjectId, url, label }), ct);
        }, ct);
        return new ProjectUrlDto(id, project.ProjectId, url, label, Parse(now), Parse(now));
    }

    public async Task DeleteProjectUrlAsync(string projectIdentity, string urlId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var deleted = await ExecuteAsync(connection,
                "DELETE FROM studio_project_urls WHERE id = $id AND project_id = $project;",
                ct, transaction, ("$id", urlId), ("$project", project.ProjectId));
            if (deleted == 0) throw new KeyNotFoundException($"Project URL '{urlId}' was not found.");
            await AuditAsync(connection, transaction, actorId, "project.url.deleted", "project-url", urlId, "{}", ct);
        }, ct);
    }

    public async Task<ProjectUrlDto> UpdateProjectUrlAsync(
        string projectIdentity, string urlId, UpdateProjectUrlRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        ProjectUrlDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadProjectUrlAsync(connection, transaction, project.ProjectId, urlId, ct)
                ?? throw new KeyNotFoundException($"Project URL '{urlId}' was not found.");
            var url = string.IsNullOrWhiteSpace(request.Url) ? existing.Url : request.Url.Trim();
            var label = request.Label is null ? existing.Label : (string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim());
            await ExecuteAsync(connection, """
                UPDATE studio_project_urls
                   SET url = $url, label = $label, updated_at = $now
                 WHERE id = $id AND project_id = $project;
                """, ct, transaction,
                ("$url", url), ("$label", label), ("$now", now), ("$id", urlId), ("$project", project.ProjectId));
            await AuditAsync(connection, transaction, actorId, "project.url.updated", "project-url", urlId,
                JsonSerializer.Serialize(new { url, label }), ct);
            updated = existing with { Url = url, Label = label, UpdatedAt = Parse(now) };
        }, ct);
        return updated!;
    }

    private static async Task<ProjectUrlDto?> ReadProjectUrlAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, string urlId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, project_id, url, label, created_at, updated_at
              FROM studio_project_urls
             WHERE id = $id AND project_id = $project;
            """, transaction, ("$id", urlId), ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ProjectUrlDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Parse(reader.GetString(4)),
            Parse(reader.GetString(5)));
    }
}
