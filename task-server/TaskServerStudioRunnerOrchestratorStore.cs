using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backs the P1 "G3_RunnerOrchestrator" route bundle. Reuses the P0
/// orchestrator-context plumbing (<see cref="ReadOrchestratorContextAsync"/>,
/// <see cref="AppendOrchestratorContextTurnAsync"/>,
/// <see cref="ListStudioOrchestratorSessionsAsync"/>) for everything keyed by
/// a raw orchestrator context key or scoped to a project's orchestrator
/// chat/session state, and the review store's <c>review_subjects</c>/
/// <c>review_attempts</c> tables for a read-only auto-review-queue
/// projection. Adds only new, prefixed tables
/// (<c>studio_runner_*</c>/<c>studio_orchestrator_*</c>/<c>studio_pending_*</c>/
/// <c>studio_token_summary_cache</c>) for genuinely new durable state: runner
/// mode/start-stop, the orchestrator log and its human override channel,
/// pending decisions, and a short-TTL cache for the aggregate token summary.
///
/// <see cref="ApplyStudioRunnerOrchestratorMigrationAsync"/> is intentionally
/// NOT wired into <c>TaskServerStore.ApplyMigrationsAsync</c> from this file -
/// that file is edited once by the integrator who wires all nine P1 groups'
/// migrations in a single pass. Every public method here instead ensures its
/// own schema via <see cref="EnsureStudioRunnerOrchestratorSchemaReadyAsync"/>
/// before touching its tables, so this store is fully self-contained and
/// testable in isolation; the call is pure idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c> DDL, safe to run redundantly once the
/// integrator also wires it into the central migration path.
/// </summary>
public sealed partial class TaskServerStore
{
    private const int DefaultOrchestratorLogLimit = 200;
    private const int DefaultOrchestratorFeedLimit = 100;
    private const int DefaultQueueStarvationWindowMinutes = 15;
    private static readonly TimeSpan TokenSummaryCacheTtl = TimeSpan.FromSeconds(60);
    private const string TokenSummaryAggregateCacheKey = "aggregate";

