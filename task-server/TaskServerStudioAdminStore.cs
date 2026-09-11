using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Durable watch paths, global CLI quota/routing settings, admin
/// orchestrator config, and named prompt documents for the P2 bundle. These
/// are instance-wide (not project-scoped) except <c>studio_watch_paths</c>,
/// which optionally names a project.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioWatchPathDto> AddWatchPathAsync(CreateWatchPathRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Pattern))
            throw new ArgumentException("A watch path name and pattern are required.");
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_watch_paths(name, pattern, project_id, created_at, created_by)
            VALUES ($name, $pattern, $project, $now, $actor)
            ON CONFLICT(name) DO UPDATE SET pattern = excluded.pattern, project_id = excluded.project_id;
            """, ct, ("$name", request.Name.Trim()), ("$pattern", request.Pattern), ("$project", request.ProjectId),
            ("$now", Iso(now)), ("$actor", actorId));
        return new StudioWatchPathDto(request.Name.Trim(), request.Pattern, request.ProjectId, now);
    }

    public async Task DeleteWatchPathAsync(string name, CancellationToken ct)
    {
        RequireWritable();
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, "DELETE FROM studio_watch_paths WHERE name = $name;", ct, ("$name", name));
    }

    public async Task<StudioWatchPathListResponse> ListWatchPathsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT name, pattern, project_id, created_at FROM studio_watch_paths ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioWatchPathDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new StudioWatchPathDto(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), Parse(reader.GetString(3))));
        return new StudioWatchPathListResponse(result);
    }

    // --- CLI settings (global) --------------------------------------------------

    public Task UpdateCliEconomyModeAsync(CliEconomyModeRequest request, string actor, CancellationToken ct) =>
        UpsertCliSettingAsync("economy-mode", request, actor, ct);
    public Task UpdateCliQuotaCapsAsync(CliQuotaCapsRequest request, string actor, CancellationToken ct) =>
        UpsertCliSettingAsync("quota-caps", request, actor, ct);
    public Task UpdateCliQuotaModelRoutesAsync(CliQuotaModelRoutesRequest request, string actor, CancellationToken ct) =>
        UpsertCliSettingAsync("quota-model-routes", request, actor, ct);
    public Task UpdateCliQuotaWaitPolicyAsync(CliQuotaWaitPolicyRequest request, string actor, CancellationToken ct) =>
        UpsertCliSettingAsync("quota-wait-policy", request, actor, ct);

    private async Task UpsertCliSettingAsync(string id, object value, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_cli_settings(id, value_json, updated_at, updated_by)
            VALUES ($id, $value, $now, $actor)
            ON CONFLICT(id) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
            """, ct, ("$id", id), ("$value", JsonSerializer.Serialize(value)), ("$now", now), ("$actor", actorId));
    }

    // --- Admin orchestrator config -----------------------------------------------

    public async Task UpdateAdminOrchestratorConfigAsync(AdminOrchestratorConfigRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_admin_settings(key, value_json, updated_at, updated_by)
            VALUES ('orchestrator', $value, $now, $actor)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
            """, ct, ("$value", JsonSerializer.Serialize(request)), ("$now", now), ("$actor", actorId));
    }

    // --- Prompts -----------------------------------------------------------------

    public async Task<StudioPromptDto> UpsertPromptAsync(string name, UpdatePromptRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Content)) throw new ArgumentException("Prompt content is required.");
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_prompts(name, content, updated_at, updated_by)
            VALUES ($name, $content, $now, $actor)
            ON CONFLICT(name) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
            """, ct, ("$name", name), ("$content", request.Content), ("$now", Iso(now)), ("$actor", actorId));
        return new StudioPromptDto(name, request.Content, now, actorId);
    }

    public async Task DeletePromptAsync(string name, CancellationToken ct)
    {
        RequireWritable();
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, "DELETE FROM studio_prompts WHERE name = $name;", ct, ("$name", name));
    }

    public async Task<StudioPromptDto?> GetPromptAsync(string name, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT name, content, updated_at, updated_by FROM studio_prompts WHERE name = $name;", ("$name", name));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StudioPromptDto(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetString(3));
    }

    public async Task<PromptPreviewResponse> PreviewPromptAsync(string name, PromptPreviewRequest request, CancellationToken ct)
    {
        var prompt = await GetPromptAsync(name, ct) ?? throw new KeyNotFoundException($"Prompt '{name}' was not found.");
        var rendered = prompt.Content;
        if (request.Variables is not null)
            foreach (var (key, value) in request.Variables)
                rendered = rendered.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        return new PromptPreviewResponse(name, rendered);
    }

    public async Task<PromptReviewResponse> ReviewPromptAsync(string name, string actorId, CancellationToken ct)
    {
        var prompt = await GetPromptAsync(name, ct) ?? throw new KeyNotFoundException($"Prompt '{name}' was not found.");
        var operation = await RecordCompletedStudioOperationAsync(
            null,
            StudioOperationDomains.AdminPrompts,
            "review",
            $"Review prompt '{name}'",
            new { name },
            new { name, reviewedAt = UtcNow },
            actorId,
            ct);
        return new PromptReviewResponse(name, operation.Id, operation.Status);
    }

    public async Task<PromptReviewResponse> RebaselinePromptAsync(string name, string actorId, CancellationToken ct)
    {
        var prompt = await GetPromptAsync(name, ct) ?? throw new KeyNotFoundException($"Prompt '{name}' was not found.");
        var operation = await RecordCompletedStudioOperationAsync(
            null,
            StudioOperationDomains.AdminPrompts,
            "rebaseline",
            $"Rebaseline prompt '{name}'",
            new { name },
            new { name, rebaselinedAt = UtcNow },
            actorId,
            ct);
        return new PromptReviewResponse(name, operation.Id, operation.Status);
    }

    public async Task<PromptReviewAllResponse> ReviewAllPromptsAsync(string actorId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT name FROM studio_prompts ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        var reviews = new List<PromptReviewResponse>(names.Count);
        foreach (var name in names) reviews.Add(await ReviewPromptAsync(name, actorId, ct));
        return new PromptReviewAllResponse(reviews);
    }
}
