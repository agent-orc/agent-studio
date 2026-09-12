using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backing store for the Studio P2 "admin/prompts/utility" bundle: the
/// orchestrator's global config singleton (<c>studio_admin_config</c>) and
/// named prompt overrides with a baseline hash and review annotation
/// (<c>studio_admin_prompts</c>). Deleting an override removes its row
/// entirely, so "row exists" and "an override is stored" are the same fact
/// - there is no separate nullable-content sentinel. Component routing
/// resolution is pure in-process computation against a small embedded
/// static table and needs no storage at all.
/// </summary>
public sealed partial class TaskServerStore
{
    private const string OrchestratorConfigKey = "orchestrator";

    internal async Task ApplyStudioP2AdminMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_admin_config(
                config_key TEXT PRIMARY KEY,
                config_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_admin_prompts(
                name TEXT PRIMARY KEY,
                override_content TEXT NOT NULL,
                baseline_hash TEXT,
                last_reviewed_at TEXT,
                review_summary_json TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_admin_prompts_updated
                ON studio_admin_prompts(updated_at);
            """, ct);
    }

    public async Task<OrchestratorConfigDto> UpsertOrchestratorConfigAsync(
        UpdateOrchestratorConfigRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
            throw new ArgumentException("Orchestrator config JSON is required.");
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_admin_config(config_key, config_json, updated_at)
                VALUES ($key, $config, $now)
                ON CONFLICT(config_key) DO UPDATE SET
                    config_json = excluded.config_json,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$key", OrchestratorConfigKey), ("$config", request.ConfigJson), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "studio-admin.orchestrator-config.updated",
                "studio-admin-config", OrchestratorConfigKey, JsonSerializer.Serialize(new { }), ct);
        }, ct);
        return new OrchestratorConfigDto(request.ConfigJson, Parse(now));
    }

    public async Task<PromptOverrideDto> UpsertPromptOverrideAsync(
        string name, UpdatePromptOverrideRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var promptName = RequirePromptName(name);
        if (request.Content is null)
            throw new ArgumentException("Prompt override content is required.");
        var now = Iso(UtcNow);
        PromptOverrideDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var createdAt = Convert.ToString(await ScalarAsync(connection,
                "SELECT created_at FROM studio_admin_prompts WHERE name = $name;", ct, transaction, ("$name", promptName)));
            var effectiveCreatedAt = string.IsNullOrEmpty(createdAt) ? now : createdAt;
            await ExecuteAsync(connection, """
                INSERT INTO studio_admin_prompts(name, override_content, baseline_hash, last_reviewed_at, review_summary_json, created_at, updated_at)
                VALUES ($name, $content, NULL, NULL, NULL, $created, $now)
                ON CONFLICT(name) DO UPDATE SET
                    override_content = excluded.override_content,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$name", promptName), ("$content", request.Content), ("$created", effectiveCreatedAt), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "studio-admin.prompt-override.upserted",
                "studio-admin-prompt", promptName, JsonSerializer.Serialize(new { name = promptName }), ct);
            result = await ReadPromptOverrideAsync(connection, transaction, promptName, ct);
        }, ct);
        return result!;
    }

    public async Task DeletePromptOverrideAsync(string name, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var promptName = RequirePromptName(name);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var deleted = await ExecuteAsync(connection,
                "DELETE FROM studio_admin_prompts WHERE name = $name;", ct, transaction, ("$name", promptName));
            if (deleted == 0)
                throw new KeyNotFoundException($"Prompt override '{promptName}' was not found.");
            await AuditAsync(connection, transaction, actorId, "studio-admin.prompt-override.deleted",
                "studio-admin-prompt", promptName, JsonSerializer.Serialize(new { name = promptName }), ct);
        }, ct);
    }

    public async Task<PreviewPromptOverrideResponse> PreviewPromptOverrideAsync(
        string name, PreviewPromptOverrideRequest request, CancellationToken ct)
    {
        var promptName = RequirePromptName(name);
        await using var connection = await OpenReadyAsync(ct);
        var stored = await ReadPromptOverrideAsync(connection, null, promptName, ct)
            ?? throw new KeyNotFoundException($"Prompt override '{promptName}' was not found.");
        var effective = string.IsNullOrEmpty(request.Content) ? stored.OverrideContent : request.Content;
        return new PreviewPromptOverrideResponse(promptName, effective);
    }

    public async Task<RebaselinePromptOverrideResponse> RebaselinePromptOverrideAsync(
        string name, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var promptName = RequirePromptName(name);
        var now = Iso(UtcNow);
        string? baselineHash = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var stored = await ReadPromptOverrideAsync(connection, transaction, promptName, ct)
                ?? throw new KeyNotFoundException($"Prompt override '{promptName}' was not found.");
            baselineHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored.OverrideContent)));
            await ExecuteAsync(connection, """
                UPDATE studio_admin_prompts
                   SET baseline_hash = $hash, updated_at = $now
                 WHERE name = $name;
                """, ct, transaction, ("$hash", baselineHash), ("$now", now), ("$name", promptName));
            await AuditAsync(connection, transaction, actorId, "studio-admin.prompt-override.rebaselined",
                "studio-admin-prompt", promptName, JsonSerializer.Serialize(new { baselineHash }), ct);
        }, ct);
        return new RebaselinePromptOverrideResponse(promptName, baselineHash!, Parse(now));
    }

    public async Task<PromptReviewAnnotationDto> ReviewPromptOverrideAsync(string name, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var promptName = RequirePromptName(name);
        PromptReviewAnnotationDto? annotation = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var stored = await ReadPromptOverrideAsync(connection, transaction, promptName, ct)
                ?? throw new KeyNotFoundException($"Prompt override '{promptName}' was not found.");
            annotation = await ReviewOnePromptAsync(connection, transaction, stored, actorId, ct);
        }, ct);
        return annotation!;
    }

    public async Task<ReviewAllPromptsResponse> ReviewAllPromptOverridesAsync(string actorId, CancellationToken ct)
    {
        RequireWritable();
        var reviewed = new List<PromptReviewAnnotationDto>();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var names = new List<string>();
            await using (var command = Command(connection, "SELECT name FROM studio_admin_prompts ORDER BY name;", transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));

            foreach (var promptName in names)
            {
                var stored = await ReadPromptOverrideAsync(connection, transaction, promptName, ct);
                if (stored is null) continue;
                reviewed.Add(await ReviewOnePromptAsync(connection, transaction, stored, actorId, ct));
            }
        }, ct);
        return new ReviewAllPromptsResponse(reviewed);
    }

    private async Task<PromptReviewAnnotationDto> ReviewOnePromptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PromptOverrideDto stored,
        string actorId,
        CancellationToken ct)
    {
        var now = Iso(UtcNow);
        var contentLength = stored.OverrideContent.Length;
        var lineCount = stored.OverrideContent.Length == 0
            ? 0
            : stored.OverrideContent.Split('\n').Length;
        var summaryJson = JsonSerializer.Serialize(new { contentLength, lineCount });
        await ExecuteAsync(connection, """
            UPDATE studio_admin_prompts
               SET last_reviewed_at = $now, review_summary_json = $summary, updated_at = $now
             WHERE name = $name;
            """, ct, transaction,
            ("$now", now), ("$summary", summaryJson), ("$name", stored.Name));
        await AuditAsync(connection, transaction, actorId, "studio-admin.prompt-override.reviewed",
            "studio-admin-prompt", stored.Name, summaryJson, ct);
        return new PromptReviewAnnotationDto(stored.Name, contentLength, lineCount, Parse(now));
    }

    private async Task<PromptOverrideDto?> ReadPromptOverrideAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string name, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT name, override_content, baseline_hash, last_reviewed_at, review_summary_json, created_at, updated_at
              FROM studio_admin_prompts
             WHERE name = $name;
            """, transaction, ("$name", name));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PromptOverrideDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Parse(reader.GetString(5)),
            Parse(reader.GetString(6)));
    }

    private static string RequirePromptName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Prompt name is required.");
        return name.Trim();
    }

    private static readonly (string PathPrefix, string OwningComponent, string FrontendOwner)[] ComponentRoutingTable =
    [
        ("/api/bus", "Bus", "Shared frontend services"),
        ("/api/drift", "Drift", "Insight surfaces"),
        ("/api/security", "Security", "Insight surfaces"),
        ("/api/supervisor", "Supervisor", "Operations console"),
        ("/api/deployment", "Deployment", "Operations console"),
        ("/api/publish", "Publish", "Operations console"),
        ("/api/wiki", "Wiki", "Documentation surfaces"),
        ("/api/design", "Design", "Design surfaces"),
        ("/api/proposals", "Proposals", "Design surfaces"),
        ("/api/skill-readiness", "Skill readiness", "Insight surfaces"),
        ("/api/crash-recovery", "Crash recovery", "Operations console"),
        ("/api/tokens", "Token usage", "Insight surfaces"),
        ("/api/cycle-time", "Cycle time", "Insight surfaces"),
        ("/api/runtime", "Runtime", "Operations console"),
        ("/api/admin", "Administration", "Admin console"),
        ("/api/devtools", "Developer tools", "Dev seat"),
    ];

    public ResolveComponentRoutingResponse ResolveComponentRouting(ResolveComponentRoutingRequest request)
    {
        var path = request.Path ?? string.Empty;
        foreach (var (prefix, owningComponent, frontendOwner) in ComponentRoutingTable)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new ResolveComponentRoutingResponse(owningComponent, frontendOwner);
        }
        return new ResolveComponentRoutingResponse("Unclassified", "Unclassified");
    }
}
