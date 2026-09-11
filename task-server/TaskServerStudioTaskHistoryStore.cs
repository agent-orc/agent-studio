using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Store surface for the G7 "task run/attempt history" P1 Studio bundle.
/// Almost everything here reshapes the existing durable state already read
/// by <see cref="GetTaskHistoryAsync"/> and <see cref="ListAttemptsAsync"/>;
/// no parallel storage is introduced for runs, events, or artifacts. Two
/// small new tables (<c>task_claude_sessions</c>, <c>task_plans</c>) back
/// genuinely new, currently-empty-by-default read surfaces.
///
/// <c>runIndex</c> convention: a 1-based, ascending-by-<c>created_at</c>
/// ordinal into the task's own run list (matching
/// <see cref="ListAttemptsAsync"/>'s <c>ORDER BY created_at, id</c> and the
/// legacy frontend's 1-based <c>runIndex</c> display convention, e.g.
/// <c>frontend/src/app/components/file-source-history</c>). It is not a run
/// id.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly HashSet<string> SessionEventKinds = new(StringComparer.Ordinal)
    {
        LifecycleEventKinds.AgentMessage,
        LifecycleEventKinds.ToolTrace,
        LifecycleEventKinds.RunnerTrace,
        LifecycleEventKinds.ProtocolUnknownFrame,
    };

    /// <summary>
    /// Creates the two new tables owned by this bundle. Table names are
    /// exactly <c>task_claude_sessions</c> and <c>task_plans</c> per the
    /// group contract. The caller wires the actual invocation into the
    /// central schema migration.
    /// </summary>
    internal async Task ApplyStudioTaskHistoryMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS task_claude_sessions(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                session_id TEXT,
                model TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_plans(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                plan_markdown TEXT,
                version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            """, ct);
    }

    /// <summary>
    /// Test/bootstrap-only helper that opens a ready connection and applies
    /// this bundle's migration. Exists because the central schema migration
    /// in <c>TaskServerStore.cs</c> does not (yet) call
    /// <see cref="ApplyStudioTaskHistoryMigrationAsync"/> -- that wiring is
    /// done by the integrator once all P1 groups land. Idempotent
    /// (<c>CREATE TABLE IF NOT EXISTS</c>), so it is harmless to call this
    /// again after that wiring exists.
    /// </summary>
    internal async Task EnsureStudioTaskHistorySchemaForTestingAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await ApplyStudioTaskHistoryMigrationAsync(connection, ct);
    }

    public async Task<StudioTaskTimelineResponse> GetTaskTimelineAsync(
        string projectId, string taskId, CancellationToken ct)
    {
        var history = await GetTaskHistoryAsync(projectId, taskId, 0, ct);
        if (history is null) return new StudioTaskTimelineResponse([]);

        var entries = new List<StudioTaskTimelineEntryDto>();
        foreach (var run in history.Runs)
        {
            entries.Add(new StudioTaskTimelineEntryDto(
                StudioTaskTimelineEntryKinds.RunStarted,
                run.StartedAt ?? run.CreatedAt,
                $"Run {run.RunId} started",
                RunId: run.RunId));
            if (run.FinishedAt is { } finishedAt)
                entries.Add(new StudioTaskTimelineEntryDto(
                    StudioTaskTimelineEntryKinds.RunFinished,
                    finishedAt,
                    $"Run {run.RunId} finished ({run.Status})",
                    RunId: run.RunId));
        }
        foreach (var evt in history.Events)
            entries.Add(new StudioTaskTimelineEntryDto(
                StudioTaskTimelineEntryKinds.Event,
                evt.OccurredAt,
                evt.Kind,
                RunId: evt.RunId,
                EventKind: evt.Kind));
        foreach (var artifact in history.Artifacts)
            entries.Add(new StudioTaskTimelineEntryDto(
                StudioTaskTimelineEntryKinds.ArtifactCreated,
                artifact.CreatedAt,
                $"Artifact {artifact.Name} created",
                RunId: artifact.RunId,
                ArtifactId: artifact.ArtifactId,
                ArtifactName: artifact.Name));
        foreach (var audit in history.Audit)
            entries.Add(new StudioTaskTimelineEntryDto(
                StudioTaskTimelineEntryKinds.AuditAction,
                audit.OccurredAt,
                audit.Action,
                AuditAction: audit.Action));

        return new StudioTaskTimelineResponse(
            entries.OrderBy(entry => entry.OccurredAt).ToList());
    }

    public async Task<IReadOnlyList<EventDto>> GetSessionEventsAsync(
        string projectId, string taskId, CancellationToken ct)
    {
        var history = await GetTaskHistoryAsync(projectId, taskId, 0, ct);
        if (history is null) return [];
        return history.Events
            .Where(evt => SessionEventKinds.Contains(evt.Kind))
            .OrderBy(evt => evt.OccurredAt)
            .ToList();
    }

    public async Task<StudioAgentWorkDetailResponse> GetAgentWorkDetailAsync(
        string projectId, string taskId, CancellationToken ct)
    {
        var history = await GetTaskHistoryAsync(projectId, taskId, 0, ct);
        var latestRun = history?.Runs.OrderBy(run => run.CreatedAt).LastOrDefault();
        if (latestRun is null) return new StudioAgentWorkDetailResponse(null, []);
        var events = history!.Events
            .Where(evt => evt.RunId == latestRun.RunId
                && (evt.Kind == LifecycleEventKinds.AgentMessage || evt.Kind == LifecycleEventKinds.ToolTrace))
            .OrderBy(evt => evt.OccurredAt)
            .ToList();
        return new StudioAgentWorkDetailResponse(latestRun.RunId, events);
    }

    public async Task<StudioAgentWorkSummaryResponse> GetAgentWorkSummaryAsync(
        string projectId, string taskId, CancellationToken ct)
    {
        var history = await GetTaskHistoryAsync(projectId, taskId, 0, ct);
        var latestRun = history?.Runs.OrderBy(run => run.CreatedAt).LastOrDefault();
        if (latestRun is null) return new StudioAgentWorkSummaryResponse(null, 0, 0, 0, null, null);

        var runEvents = history!.Events.Where(evt => evt.RunId == latestRun.RunId).ToList();
        var agentMessages = runEvents.Count(evt => evt.Kind == LifecycleEventKinds.AgentMessage);
        var toolTraces = runEvents.Count(evt => evt.Kind == LifecycleEventKinds.ToolTrace);
        var runnerTraces = runEvents.Count(evt => evt.Kind == LifecycleEventKinds.RunnerTrace);
        DateTime? first = runEvents.Count == 0 ? null : runEvents.Min(evt => evt.OccurredAt);
        DateTime? last = runEvents.Count == 0 ? null : runEvents.Max(evt => evt.OccurredAt);
        return new StudioAgentWorkSummaryResponse(latestRun.RunId, agentMessages, toolTraces, runnerTraces, first, last);
    }

    /// <summary>
    /// Resolves a 1-based, ascending-by-<c>created_at</c> <c>runIndex</c>
    /// (see class doc comment) into the actual run, reusing
    /// <see cref="ListAttemptsAsync"/> rather than re-querying.
    /// </summary>
    private async Task<RunDto?> ResolveRunByIndexAsync(
        string projectId, string taskId, int runIndex, CancellationToken ct)
    {
        if (runIndex < 1) return null;
        var attempts = await ListAttemptsAsync(projectId, taskId, ct);
        return runIndex <= attempts.Count ? attempts[runIndex - 1].Run : null;
    }

    public async Task<RunCommitsDto?> GetRunCommitsAsync(
        string projectId, string taskId, int runIndex, CancellationToken ct)
    {
        var run = await ResolveRunByIndexAsync(projectId, taskId, runIndex, ct);
        if (run is null) return null;

        string? repositoryUrl = null;
        string? resultRef = null;
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection,
            "SELECT repository_url, result_ref FROM runs WHERE id = $run;", ("$run", run.RunId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                repositoryUrl = reader.IsDBNull(0) ? null : reader.GetString(0);
                resultRef = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        return new RunCommitsDto(run.RunId, runIndex, run.ResultSha, run.RepositoryId, repositoryUrl, resultRef);
    }

    private static bool IsDiffArtifact(ArtifactDto artifact)
        => artifact.Name.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)
           || artifact.Name.EndsWith(".diff", StringComparison.OrdinalIgnoreCase)
           || string.Equals(artifact.MediaType, "text/x-diff", StringComparison.OrdinalIgnoreCase);

    private static bool IsFilesChangedArtifact(ArtifactDto artifact)
        => artifact.Name.EndsWith("files-changed.json", StringComparison.OrdinalIgnoreCase);

    public async Task<RunDiffResponse?> GetRunDiffAsync(
        string projectId, string taskId, int runIndex, CancellationToken ct)
    {
        var run = await ResolveRunByIndexAsync(projectId, taskId, runIndex, ct);
        if (run is null) return null;
        var history = await GetTaskHistoryAsync(projectId, taskId, 0, ct);
        var artifact = history?.Artifacts.Where(a => a.RunId == run.RunId).FirstOrDefault(IsDiffArtifact);
        if (artifact is null) return new RunDiffResponse(false);
        var content = await GetArtifactContentAsync(run.RunId, artifact.ArtifactId, ct);
        if (content is null) return new RunDiffResponse(false);
        return new RunDiffResponse(true, content.ArtifactId, content.Name, content.MediaType, content.ContentBase64);
    }

    public async Task<RunFilesResponse?> GetRunFilesAsync(
        string projectId, string taskId, int runIndex, CancellationToken ct)
    {
        var run = await ResolveRunByIndexAsync(projectId, taskId, runIndex, ct);
        if (run is null) return null;
        var history = await GetTaskHistoryAsync(projectId, taskId, 0, ct);
        var artifact = history?.Artifacts.Where(a => a.RunId == run.RunId).FirstOrDefault(IsFilesChangedArtifact);
        if (artifact is null) return new RunFilesResponse(false);
        var content = await GetArtifactContentAsync(run.RunId, artifact.ArtifactId, ct);
        if (content is null) return new RunFilesResponse(false);
        return new RunFilesResponse(true, content.ArtifactId, content.Name, content.MediaType, content.ContentBase64);
    }

    public async Task<RunContextResponse?> GetRunContextAsync(
        string projectId, string taskId, int runIndex, CancellationToken ct)
    {
        var run = await ResolveRunByIndexAsync(projectId, taskId, runIndex, ct);
        if (run is null) return null;

        await using var connection = await OpenReadyAsync(ct);
        string? contextKey = null;
        await using (var command = Command(connection, """
            SELECT context_key FROM orchestrator_contexts
             WHERE kind = 'task' AND task_id = $task
             LIMIT 1;
            """, ("$task", taskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct)) contextKey = reader.GetString(0);
        }
        if (string.IsNullOrWhiteSpace(contextKey)) return new RunContextResponse(false);

        var start = Iso(run.CreatedAt);
        var end = Iso(run.FinishedAt ?? UtcNow);
        var turns = new List<RunContextTurnDto>();
        await using (var command = Command(connection, """
            SELECT turn_id, created_at, role, body FROM orchestrator_context_turns
             WHERE context_key = $context AND created_at >= $start AND created_at <= $end
             ORDER BY sequence;
            """, ("$context", contextKey), ("$start", start), ("$end", end)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                turns.Add(new RunContextTurnDto(
                    reader.GetString(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }

        return turns.Count == 0
            ? new RunContextResponse(false, contextKey)
            : new RunContextResponse(true, contextKey, turns);
    }

    public async Task<ClaudeSessionInfoDto?> GetClaudeSessionInfoAsync(string taskId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT task_id, session_id, model, updated_at FROM task_claude_sessions WHERE task_id = $task;
            """, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ClaudeSessionInfoDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Parse(reader.GetString(3)));
    }

    /// <summary>
    /// No HTTP route in this bundle writes <c>task_claude_sessions</c> --
    /// nothing populates it yet. This exists purely so tests (and, later,
    /// whatever does start populating it) can insert a row directly via the
    /// store.
    /// </summary>
    public async Task<ClaudeSessionInfoDto> SetClaudeSessionInfoAsync(
        string taskId, string? sessionId, string? model, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var now = Iso(UtcNow);
        await ExecuteAsync(connection, """
            INSERT INTO task_claude_sessions(task_id, session_id, model, updated_at)
            VALUES ($task, $session, $model, $updated)
            ON CONFLICT(task_id) DO UPDATE SET
                session_id = excluded.session_id,
                model = excluded.model,
                updated_at = excluded.updated_at;
            """, ct, ("$task", taskId), ("$session", sessionId), ("$model", model), ("$updated", now));
        return (await GetClaudeSessionInfoAsync(taskId, ct))!;
    }

    public async Task<TaskPlanDto?> GetTaskPlanAsync(string taskId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT task_id, plan_markdown, version, updated_at FROM task_plans WHERE task_id = $task;
            """, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TaskPlanDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            Parse(reader.GetString(3)));
    }

    /// <summary>
    /// This bundle's route list is GET-only for <c>plan</c> -- no writer
    /// route is mapped. Exists purely for direct store insertion from
    /// tests, matching <see cref="SetClaudeSessionInfoAsync"/>.
    /// </summary>
    public async Task<TaskPlanDto> SetTaskPlanAsync(
        string taskId, string? planMarkdown, int version, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var now = Iso(UtcNow);
        await ExecuteAsync(connection, """
            INSERT INTO task_plans(task_id, plan_markdown, version, updated_at)
            VALUES ($task, $plan, $version, $updated)
            ON CONFLICT(task_id) DO UPDATE SET
                plan_markdown = excluded.plan_markdown,
                version = excluded.version,
                updated_at = excluded.updated_at;
            """, ct, ("$task", taskId), ("$plan", planMarkdown), ("$version", version), ("$updated", now));
        return (await GetTaskPlanAsync(taskId, ct))!;
    }

    /// <summary>
    /// Maps the project's existing flow definition stages to a prompt-text
    /// list. <c>stages_json</c> only carries the ordered stage enum today,
    /// so there is no prompt-shaped data to surface yet -- each step gets an
    /// empty prompt list, per the group contract's "if none, return an
    /// empty list per step" instruction.
    /// </summary>
    public async Task<StepPromptsResponse> GetStepPromptsAsync(string projectId, CancellationToken ct)
    {
        var flow = await GetFlowDefinitionAsync(projectId, ct);
        if (flow is null) return new StepPromptsResponse([]);
        var steps = flow.Stages
            .Select(stage => new StepPromptDto(stage.ToString(), Array.Empty<string>()))
            .ToList();
        return new StepPromptsResponse(steps);
    }
}
