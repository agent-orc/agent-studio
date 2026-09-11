using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Store for the P1 "G8_TaskReview" Studio route bundle. See
/// <c>StudioTaskReviewEndpoints.cs</c> for route mapping and
/// <c>StudioTaskReviewContracts.cs</c> for the DTO shapes.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly IReadOnlyList<string> CodeReviewDefaultSeverityLevels = ["pass", "concerns", "block"];
    private const string CodeReviewDefaultSeverity = "concerns";
    private static readonly IReadOnlyList<string> CodeReviewDefaultScope = ["changed-files"];

    /// <summary>
    /// Creates the four G8_TaskReview tables. Additive-only (CREATE TABLE IF
    /// NOT EXISTS / CREATE INDEX IF NOT EXISTS), safe to call multiple times
    /// and safe to call on a store that already has every other migration
    /// applied. The call site into the shared migration pipeline is wired up
    /// separately (not from this file, per this group's file-ownership
    /// rules).
    /// </summary>
    internal async Task ApplyStudioTaskReviewMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS task_pipeline_step_runs(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                step_id TEXT NOT NULL,
                status TEXT NOT NULL,
                actor_id TEXT,
                requested_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_task_pipeline_step_runs_task
                ON task_pipeline_step_runs(task_id, step_id, requested_at);

            CREATE TABLE IF NOT EXISTS task_code_review_findings(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                file_name TEXT NOT NULL,
                body TEXT NOT NULL,
                severity TEXT,
                created_at TEXT NOT NULL,
                version INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_task_code_review_findings_task
                ON task_code_review_findings(task_id, file_name, created_at);

            CREATE TABLE IF NOT EXISTS task_review_evidence_actions(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                evidence_id TEXT NOT NULL,
                action TEXT NOT NULL CHECK(action IN ('acknowledge','follow-up')),
                note TEXT,
                actor_id TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_task_review_evidence_actions_task
                ON task_review_evidence_actions(task_id, evidence_id, action);

            CREATE TABLE IF NOT EXISTS task_summaries(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                summary_markdown TEXT,
                kind TEXT NOT NULL DEFAULT 'interim',
                regeneration_requested_count INTEGER NOT NULL DEFAULT 0,
                version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            """, ct);
    }

    // ---- code review findings -----------------------------------------------

    public async Task<CodeReviewFindingDto> SubmitCodeReviewFindingAsync(
        string projectId,
        string taskIdentity,
        SubmitCodeReviewFindingRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("File name is required.");
        if (string.IsNullOrWhiteSpace(request.Body)) throw new ArgumentException("Finding body is required.");
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        var findingId = StableOrGeneratedId(request.FindingId, "crf");
        CodeReviewFindingDto? created = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            // "version" is a per-task ordinal (1, 2, 3, ...) recording the
            // order findings were submitted in, not an optimistic-concurrency
            // expected-version the way task/flow-definition versions are.
            var count = Convert.ToInt64(
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM task_code_review_findings WHERE task_id = $task;",
                    ct, transaction, ("$task", task.TaskId)) ?? 0L,
                CultureInfo.InvariantCulture);
            var version = count + 1;
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO task_code_review_findings(id, task_id, file_name, body, severity, created_at, version)
                VALUES ($id, $task, $file, $body, $severity, $now, $version);
                """, ct, transaction,
                ("$id", findingId), ("$task", task.TaskId), ("$file", request.FileName.Trim()),
                ("$body", request.Body), ("$severity", request.Severity), ("$now", now), ("$version", version));
            await AuditAsync(connection, transaction, actorId, "task.code-review-finding-submitted",
                "task", task.TaskId,
                JsonSerializer.Serialize(new { findingId, request.FileName, request.Severity }), ct);
            created = new CodeReviewFindingDto(
                findingId, task.TaskId, request.FileName.Trim(), request.Body, request.Severity, Parse(now), version);
        }, ct);
        return created!;
    }

    public async Task<CodeReviewFileFindingsResponse> GetCodeReviewFindingsForFileAsync(
        string projectId, string taskIdentity, string fileName, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, body, severity, created_at, version
              FROM task_code_review_findings
             WHERE task_id = $task AND file_name = $file
             ORDER BY created_at, id;
            """, ("$task", task.TaskId), ("$file", fileName));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var findings = new List<CodeReviewFindingDto>();
        while (await reader.ReadAsync(ct))
        {
            findings.Add(new CodeReviewFindingDto(
                reader.GetString(0),
                task.TaskId,
                fileName,
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Parse(reader.GetString(3)),
                reader.GetInt64(4)));
        }
        return new CodeReviewFileFindingsResponse(task.TaskId, fileName, findings);
    }

    public async Task<CodeReviewFindingListResponse> ListCodeReviewFindingsAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, file_name, severity, created_at
              FROM task_code_review_findings
             WHERE task_id = $task
             ORDER BY created_at, id;
            """, ("$task", task.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var findings = new List<CodeReviewFindingSummaryDto>();
        while (await reader.ReadAsync(ct))
        {
            findings.Add(new CodeReviewFindingSummaryDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Parse(reader.GetString(3))));
        }
        return new CodeReviewFindingListResponse(task.TaskId, findings);
    }

    /// <summary>
    /// Global, not task-scoped. See the doc comment on
    /// <see cref="CodeReviewDefaultsDto"/> for why this is a small static
    /// default rather than reading from a store.
    /// </summary>
    public Task<CodeReviewDefaultsDto> GetCodeReviewDefaultsAsync(CancellationToken ct)
        => Task.FromResult(new CodeReviewDefaultsDto(
            CodeReviewDefaultSeverityLevels, CodeReviewDefaultSeverity, CodeReviewDefaultScope));

    // ---- pipeline ------------------------------------------------------------

    public async Task<TaskPipelineViewDto> GetTaskPipelineAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        // The step list itself is owned by orchestration, not duplicated here.
        var flow = await GetFlowDefinitionAsync(task.ProjectId, ct);

        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, step_id, status, actor_id, requested_at, completed_at
              FROM task_pipeline_step_runs
             WHERE task_id = $task
             ORDER BY requested_at DESC, id DESC;
            """, ("$task", task.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var latestByStep = new Dictionary<string, TaskPipelineStepViewDto>(StringComparer.Ordinal);
        var stepRunOrder = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var stepId = reader.GetString(1);
            if (latestByStep.ContainsKey(stepId)) continue; // first hit per step is the latest (DESC order)
            latestByStep[stepId] = new TaskPipelineStepViewDto(
                stepId,
                reader.GetString(0),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)));
            stepRunOrder.Add(stepId);
        }

        var steps = new List<TaskPipelineStepViewDto>();
        if (flow is not null)
        {
            foreach (var stage in flow.Stages)
            {
                var stepId = stage.ToString();
                steps.Add(latestByStep.TryGetValue(stepId, out var existing)
                    ? existing
                    : new TaskPipelineStepViewDto(stepId, null, null, null, null, null));
            }
        }
        // Any step-run rows for step ids outside the current flow definition
        // (e.g. a stage that has since been removed from the flow) still
        // surface, appended after the defined steps, so a recorded request is
        // never silently dropped from the view.
        foreach (var stepId in stepRunOrder)
            if (!steps.Any(step => string.Equals(step.StepId, stepId, StringComparison.Ordinal)))
                steps.Add(latestByStep[stepId]);

        return new TaskPipelineViewDto(task.TaskId, task.ProjectId, flow?.Version, steps);
    }

    /// <summary>
    /// Records a durable REQUEST to run a pipeline step for this task. This
    /// method does not and cannot execute anything: the Task Server has no
    /// direct access to a Runner or task workspace. It only ever inserts a
    /// <c>status = "requested"</c> row; actual execution is a Runner's job
    /// via the existing claim/lease flow, out of scope here.
    /// </summary>
    public async Task<TaskPipelineStepRunDto> RunPipelineStepAsync(
        string projectId,
        string taskIdentity,
        string stepId,
        RunPipelineStepRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(stepId)) throw new ArgumentException("Step id is required.");
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        var id = StableOrGeneratedId(null, "pls");
        TaskPipelineStepRunDto? created = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO task_pipeline_step_runs(id, task_id, step_id, status, actor_id, requested_at, completed_at)
                VALUES ($id, $task, $step, 'requested', $actor, $now, NULL);
                """, ct, transaction,
                ("$id", id), ("$task", task.TaskId), ("$step", stepId), ("$actor", actorId), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "task.pipeline-step-run-requested",
                "task", task.TaskId, JsonSerializer.Serialize(new { id, stepId, request.Note }), ct);
            created = new TaskPipelineStepRunDto(id, task.TaskId, stepId, "requested", actorId, Parse(now), null);
        }, ct);
        return created!;
    }

    // ---- regression radar ------------------------------------------------------

    /// <summary>
    /// Computed on read from <c>ListAttemptsAsync</c>'s already-classified
    /// <see cref="ExecutionOutcomeDecision"/> per attempt. See the doc
    /// comment on <see cref="RegressionRadarDto"/> for the exact
    /// "regression-flagged" derivation rule this uses.
    /// </summary>
    public async Task<RegressionRadarDto> GetRegressionRadarAsync(
        string projectId, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        var attempts = await ListAttemptsAsync(task.ProjectId, task.TaskId, ct);
        var flagged = new List<RegressionRadarEntryDto>();
        foreach (var attempt in attempts)
        {
            var decision = attempt.OutcomeDecision;
            if (decision is null) continue;
            if (decision.IsInfrastructureOutcome) continue;
            if (decision.Outcome == ExecutionOutcomeKind.SuccessfulCompletion) continue;
            flagged.Add(new RegressionRadarEntryDto(
                attempt.Run.RunId, decision.Outcome, decision.RecoveryAction, decision.Confidence,
                decision.Detail, attempt.Run.FinishedAt));
        }
        return new RegressionRadarDto(task.TaskId, flagged.Count > 0, attempts.Count, flagged);
    }

    // ---- review evidence actions ------------------------------------------------

    public Task<ReviewEvidenceActionDto> AcknowledgeReviewEvidenceAsync(
        string projectId, string taskIdentity, string evidenceId, ReviewEvidenceActionRequest request,
        string actorId, CancellationToken ct)
        => RecordReviewEvidenceActionAsync(projectId, taskIdentity, evidenceId, "acknowledge", request, actorId, ct);

    public Task<ReviewEvidenceActionDto> FollowUpReviewEvidenceAsync(
        string projectId, string taskIdentity, string evidenceId, ReviewEvidenceActionRequest request,
        string actorId, CancellationToken ct)
        => RecordReviewEvidenceActionAsync(projectId, taskIdentity, evidenceId, "follow-up", request, actorId, ct);

    /// <summary>
    /// Appends one review-evidence action row. <c>acknowledge</c> is
    /// idempotent by (task, evidence, action): a second acknowledge of the
    /// same evidence returns the existing row instead of erroring or
    /// duplicating. <c>follow-up</c> is deliberately NOT idempotent -- each
    /// follow-up is a discrete note (e.g. "still investigating", "fixed in
    /// abc123"), so repeated follow-ups on the same evidence each append a
    /// new row.
    /// </summary>
    private async Task<ReviewEvidenceActionDto> RecordReviewEvidenceActionAsync(
        string projectId,
        string taskIdentity,
        string evidenceId,
        string action,
        ReviewEvidenceActionRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(evidenceId)) throw new ArgumentException("Evidence id is required.");
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        ReviewEvidenceActionDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            if (action == "acknowledge")
            {
                await using var existingCommand = Command(connection, """
                    SELECT id, note, actor_id, created_at
                      FROM task_review_evidence_actions
                     WHERE task_id = $task AND evidence_id = $evidence AND action = 'acknowledge'
                     ORDER BY created_at LIMIT 1;
                    """, transaction, ("$task", task.TaskId), ("$evidence", evidenceId));
                await using var existingReader = await existingCommand.ExecuteReaderAsync(ct);
                if (await existingReader.ReadAsync(ct))
                {
                    result = new ReviewEvidenceActionDto(
                        existingReader.GetString(0),
                        task.TaskId,
                        evidenceId,
                        action,
                        existingReader.IsDBNull(1) ? null : existingReader.GetString(1),
                        existingReader.IsDBNull(2) ? null : existingReader.GetString(2),
                        Parse(existingReader.GetString(3)));
                    return;
                }
            }

            var id = StableOrGeneratedId(null, "rea");
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO task_review_evidence_actions(id, task_id, evidence_id, action, note, actor_id, created_at)
                VALUES ($id, $task, $evidence, $action, $note, $actor, $now);
                """, ct, transaction,
                ("$id", id), ("$task", task.TaskId), ("$evidence", evidenceId), ("$action", action),
                ("$note", request.Note), ("$actor", actorId), ("$now", now));
            await AuditAsync(connection, transaction, actorId, $"task.review-evidence-{action}",
                "task", task.TaskId, JsonSerializer.Serialize(new { id, evidenceId, request.Note }), ct);
            result = new ReviewEvidenceActionDto(id, task.TaskId, evidenceId, action, request.Note, actorId, Parse(now));
        }, ct);
        return result!;
    }

    // ---- task summaries --------------------------------------------------------

    public async Task<TaskSummaryDto> SubmitInterimSummaryAsync(
        string projectId, string taskIdentity, InterimSummaryRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (request.SummaryMarkdown is null) throw new ArgumentException("Summary markdown is required.");
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        TaskSummaryDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskSummaryAsync(connection, transaction, task.TaskId, ct);
            var now = Iso(UtcNow);
            var version = (existing?.Version ?? 0) + 1;
            var regenerationCount = existing?.RegenerationRequestedCount ?? 0;
            await ExecuteAsync(connection, """
                INSERT INTO task_summaries(task_id, summary_markdown, kind, regeneration_requested_count, version, updated_at)
                VALUES ($task, $markdown, 'interim', $regen, $version, $now)
                ON CONFLICT(task_id) DO UPDATE SET
                    summary_markdown = excluded.summary_markdown,
                    kind = 'interim',
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$task", task.TaskId), ("$markdown", request.SummaryMarkdown), ("$regen", regenerationCount),
                ("$version", version), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "task.summary-interim-updated",
                "task", task.TaskId, JsonSerializer.Serialize(new { version }), ct);
            result = new TaskSummaryDto(
                task.TaskId, request.SummaryMarkdown, "interim", regenerationCount, version, Parse(now));
        }, ct);
        return result!;
    }

    /// <summary>
    /// The Task Server cannot itself run an LLM to regenerate a summary, so
    /// this is honest about that boundary: it only bumps
    /// <c>regeneration_requested_count</c> and <c>updated_at</c> to record
    /// that regeneration was requested. <c>summary_markdown</c>,
    /// <c>kind</c>, and <c>version</c> are left exactly as they were (or
    /// NULL / "interim" / 0 if no summary existed yet) -- no content is
    /// fabricated.
    /// </summary>
    public async Task<TaskSummaryDto> RegenerateSummaryAsync(
        string projectId, string taskIdentity, RegenerateSummaryRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var task = await GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        TaskSummaryDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskSummaryAsync(connection, transaction, task.TaskId, ct);
            var now = Iso(UtcNow);
            var regenerationCount = (existing?.RegenerationRequestedCount ?? 0) + 1;
            var version = existing?.Version ?? 0;
            var kind = existing?.Kind ?? "interim";
            var markdown = existing?.SummaryMarkdown;
            await ExecuteAsync(connection, """
                INSERT INTO task_summaries(task_id, summary_markdown, kind, regeneration_requested_count, version, updated_at)
                VALUES ($task, $markdown, $kind, $regen, $version, $now)
                ON CONFLICT(task_id) DO UPDATE SET
                    regeneration_requested_count = excluded.regeneration_requested_count,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$task", task.TaskId), ("$markdown", markdown), ("$kind", kind), ("$regen", regenerationCount),
                ("$version", version), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "task.summary-regeneration-requested",
                "task", task.TaskId, JsonSerializer.Serialize(new { regenerationCount, request.Reason }), ct);
            result = new TaskSummaryDto(task.TaskId, markdown, kind, regenerationCount, version, Parse(now));
        }, ct);
        return result!;
    }

    private static async Task<TaskSummaryDto?> ReadTaskSummaryAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT summary_markdown, kind, regeneration_requested_count, version, updated_at
              FROM task_summaries WHERE task_id = $task;
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TaskSummaryDto(
            taskId,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            Parse(reader.GetString(4)));
    }
}
