using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private const int ContextSummaryMaxLength = 180;

    /// <summary>
    /// Rebuilds <c>orchestrator_contexts</c> for installs created before the
    /// <c>workbench</c> kind existed (schema 12 and earlier). SQLite cannot
    /// widen a CHECK constraint or add a column referenced by one via
    /// <c>ALTER TABLE ADD COLUMN</c>, so this renames the old table, recreates
    /// it with the widened constraint and new <c>workbench_key</c> column,
    /// copies every row across, and drops the renamed original. A fresh
    /// install already gets the widened table from the literal
    /// <c>CREATE TABLE IF NOT EXISTS</c> above, so this is a no-op there.
    /// </summary>
    internal static async Task ApplyWorkbenchContextMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (await ColumnExistsAsync(connection, "orchestrator_contexts", "workbench_key", ct))
            return;

        await ExecuteAsync(connection, """
            ALTER TABLE orchestrator_contexts RENAME TO orchestrator_contexts_pre_workbench;
            CREATE TABLE orchestrator_contexts(
                context_key TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK(kind IN ('project', 'task', 'workbench')),
                project_id TEXT NOT NULL REFERENCES projects(id),
                task_id TEXT REFERENCES tasks(id),
                workbench_key TEXT,
                summary TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                hidden_at TEXT,
                CHECK(
                    (kind = 'project' AND task_id IS NULL AND workbench_key IS NULL) OR
                    (kind = 'task' AND task_id IS NOT NULL AND workbench_key IS NULL) OR
                    (kind = 'workbench' AND task_id IS NULL AND workbench_key IS NOT NULL)
                )
            );
            INSERT INTO orchestrator_contexts(
                context_key, kind, project_id, task_id, workbench_key, summary, created_at, updated_at, hidden_at)
            SELECT context_key, kind, project_id, task_id, NULL, summary, created_at, updated_at, hidden_at
              FROM orchestrator_contexts_pre_workbench;
            DROP TABLE orchestrator_contexts_pre_workbench;
            """, ct);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var command = Command(connection, $"PRAGMA table_info({table});");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<OrchestratorContextDto>> ListOrchestratorContextsAsync(
        bool includeHidden,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var result = new List<OrchestratorContextDto>();
        await using var command = Command(connection, """
            SELECT c.context_key, c.kind, c.project_id, p.name, c.task_id, t.task_key,
                   c.summary, c.created_at, c.updated_at, c.hidden_at,
                   (SELECT count(*) FROM orchestrator_context_turns turn WHERE turn.context_key = c.context_key),
                   (SELECT turn.model FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key AND turn.model IS NOT NULL
                     ORDER BY turn.sequence DESC LIMIT 1),
                   COALESCE((SELECT sum(turn.input_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   COALESCE((SELECT sum(turn.output_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   COALESCE((SELECT sum(turn.cache_read_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   COALESCE((SELECT sum(turn.cache_creation_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   c.workbench_key
              FROM orchestrator_contexts c
              JOIN projects p ON p.id = c.project_id
              LEFT JOIN tasks t ON t.id = c.task_id
             WHERE $include_hidden = 1 OR c.hidden_at IS NULL
             ORDER BY CASE c.kind WHEN 'project' THEN 0 WHEN 'workbench' THEN 1 ELSE 2 END,
                      COALESCE(NULLIF(c.updated_at, ''), c.created_at) DESC,
                      c.context_key;
            """, ("$include_hidden", includeHidden ? 1 : 0));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadOrchestratorContext(reader));
        return result;
    }

    public async Task<OrchestratorContextDto> EnsureOrchestratorContextAsync(
        string projectIdentity,
        string? taskIdentity,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestratorContextDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = await ResolveOrchestratorContextTargetAsync(
                connection, transaction, projectIdentity, taskIdentity, ct);
            result = await EnsureOrchestratorContextAsync(
                connection, transaction, target, actorId, ct);
        }, ct);
        return result!;
    }

    public async Task<OrchestratorContextTranscriptResponse> ReadOrchestratorContextAsync(
        string projectIdentity,
        string? taskIdentity,
        int limit,
        string actorId,
        CancellationToken ct)
    {
        var context = await EnsureOrchestratorContextAsync(
            projectIdentity, taskIdentity, actorId, ct);
        return await ReadTranscriptAsync(context, limit, ct);
    }

    /// <summary>
    /// Ensures the Dossier's context row exists. Mirrors
    /// <see cref="EnsureOrchestratorContextAsync(string, string?, string, CancellationToken)"/>
    /// for the <c>workbench</c> kind: <paramref name="workbenchIdentity"/> is
    /// the Dossier's catalogue key (e.g. <c>AGT-W43</c>), trusted as opaque -
    /// the Dossier descriptor lives in the Studio-owned repository checkout,
    /// not this store, so there is no local table to validate it against.
    /// </summary>
    public async Task<OrchestratorContextDto> EnsureOrchestratorWorkbenchContextAsync(
        string projectIdentity,
        string workbenchIdentity,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestratorContextDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = await ResolveOrchestratorWorkbenchContextTargetAsync(
                connection, transaction, projectIdentity, workbenchIdentity, ct);
            result = await EnsureOrchestratorContextAsync(
                connection, transaction, target, actorId, ct);
        }, ct);
        return result!;
    }

    public async Task<OrchestratorContextTranscriptResponse> ReadOrchestratorWorkbenchContextAsync(
        string projectIdentity,
        string workbenchIdentity,
        int limit,
        string actorId,
        CancellationToken ct)
    {
        var context = await EnsureOrchestratorWorkbenchContextAsync(
            projectIdentity, workbenchIdentity, actorId, ct);
        return await ReadTranscriptAsync(context, limit, ct);
    }

    private async Task<OrchestratorContextTranscriptResponse> ReadTranscriptAsync(
        OrchestratorContextDto context,
        int limit,
        CancellationToken ct)
    {
        var boundedLimit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenReadyAsync(ct);
        var turns = new List<OrchestratorContextTurnDto>();
        await using var command = Command(connection, """
            SELECT turn_id, created_at, role, body, model,
                   input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                   error_message, error_detail, attachments_json, receipt_json
              FROM (
                    SELECT sequence, turn_id, created_at, role, body, model,
                           input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                           error_message, error_detail, attachments_json, receipt_json
                      FROM orchestrator_context_turns
                     WHERE context_key = $context
                     ORDER BY sequence DESC
                     LIMIT $limit
                   ) recent
             ORDER BY sequence;
            """, ("$context", context.ContextKey), ("$limit", boundedLimit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) turns.Add(ReadOrchestratorContextTurn(reader));
        return new OrchestratorContextTranscriptResponse(context, turns);
    }

    public async Task<OrchestratorContextTurnDto> AppendOrchestratorContextTurnAsync(
        string projectIdentity,
        string? taskIdentity,
        AppendOrchestratorContextTurnRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateOrchestratorContextTurn(request.Turn);
        OrchestratorContextTurnDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = await ResolveOrchestratorContextTargetAsync(
                connection, transaction, projectIdentity, taskIdentity, ct);
            result = await AppendTurnToContextAsync(connection, transaction, target, request, actorId, ct);
        }, ct);
        return result!;
    }

    /// <summary>Mirrors <see cref="AppendOrchestratorContextTurnAsync"/> for the <c>workbench</c> kind.</summary>
    public async Task<OrchestratorContextTurnDto> AppendOrchestratorWorkbenchContextTurnAsync(
        string projectIdentity,
        string workbenchIdentity,
        AppendOrchestratorContextTurnRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateOrchestratorContextTurn(request.Turn);
        OrchestratorContextTurnDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = await ResolveOrchestratorWorkbenchContextTargetAsync(
                connection, transaction, projectIdentity, workbenchIdentity, ct);
            result = await AppendTurnToContextAsync(connection, transaction, target, request, actorId, ct);
        }, ct);
        return result!;
    }

    private async Task<OrchestratorContextTurnDto> AppendTurnToContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrchestratorContextTarget target,
        AppendOrchestratorContextTurnRequest request,
        string actorId,
        CancellationToken ct)
    {
        var context = await EnsureOrchestratorContextAsync(connection, transaction, target, actorId, ct);
        var canonicalTurn = request.Turn with
        {
            Receipt = request.Turn.Receipt is null
                ? null
                : request.Turn.Receipt with { ContextKey = context.ContextKey },
            Attachments = request.Turn.Attachments?.Select(item =>
                new OrchestratorContextAttachmentDto(item.Alt, item.RelativePath)).ToArray(),
        };
        var payloadSha = TurnPayloadSha(canonicalTurn);
        var existingSha = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT payload_sha256 FROM orchestrator_context_turns WHERE turn_id = $turn;",
            ct,
            transaction,
            ("$turn", canonicalTurn.TurnId)), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(existingSha))
        {
            if (!string.Equals(existingSha, payloadSha, StringComparison.Ordinal))
                throw new TaskServerConflictException(
                    "orchestrator-turn-conflict",
                    $"Turn '{canonicalTurn.TurnId}' already exists with different content.");
            return canonicalTurn;
        }

        if (canonicalTurn.Receipt is not null)
        {
            var userTurnContext = Convert.ToString(await ScalarAsync(
                connection,
                "SELECT context_key FROM orchestrator_context_turns WHERE turn_id = $turn;",
                ct,
                transaction,
                ("$turn", canonicalTurn.Receipt.UserTurnId)), CultureInfo.InvariantCulture);
            if (!string.Equals(userTurnContext, context.ContextKey, StringComparison.Ordinal))
                throw new TaskServerConflictException(
                    "orchestrator-receipt-user-turn-missing",
                    "A context receipt must reference a persisted user turn in the same context.");
        }

        var usage = canonicalTurn.TokenUsage;
        var receiptJson = canonicalTurn.Receipt is null
            ? null
            : JsonSerializer.Serialize(canonicalTurn.Receipt);
        await ExecuteAsync(connection, """
            INSERT INTO orchestrator_context_turns(
                context_key, turn_id, created_at, role, body, model,
                input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                error_message, error_detail, attachments_json, receipt_json, payload_sha256)
            VALUES (
                $context, $turn, $created, $role, $body, $model,
                $input, $output, $cache_read, $cache_creation,
                $error, $detail, $attachments, $receipt, $sha);
            """, ct, transaction,
            ("$context", context.ContextKey),
            ("$turn", canonicalTurn.TurnId),
            ("$created", Iso(canonicalTurn.CreatedAt)),
            ("$role", canonicalTurn.Role),
            ("$body", canonicalTurn.Body),
            ("$model", canonicalTurn.Model),
            ("$input", usage?.InputTokens ?? 0),
            ("$output", usage?.OutputTokens ?? 0),
            ("$cache_read", usage?.CacheReadTokens ?? 0),
            ("$cache_creation", usage?.CacheCreationTokens ?? 0),
            ("$error", canonicalTurn.ErrorMessage),
            ("$detail", canonicalTurn.ErrorDetail),
            ("$attachments", canonicalTurn.Attachments is null
                ? null
                : JsonSerializer.Serialize(canonicalTurn.Attachments)),
            ("$receipt", receiptJson),
            ("$sha", payloadSha));

        var summary = string.Equals(canonicalTurn.Role, "user", StringComparison.Ordinal)
            ? BuildContextSummary(canonicalTurn.Body, context.Summary)
            : context.Summary;
        await ExecuteAsync(connection, """
            UPDATE orchestrator_contexts
               SET summary = $summary, updated_at = $updated
             WHERE context_key = $context;
            """, ct, transaction,
            ("$summary", summary),
            ("$updated", Iso(canonicalTurn.CreatedAt)),
            ("$context", context.ContextKey));
        await AuditAsync(connection, transaction, actorId, "orchestrator-context.turn-appended",
            "orchestrator-context", context.ContextKey,
            JsonSerializer.Serialize(new
            {
                canonicalTurn.TurnId,
                canonicalTurn.Role,
                receiptId = canonicalTurn.Receipt?.ReceiptId,
                userTurnId = canonicalTurn.Receipt?.UserTurnId,
            }), ct);
        return canonicalTurn;
    }

    public async Task<ImportLegacyOrchestratorChatResponse> ImportLegacyOrchestratorChatAsync(
        string projectIdentity,
        ImportLegacyOrchestratorChatRequest request,
        string actorId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceSha256))
            throw new ArgumentException("A source SHA-256 is required.");
        var imported = 0;
        var present = 0;
        var rejected = 0;
        var context = await EnsureOrchestratorContextAsync(projectIdentity, null, actorId, ct);
        var knownTurnIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, """
            SELECT turn_id
              FROM orchestrator_context_turns
             WHERE context_key = $context;
            """, ("$context", context.ContextKey)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) knownTurnIds.Add(reader.GetString(0));
        }
        foreach (var turn in request.Turns.OrderBy(item => item.CreatedAt))
        {
            try
            {
                if (knownTurnIds.Contains(turn.TurnId))
                {
                    present++;
                    continue;
                }
                await AppendOrchestratorContextTurnAsync(
                    projectIdentity,
                    null,
                    new AppendOrchestratorContextTurnRequest(turn with { Receipt = null }),
                    actorId,
                    ct);
                knownTurnIds.Add(turn.TurnId);
                imported++;
            }
            catch (Exception exception) when (exception is ArgumentException or TaskServerConflictException)
            {
                rejected++;
            }
        }
        return new ImportLegacyOrchestratorChatResponse(
            context.ContextKey, imported, present, rejected);
    }

    private static async Task<OrchestratorContextTarget> ResolveOrchestratorContextTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectIdentity,
        string? taskIdentity,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectIdentity))
            throw new ArgumentException("Project identity is required.");
        string? projectId = null;
        string? projectName = null;
        await using (var project = Command(connection, """
            SELECT id, name FROM projects
             WHERE id = $identity OR name = $identity COLLATE NOCASE
             ORDER BY CASE WHEN id = $identity THEN 0 ELSE 1 END
             LIMIT 1;
            """, transaction, ("$identity", projectIdentity.Trim())))
        await using (var reader = await project.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                projectId = reader.GetString(0);
                projectName = reader.GetString(1);
            }
        }
        if (projectId is null || projectName is null)
            throw new KeyNotFoundException($"Project '{projectIdentity}' was not found.");

        if (string.IsNullOrWhiteSpace(taskIdentity))
            return new OrchestratorContextTarget(
                $"project:{projectName}", OrchestratorContextKinds.Project,
                projectId, projectName, null, null, $"Project chat for {projectName}", null);

        string? taskId = null;
        string? taskKey = null;
        string? taskTitle = null;
        string? taskState = null;
        await using (var task = Command(connection, """
            SELECT id, task_key, title, state FROM tasks
             WHERE project_id = $project
               AND (id = $identity OR task_key = upper($identity))
             LIMIT 1;
            """, transaction, ("$project", projectId), ("$identity", taskIdentity.Trim())))
        await using (var reader = await task.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                taskId = reader.GetString(0);
                taskKey = reader.GetString(1);
                taskTitle = reader.GetString(2);
                taskState = reader.GetString(3);
            }
        }
        if (taskId is null || taskKey is null)
            throw new KeyNotFoundException(
                $"Task '{taskIdentity}' was not found in project '{projectIdentity}'.");
        return new OrchestratorContextTarget(
            $"task:{projectName}/{taskKey}", OrchestratorContextKinds.Task,
            projectId, projectName, taskId, taskKey,
            string.IsNullOrWhiteSpace(taskTitle) ? taskKey : taskTitle!, taskState);
    }

    /// <summary>
    /// Resolves a Dossier (workbench) context target. Unlike a task, a Dossier
    /// has no row in this store to validate against - its descriptor is a
    /// file the Studio backend reads from the watched repository - so the
    /// Dossier key is trusted as opaque once the project itself resolves,
    /// mirroring how a project-scope target trusts the project name.
    /// </summary>
    private static async Task<OrchestratorContextTarget> ResolveOrchestratorWorkbenchContextTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectIdentity,
        string workbenchIdentity,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectIdentity))
            throw new ArgumentException("Project identity is required.");
        if (string.IsNullOrWhiteSpace(workbenchIdentity))
            throw new ArgumentException("Dossier identity is required.");

        string? projectId = null;
        string? projectName = null;
        await using (var project = Command(connection, """
            SELECT id, name FROM projects
             WHERE id = $identity OR name = $identity COLLATE NOCASE
             ORDER BY CASE WHEN id = $identity THEN 0 ELSE 1 END
             LIMIT 1;
            """, transaction, ("$identity", projectIdentity.Trim())))
        await using (var reader = await project.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                projectId = reader.GetString(0);
                projectName = reader.GetString(1);
            }
        }
        if (projectId is null || projectName is null)
            throw new KeyNotFoundException($"Project '{projectIdentity}' was not found.");

        var workbenchKey = workbenchIdentity.Trim();
        return new OrchestratorContextTarget(
            $"workbench:{projectName}/{workbenchKey}", OrchestratorContextKinds.Workbench,
            projectId, projectName, null, null,
            $"Dossier chat for {workbenchKey}", null, workbenchKey);
    }

    private async Task<OrchestratorContextDto> EnsureOrchestratorContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrchestratorContextTarget target,
        string actorId,
        CancellationToken ct)
    {
        var now = UtcNow;
        var hiddenAt = OrchestratorContextVisibilityPolicy.IsHidden(target.Kind, target.TaskState)
            ? now
            : (DateTime?)null;
        await ExecuteAsync(connection, """
            INSERT INTO orchestrator_contexts(
                context_key, kind, project_id, task_id, workbench_key, summary,
                created_at, updated_at, hidden_at)
            VALUES ($key, $kind, $project, $task, $workbench, $summary, $now, $now, $hidden)
            ON CONFLICT(context_key) DO UPDATE SET
                project_id = excluded.project_id,
                task_id = excluded.task_id,
                workbench_key = excluded.workbench_key,
                hidden_at = excluded.hidden_at;
            """, ct, transaction,
            ("$key", target.ContextKey),
            ("$kind", target.Kind),
            ("$project", target.ProjectId),
            ("$task", target.TaskId),
            ("$workbench", target.WorkbenchKey),
            ("$summary", target.InitialSummary),
            ("$now", Iso(now)),
            ("$hidden", hiddenAt is null ? null : Iso(hiddenAt.Value)));

        await using var command = Command(connection, """
            SELECT c.context_key, c.kind, c.project_id, p.name, c.task_id, t.task_key,
                   c.summary, c.created_at, c.updated_at, c.hidden_at,
                   (SELECT count(*) FROM orchestrator_context_turns turn WHERE turn.context_key = c.context_key),
                   NULL, 0, 0, 0, 0,
                   c.workbench_key
              FROM orchestrator_contexts c
              JOIN projects p ON p.id = c.project_id
              LEFT JOIN tasks t ON t.id = c.task_id
             WHERE c.context_key = $key;
            """, transaction, ("$key", target.ContextKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The orchestrator context could not be materialized.");
        var result = ReadOrchestratorContext(reader);
        await AuditAsync(connection, transaction, actorId, "orchestrator-context.ensured",
            "orchestrator-context", result.ContextKey,
            JsonSerializer.Serialize(new { result.Kind, result.ProjectId, result.TaskId, result.HiddenAt }), ct);
        return result;
    }

    private static OrchestratorContextDto ReadOrchestratorContext(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6), Parse(reader.GetString(7)), Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));

    private static OrchestratorContextTurnDto ReadOrchestratorContextTurn(SqliteDataReader reader)
    {
        var usage = reader.GetInt64(5) == 0 && reader.GetInt64(6) == 0
                    && reader.GetInt64(7) == 0 && reader.GetInt64(8) == 0
            ? null
            : new OrchestratorContextTokenUsageDto(
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
        return new OrchestratorContextTurnDto(
            reader.GetString(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            usage,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11)
                ? null
                : JsonSerializer.Deserialize<IReadOnlyList<OrchestratorContextAttachmentDto>>(reader.GetString(11)),
            reader.IsDBNull(12)
                ? null
                : JsonSerializer.Deserialize<OrchestratorContextReceiptDto>(reader.GetString(12)));
    }

    private static void ValidateOrchestratorContextTurn(OrchestratorContextTurnDto turn)
    {
        if (string.IsNullOrWhiteSpace(turn.TurnId)
            || turn.TurnId.Length > 80
            || turn.TurnId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Turn id must contain only letters, digits, '-' or '_'.");
        if (turn.Role is not ("user" or "orchestrator"))
            throw new ArgumentException("Turn role must be 'user' or 'orchestrator'.");
        if (turn.Body.Length > 1_000_000)
            throw new ArgumentException("Turn body exceeds the 1,000,000 character limit.");
        if (turn.Attachments?.Any(item => string.IsNullOrWhiteSpace(item.RelativePath)
                                         || item.RelativePath.Contains("..", StringComparison.Ordinal)) == true)
            throw new ArgumentException("Attachment references must be bounded relative paths.");
    }

    private static string BuildContextSummary(string body, string fallback)
    {
        var compact = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(compact)) return fallback;
        return compact.Length <= ContextSummaryMaxLength
            ? compact
            : compact[..(ContextSummaryMaxLength - 3)].TrimEnd() + "...";
    }

    private static string TurnPayloadSha(OrchestratorContextTurnDto turn)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(turn)))).ToLowerInvariant();

    private sealed record OrchestratorContextTarget(
        string ContextKey,
        string Kind,
        string ProjectId,
        string ProjectName,
        string? TaskId,
        string? TaskKey,
        string InitialSummary,
        string? TaskState,
        string? WorkbenchKey = null);
}