    internal async Task ApplyStudioRunnerOrchestratorMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_runner_project_state(
                project_id TEXT PRIMARY KEY REFERENCES projects(id),
                mode TEXT,
                running INTEGER NOT NULL DEFAULT 0,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_orchestrator_log(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                entry_json TEXT NOT NULL,
                is_override INTEGER NOT NULL DEFAULT 0,
                actor_id TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_orchestrator_log_project
                ON studio_orchestrator_log(project_id, created_at);
            CREATE TABLE IF NOT EXISTS studio_pending_decisions(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                description TEXT NOT NULL,
                created_at TEXT NOT NULL,
                resolved_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_studio_pending_decisions_project
                ON studio_pending_decisions(project_id, resolved_at, created_at);
            CREATE TABLE IF NOT EXISTS studio_token_summary_cache(
                cache_key TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                computed_at TEXT NOT NULL
            );
            """, ct);
    }

    private async Task EnsureStudioRunnerOrchestratorSchemaReadyAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await ApplyStudioRunnerOrchestratorMigrationAsync(connection, ct);
    }

    // ---- contextKey-scoped orchestrator chat --------------------------------------------------

    public async Task<OrchestratorContextTranscriptResponse> GetRunnerOrchestratorChatByContextKeyAsync(
        string rawContextKey, string actorId, CancellationToken ct)
    {
        var (projectIdentity, taskIdentity) = ParseRunnerOrchestratorContextKey(rawContextKey);
        return await ReadOrchestratorContextAsync(projectIdentity, taskIdentity, 200, actorId, ct);
    }

    public async Task<RunnerOrchestratorChatMessageResponse> SendRunnerOrchestratorChatMessageByContextKeyAsync(
        string rawContextKey, StudioOrchestratorChatMessageRequest request, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("text is required");
        var (projectIdentity, taskIdentity) = ParseRunnerOrchestratorContextKey(rawContextKey);
        var turn = await AppendOrchestratorContextTurnAsync(
            projectIdentity,
            taskIdentity,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                $"chat_{Guid.NewGuid():N}", UtcNow, "user", request.Text, request.Model,
                Attachments: request.Attachments)),
            actorId,
            ct);
        var transcript = await ReadOrchestratorContextAsync(projectIdentity, taskIdentity, 200, actorId, ct);
        return new RunnerOrchestratorChatMessageResponse(turn, transcript);
    }

    /// <summary>
    /// Mirrors how <c>StudioEndpoints.BuildOrchestratorDigestAsync</c> and its
    /// private <c>ParseStudioOrchestratorContextKey</c> helper parse the
    /// three raw context-key shapes. The bare "global" key (no prefix) is
    /// treated as an ordinary project identity, the same way
    /// <c>/{project}/orchestrator-chat</c> already treats any bare identity:
    /// chat turns always need a project to persist against, so a caller that
    /// wants a durable "global" conversation is expected to have a project
    /// literally named or keyed "global".
    /// </summary>
    private static (string ProjectIdentity, string? TaskIdentity) ParseRunnerOrchestratorContextKey(string rawContextKey)
    {
        if (string.IsNullOrWhiteSpace(rawContextKey))
            throw new ArgumentException("Invalid orchestrator context key.");
        var decoded = Uri.UnescapeDataString(rawContextKey.Trim());
        if (decoded.StartsWith("project:", StringComparison.Ordinal))
        {
            var identity = decoded["project:".Length..];
            if (string.IsNullOrWhiteSpace(identity))
                throw new ArgumentException("Invalid orchestrator context key.");
            return (identity, null);
        }
        if (decoded.StartsWith("task:", StringComparison.Ordinal))
        {
            var rest = decoded["task:".Length..];
            var separator = rest.IndexOf('/');
            if (separator <= 0 || separator == rest.Length - 1)
                throw new ArgumentException("Invalid orchestrator context key.");
            return (rest[..separator], rest[(separator + 1)..]);
        }
        return (decoded, null);
    }

    // ---- runner mode / start / stop ------------------------------------------------------------

    public async Task<RunnerProjectStateDto> UpdateRunnerModeAsync(
        string projectIdentity, UpdateRunnerModeRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        RunnerProjectStateDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadRunnerProjectStateAsync(connection, transaction, project.ProjectId, ct);
            var currentVersion = current?.Version ?? 0;
            if (request.ExpectedVersion != currentVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected version {request.ExpectedVersion} but the current version is {currentVersion}.");
            var now = UtcNow;
            var nextVersion = currentVersion + 1;
            var running = current?.Running ?? false;
            await UpsertRunnerProjectStateAsync(connection, transaction, project.ProjectId, request.Mode, running, nextVersion, now, ct);
            await AuditAsync(connection, transaction, actorId, "studio-runner.mode-changed",
                "studio-runner-project-state", project.ProjectId,
                JsonSerializer.Serialize(new { request.Mode, version = nextVersion }), ct);
            result = new RunnerProjectStateDto(project.ProjectId, request.Mode, running, nextVersion, now);
        }, ct);
        return result!;
    }

    public Task<RunnerProjectStateDto> StartRunnerAsync(string projectIdentity, string actorId, CancellationToken ct)
        => SetRunnerRunningAsync(projectIdentity, running: true, "studio-runner.started", actorId, ct);

    public Task<RunnerProjectStateDto> StopRunnerAsync(string projectIdentity, string actorId, CancellationToken ct)
        => SetRunnerRunningAsync(projectIdentity, running: false, "studio-runner.stopped", actorId, ct);

    private async Task<RunnerProjectStateDto> SetRunnerRunningAsync(
        string projectIdentity, bool running, string auditAction, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        RunnerProjectStateDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadRunnerProjectStateAsync(connection, transaction, project.ProjectId, ct);
            var now = UtcNow;
            var nextVersion = (current?.Version ?? 0) + 1;
            await UpsertRunnerProjectStateAsync(connection, transaction, project.ProjectId, current?.Mode, running, nextVersion, now, ct);
            await AuditAsync(connection, transaction, actorId, auditAction,
                "studio-runner-project-state", project.ProjectId,
                JsonSerializer.Serialize(new { running, version = nextVersion }), ct);
            result = new RunnerProjectStateDto(project.ProjectId, current?.Mode, running, nextVersion, now);
        }, ct);
        return result!;
    }

    private static async Task<RunnerProjectStateDto?> ReadRunnerProjectStateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT project_id, mode, running, version, updated_at
              FROM studio_runner_project_state
             WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new RunnerProjectStateDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3),
            Parse(reader.GetString(4)));
    }

    private static async Task UpsertRunnerProjectStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string? mode, bool running,
        long version, DateTime updatedAt, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO studio_runner_project_state(project_id, mode, running, version, updated_at)
            VALUES ($project, $mode, $running, $version, $updated)
            ON CONFLICT(project_id) DO UPDATE SET
                mode = excluded.mode, running = excluded.running,
                version = excluded.version, updated_at = excluded.updated_at;
            """, ct, transaction,
            ("$project", projectId), ("$mode", mode), ("$running", running ? 1 : 0),
            ("$version", version), ("$updated", Iso(updatedAt)));

    // ---- orchestrator log -----------------------------------------------------------------------

    public async Task<OrchestratorLogResponse> ListOrchestratorLogAsync(string projectIdentity, int? limit, CancellationToken ct)
    {
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        var boundedLimit = Math.Clamp(limit ?? DefaultOrchestratorLogLimit, 1, 2000);
        await using var connection = await OpenReadyAsync(ct);
        var entries = new List<OrchestratorLogEntryDto>();
        await using var command = Command(connection, """
            SELECT id, project_id, entry_json, is_override, actor_id, created_at
              FROM (
                    SELECT id, project_id, entry_json, is_override, actor_id, created_at
                      FROM studio_orchestrator_log
                     WHERE project_id = $project
                     ORDER BY created_at DESC
                     LIMIT $limit
                   )
             ORDER BY created_at;
            """, ("$project", project.ProjectId), ("$limit", boundedLimit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) entries.Add(ReadOrchestratorLogEntry(reader));
        return new OrchestratorLogResponse(entries);
    }

    public async Task<OrchestratorLogEntryDto> AppendOrchestratorLogOverrideAsync(
        string projectIdentity, OrchestratorLogOverrideRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = $"solg_{Guid.NewGuid():N}";
        var now = UtcNow;
        var entryJson = request.Entry.GetRawText();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_orchestrator_log(id, project_id, entry_json, is_override, actor_id, created_at)
                VALUES ($id, $project, $entry, 1, $actor, $created);
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$entry", entryJson),
                ("$actor", actorId), ("$created", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "studio-orchestrator-log.override",
                "studio-orchestrator-log", id, entryJson, ct);
        }, ct);
        using var document = JsonDocument.Parse(entryJson);
        return new OrchestratorLogEntryDto(id, project.ProjectId, document.RootElement.Clone(), true, actorId, now);
    }

    private static OrchestratorLogEntryDto ReadOrchestratorLogEntry(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(2));
        return new OrchestratorLogEntryDto(
            reader.GetString(0), reader.GetString(1), document.RootElement.Clone(),
            reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4),
            Parse(reader.GetString(5)));
    }

    // ---- pending decisions -----------------------------------------------------------------------

    public async Task<PendingDecisionListResponse> ListPendingDecisionsAsync(string projectIdentity, CancellationToken ct)
    {
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var decisions = new List<PendingDecisionDto>();
        await using var command = Command(connection, """
            SELECT id, project_id, description, created_at, resolved_at
              FROM studio_pending_decisions
             WHERE project_id = $project AND resolved_at IS NULL
             ORDER BY created_at;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) decisions.Add(ReadPendingDecision(reader));
        return new PendingDecisionListResponse(decisions);
    }

    /// <summary>
    /// Nothing in the Task Server populates pending decisions yet - this is a
    /// genuine write surface with no producer wired up elsewhere. Exposed
    /// publicly so tests (and, later, a real decision-raising code path) can
    /// insert a row and see it round-trip over <c>GET .../pending-decisions</c>.
    /// </summary>
    public async Task<PendingDecisionDto> CreateStudioPendingDecisionAsync(
        string projectIdentity, string description, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A description is required.");
        RequireWritable();
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = $"spd_{Guid.NewGuid():N}";
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_pending_decisions(id, project_id, description, created_at, resolved_at)
                VALUES ($id, $project, $description, $created, NULL);
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$description", description), ("$created", Iso(now)));
            await AuditAsync(connection, transaction, actorId, "studio-pending-decision.created",
                "studio-pending-decision", id, JsonSerializer.Serialize(new { description }), ct);
        }, ct);
        return new PendingDecisionDto(id, project.ProjectId, description, now, null);
    }

    private static PendingDecisionDto ReadPendingDecision(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : Parse(reader.GetString(4)));

    // ---- orchestrator session (project + global) ---------------------------------------------------

    public async Task<OrchestratorSessionListResponse> GetRunnerOrchestratorSessionAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var all = await ListStudioOrchestratorSessionsAsync(ct);
        var filtered = all.Sessions
            .Where(session => string.Equals(session.ProjectId, project.ProjectId, StringComparison.Ordinal))
            .ToList();
        return new OrchestratorSessionListResponse(filtered);
    }

    public Task<OrchestratorSessionListResponse> GetGlobalRunnerOrchestratorSessionAsync(CancellationToken ct)
        => ListStudioOrchestratorSessionsAsync(ct);

    // ---- token summary (live + cached aggregate) ----------------------------------------------------

    public async Task<TokenSummaryDto> GetRunnerTokenSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        return await ReadTokenSummaryForProjectAsync(connection, null, project.ProjectId, ct);
    }

    public async Task<TokenSummaryAggregateDto> GetRunnerTokenSummaryAggregateAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ComputeTokenSummaryAggregateAsync(connection, null, ct);
    }

    /// <summary>
    /// Genuinely different code path from the live aggregate above: reads (or
    /// recomputes and refreshes) a small cache table with a 60-second TTL,
    /// rather than always recomputing from <c>orchestrator_context_turns</c>.
    /// Does not call <see cref="RequireWritable"/> - refreshing the cache is
    /// an internal implementation detail of a read endpoint, not a
    /// user-facing mutation, so it must keep working even while the server is
    /// in read-only mode.
    /// </summary>
    public async Task<CachedTokenSummaryAggregateResponse> GetRunnerTokenSummaryAggregateCachedAsync(CancellationToken ct)
    {
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        await using (var readConnection = await OpenReadyAsync(ct))
        {
            var cached = await ReadTokenSummaryCacheAsync(readConnection, null, ct);
            if (cached is not null && UtcNow - cached.Value.ComputedAt < TokenSummaryCacheTtl)
                return new CachedTokenSummaryAggregateResponse(cached.Value.Summary, cached.Value.ComputedAt);
        }

        TokenSummaryAggregateDto? summary = null;
        var computedAt = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            summary = await ComputeTokenSummaryAggregateAsync(connection, transaction, ct);
            computedAt = UtcNow;
            var payload = JsonSerializer.Serialize(summary);
            await ExecuteAsync(connection, """
                INSERT INTO studio_token_summary_cache(cache_key, payload_json, computed_at)
                VALUES ($key, $payload, $computed)
                ON CONFLICT(cache_key) DO UPDATE SET
                    payload_json = excluded.payload_json, computed_at = excluded.computed_at;
                """, ct, transaction,
                ("$key", TokenSummaryAggregateCacheKey), ("$payload", payload), ("$computed", Iso(computedAt)));
        }, ct);
        return new CachedTokenSummaryAggregateResponse(summary!, computedAt);
    }

    private static async Task<TokenSummaryDto> ReadTokenSummaryForProjectAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT COALESCE(SUM(turn.input_tokens), 0), COALESCE(SUM(turn.output_tokens), 0),
                   COALESCE(SUM(turn.cache_read_tokens), 0), COALESCE(SUM(turn.cache_creation_tokens), 0),
                   COUNT(*)
              FROM orchestrator_context_turns turn
              JOIN orchestrator_contexts c ON c.context_key = turn.context_key
             WHERE c.project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new TokenSummaryDto(
            projectId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static async Task<TokenSummaryAggregateDto> ComputeTokenSummaryAggregateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var byProject = new List<TokenSummaryDto>();
        await using (var command = Command(connection, """
            SELECT c.project_id,
                   COALESCE(SUM(turn.input_tokens), 0), COALESCE(SUM(turn.output_tokens), 0),
                   COALESCE(SUM(turn.cache_read_tokens), 0), COALESCE(SUM(turn.cache_creation_tokens), 0),
                   COUNT(*)
              FROM orchestrator_context_turns turn
              JOIN orchestrator_contexts c ON c.context_key = turn.context_key
             GROUP BY c.project_id;
            """, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                byProject.Add(new TokenSummaryDto(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
        }
        return new TokenSummaryAggregateDto(
            byProject.Sum(item => item.InputTokens), byProject.Sum(item => item.OutputTokens),
            byProject.Sum(item => item.CacheReadTokens), byProject.Sum(item => item.CacheCreationTokens),
            byProject.Sum(item => item.TurnCount), byProject);
    }

    private static async Task<(TokenSummaryAggregateDto Summary, DateTime ComputedAt)?> ReadTokenSummaryCacheAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT payload_json, computed_at FROM studio_token_summary_cache WHERE cache_key = $key;
            """, transaction, ("$key", TokenSummaryAggregateCacheKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var summary = JsonSerializer.Deserialize<TokenSummaryAggregateDto>(reader.GetString(0))!;
        return (summary, Parse(reader.GetString(1)));
    }

    // ---- auto-review queue (read-only projection) -----------------------------------------------

    public async Task<AutoReviewQueueResponse> GetAutoReviewQueueAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var entries = new List<AutoReviewQueueEntryDto>();
        await using var command = Command(connection, """
            SELECT a.id, a.subject_id, a.task_id, t.task_key, t.project_id,
                   a.attempt_number, a.status, a.executor_id, a.created_at
              FROM review_attempts a
              JOIN tasks t ON t.id = a.task_id
             WHERE a.status IN ('queued', 'leased', 'process-unknown')
             ORDER BY a.created_at;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(new AutoReviewQueueEntryDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), Parse(reader.GetString(8))));
        return new AutoReviewQueueResponse(entries);
    }

    // ---- cross-project orchestrator activity feed ------------------------------------------------

    public async Task<OrchestratorFeedResponse> GetOrchestratorFeedAsync(int? limit, CancellationToken ct)
    {
        await EnsureStudioRunnerOrchestratorSchemaReadyAsync(ct);
        var boundedLimit = Math.Clamp(limit ?? DefaultOrchestratorFeedLimit, 1, 1000);
        await using var connection = await OpenReadyAsync(ct);
        var entries = new List<OrchestratorFeedEntryDto>();
        await using var command = Command(connection, """
            SELECT occurred_at, kind, ref_id, project_id, body
              FROM (
                    SELECT created_at AS occurred_at, 'log' AS kind, id AS ref_id,
                           project_id AS project_id, entry_json AS body
                      FROM studio_orchestrator_log
                    UNION ALL
                    SELECT turn.created_at AS occurred_at, 'turn' AS kind, turn.turn_id AS ref_id,
                           c.project_id AS project_id, turn.body AS body
                      FROM orchestrator_context_turns turn
                      JOIN orchestrator_contexts c ON c.context_key = turn.context_key
                   )
             ORDER BY occurred_at DESC
             LIMIT $limit;
            """, ("$limit", boundedLimit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(new OrchestratorFeedEntryDto(
                reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), Parse(reader.GetString(0))));
        return new OrchestratorFeedResponse(entries);
    }

    // ---- queue starvation (computed live) --------------------------------------------------------

    public async Task<QueueStarvationResponse> GetQueueStarvationAsync(int? windowMinutes, CancellationToken ct)
    {
        var window = windowMinutes is > 0 ? windowMinutes.Value : DefaultQueueStarvationWindowMinutes;
        var now = UtcNow;
        var cutoff = now.AddMinutes(-window);
        await using var connection = await OpenReadyAsync(ct);
        var tasks = new List<QueueStarvationTaskDto>();
        await using var command = Command(connection, """
            SELECT t.id, t.task_key, t.project_id, t.created_at
              FROM tasks t
             WHERE t.state = $ready
               AND NOT EXISTS (
                     SELECT 1 FROM runs r WHERE r.task_id = t.id AND r.created_at >= $cutoff
                   )
             ORDER BY t.created_at;
            """, ("$ready", StudioTaskLanes.Ready), ("$cutoff", Iso(cutoff)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var createdAt = Parse(reader.GetString(3));
            tasks.Add(new QueueStarvationTaskDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), createdAt, (now - createdAt).TotalMinutes));
        }
        return new QueueStarvationResponse(tasks.Count, tasks);
    }
}
