using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Board read model per project (pipeline delivery target, section 05, M1 and
/// M2, decision D7 A-B). Triggers on <c>tasks</c> append every change of a
/// board field to <c>board_changes</c> and advance the project's generation,
/// so every writer is covered without touching it: Studio moves, runner
/// transitions, retention, legacy import and the workspace shadow import.
/// The generation is the cursor of the project's last committed change; one
/// transaction can advance it by more than one, and readers only ever see
/// committed generations.
/// </summary>
public sealed partial class TaskServerStore
{
    /// <summary>Change-log rows kept before the oldest are pruned; older cursors resync.</summary>
    internal const int BoardChangeRetention = 20_000;

    private const int BoardChangeFeedDefaultLimit = 500;
    private const int BoardChangeFeedMaxLimit = 2_000;

    // Plain UPDATE plus INSERT ... WHERE NOT EXISTS rather than INSERT OR
    // REPLACE: a conflict clause inside a trigger yields to the clause of the
    // statement that fired it, so an outer OR IGNORE would freeze the generation.
    private static string AdvanceBoardGenerationSql(string project) => $$"""
        UPDATE board_generations SET generation = (SELECT MAX(generation) FROM board_changes)
         WHERE project_id = {{project}};
        INSERT INTO board_generations(project_id, generation)
            SELECT {{project}}, (SELECT MAX(generation) FROM board_changes)
             WHERE NOT EXISTS (SELECT 1 FROM board_generations WHERE project_id = {{project}});
        """;

    private static readonly string PruneBoardChangesSql = $$"""
        DELETE FROM board_changes
         WHERE generation <= (SELECT MAX(generation) FROM board_changes) - {{BoardChangeRetention}};
        """;

    private static async Task ApplyBoardReadModelMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, $$"""
            CREATE TABLE IF NOT EXISTS board_changes(
                generation INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                task_key TEXT NOT NULL,
                change TEXT NOT NULL CHECK(change IN ('upsert', 'remove')),
                state TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_board_changes_project ON board_changes(project_id, generation);
            CREATE TABLE IF NOT EXISTS board_generations(
                project_id TEXT PRIMARY KEY,
                generation INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS board_workspace_imports(
                project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
                source_generation INTEGER NOT NULL,
                imported_at TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS trg_tasks_board_insert AFTER INSERT ON tasks
            BEGIN
                INSERT INTO board_changes(project_id, task_id, task_key, change, state)
                    VALUES (NEW.project_id, NEW.id, NEW.task_key, 'upsert', NEW.state);
                {{AdvanceBoardGenerationSql("NEW.project_id")}}
                {{PruneBoardChangesSql}}
            END;
            CREATE TRIGGER IF NOT EXISTS trg_tasks_board_update AFTER UPDATE ON tasks
            WHEN NEW.project_id IS NOT OLD.project_id
              OR NEW.task_key IS NOT OLD.task_key
              OR NEW.title IS NOT OLD.title
              OR NEW.state IS NOT OLD.state
              OR NEW.rank IS NOT OLD.rank
              OR NEW.archive_state IS NOT OLD.archive_state
            BEGIN
                INSERT INTO board_changes(project_id, task_id, task_key, change, state)
                    SELECT OLD.project_id, OLD.id, OLD.task_key, 'remove', OLD.state
                     WHERE NEW.project_id IS NOT OLD.project_id;
                {{AdvanceBoardGenerationSql("OLD.project_id")}}
                INSERT INTO board_changes(project_id, task_id, task_key, change, state)
                    VALUES (NEW.project_id, NEW.id, NEW.task_key, 'upsert', NEW.state);
                {{AdvanceBoardGenerationSql("NEW.project_id")}}
                {{PruneBoardChangesSql}}
            END;
            CREATE TRIGGER IF NOT EXISTS trg_tasks_board_delete AFTER DELETE ON tasks
            BEGIN
                INSERT INTO board_changes(project_id, task_id, task_key, change, state)
                    VALUES (OLD.project_id, OLD.id, OLD.task_key, 'remove', OLD.state);
                {{AdvanceBoardGenerationSql("OLD.project_id")}}
                {{PruneBoardChangesSql}}
            END;
            """, ct);

    /// <summary>
    /// Reads the generation and the card rows in one read transaction, so the
    /// returned generation always names exactly the returned cards.
    /// </summary>
    public async Task<BoardReadModelResponse> GetBoardReadModelAsync(string projectIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var (projectId, projectName) = await RequireBoardProjectAsync(connection, transaction, projectIdentity, ct);
        var generation = await ReadBoardGenerationAsync(connection, transaction, projectId, ct);
        var source = await ScalarAsync(connection,
            "SELECT source_generation FROM board_workspace_imports WHERE project_id = $project;",
            ct, transaction, ("$project", projectId));
        var cards = new List<BoardCardDto>();
        await using (var command = Command(connection, """
            SELECT id, task_key, title, state, rank, version, archive_state, updated_at
              FROM tasks WHERE project_id = $project;
            """, transaction, ("$project", projectId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                cards.Add(new BoardCardDto(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    Parse(reader.GetString(7))));
            }
        }
        await transaction.CommitAsync(ct);
        return BoardReadModelProjection.Project(
            projectId,
            projectName,
            generation,
            source is null or DBNull ? null : Convert.ToInt64(source, CultureInfo.InvariantCulture),
            cards);
    }

    /// <summary>The project's current board generation, for a conditional read without the card rows.</summary>
    public async Task<long> GetBoardGenerationAsync(string projectIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var (projectId, _) = await RequireBoardProjectAsync(connection, null, projectIdentity, ct);
        return await ReadBoardGenerationAsync(connection, null, projectId, ct);
    }

    public async Task<BoardChangeFeedResponse> GetBoardChangesAsync(
        string projectIdentity, long after, int? limit, CancellationToken ct)
    {
        if (after < 0) throw new ArgumentException("The change cursor cannot be negative.");
        var take = Math.Clamp(limit ?? BoardChangeFeedDefaultLimit, 1, BoardChangeFeedMaxLimit);
        await using var connection = await OpenReadyAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var (projectId, _) = await RequireBoardProjectAsync(connection, transaction, projectIdentity, ct);
        var generation = await ReadBoardGenerationAsync(connection, transaction, projectId, ct);
        var oldest = await ScalarAsync(connection, "SELECT MIN(generation) FROM board_changes;", ct, transaction);
        var oldestRetained = oldest is null or DBNull ? (long?)null : Convert.ToInt64(oldest, CultureInfo.InvariantCulture);
        var resync = after > generation || (oldestRetained is { } floor && after < floor - 1 && after < generation);
        var changes = new List<BoardChangeDto>();
        if (!resync)
        {
            await using var command = Command(connection, """
                SELECT generation, task_id, task_key, change, state
                  FROM board_changes
                 WHERE project_id = $project AND generation > $after
                 ORDER BY generation
                 LIMIT $limit;
                """, transaction, ("$project", projectId), ("$after", after), ("$limit", take));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                changes.Add(new BoardChangeDto(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        await transaction.CommitAsync(ct);
        return new BoardChangeFeedResponse(projectId, after, generation, resync, changes);
    }

    /// <summary>
    /// Applies card core from the workspace index while the workspace is the
    /// write authority (M1 shadow, M2 dual write). A card is created with the
    /// legacy importer's deterministic id, so a later full import addresses
    /// the same row. A removal of a card with run history is rejected and the
    /// row is kept: history is never dropped to make the shadow agree.
    /// </summary>
    public async Task<BoardWorkspaceImportResponse> ImportWorkspaceBoardAsync(
        string projectIdentity, BoardWorkspaceImportRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (request.SourceGeneration < 0) throw new ArgumentException("The source generation cannot be negative.");
        var upserts = request.Upserts ?? [];
        var removals = request.Removals ?? [];
        foreach (var card in upserts)
        {
            if (string.IsNullOrWhiteSpace(card.TaskKey)) throw new ArgumentException("Every imported card needs a task key.");
            if (string.IsNullOrWhiteSpace(card.State)) throw new ArgumentException($"Card {card.TaskKey} has no state.");
        }

        BoardWorkspaceImportResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (projectId, _) = await RequireBoardProjectAsync(connection, transaction, projectIdentity, ct);
            var now = Iso(UtcNow);
            var rejected = new List<BoardImportRejection>();
            int inserted = 0, updated = 0, unchanged = 0, removed = 0;
            long highestNumber = 0;
            foreach (var card in upserts)
            {
                var key = card.TaskKey.Trim().ToUpperInvariant();
                var title = string.IsNullOrWhiteSpace(card.Title) ? key : card.Title.Trim();
                var existing = await ReadBoardRowAsync(connection, transaction, key, ct);
                if (existing is { } row && !string.Equals(row.ProjectId, projectId, StringComparison.Ordinal))
                {
                    rejected.Add(new BoardImportRejection(key, "task-key-in-other-project",
                        $"Task key {key} belongs to another Task Server project."));
                    continue;
                }
                if (existing is null)
                {
                    await ExecuteAsync(connection, """
                        INSERT INTO tasks(id, project_id, task_key, title, body, state, version, created_at, updated_at, rank)
                        VALUES ($id, $project, $key, $title, NULL, $state, 1, $now, $now, $rank);
                        """, ct, transaction,
                        ("$id", DeterministicId("tsk", $"{projectId}:{key}")), ("$project", projectId),
                        ("$key", key), ("$title", title), ("$state", card.State), ("$now", now), ("$rank", card.Rank));
                    inserted++;
                    highestNumber = Math.Max(highestNumber, TaskNumber(key));
                    continue;
                }
                var changed = await ExecuteAsync(connection, """
                    UPDATE tasks
                       SET title = $title, state = $state, rank = $rank,
                           version = version + 1, updated_at = $now
                     WHERE id = $id AND (title IS NOT $title OR state IS NOT $state OR rank IS NOT $rank);
                    """, ct, transaction,
                    ("$id", existing.Value.TaskId), ("$title", title), ("$state", card.State),
                    ("$rank", card.Rank), ("$now", now));
                if (changed > 0) updated++;
                else unchanged++;
            }

            foreach (var rawKey in removals)
            {
                var key = rawKey.Trim().ToUpperInvariant();
                var existing = await ReadBoardRowAsync(connection, transaction, key, ct);
                if (existing is not { } row || !string.Equals(row.ProjectId, projectId, StringComparison.Ordinal))
                    continue;
                var runs = Convert.ToInt64(await ScalarAsync(connection,
                    "SELECT count(*) FROM runs WHERE task_id = $task;", ct, transaction, ("$task", row.TaskId)),
                    CultureInfo.InvariantCulture);
                if (runs > 0)
                {
                    rejected.Add(new BoardImportRejection(key, "task-has-history",
                        $"Task {key} has run history in the Task Server and is kept."));
                    continue;
                }
                await ExecuteAsync(connection, """
                    DELETE FROM orchestrator_context_turns WHERE context_key IN (
                        SELECT context_key FROM orchestrator_contexts WHERE task_id = $task);
                    DELETE FROM orchestrator_contexts WHERE task_id = $task;
                    DELETE FROM tasks WHERE id = $task;
                    """, ct, transaction, ("$task", row.TaskId));
                removed++;
            }

            if (highestNumber > 0)
                await ExecuteAsync(connection,
                    "UPDATE projects SET next_task_number = MAX(next_task_number, $next) WHERE id = $id;",
                    ct, transaction, ("$next", highestNumber + 1), ("$id", projectId));
            await ExecuteAsync(connection, """
                INSERT INTO board_workspace_imports(project_id, source_generation, imported_at)
                VALUES ($project, $source, $now)
                ON CONFLICT(project_id) DO UPDATE
                   SET source_generation = excluded.source_generation, imported_at = excluded.imported_at;
                """, ct, transaction, ("$project", projectId), ("$source", request.SourceGeneration), ("$now", now));
            if (inserted + updated + removed + rejected.Count > 0)
                await AuditAsync(connection, transaction, actorId, "board.workspace-imported", "project", projectId,
                    JsonSerializer.Serialize(new
                    {
                        request.SourceGeneration, inserted, updated, removed, rejected = rejected.Count,
                    }), ct);
            var generation = await ReadBoardGenerationAsync(connection, transaction, projectId, ct);
            result = new BoardWorkspaceImportResponse(
                projectId, generation, request.SourceGeneration, inserted, updated, unchanged, removed, rejected);
        }, ct);
        return result!;
    }

    private static async Task<(string ProjectId, string ProjectName)> RequireBoardProjectAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectIdentity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectIdentity) || projectIdentity == UnscopedProjectToken)
            throw new ArgumentException("A board is read per project.");
        await using var command = Command(connection, $$"""
            SELECT id, name FROM projects WHERE id = {{ProjectIdentityToIdSql}};
            """, transaction, ("$project", projectIdentity));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Project {projectIdentity} was not found.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<long> ReadBoardGenerationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        var value = await ScalarAsync(connection,
            "SELECT generation FROM board_generations WHERE project_id = $project;",
            ct, transaction, ("$project", projectId));
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<(string TaskId, string ProjectId)?> ReadBoardRowAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskKey, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT id, project_id FROM tasks WHERE task_key = $key;", transaction, ("$key", taskKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static long TaskNumber(string taskKey)
    {
        var dash = taskKey.LastIndexOf('-');
        return dash >= 0 && long.TryParse(taskKey[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }
}
