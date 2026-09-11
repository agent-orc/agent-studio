using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    // 13 adds retention policies, archive runs and manifests, task archive
    // stub columns, and an artifacts.archived flag with nullable content.
    // 14 adds studio human users and sessions, a tasks.rank lane-ordering
    // column, and the replayable studio_stream_events cursor log backing
    // the /hubs/v1/studio hub.
    // 15 persists review workspace namespaces and idempotent reclaim responses
    // so a replacement daemon can re-fence a detached worker in place.
    // 16 persists review daemon restart observations and their lost-review
    // counts for the 24-hour execution-host projection.
    // The migration block is idempotent; the number guards downgrades from
    // binaries that do not know this state.
    public const int CurrentSchemaVersion = 16;

    /// <summary>
    /// Reserved <c>projectId</c> route value meaning "resolve this task by id
    /// alone." The OrchestratorApi connector substitutes this literal when a
    /// legacy single-parameter frontend path such as
    /// <c>/api/tasks/{taskId}/move</c> has no project id to forward. A real
    /// project id is never this literal, so the substitution cannot collide
    /// with an actual project.
    /// </summary>
    public const string UnscopedProjectToken = "-";

    private const string TimestampFormat = "O";
    private readonly TaskServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly IResultFinalizationSummaryGenerator _resultSummaries;
    private readonly ITaskServerEventPublisher? _operationalEvents;
    private readonly ILogger<TaskServerStore>? _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DateTime _startedAt;
    private static readonly JsonSerializerOptions OutcomeJson = CreateOutcomeJson();
    private string _serverId = string.Empty;
    private TaskServerMode _mode = TaskServerMode.Maintenance;
    private int _outboxBacklog;
    private long? _oldestUnacknowledgedSequence;
    private IReadOnlyDictionary<string, int> _finalHandoffStates =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public TaskServerStore(IOptions<TaskServerOptions> options, TimeProvider clock)
        : this(
            options,
            clock,
            new ApplicationResultFinalizationSummaryGenerator(),
            operationalEvents: null,
            logger: null)
    {
    }

    public TaskServerStore(
        IOptions<TaskServerOptions> options,
        TimeProvider clock,
        IResultFinalizationSummaryGenerator resultSummaries)
        : this(options, clock, resultSummaries, operationalEvents: null, logger: null)
    {
    }

    public TaskServerStore(
        IOptions<TaskServerOptions> options,
        TimeProvider clock,
        IResultFinalizationSummaryGenerator resultSummaries,
        ITaskServerEventPublisher? operationalEvents,
        ILogger<TaskServerStore>? logger)
    {
        _options = options.Value;
        _clock = clock;
        _resultSummaries = resultSummaries;
        _operationalEvents = operationalEvents;
        _logger = logger;
        _startedAt = UtcNow;
    }

    public string DataDirectory => _options.ResolveDataDirectory();
    public string DatabasePath => Path.Combine(DataDirectory, "task-server.db");
    public string BackupDirectory => _options.ResolveBackupDirectory();
    public bool AuthorityReady { get; private set; }
    public string ServerId => _serverId;
    public TaskServerMode Mode => _mode;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => await InitializeCoreAsync(quarantineActiveAuthority: true, cancellationToken);

    public async Task InitializeForBackupAsync(CancellationToken cancellationToken = default)
        => await InitializeCoreAsync(quarantineActiveAuthority: false, cancellationToken);

    private async Task InitializeCoreAsync(
        bool quarantineActiveAuthority,
        CancellationToken cancellationToken)
    {
        AuthorityReady = false;
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            await ApplyMigrationsAsync(connection, cancellationToken);
            _serverId = await GetOrCreateMetaAsync(connection, "server_id", $"srv_{Guid.NewGuid():N}", cancellationToken);
            var storedMode = await GetOrCreateMetaAsync(connection, "mode", TaskServerMode.Normal.ToString(), cancellationToken);
            _mode = Enum.TryParse<TaskServerMode>(storedMode, true, out var parsedMode)
                ? parsedMode
                : TaskServerMode.Maintenance;
            await RefreshOutboxSummaryAsync(connection, cancellationToken);

            if (quarantineActiveAuthority)
            {
                // A server restart cannot infer that an old runner process stopped.
                // Preserve its fence and fail the attempt closed until an explicit,
                // audited recovery releases it. Offline backup deliberately skips
                // both the lifecycle evidence and the authority transition.
                var restartMarker = Guid.NewGuid().ToString("N");
                await ExecuteAsync(connection, """
                    INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
                    SELECT 'evt_unavailable_' || id || '_' || $marker,
                           id,
                           task_id,
                           $unavailableKind,
                           $payload,
                           'task-server-unavailable:' || id || ':' || $marker,
                           fence,
                           $now
                      FROM runs
                     WHERE status = 'running';
                    INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
                    SELECT 'evt_restart_' || id || '_' || $marker,
                           id,
                           task_id,
                           $kind,
                           $payload,
                           'task-server-restart:' || id || ':' || $marker,
                           fence,
                           $now
                      FROM runs
                     WHERE status = 'running';
                    """, cancellationToken,
                    ("$marker", restartMarker),
                    ("$unavailableKind", LifecycleEventKinds.TaskServerUnavailable),
                    ("$kind", LifecycleEventKinds.ProcessUnknown),
                    ("$payload", JsonSerializer.Serialize(new
                    {
                        failure = "task-server-unavailable",
                        authority = "fail-closed",
                        replacementAdmission = "positive-no-overlap-evidence-required",
                    })),
                    ("$now", Iso(UtcNow)));
                await ExecuteAsync(connection, """
                    UPDATE leases
                       SET status = 'process-unknown'
                     WHERE status = 'active';
                    UPDATE runs
                       SET status = 'process-unknown'
                     WHERE status = 'running';
                    UPDATE review_attempts
                       SET status = 'process-unknown'
                     WHERE status = 'leased';
                    UPDATE orchestration_runs
                       SET status = 'pending'
                     WHERE status = 'leased';
                    UPDATE orchestration_leases
                       SET status = 'server-restarted'
                     WHERE status = 'active';
                    """, cancellationToken);

                await using var transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await SupersedeUnclaimableReviewAttemptsAsync(
                    connection,
                    transaction,
                    "task-server-boot",
                    "boot-sweep",
                    taskId: null,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            var integrity = Convert.ToString(await ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken), CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Task Server store integrity check failed: {integrity}");

            AuthorityReady = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public TaskServerStatusDto Status()
    {
        var version = TaskServerBuildIdentity.Current.DisplayVersion;
        return new TaskServerStatusDto(
            _serverId,
            version,
            CurrentSchemaVersion,
            _mode,
            AuthorityReady,
            DataDirectory,
            new ProtocolRangeDto(
                TaskServerProtocol.Current,
                TaskServerProtocol.MinimumSupported,
                TaskServerProtocol.MaximumSupported,
                version,
                _serverId,
                ["studio", "runner", "review-runner", TaskServerProtocol.EngineClientKind, "management"],
                ["coding-plane", "review-plane", "orchestration-plane", "host-orchestrator", "management-plane"]),
            _startedAt,
            _outboxBacklog,
            _oldestUnacknowledgedSequence,
            _finalHandoffStates);
    }

    public async Task<WorkspaceDto> CreateWorkspaceAsync(CreateWorkspaceRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Workspace name is required.");
        var id = StableOrGeneratedId(request.WorkspaceId, "wsp");
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO workspaces(id, name, version, created_at, updated_at)
                VALUES ($id, $name, 1, $now, $now);
                """, ct, transaction, ("$id", id), ("$name", request.Name.Trim()), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "workspace.created", "workspace", id,
                JsonSerializer.Serialize(new { request.Name }), ct);
        }, ct);
        return new WorkspaceDto(id, request.Name.Trim(), 1, Parse(now), Parse(now));
    }

    public async Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT id, name, version, created_at, updated_at FROM workspaces ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkspaceDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new WorkspaceDto(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Parse(reader.GetString(3)), Parse(reader.GetString(4))));
        return result;
    }

    public async Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TaskKeyPrefix))
            throw new ArgumentException("Project name and task key prefix are required.");
        var id = StableOrGeneratedId(request.ProjectId, "prj");
        var prefix = request.TaskKeyPrefix.Trim().ToUpperInvariant();
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO projects(id, workspace_id, name, task_key_prefix, next_task_number, version, created_at, updated_at)
                VALUES ($id, $workspace, $name, $prefix, 1, 1, $now, $now);
                INSERT INTO orchestrator_contexts(
                    context_key, kind, project_id, task_id, summary, created_at, updated_at, hidden_at)
                VALUES ($context_key, 'project', $id, NULL, $summary, $now, $now, NULL);
                INSERT INTO flow_definitions(project_id, version, stages_json, max_reissue_attempts, updated_at)
                VALUES ($id, 0, $stages, $max_reissues, $now);
                """, ct, transaction,
                ("$id", id), ("$workspace", request.WorkspaceId), ("$name", request.Name.Trim()),
                ("$prefix", prefix), ("$now", now),
                ("$context_key", $"project:{request.Name.Trim()}"),
                ("$summary", $"Project chat for {request.Name.Trim()}"),
                ("$stages", JsonSerializer.Serialize(OrchestrationDefaults.CreateStages())),
                ("$max_reissues", OrchestrationDefaults.MaxReissueAttempts));
            await AuditAsync(connection, transaction, actorId, "project.created", "project", id,
                JsonSerializer.Serialize(new { request.WorkspaceId, request.Name, taskKeyPrefix = prefix }), ct);
        }, ct);
        return new ProjectDto(id, request.WorkspaceId, request.Name.Trim(), prefix, 1, Parse(now), Parse(now));
    }

    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(string? workspaceId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var sql = "SELECT id, workspace_id, name, task_key_prefix, version, created_at, updated_at FROM projects" +
                  (string.IsNullOrWhiteSpace(workspaceId) ? string.Empty : " WHERE workspace_id = $workspace") + " ORDER BY name;";
        await using var command = Command(connection, sql, ("$workspace", workspaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ProjectDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new ProjectDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), Parse(reader.GetString(5)), Parse(reader.GetString(6))));
        return result;
    }

    public async Task<TaskDto> CreateTaskAsync(string projectId, CreateTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("Task title is required.");
        var taskId = StableOrGeneratedId(request.TaskId, "tsk");
        TaskDto? created = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (prefix, next) = await ReadProjectCounterAsync(connection, transaction, projectId, ct);
            var taskKey = string.IsNullOrWhiteSpace(request.TaskKey)
                ? $"{prefix}-{next}"
                : request.TaskKey.Trim().ToUpperInvariant();
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO tasks(id, project_id, task_key, title, body, state, version, created_at, updated_at)
                VALUES ($id, $project, $key, $title, $body, $state, 1, $now, $now);
                """, ct, transaction,
                ("$id", taskId), ("$project", projectId), ("$key", taskKey), ("$title", request.Title.Trim()),
                ("$body", request.Body), ("$state", request.State), ("$now", now));
            await ExecuteAsync(connection,
                "UPDATE projects SET next_task_number = MAX(next_task_number, $next), updated_at = $now WHERE id = $id;",
                ct, transaction, ("$next", next + 1), ("$now", now), ("$id", projectId));
            await AuditAsync(connection, transaction, actorId, "task.created", "task", taskId,
                JsonSerializer.Serialize(new { projectId, taskKey, request.State }), ct);
            created = new TaskDto(taskId, projectId, taskKey, request.Title.Trim(), request.State, 1, Parse(now), Parse(now), request.Body);
        }, ct);
        return created!;
    }

    public async Task<TaskDto?> GetTaskAsync(string projectId, string taskIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var sql = projectId == UnscopedProjectToken
            ? """
              SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
                FROM tasks
               WHERE id = $identity;
              """
            : """
              SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
                FROM tasks
               WHERE project_id = $project AND (id = $identity OR task_key = upper($identity));
              """;
        await using var command = Command(connection, sql, ("$project", projectId), ("$identity", taskIdentity));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<TaskHistoryDto?> GetTaskHistoryAsync(
        string projectId,
        string taskIdentity,
        long after,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        TaskDto? task;
        await using (var taskCommand = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
              FROM tasks
             WHERE project_id = $project AND (id = $identity OR task_key = upper($identity));
            """, ("$project", projectId), ("$identity", taskIdentity)))
        await using (var taskReader = await taskCommand.ExecuteReaderAsync(ct))
            task = await taskReader.ReadAsync(ct) ? ReadTask(taskReader) : null;
        if (task is null) return null;

        var runs = new List<RunDto>();
        await using (var runCommand = Command(connection, """
            SELECT id, task_id, status, runner_id, fence, created_at, started_at, finished_at,
                   result_sha, repository_id
              FROM runs WHERE task_id = $task ORDER BY created_at, id;
            """, ("$task", task.TaskId)))
        await using (var runReader = await runCommand.ExecuteReaderAsync(ct))
        {
            while (await runReader.ReadAsync(ct))
            {
                runs.Add(new RunDto(
                    runReader.GetString(0),
                    runReader.GetString(1),
                    runReader.GetString(2),
                    runReader.IsDBNull(3) ? null : runReader.GetString(3),
                    runReader.IsDBNull(4) ? null : runReader.GetInt64(4),
                    Parse(runReader.GetString(5)),
                    runReader.IsDBNull(6) ? null : Parse(runReader.GetString(6)),
                    runReader.IsDBNull(7) ? null : Parse(runReader.GetString(7)),
                    runReader.IsDBNull(8) ? null : runReader.GetString(8),
                    runReader.IsDBNull(9) ? null : runReader.GetString(9)));
            }
        }

        var events = new List<EventDto>();
        await using (var eventCommand = Command(connection, """
            SELECT cursor, event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at, sequence
              FROM events
             WHERE task_id = $task AND cursor > $after
             ORDER BY cursor
             LIMIT 1000;
            """, ("$task", task.TaskId), ("$after", after)))
        await using (var eventReader = await eventCommand.ExecuteReaderAsync(ct))
            while (await eventReader.ReadAsync(ct)) events.Add(ReadEvent(eventReader));

        var artifacts = new List<ArtifactDto>();
        await using (var artifactCommand = Command(connection, """
            SELECT a.id, a.run_id, a.name, a.media_type, a.sha256, a.size_bytes,
                   a.idempotency_key, a.fence, a.created_at, a.sequence
              FROM artifacts a
              JOIN runs r ON r.id = a.run_id
             WHERE r.task_id = $task
             ORDER BY a.created_at, a.id;
            """, ("$task", task.TaskId)))
        await using (var artifactReader = await artifactCommand.ExecuteReaderAsync(ct))
            while (await artifactReader.ReadAsync(ct)) artifacts.Add(ReadArtifact(artifactReader));

        var audit = new List<AuditRecordDto>();
        await using (var auditCommand = Command(connection, """
            SELECT sequence, occurred_at, actor_id, action, target_type, target_id, detail_json
              FROM audit
             WHERE target_id = $task
                OR target_id IN (SELECT id FROM runs WHERE task_id = $task)
             ORDER BY sequence;
            """, ("$task", task.TaskId)))
        await using (var auditReader = await auditCommand.ExecuteReaderAsync(ct))
        {
            while (await auditReader.ReadAsync(ct))
            {
                audit.Add(new AuditRecordDto(
                    auditReader.GetInt64(0),
                    Parse(auditReader.GetString(1)),
                    auditReader.GetString(2),
                    auditReader.GetString(3),
                    auditReader.GetString(4),
                    auditReader.GetString(5),
                    auditReader.GetString(6)));
            }
        }

        ResultFinalizationDto? resultFinalization = null;
        await using (var resultCommand = Command(connection, """
            SELECT run_id, status, attempt_count, max_attempts, artifact_id,
                   artifact_sha256, error, updated_at
              FROM result_finalizations
             WHERE run_id IN (SELECT id FROM runs WHERE task_id = $task)
             ORDER BY updated_at DESC
             LIMIT 1;
            """, ("$task", task.TaskId)))
        await using (var resultReader = await resultCommand.ExecuteReaderAsync(ct))
        {
            if (await resultReader.ReadAsync(ct))
                resultFinalization = ReadResultFinalization(resultReader);
        }

        return new TaskHistoryDto(
            task,
            runs,
            events,
            artifacts,
            audit,
            events.Count == 0 ? after : events[^1].Cursor,
            resultFinalization);
    }

    public async Task<IReadOnlyList<TaskDto>> ListTasksAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
              FROM tasks WHERE project_id = $project ORDER BY created_at, task_key;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<TaskDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadTask(reader));
        return result;
    }

    public async Task<IReadOnlyList<ExecutionAttemptTimelineDto>> ListAttemptsAsync(
        string projectId,
        string taskIdentity,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        string? taskId;
        await using (var taskCommand = Command(connection, """
            SELECT id FROM tasks
             WHERE project_id = $project AND (id = $identity OR task_key = upper($identity));
            """, ("$project", projectId), ("$identity", taskIdentity)))
        {
            taskId = Convert.ToString(await taskCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(taskId)) throw new KeyNotFoundException("Task was not found.");

        var runs = new List<RunDto>();
        await using (var runCommand = Command(connection, """
            SELECT id, task_id, status, runner_id, fence, created_at, started_at, finished_at
              FROM runs WHERE task_id = $task ORDER BY created_at, id;
            """, ("$task", taskId)))
        await using (var reader = await runCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) runs.Add(ReadRun(reader));
        }

        var result = new List<ExecutionAttemptTimelineDto>(runs.Count);
        foreach (var run in runs)
        {
            await using var outcomeCommand = Command(connection, """
                SELECT payload_json FROM events
                 WHERE run_id = $run AND kind = 'execution.outcome.classified'
                 ORDER BY cursor DESC LIMIT 1;
                """, ("$run", run.RunId));
            var payload = Convert.ToString(await outcomeCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            var decision = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<ExecutionOutcomeDecision>(payload, OutcomeJson);
            result.Add(new ExecutionAttemptTimelineDto(run, decision));
        }
        return result;
    }

    public async Task<TaskDto?> UpdateTaskAsync(string projectId, string taskIdentity, UpdateTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        TaskDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct);
            if (existing is null) return;
            if (existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException("resource-version-mismatch", $"Expected task version {request.ExpectedVersion}, current version is {existing.Version}.");
            var now = UtcNow;
            updated = existing with
            {
                Title = request.Title?.Trim() ?? existing.Title,
                Body = request.Body ?? existing.Body,
                State = request.State ?? existing.State,
                Version = existing.Version + 1,
                UpdatedAt = now,
            };
            await ExecuteAsync(connection, """
                UPDATE tasks SET title = $title, body = $body, state = $state, version = $version, updated_at = $updated
                 WHERE id = $id AND version = $expected;
                UPDATE orchestrator_contexts
                   SET hidden_at = CASE WHEN $state = '7-archive' THEN COALESCE(hidden_at, $updated) ELSE NULL END
                 WHERE task_id = $id;
                """, ct, transaction,
                ("$title", updated.Title), ("$body", updated.Body), ("$state", updated.State),
                ("$version", updated.Version), ("$updated", Iso(now)), ("$id", updated.TaskId), ("$expected", request.ExpectedVersion));
            if (updated.State is "6-completed" or "7-archive")
            {
                await SupersedeUnclaimableReviewAttemptsAsync(
                    connection,
                    transaction,
                    actorId,
                    "lane-transition",
                    updated.TaskId,
                    ct);
                var retainedThrough = now.AddDays(Math.Max(1, _options.ResultRetentionDays));
                await ExecuteAsync(connection, """
                    UPDATE result_handoffs
                       SET retain_until = CASE
                           WHEN retain_until < $retained_through THEN $retained_through
                           ELSE retain_until
                       END
                     WHERE task_id = $task;
                    """, ct, transaction,
                    ("$retained_through", Iso(retainedThrough)),
                    ("$task", updated.TaskId));
            }
            await AuditAsync(connection, transaction, actorId, "task.updated", "task", updated.TaskId,
                JsonSerializer.Serialize(new { request.ExpectedVersion, updated.Version, updated.State }), ct);
        }, ct);
        return updated;
    }

    public async Task<RunnerDto> RegisterRunnerAsync(string runnerId, RegisterRunnerRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (!TaskServerProtocol.Supports(request.ProtocolVersion))
            throw new TaskServerProtocolException(request.ProtocolVersion);
        if ((!string.IsNullOrWhiteSpace(request.HostOrchestratorMinimum)
             || !string.IsNullOrWhiteSpace(request.HostOrchestratorMaximum))
            && !HostOrchestratorContract.Overlaps(
                request.HostOrchestratorMinimum,
                request.HostOrchestratorMaximum))
        {
            throw new HostOrchestratorContractException(
                request.HostOrchestratorMinimum,
                request.HostOrchestratorMaximum);
        }
        if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.HostId))
            throw new ArgumentException("Runner host and instance ids are required.");
        var capabilities = request.Capabilities ?? [];
        if (capabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal)
            && capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal))
            throw new TaskServerConflictException(
                "runner-role-conflict",
                "Coding and review executors require separate registered service identities.");
        var activeAttempts = request.ActiveAttempts ?? [];
        if (activeAttempts.Count > 256)
            throw new ArgumentException("At most 256 active attempts may be reported during registration.");
        var expectedAttemptKind = capabilities.Contains(
            ReviewCapabilities.ReviewExecutor,
            StringComparer.Ordinal)
            ? RunnerAttemptKinds.Review
            : RunnerAttemptKinds.Coding;
        if (activeAttempts.Any(attempt => !string.Equals(
                attempt.Kind, expectedAttemptKind, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"A {expectedAttemptKind} runner may report only {expectedAttemptKind} attempts.");
        }
        if (activeAttempts
            .Select(attempt => attempt.AttemptId)
            .Distinct(StringComparer.Ordinal)
            .Count() != activeAttempts.Count)
        {
            throw new ArgumentException("Active attempt ids must be unique within a registration.");
        }

        var id = StableOrGeneratedId(runnerId, "rnr");
        var registeredAt = UtcNow;
        var now = Iso(registeredAt);
        var bootstrapMaxParallelism = Math.Clamp(request.BootstrapMaxParallelism, 1, 256);
        var managesCodingCapacity = capabilities.Contains(
            ReviewCapabilities.CodingExecutor,
            StringComparer.Ordinal);
        RuntimeCapacitySettingsDto? runtimeCapacity = null;
        IReadOnlyList<RunnerAttemptAdoption> attemptAdoptions = [];
        List<TaskServerOperationalEvent> operationalEvents = [];
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existingCapabilitiesJson = Convert.ToString(
                await ScalarAsync(
                    connection,
                    "SELECT capabilities_json FROM runners WHERE id = $id;",
                    ct,
                    transaction,
                    ("$id", id)),
                CultureInfo.InvariantCulture);
            var previousInstanceId = Convert.ToString(
                await ScalarAsync(
                    connection,
                    "SELECT instance_id FROM runners WHERE id = $id;",
                    ct,
                    transaction,
                    ("$id", id)),
                CultureInfo.InvariantCulture);
            var existingIsReviewExecutor = false;
            if (!string.IsNullOrWhiteSpace(existingCapabilitiesJson))
            {
                var existingCapabilities =
                    JsonSerializer.Deserialize<string[]>(existingCapabilitiesJson) ?? [];
                existingIsReviewExecutor = existingCapabilities.Contains(
                    ReviewCapabilities.ReviewExecutor,
                    StringComparer.Ordinal);
                var existingExecutorRole = ExecutorRole(existingCapabilities);
                var requestedExecutorRole = ExecutorRole(capabilities);
                var changesExecutorRole = existingExecutorRole is not null
                    && !string.Equals(existingExecutorRole, requestedExecutorRole, StringComparison.Ordinal);
                if (changesExecutorRole)
                    throw new TaskServerConflictException(
                        "runner-role-conflict",
                        "A registered coding or review service identity cannot remove or change its executor role.");
            }
            await ExecuteAsync(connection, """
                INSERT INTO runners(
                    id, name, host_id, instance_id, runner_version, protocol_version,
                    capabilities_json, status, registered_at, last_seen_at,
                    host_orchestrator_minimum, host_orchestrator_maximum,
                    role_max_parallelism,
                    effective_max_parallelism, runtime_capacity_applied_at,
                    runtime_capacity_applied_version)
                VALUES (
                    $id, $name, $host, $instance, $version, $protocol,
                    $capabilities, 'active', $now, $now, $hostOrchestratorMinimum,
                    $hostOrchestratorMaximum, $roleMaxParallelism, NULL, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    host_id = excluded.host_id,
                    instance_id = excluded.instance_id,
                    runner_version = excluded.runner_version,
                    protocol_version = excluded.protocol_version,
                    capabilities_json = excluded.capabilities_json,
                    host_orchestrator_minimum = excluded.host_orchestrator_minimum,
                    host_orchestrator_maximum = excluded.host_orchestrator_maximum,
                    role_max_parallelism = excluded.role_max_parallelism,
                    effective_max_parallelism = CASE
                        WHEN runners.instance_id <> excluded.instance_id
                          OR runners.host_id <> excluded.host_id
                        THEN NULL
                        ELSE runners.effective_max_parallelism
                    END,
                    runtime_capacity_applied_at = CASE
                        WHEN runners.instance_id <> excluded.instance_id
                          OR runners.host_id <> excluded.host_id
                        THEN NULL
                        ELSE runners.runtime_capacity_applied_at
                    END,
                    runtime_capacity_applied_version = CASE
                        WHEN runners.instance_id <> excluded.instance_id
                          OR runners.host_id <> excluded.host_id
                        THEN NULL
                        ELSE runners.runtime_capacity_applied_version
                    END,
                    status = CASE WHEN runners.status = 'retired' THEN 'retired' ELSE 'active' END,
                    last_seen_at = excluded.last_seen_at;
                """, ct, transaction,
                ("$id", id), ("$name", request.Name.Trim()), ("$host", request.HostId.Trim()),
                ("$instance", request.InstanceId.Trim()), ("$version", request.RunnerVersion),
                ("$protocol", request.ProtocolVersion), ("$capabilities", JsonSerializer.Serialize(capabilities)),
                ("$hostOrchestratorMinimum", request.HostOrchestratorMinimum),
                ("$hostOrchestratorMaximum", request.HostOrchestratorMaximum),
                ("$roleMaxParallelism", bootstrapMaxParallelism),
                ("$now", now));
            if (managesCodingCapacity)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO runtime_capacity_settings(
                        host_id, max_parallelism, target_load_percent, ramp_strategy,
                        version, updated_at)
                    VALUES ($host, $max, 80, 'balanced', 1, $now)
                    ON CONFLICT(host_id) DO NOTHING;
                    """, ct, transaction,
                    ("$host", request.HostId.Trim()),
                    ("$max", bootstrapMaxParallelism),
                    ("$now", now));
                runtimeCapacity = await ReadRuntimeCapacitySettingsAsync(
                    connection,
                    transaction,
                    request.HostId.Trim(),
                    ct);
            }
            attemptAdoptions = await ReAdoptRunnerAttemptsAsync(
                connection,
                transaction,
                id,
                request.HostId.Trim(),
                activeAttempts,
                request.AttemptLeaseTtlSeconds,
                ct);
            var isReviewRestart = existingIsReviewExecutor
                                  && capabilities.Contains(
                                      ReviewCapabilities.ReviewExecutor,
                                      StringComparer.Ordinal)
                                  && !string.IsNullOrWhiteSpace(previousInstanceId)
                                  && !string.Equals(
                                      previousInstanceId,
                                      request.InstanceId.Trim(),
                                      StringComparison.Ordinal);
            if (isReviewRestart)
            {
                var reviewAttempts = new List<ReviewRestartAttemptObservation>();
                foreach (var adoption in attemptAdoptions.Where(item => string.Equals(
                             item.Kind,
                             RunnerAttemptKinds.Review,
                             StringComparison.Ordinal)))
                {
                    var task = await ResolveReviewRestartTaskAsync(
                        connection,
                        transaction,
                        adoption.TaskKey,
                        ct);
                    reviewAttempts.Add(new ReviewRestartAttemptObservation(
                        adoption.AttemptId,
                        task.TaskKey,
                        adoption.Status,
                        adoption.Message ?? "no server detail",
                        task.ProjectId,
                        task.TaskId));
                }
                var lostAttempts = reviewAttempts
                    .Where(item => !string.Equals(
                        item.Status,
                        "adopted",
                        StringComparison.Ordinal))
                    .ToArray();
                await ExecuteAsync(connection, """
                    INSERT INTO runner_review_restarts(
                        runner_id, instance_id, restarted_at, reviews_lost)
                    VALUES ($runner, $instance, $at, $lost)
                    ON CONFLICT(runner_id) DO UPDATE SET
                        instance_id = excluded.instance_id,
                        restarted_at = excluded.restarted_at,
                        reviews_lost = excluded.reviews_lost;
                    """, ct, transaction,
                    ("$runner", id),
                    ("$instance", request.InstanceId.Trim()),
                    ("$at", now),
                    ("$lost", lostAttempts.Length));
                var attemptSummary = reviewAttempts.Count == 0
                    ? "none"
                    : string.Join(
                        ", ",
                        reviewAttempts.Select(item =>
                            $"{item.AttemptId}:{item.TaskKey}:{item.Status}"));
                var restartDetail = new
                {
                    audience = "supervisor",
                    runnerId = id,
                    hostId = request.HostId.Trim(),
                    previousInstanceId,
                    instanceId = request.InstanceId.Trim(),
                    restartedAt = registeredAt,
                    reviewsLost = lostAttempts.Length,
                    cause = "runner-instance-generation-changed",
                    attempts = reviewAttempts.Select(item => new
                    {
                        attemptId = item.AttemptId,
                        taskKey = item.TaskKey,
                        status = item.Status,
                        cause = item.Cause,
                    }).ToArray(),
                    message = $"Review daemon '{id}' restarted as instance "
                              + $"'{request.InstanceId.Trim()}'. Attempts={attemptSummary}; "
                              + "cause=runner instance generation changed.",
                };
                await AuditAsync(
                    connection,
                    transaction,
                    actorId,
                    "review-daemon.restarted",
                    "runner",
                    id,
                    JsonSerializer.Serialize(restartDetail),
                    ct);
                await AppendStudioStreamEventAsync(
                    connection,
                    transaction,
                    "review-daemon.restarted",
                    projectId: null,
                    taskId: null,
                    payload: restartDetail,
                    occurredAt: registeredAt,
                    ct: ct);
                operationalEvents.Add(new TaskServerOperationalEvent(
                    "review-daemon.restarted",
                    registeredAt,
                    EffectiveActor(actorId),
                    restartDetail));

                foreach (var lost in lostAttempts)
                {
                    var lossDetail = new
                    {
                        audience = "supervisor",
                        runnerId = id,
                        hostId = request.HostId.Trim(),
                        instanceId = request.InstanceId.Trim(),
                        attemptId = lost.AttemptId,
                        taskKey = lost.TaskKey,
                        status = lost.Status,
                        cause = lost.Cause,
                        message = $"Review attempt {lost.AttemptId} for card {lost.TaskKey} was lost "
                                  + $"during daemon restart. status={lost.Status}; cause={lost.Cause}.",
                    };
                    await AuditAsync(
                        connection,
                        transaction,
                        actorId,
                        "review-attempt.lost-on-restart",
                        "review-attempt",
                        lost.AttemptId,
                        JsonSerializer.Serialize(lossDetail),
                        ct);
                    await AppendStudioStreamEventAsync(
                        connection,
                        transaction,
                        "review-attempt.lost-on-restart",
                        lost.ProjectId,
                        lost.TaskId,
                        lossDetail,
                        registeredAt,
                        ct);
                    operationalEvents.Add(new TaskServerOperationalEvent(
                        "review-attempt.lost-on-restart",
                        registeredAt,
                        EffectiveActor(actorId),
                        lossDetail));
                }
            }
            if (activeAttempts.Count > 0)
            {
                var activeSlots = attemptAdoptions.Count(item =>
                    string.Equals(item.Status, "adopted", StringComparison.Ordinal));
                var observedAt = UtcNow;
                var telemetry = new HostTelemetrySnapshotDto(
                    observedAt,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    0,
                    activeSlots,
                    TaskServerConnectionStatus: "connected",
                    TaskServerConnectionObservedAt: observedAt);
                await ExecuteAsync(connection, """
                    INSERT INTO runner_telemetry_latest(runner_id, payload_json, observed_at)
                    VALUES ($runner, $payload, $observed)
                    ON CONFLICT(runner_id) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        observed_at = excluded.observed_at;
                    """, ct, transaction,
                    ("$runner", id),
                    ("$payload", JsonSerializer.Serialize(telemetry)),
                    ("$observed", Iso(observedAt)));
            }
            await AuditAsync(connection, transaction, actorId, "runner.registered", "runner", id,
                JsonSerializer.Serialize(new
                {
                    request.HostId,
                    request.InstanceId,
                    request.RunnerVersion,
                    request.ProtocolVersion,
                    request.HostOrchestratorMinimum,
                    request.HostOrchestratorMaximum,
                    bootstrapMaxParallelism,
                    runtimeCapacity?.MaxParallelism,
                    activeAttemptsReported = activeAttempts.Count,
                    activeAttemptsAdopted = attemptAdoptions.Count(item =>
                        string.Equals(item.Status, "adopted", StringComparison.Ordinal)),
                }), ct);
        }, ct);
        await PublishOperationalEventsAsync(operationalEvents, ct);
        return new RunnerDto(id, request.Name.Trim(), request.HostId.Trim(), request.InstanceId.Trim(), request.RunnerVersion,
            request.ProtocolVersion, "active", Parse(now), Parse(now), runtimeCapacity, attemptAdoptions);
    }

    private async Task<IReadOnlyList<RunnerAttemptAdoption>> ReAdoptRunnerAttemptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string hostId,
        IReadOnlyList<RunnerActiveAttempt> attempts,
        int requestedTtlSeconds,
        CancellationToken ct)
    {
        if (attempts.Count == 0) return [];
        var expiresAt = UtcNow.AddSeconds(NormalizeTtl(requestedTtlSeconds));
        var results = new List<RunnerAttemptAdoption>(attempts.Count);
        foreach (var reported in attempts)
        {
            results.Add(string.Equals(reported.Kind, RunnerAttemptKinds.Review, StringComparison.Ordinal)
                ? await ReAdoptReviewAttemptAsync(
                    connection, transaction, runnerId, hostId, reported, expiresAt, ct)
                : await ReAdoptCodingAttemptAsync(
                    connection, transaction, runnerId, reported, expiresAt, ct));
        }
        return results;
    }

    private static async Task<RunnerAttemptAdoption> ReAdoptCodingAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        RunnerActiveAttempt reported,
        DateTime expiresAt,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT t.task_key, r.status, l.lease_id, l.runner_id, l.instance_id,
                   l.fence, l.status
              FROM runs r
              JOIN tasks t ON t.id = r.task_id
              LEFT JOIN leases l ON l.run_id = r.id
             WHERE r.id = $attempt;
            """, transaction, ("$attempt", reported.AttemptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Adoption(reported, "not-found", "RunAttempt was not found.");
        var taskKey = reader.GetString(0);
        var runStatus = reader.GetString(1);
        if (reader.IsDBNull(2))
            return Adoption(reported, "invalid-state", "RunAttempt has no durable lease.");
        var leaseId = reader.GetString(2);
        var leaseRunner = reader.GetString(3);
        var leaseInstance = reader.GetString(4);
        var fence = reader.GetInt64(5);
        var leaseStatus = reader.GetString(6);
        await reader.DisposeAsync();
        if (runStatus is not ("running" or "process-unknown")
            || leaseStatus is not ("active" or "process-unknown"))
        {
            return Adoption(reported, "invalid-state", $"RunAttempt is {runStatus} with lease {leaseStatus}.");
        }
        if (!string.Equals(taskKey, reported.TaskKey, StringComparison.Ordinal)
            || !string.Equals(leaseRunner, runnerId, StringComparison.Ordinal)
            || !string.Equals(leaseInstance, reported.LeaseInstanceId, StringComparison.Ordinal)
            || !string.Equals(leaseId, reported.LeaseId, StringComparison.Ordinal)
            || fence != reported.Fence
            || reported.AuthorityEpoch != 0)
        {
            return Adoption(reported, "stale-authority", "RunAttempt authority does not match the durable server record.");
        }
        await ExecuteAsync(connection, """
            UPDATE leases
               SET status = 'active', expires_at = $expires
             WHERE run_id = $attempt;
            UPDATE runs
               SET status = 'running'
             WHERE id = $attempt;
            """, ct, transaction,
            ("$expires", Iso(expiresAt)),
            ("$attempt", reported.AttemptId));
        return Adoption(reported, "adopted", expiresAt: expiresAt);
    }

    private static async Task<RunnerAttemptAdoption> ReAdoptReviewAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string hostId,
        RunnerActiveAttempt reported,
        DateTime expiresAt,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT task_id, status, executor_id, instance_id, host_id, lease_id, fence
              FROM review_attempts
             WHERE id = $attempt;
            """, transaction, ("$attempt", reported.AttemptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Adoption(reported, "not-found", "ReviewAttempt was not found.");
        var taskKey = reader.GetString(0);
        var status = reader.GetString(1);
        var executorId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var leaseInstance = reader.IsDBNull(3) ? null : reader.GetString(3);
        var leaseHost = reader.IsDBNull(4) ? null : reader.GetString(4);
        var leaseId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var fence = reader.GetInt64(6);
        await reader.DisposeAsync();
        if (status is not ("leased" or "process-unknown") || leaseId is null)
            return Adoption(reported, "invalid-state", $"ReviewAttempt is {status}.");
        if (!string.Equals(taskKey, reported.TaskKey, StringComparison.Ordinal)
            || !string.Equals(executorId, runnerId, StringComparison.Ordinal)
            || !string.Equals(leaseHost, hostId, StringComparison.Ordinal)
            || !string.Equals(leaseInstance, reported.LeaseInstanceId, StringComparison.Ordinal)
            || !string.Equals(leaseId, reported.LeaseId, StringComparison.Ordinal)
            || fence != reported.Fence
            || reported.AuthorityEpoch != 0)
        {
            return Adoption(reported, "stale-authority", "ReviewAttempt authority does not match the durable server record.");
        }
        await ExecuteAsync(connection, """
            UPDATE review_attempts
               SET status = 'leased', expires_at = $expires
             WHERE id = $attempt;
            """, ct, transaction,
            ("$expires", Iso(expiresAt)),
            ("$attempt", reported.AttemptId));
        return Adoption(reported, "adopted", expiresAt: expiresAt);
    }

    private static RunnerAttemptAdoption Adoption(
        RunnerActiveAttempt reported,
        string status,
        string? message = null,
        DateTime? expiresAt = null)
        => new(reported.Kind, reported.AttemptId, reported.TaskKey, status, expiresAt, message);

    private static async Task<ReviewRestartTaskReference> ResolveReviewRestartTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string reportedTaskKey,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, project_id, task_key
              FROM tasks
             WHERE id = $identity
                OR task_key = $identity COLLATE NOCASE
             ORDER BY CASE WHEN id = $identity THEN 0 ELSE 1 END
             LIMIT 1;
            """, transaction, ("$identity", reportedTaskKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ReviewRestartTaskReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2))
            : new ReviewRestartTaskReference(null, null, reportedTaskKey);
    }

    private async Task PublishOperationalEventsAsync(
        IReadOnlyList<TaskServerOperationalEvent> messages,
        CancellationToken ct)
    {
        if (_operationalEvents is null) return;
        foreach (var message in messages)
        {
            try
            {
                await _operationalEvents.PublishAsync(message, ct);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    exception,
                    "task-server operational event publish failed kind={Kind}",
                    message.Kind);
            }
        }
    }

    private static string EffectiveActor(string actorId)
        => string.IsNullOrWhiteSpace(actorId) ? "anonymous-local" : actorId;

    private sealed record ReviewRestartTaskReference(
        string? TaskId,
        string? ProjectId,
        string TaskKey);

    private sealed record ReviewRestartAttemptObservation(
        string AttemptId,
        string TaskKey,
        string Status,
        string Cause,
        string? ProjectId,
        string? TaskId);

    public async Task<ClaimResponse> ClaimAsync(ClaimRequest request, string actorId, CancellationToken ct)
    {
        if (request.AvailableSlots > 0)
            RequireAdmission();
        else
            RequireWritable();
        ClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            await RecordRunnerInventoryAsync(
                connection, transaction, request.RunnerId, request.InstanceId,
                request.Inventory, actorId, ct);
            var reconciliationActions = await ReadPendingReconciliationActionsAsync(
                connection, transaction, request.RunnerId, request.InstanceId, ct);
            var capabilityRunner = await ReadCapabilityRunnerAsync(
                connection, transaction, request.RunnerId, request.InstanceId, ct);
            var runtimeCapacity = await ReadRuntimeCapacitySettingsAsync(
                connection,
                transaction,
                capabilityRunner.HostId,
                ct)
                ?? throw new InvalidOperationException(
                    $"Runtime capacity is missing for host '{capabilityRunner.HostId}'.");
            var hostProjectPolicy = await ReadHostProjectPolicyAsync(
                connection,
                transaction,
                capabilityRunner.HostId,
                ct);
            var capabilityAdmission = await EvaluateCapabilityAdmissionAsync(
                connection,
                transaction,
                request.RunnerId,
                capabilityRunner.HostId,
                request.RequiredCapabilities,
                ct);
            var reportedMaxParallelism = request.EffectiveMaxParallelism is >= 1 and <= 256
                ? request.EffectiveMaxParallelism
                : null;
            var adoption = RuntimeCapacityAdoptionPolicy.Decide(
                runtimeCapacity,
                reportedMaxParallelism,
                request.RuntimeCapacityAppliedVersion,
                capabilityRunner.RuntimeCapacityAppliedVersion);
            var observedAt = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE runners
                   SET last_seen_at = $now,
                       effective_max_parallelism = COALESCE($effective, effective_max_parallelism),
                       runtime_capacity_applied_at = CASE
                           WHEN $newConfirmation = 1
                           THEN $now
                           ELSE runtime_capacity_applied_at
                       END,
                       runtime_capacity_applied_version = CASE
                           WHEN $confirms = 1
                           THEN $appliedVersion
                           ELSE runtime_capacity_applied_version
                       END
                 WHERE id = $id;
                """, ct, transaction,
                ("$now", Iso(observedAt)),
                ("$effective", reportedMaxParallelism),
                ("$confirms", adoption.ConfirmsDesired ? 1 : 0),
                ("$newConfirmation", adoption.EmitAudit ? 1 : 0),
                ("$appliedVersion", runtimeCapacity.Version),
                ("$id", request.RunnerId));
            if (adoption.EmitAudit)
            {
                await AuditAsync(
                    connection,
                    transaction,
                    actorId,
                    "runtime-capacity.applied",
                    "runner",
                    request.RunnerId,
                    JsonSerializer.Serialize(new
                    {
                        runtimeCapacity.HostId,
                        request.InstanceId,
                        runtimeCapacity.Version,
                        runtimeCapacity.MaxParallelism,
                    }),
                    ct);
            }

            if (!capabilityAdmission.Eligible)
            {
                response = new ClaimResponse(
                    "empty",
                    Message: capabilityAdmission.Message,
                    ReconciliationActions: reconciliationActions,
                    RuntimeCapacity: runtimeCapacity);
                return;
            }
            if (request.AvailableSlots <= 0)
            {
                response = new ClaimResponse(
                    "empty",
                    Message: "Runner has no available execution slot.",
                    ReconciliationActions: reconciliationActions,
                    RuntimeCapacity: runtimeCapacity);
                return;
            }
            var occupiedHostSlots = await CountOccupiedHostSlotsAsync(
                connection,
                transaction,
                capabilityRunner.HostId,
                ct);
            if (occupiedHostSlots >= runtimeCapacity.MaxParallelism)
            {
                response = new ClaimResponse(
                    "empty",
                    Message:
                        $"Host runtime capacity is full ({occupiedHostSlots}/{runtimeCapacity.MaxParallelism}).",
                    ReconciliationActions: reconciliationActions,
                    RuntimeCapacity: runtimeCapacity);
                return;
            }

            TaskDto? task;
            await using (var command = Command(connection, """
                SELECT t.id, t.project_id, t.task_key, t.title, t.state, t.version, t.created_at, t.updated_at, t.body,
                       t.archive_state, t.archived_at
                  FROM tasks t
                 WHERE t.state = '2-ready'
                   AND NOT EXISTS (
                       SELECT 1 FROM leases l
                        WHERE l.task_id = t.id AND l.status IN ('active', 'process-unknown'))
                   AND (
                       NOT EXISTS (
                           SELECT 1 FROM host_project_policies policy
                            WHERE policy.host_id = $host)
                       OR EXISTS (
                           SELECT 1 FROM host_project_policies policy
                            WHERE policy.host_id = $host
                              AND policy.allow_all_projects = 1)
                       OR EXISTS (
                           SELECT 1 FROM host_allowed_projects allowed
                            WHERE allowed.host_id = $host
                              AND allowed.project_id = t.project_id))
                 ORDER BY t.created_at, t.task_key
                 LIMIT 1;
                """, transaction, ("$host", capabilityRunner.HostId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                task = await reader.ReadAsync(ct) ? ReadTask(reader) : null;

            if (task is null)
            {
                response = new ClaimResponse(
                    "empty",
                    Message: "No admissible task is ready.",
                    ReconciliationActions: reconciliationActions,
                    RuntimeCapacity: runtimeCapacity);
                return;
            }

            var fence = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT last_fence FROM fence_counters WHERE task_id = $task;", ct, transaction, ("$task", task.TaskId))
                ?? 0L, CultureInfo.InvariantCulture) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO fence_counters(task_id, last_fence) VALUES ($task, $fence)
                ON CONFLICT(task_id) DO UPDATE SET last_fence = excluded.last_fence;
                """, ct, transaction, ("$task", task.TaskId), ("$fence", fence));

            var runId = $"run_{Guid.NewGuid():N}";
            var leaseId = $"lse_{Guid.NewGuid():N}";
            var now = UtcNow;
            var expires = now.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, """
                INSERT INTO runs(
                    id, task_id, status, runner_id, fence, created_at, started_at,
                    required_capabilities_json, canary_capabilities_json)
                VALUES (
                    $run, $task, 'running', $runner, $fence, $now, $now,
                    $requiredCapabilities, $canaryCapabilities);
                INSERT INTO leases(task_id, lease_id, run_id, runner_id, instance_id, fence, acquired_at, expires_at, status)
                VALUES ($task, $lease, $run, $runner, $instance, $fence, $now, $expires, 'active');
                UPDATE tasks SET state = '3-progress', version = version + 1, updated_at = $now WHERE id = $task;
                """, ct, transaction,
                ("$run", runId), ("$task", task.TaskId), ("$runner", request.RunnerId), ("$instance", request.InstanceId),
                ("$fence", fence), ("$lease", leaseId), ("$now", Iso(now)), ("$expires", Iso(expires)),
                ("$requiredCapabilities", JsonSerializer.Serialize(capabilityAdmission.Required)),
                ("$canaryCapabilities", JsonSerializer.Serialize(capabilityAdmission.Canaries)));
            await ReserveCanariesAsync(
                connection,
                transaction,
                request.RunnerId,
                capabilityAdmission.Canaries,
                runId,
                ct);
            await AuditAsync(connection, transaction, actorId, "run.claimed", "run", runId,
                JsonSerializer.Serialize(new
                {
                    task.TaskId,
                    task.ProjectId,
                    request.RunnerId,
                    request.InstanceId,
                    fence,
                    hostProjectPolicyVersion = hostProjectPolicy?.Version,
                }), ct);

            var run = new RunDto(runId, task.TaskId, "running", request.RunnerId, fence, now, now, null);
            var lease = new LeaseDto(leaseId, runId, task.TaskId, request.RunnerId, request.InstanceId, fence, now, expires, "active");
            response = new ClaimResponse(
                "claimed",
                run,
                task with { State = "3-progress", Version = task.Version + 1, UpdatedAt = now },
                lease,
                ReconciliationActions: reconciliationActions,
                RequiredCapabilities: capabilityAdmission.Required,
                CanaryCapabilities: capabilityAdmission.Canaries,
                RuntimeCapacity: runtimeCapacity);
        }, ct);
        return response!;
    }

    public async Task<LeaseResponse> RenewLeaseAsync(string runId, LeaseRenewRequest request, string actorId, CancellationToken ct)
    {
        // Draining closes new admission, not heartbeat renewal for work that is
        // already fenced. ReadOnly and Maintenance still block the write.
        RequireWritable();
        LeaseDto? renewed = null;
        IReadOnlyList<RunnerReconciliationAction> reconciliationActions = [];
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId, request.LeaseId, request.Fence);
            await RecordRunnerInventoryAsync(
                connection, transaction, request.RunnerId, request.InstanceId,
                request.Inventory, actorId, ct);
            reconciliationActions = await ReadPendingReconciliationActionsAsync(
                connection, transaction, request.RunnerId, request.InstanceId, ct);
            if (!string.Equals(lease.Status, "active", StringComparison.Ordinal))
                throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
            if (lease.ExpiresAt <= UtcNow)
            {
                throw new TaskServerConflictException("lease-expired-process-unknown", "Lease expired. Positive containment proof is required before recovery.");
            }
            var expires = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, "UPDATE leases SET expires_at = $expires WHERE run_id = $run;", ct, transaction,
                ("$expires", Iso(expires)), ("$run", runId));
            await ExecuteAsync(connection, "UPDATE runners SET last_seen_at = $now WHERE id = $runner;", ct, transaction,
                ("$now", Iso(UtcNow)), ("$runner", request.RunnerId));
            renewed = lease with { ExpiresAt = expires };
            await AuditAsync(connection, transaction, actorId, "lease.renewed", "run", runId,
                JsonSerializer.Serialize(new { request.Fence, expiresAt = expires }), ct);
        }, ct);
        return new LeaseResponse(
            "renewed",
            renewed,
            ReconciliationActions: reconciliationActions);
    }

    public async Task<LeaseResponse> ReleaseLeaseAsync(string runId, LeaseReleaseRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        LeaseDto? released = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId, request.LeaseId, request.Fence);
            if (!string.Equals(lease.Status, "active", StringComparison.Ordinal))
                throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'released' WHERE run_id = $run;
                UPDATE runs SET status = $outcome, finished_at = $now WHERE id = $run;
                UPDATE tasks SET state = '2-ready', version = version + 1, updated_at = $now
                 WHERE id = $task AND state = '3-progress';
                UPDATE work_permits SET status = 'released'
                 WHERE run_id = $run AND status = 'accepted';
                """, ct, transaction, ("$run", runId), ("$outcome", request.Outcome),
                ("$now", Iso(UtcNow)), ("$task", lease.TaskId));
            released = lease with { Status = "released" };
            await AuditAsync(connection, transaction, actorId, "lease.released", "run", runId,
                JsonSerializer.Serialize(new { request.Fence, request.Outcome }), ct);
        }, ct);
        return new LeaseResponse("released", released);
    }

    public async Task<ResultHandoffAck> AcknowledgeResultHandoffAsync(
        string runId,
        ResultHandoffRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ResultEnvelopeDigest.Validate(request.Envelope);
        if (!string.Equals(request.Envelope.SourceRunAttemptId, runId, StringComparison.Ordinal))
            throw new ArgumentException("Result envelope sourceRunAttemptId must match the route run id.");
        var computedDigest = ResultEnvelopeDigest.Compute(request.Envelope);
        if (!string.Equals(computedDigest, request.EnvelopeDigest, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Result envelope digest does not match the canonical envelope.");

        ResultHandoffAck? acknowledgement = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadResultHandoffAsync(connection, transaction, runId, ct);
            if (existing is not null)
            {
                ValidateHandoffReplay(existing, request);
                await RestoreHandoffReplayAuthorityAsync(
                    connection, transaction, runId, request, ct);
                acknowledgement = existing.Acknowledgement with { Replay = true };
                return;
            }

            var idempotent = await ReadResultHandoffByKeyAsync(
                connection, transaction, request.IdempotencyKey, ct);
            if (idempotent is not null)
            {
                ValidateHandoffReplay(idempotent, request);
                await RestoreHandoffReplayAuthorityAsync(
                    connection, transaction, runId, request, ct);
                acknowledgement = idempotent.Acknowledgement with { Replay = true };
                return;
            }

            // Existing acknowledgements replay their exact historical envelope,
            // including the pre-BP-01 ref form. Every new handoff must use the
            // current attempt + fence + SHA identity.
            ValidateImmutableSource(runId, request.Fence, request.Envelope);
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(
                lease,
                request.RunnerId,
                request.InstanceId,
                request.LeaseId,
                request.Fence);
            await EnsureOutboxLeaseCurrentAsync(connection, transaction, lease, ct);
            var now = UtcNow;
            var retainUntil = now.AddDays(Math.Max(1, _options.ResultRetentionDays));
            await RecordOutboxSequenceAsync(
                connection,
                transaction,
                runId,
                request.Sequence,
                request.IdempotencyKey,
                "result-handoff",
                ct);
            await ExecuteAsync(connection, """
                INSERT INTO result_handoffs(
                    run_id, task_id, runner_id, instance_id, lease_id, fence,
                    repository_id, repository_url, source_run_attempt_id,
                    base_sha, result_sha, immutable_remote_ref, source_bundle_digest,
                    artifact_manifest_digest, submodules_json, lfs_objects_json,
                    envelope_digest, sequence, idempotency_key, acknowledged_at, retain_until)
                VALUES (
                    $run, $task, $runner, $instance, $lease, $fence,
                    $repository, $repository_url, $source_run, $base_sha, $result_sha,
                    $remote_ref, $bundle_digest, $manifest_digest, $submodules,
                    $lfs, $envelope_digest, $sequence, $key, $acknowledged, $retain_until);
                """, ct, transaction,
                ("$run", runId),
                ("$task", lease.TaskId),
                ("$runner", request.RunnerId),
                ("$instance", request.InstanceId),
                ("$lease", request.LeaseId),
                ("$fence", request.Fence),
                ("$repository", request.Envelope.RepositoryId),
                ("$repository_url", request.Envelope.RepositoryUrl),
                ("$source_run", request.Envelope.SourceRunAttemptId),
                ("$base_sha", request.Envelope.BaseSha.ToLowerInvariant()),
                ("$result_sha", request.Envelope.ResultSha.ToLowerInvariant()),
                ("$remote_ref", request.Envelope.ImmutableRemoteRef),
                ("$bundle_digest", request.Envelope.SourceBundleDigest),
                ("$manifest_digest", request.Envelope.ArtifactManifestDigest.ToLowerInvariant()),
                ("$submodules", JsonSerializer.Serialize(request.Envelope.Submodules ?? [])),
                ("$lfs", JsonSerializer.Serialize(request.Envelope.LfsObjects ?? [])),
                ("$envelope_digest", computedDigest),
                ("$sequence", request.Sequence),
                ("$key", request.IdempotencyKey),
                ("$acknowledged", Iso(now)),
                ("$retain_until", Iso(retainUntil)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "result-handoff.acknowledged",
                "run",
                runId,
                JsonSerializer.Serialize(new
                {
                    request.Sequence,
                    envelopeDigest = computedDigest,
                    request.Envelope.RepositoryId,
                    request.Envelope.ResultSha,
                    request.Envelope.ImmutableRemoteRef,
                    request.Envelope.SourceBundleDigest,
                    retainUntil,
                }),
                ct);
            acknowledgement = new ResultHandoffAck(
                runId,
                request.Sequence,
                computedDigest,
                "acknowledged",
                now,
                retainUntil,
                false);
        }, ct);
        return acknowledgement!;
    }

    public async Task<ResultHandoffDto?> GetResultHandoffAsync(
        string runId,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT run_id, repository_id, source_run_attempt_id, base_sha,
                   result_sha, immutable_remote_ref, source_bundle_digest,
                   artifact_manifest_digest, submodules_json, lfs_objects_json,
                   envelope_digest, sequence, acknowledged_at, retain_until,
                   repository_url
              FROM result_handoffs
             WHERE run_id = $run;
            """, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var envelope = new ImmutableResultEnvelope(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            JsonSerializer.Deserialize<List<ResultDependencyIdentity>>(reader.GetString(8)),
            JsonSerializer.Deserialize<List<ResultDependencyIdentity>>(reader.GetString(9)),
            reader.IsDBNull(14) ? null : reader.GetString(14));
        return new ResultHandoffDto(
            reader.GetString(0),
            envelope,
            reader.GetString(10),
            reader.GetInt64(11),
            Parse(reader.GetString(12)),
            Parse(reader.GetString(13)));
    }

    public async Task<RunDto> CompleteRunAsync(string runId, CompleteRunRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        RunDto? completed = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            if (request.OutcomeDecision is not null
                && !string.Equals(
                    request.Outcome,
                    request.OutcomeDecision.Outcome.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new TaskServerConflictException(
                    "outcome-decision-mismatch",
                    "The run outcome does not match the shared typed outcome decision.");
            }

            var outcomePayload = request.OutcomeDecision is null
                ? null
                : JsonSerializer.Serialize(request.OutcomeDecision, OutcomeJson);
            var outcomeKey = $"execution-outcome:{runId}:{request.OutcomeDecision?.ClassifierVersion}";
            var prior = await ReadRunCompletionAsync(connection, transaction, runId, ct);
            if (prior is not null)
            {
                ValidateCompletionReplay(prior, request);
                if (outcomePayload is not null)
                {
                    var priorOutcome = await ReadEventByIdempotencyKeyAsync(
                        connection,
                        transaction,
                        outcomeKey,
                        ct);
                    if (priorOutcome is null
                        || !string.Equals(
                            priorOutcome.PayloadJson,
                            outcomePayload,
                            StringComparison.Ordinal))
                    {
                        throw new TaskServerConflictException(
                            "completion-conflict",
                            "The run is already completed with different classified facts.");
                    }
                }
                completed = prior.Run;
                return;
            }

            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId, request.LeaseId, request.Fence);
            await EnsureOutboxLeaseCurrentAsync(
                connection,
                transaction,
                lease,
                request.RunnerId,
                request.InstanceId,
                request.LeaseId,
                ct);
            var incompletePostSteps = Convert.ToInt32(await ScalarAsync(connection, """
                SELECT count(*)
                  FROM post_step_executions
                 WHERE run_id = $run AND status <> 'completed';
                """, ct, transaction, ("$run", runId)) ?? 0L, CultureInfo.InvariantCulture);
            if (incompletePostSteps > 0)
            {
                throw new TaskServerConflictException(
                    "host-post-processing-incomplete",
                    $"{incompletePostSteps} host post-processing step(s) are not complete.");
            }
            StoredResultHandoff? resultHandoff = null;
            if (RequiresResultEnvelope(request.Outcome))
            {
                if (string.IsNullOrWhiteSpace(request.ResultEnvelopeDigest))
                    throw new TaskServerConflictException(
                        "result-handoff-required",
                        "Successful coding completion requires an acknowledged immutable result envelope.");
                resultHandoff = await ReadResultHandoffAsync(connection, transaction, runId, ct);
                if (resultHandoff is null
                    || !string.Equals(
                        resultHandoff.Acknowledgement.EnvelopeDigest,
                        request.ResultEnvelopeDigest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new TaskServerConflictException(
                        "result-handoff-required",
                        "Successful coding completion requires the matching acknowledged immutable result envelope.");
                }
            }
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.Sequence is null)
                throw new ArgumentException("Run completion idempotencyKey and monotonic sequence are required.");
            await RecordOutboxSequenceAsync(
                connection,
                transaction,
                runId,
                request.Sequence,
                request.IdempotencyKey,
                "completion",
                ct);
            var now = UtcNow;
            if (request.OutcomeDecision is not null)
            {
                if (!string.Equals(request.OutcomeDecision.RawFacts.AttemptId, runId, StringComparison.Ordinal))
                    throw new TaskServerConflictException("attempt-identity-mismatch", "Outcome facts are not bound to this immutable attempt.");

                var eventId = DeterministicId("evt", outcomeKey);
                var eventRequest = new EventIngestRequest(
                    eventId,
                    "execution.outcome.classified",
                    outcomePayload!,
                    outcomeKey,
                    request.Fence,
                    now);
                await ExecuteAsync(connection, """
                    INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
                    VALUES ($event, $run, $task, $kind, $payload, $key, $fence, $occurred)
                    ON CONFLICT(idempotency_key) DO NOTHING;
                    """, ct, transaction,
                    ("$event", eventId), ("$run", runId), ("$task", lease.TaskId),
                    ("$kind", eventRequest.Kind), ("$payload", outcomePayload), ("$key", outcomeKey),
                    ("$fence", request.Fence), ("$occurred", Iso(now)));
                var persisted = await ReadEventByIdempotencyKeyAsync(connection, transaction, outcomeKey, ct);
                ValidateEventReplay(persisted, runId, lease.TaskId, eventRequest);
            }
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'completed' WHERE run_id = $run;
                UPDATE runs
                   SET status = $outcome,
                       finished_at = $now,
                       result_sha = $resultSha,
                       repository_id = $repositoryId,
                       repository_url = $repositoryUrl,
                       result_ref = $resultRef,
                       source_bundle_artifact_id = NULL,
                       source_bundle_sha256 = $bundleSha
                 WHERE id = $run;
                UPDATE tasks SET state = '4-auto-review', version = version + 1, updated_at = $now WHERE id = $task;
                UPDATE work_permits SET status = 'completed'
                 WHERE run_id = $run AND status = 'accepted';
                INSERT INTO run_completions(
                    run_id, outcome, summary, envelope_digest, sequence,
                    idempotency_key, completed_at)
                VALUES (
                    $run, $outcome, $summary, $envelope_digest, $sequence,
                    $key, $now);
                """, ct, transaction,
                ("$run", runId),
                ("$outcome", request.Outcome),
                ("$summary", request.Summary),
                ("$envelope_digest", request.ResultEnvelopeDigest),
                ("$sequence", request.Sequence),
                ("$key", request.IdempotencyKey),
                ("$now", Iso(now)),
                ("$task", lease.TaskId),
                ("$resultSha", resultHandoff?.Envelope.ResultSha),
                ("$repositoryId", resultHandoff?.Envelope.RepositoryId),
                ("$repositoryUrl", resultHandoff?.Envelope.RepositoryUrl),
                ("$resultRef", resultHandoff?.Envelope.ImmutableRemoteRef),
                ("$bundleSha", resultHandoff?.Envelope.SourceBundleDigest));
            await ResolveCanarySuccessAsync(
                connection,
                transaction,
                request.RunnerId,
                runId,
                RequiresResultEnvelope(request.Outcome)
                    ? "coding canary completed with an immutable result handoff"
                    : "coding canary reached an authoritative typed terminal without a capability failure",
                ct);
            await AppendLifecycleEventAsync(
                connection,
                transaction,
                runId,
                lease.TaskId,
                lease.Fence,
                LifecycleEventKinds.RunCompleted,
                new
                {
                    request.Outcome,
                    request.Summary,
                    authority = "task-server",
                    nextState = "4-auto-review",
                },
                ct);
            await AppendLifecycleEventAsync(
                connection,
                transaction,
                runId,
                lease.TaskId,
                lease.Fence,
                LifecycleEventKinds.PostProcessingCompleted,
                new
                {
                    artifacts = "canonical-store",
                    reviewAuthority = "task-server",
                },
                ct);
            await AuditAsync(connection, transaction, actorId, "run.completed", "run", runId,
                JsonSerializer.Serialize(new
                {
                    request.Fence,
                    request.Outcome,
                    request.Summary,
                    request.ResultEnvelopeDigest,
                    request.Sequence,
                    request.IdempotencyKey,
                    classifierVersion = request.OutcomeDecision?.ClassifierVersion,
                    recoveryAction = request.OutcomeDecision?.RecoveryAction.ToString(),
                }), ct);
            completed = new RunDto(
                runId,
                lease.TaskId,
                request.Outcome,
                request.RunnerId,
                request.Fence,
                lease.AcquiredAt,
                lease.AcquiredAt,
                now,
                resultHandoff?.Envelope.ResultSha,
                resultHandoff?.Envelope.RepositoryId);
        }, ct);
        return completed!;
    }

    public async Task<EventDto> IngestEventAsync(string runId, EventIngestRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (Encoding.UTF8.GetByteCount(request.PayloadJson) > _options.MaximumEventPayloadBytes)
            throw new ArgumentException(
                $"Event payload exceeds the {_options.MaximumEventPayloadBytes}-byte limit.");
        EventDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replay = await ReadEventByIdempotencyKeyAsync(
                connection, transaction, request.IdempotencyKey, ct);
            if (replay is not null)
            {
                ValidateEventReplay(replay, runId, replay.TaskId, request);
                result = replay;
                return;
            }
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            if (lease.Fence != request.Fence)
                throw new TaskServerConflictException("stale-fence", "Event fence does not match the run authority.");
            await EnsureOutboxLeaseCurrentAsync(
                connection,
                transaction,
                lease,
                request.RunnerId,
                request.InstanceId,
                request.LeaseId,
                ct);
            await RecordOutboxSequenceAsync(
                connection,
                transaction,
                runId,
                request.Sequence,
                request.IdempotencyKey,
                "event",
                ct);
            var occurred = request.OccurredAt?.ToUniversalTime() ?? UtcNow;
            var inserted = await ExecuteAsync(connection, """
                INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at, sequence)
                VALUES ($event, $run, $task, $kind, $payload, $key, $fence, $occurred, $sequence)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """, ct, transaction,
                ("$event", request.EventId), ("$run", runId), ("$task", lease.TaskId), ("$kind", request.Kind),
                ("$payload", request.PayloadJson), ("$key", request.IdempotencyKey), ("$fence", request.Fence),
                ("$occurred", Iso(occurred)), ("$sequence", request.Sequence));
            result = await ReadEventByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, ct);
            ValidateEventReplay(result, runId, lease.TaskId, request);
            if (inserted > 0)
                await AuditAsync(connection, transaction, actorId, "event.ingested", "run", runId,
                    JsonSerializer.Serialize(new { request.EventId, request.Kind, request.IdempotencyKey, request.Fence }), ct);
        }, ct);
        return result!;
    }

    public async Task<IReadOnlyList<EventDto>> ListEventsAsync(string runId, long after, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT cursor, event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at, sequence
              FROM events WHERE run_id = $run AND cursor > $after ORDER BY cursor LIMIT 1000;
            """, ("$run", runId), ("$after", after));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<EventDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadEvent(reader));
        return result;
    }

    public async Task<ArtifactDto> IngestArtifactAsync(string runId, ArtifactIngestRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ArtifactDto? result = null;
        byte[] content;
        try { content = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("Artifact content is not valid base64."); }
        var actualSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualSha, request.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Artifact SHA-256 does not match the uploaded content.");

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replay = await ReadArtifactByIdempotencyKeyAsync(
                connection, transaction, request.IdempotencyKey, ct);
            if (replay is not null)
            {
                ValidateArtifactReplay(replay, runId, request, actualSha, content.LongLength);
                result = replay;
                return;
            }
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            if (lease.Fence != request.Fence)
                throw new TaskServerConflictException("stale-fence", "Artifact fence does not match the run authority.");
            await EnsureOutboxLeaseCurrentAsync(
                connection,
                transaction,
                lease,
                request.RunnerId,
                request.InstanceId,
                request.LeaseId,
                ct);
            await RecordOutboxSequenceAsync(
                connection,
                transaction,
                runId,
                request.Sequence,
                request.IdempotencyKey,
                "artifact",
                ct);
            var now = UtcNow;
            var inserted = await ExecuteAsync(connection, """
                INSERT INTO artifacts(id, run_id, name, media_type, sha256, content, size_bytes, idempotency_key, fence, created_at, sequence)
                VALUES ($id, $run, $name, $media, $sha, $content, $size, $key, $fence, $now, $sequence)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """, ct, transaction,
                ("$id", request.ArtifactId), ("$run", runId), ("$name", request.Name), ("$media", request.MediaType),
                ("$sha", actualSha), ("$content", content), ("$size", content.LongLength), ("$key", request.IdempotencyKey),
                ("$fence", request.Fence), ("$now", Iso(now)), ("$sequence", request.Sequence));
            result = await ReadArtifactByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, ct);
            ValidateArtifactReplay(result, runId, request, actualSha, content.LongLength);
            if (inserted > 0)
                await AuditAsync(connection, transaction, actorId, "artifact.ingested", "run", runId,
                    JsonSerializer.Serialize(new { request.ArtifactId, request.Name, sha256 = actualSha, request.Fence }), ct);
        }, ct);
        return result!;
    }

    public async Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(string runId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, run_id, name, media_type, sha256, size_bytes, idempotency_key, fence, created_at, sequence
              FROM artifacts WHERE run_id = $run ORDER BY created_at, id;
            """, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArtifactDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadArtifact(reader));
        return result;
    }

    public async Task<RunnerOutboxStatusDto> ReportRunnerOutboxAsync(
        string runnerId,
        RunnerOutboxStatusRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (request.LastAcknowledgedSequence > request.LastSequence)
            throw new ArgumentException("Outbox acknowledgement cannot exceed its last sequence.");
        if (request.BacklogCount < 0)
            throw new ArgumentException("Outbox backlog cannot be negative.");
        if (string.IsNullOrWhiteSpace(request.RunId))
            throw new ArgumentException("Outbox status runId is required.");
        if ((request.BacklogCount == 0) != (request.OldestUnacknowledgedSequence is null))
            throw new ArgumentException("Oldest unacknowledged sequence must be present exactly when backlog is non-zero.");

        RunnerOutboxStatusDto? status = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, runnerId, request.InstanceId, ct);
            var existingSequence = Convert.ToInt64(
                await ScalarAsync(
                    connection,
                    "SELECT last_sequence FROM runner_outbox_status WHERE runner_id = $runner AND run_id = $run;",
                    ct,
                    transaction,
                    ("$runner", runnerId),
                    ("$run", request.RunId)) ?? 0L,
                CultureInfo.InvariantCulture);
            if (request.LastSequence < existingSequence)
                throw new TaskServerConflictException(
                    "stale-outbox-status",
                    $"Outbox status sequence {request.LastSequence} is older than {existingSequence}.");
            await ExecuteAsync(connection, """
                INSERT INTO runner_outbox_status(
                    runner_id, instance_id, last_sequence, last_acknowledged_sequence,
                    backlog_count, oldest_unacknowledged_sequence, final_handoff_state,
                    run_id, envelope_digest, observed_at)
                VALUES (
                    $runner, $instance, $last, $acknowledged, $backlog, $oldest,
                    $state, $run, $digest, $observed)
                ON CONFLICT(runner_id, run_id) DO UPDATE SET
                    instance_id = excluded.instance_id,
                    last_sequence = excluded.last_sequence,
                    last_acknowledged_sequence = excluded.last_acknowledged_sequence,
                    backlog_count = excluded.backlog_count,
                    oldest_unacknowledged_sequence = excluded.oldest_unacknowledged_sequence,
                    final_handoff_state = excluded.final_handoff_state,
                    run_id = excluded.run_id,
                    envelope_digest = excluded.envelope_digest,
                    observed_at = excluded.observed_at;
                """, ct, transaction,
                ("$runner", runnerId),
                ("$instance", request.InstanceId),
                ("$last", request.LastSequence),
                ("$acknowledged", request.LastAcknowledgedSequence),
                ("$backlog", request.BacklogCount),
                ("$oldest", request.OldestUnacknowledgedSequence),
                ("$state", request.FinalHandoffState),
                ("$run", request.RunId),
                ("$digest", request.EnvelopeDigest),
                ("$observed", Iso(request.ObservedAt.ToUniversalTime())));
            await RefreshOutboxSummaryAsync(connection, ct, transaction);
            status = new RunnerOutboxStatusDto(
                runnerId,
                request.InstanceId,
                request.LastSequence,
                request.LastAcknowledgedSequence,
                request.BacklogCount,
                request.OldestUnacknowledgedSequence,
                request.FinalHandoffState,
                request.RunId,
                request.EnvelopeDigest,
                request.ObservedAt.ToUniversalTime());
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "runner.outbox.observed",
                "runner",
                runnerId,
                JsonSerializer.Serialize(new
                {
                    request.BacklogCount,
                    request.OldestUnacknowledgedSequence,
                    request.FinalHandoffState,
                    request.RunId,
                }),
                ct);
        }, ct);
        return status!;
    }

    public async Task<IReadOnlyList<RunnerOutboxStatusDto>> ListRunnerOutboxesAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT runner_id, instance_id, last_sequence, last_acknowledged_sequence,
                   backlog_count, oldest_unacknowledged_sequence, final_handoff_state,
                   run_id, envelope_digest, observed_at
              FROM runner_outbox_status
             ORDER BY runner_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<RunnerOutboxStatusDto>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RunnerOutboxStatusDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                Parse(reader.GetString(9))));
        }
        return result;
    }

    public async Task<TaskServerStatusDto> ChangeModeAsync(ChangeModeRequest request, string actorId, CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A maintenance reason is required.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await SetMetaAsync(connection, transaction, "mode", request.Mode.ToString(), ct);
            await AuditAsync(connection, transaction, actorId, "server.mode.changed", "server", _serverId,
                JsonSerializer.Serialize(new { from = _mode, to = request.Mode, request.Reason }), ct);
            _mode = request.Mode;
        }, ct, requireReady: false);
        return Status();
    }

    public async Task<PrepareShutdownResult> PrepareShutdownAsync(PrepareShutdownRequest request, string actorId, CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode is not TaskServerMode.Draining and not TaskServerMode.Maintenance)
            throw new TaskServerConflictException("drain-required", "Enter draining mode before safe shutdown preparation.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A shutdown reason is required.");
        PrepareShutdownResult? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var unresolved = Convert.ToInt32(await ScalarAsync(connection,
                """
                SELECT
                    (SELECT count(*) FROM leases
                      WHERE status IN ('active', 'process-unknown'))
                  + (SELECT count(*) FROM review_attempts
                      WHERE status IN ('leased', 'process-unknown'));
                """, ct, transaction) ?? 0L,
                CultureInfo.InvariantCulture);
            if (unresolved > 0)
            {
                result = new PrepareShutdownResult(false, unresolved, _mode, "Active or process-unknown attempts still hold durable authority.");
                await AuditAsync(connection, transaction, actorId, "server.shutdown.deferred", "server", _serverId,
                    JsonSerializer.Serialize(new { request.Reason, unresolved }), ct);
                return;
            }
            await SetMetaAsync(connection, transaction, "mode", TaskServerMode.Maintenance.ToString(), ct);
            _mode = TaskServerMode.Maintenance;
            result = new PrepareShutdownResult(true, 0, _mode, "No attempt authority is unresolved. The supervised process may stop.");
            await AuditAsync(connection, transaction, actorId, "server.shutdown.prepared", "server", _serverId,
                JsonSerializer.Serialize(new { request.Reason }), ct);
        }, ct, requireReady: false);
        return result!;
    }

    public async Task<LeaseResponse> ResolveUnknownAttemptAsync(string runId, ResolveUnknownAttemptRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.ContainmentProof))
            throw new ArgumentException("Positive containment proof is required.");
        LeaseDto? resolved = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            if (!string.Equals(lease.Status, "process-unknown", StringComparison.Ordinal))
                throw new TaskServerConflictException("attempt-not-unknown", $"Lease status is '{lease.Status}'.");
            var targetState = string.Equals(request.Resolution, "requeue", StringComparison.OrdinalIgnoreCase)
                ? "2-ready"
                : "5-human-review";
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'fenced' WHERE run_id = $run;
                UPDATE runs SET status = 'interrupted', finished_at = $now WHERE id = $run;
                UPDATE tasks SET state = $state, version = version + 1, updated_at = $now WHERE id = $task;
                UPDATE work_permits SET status = 'fenced'
                 WHERE run_id = $run AND status = 'accepted';
                """, ct, transaction, ("$run", runId), ("$now", Iso(UtcNow)), ("$state", targetState), ("$task", lease.TaskId));
            await AppendLifecycleEventAsync(
                connection,
                transaction,
                runId,
                lease.TaskId,
                lease.Fence,
                LifecycleEventKinds.RunnerUnavailable,
                new
                {
                    runnerId = lease.RunnerId,
                    instanceId = lease.InstanceId,
                    request.ContainmentProof,
                    observation = "The previous Runner generation is positively unavailable.",
                },
                ct);
            await AppendLifecycleEventAsync(
                connection,
                transaction,
                runId,
                lease.TaskId,
                lease.Fence,
                LifecycleEventKinds.NoOverlapProven,
                new
                {
                    request.ContainmentProof,
                    request.Resolution,
                    targetState,
                    previousState = "process-unknown",
                },
                ct);
            await AuditAsync(connection, transaction, actorId, "attempt.unknown.resolved", "run", runId,
                JsonSerializer.Serialize(new { request.ContainmentProof, request.Resolution, lease.Fence }), ct);
            resolved = lease with { Status = "fenced" };
        }, ct);
        return new LeaseResponse("fenced", resolved, "The old authority was closed using audited containment proof.");
    }

    public async Task<BackupResult> CreateBackupAsync(BackupRequest request, string actorId, CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Task Server is not ready.");
        await _writeGate.WaitAsync(ct);
        try
        {
            var safeName = SanitizeBackupName(request.Name);
            var backupId = $"{UtcNow:yyyyMMddHHmmssfff}-{safeName}-{Guid.NewGuid():N}";
            var path = Path.Combine(BackupDirectory, backupId + ".db");
            await using var source = Open();
            await source.OpenAsync(ct);
            await ConfigureConnectionAsync(source, ct);
            // Write and verify the snapshot inside a nested scope so the destination
            // connection is fully closed before the file is hashed or the .db is later
            // moved/deleted. Pooling stays off so dispose actually releases the OS file
            // handle instead of parking it in the pool ("used by another process").
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
            {
                await destination.OpenAsync(ct);
                source.BackupDatabase(destination);
                var integrity = Convert.ToString(await ScalarAsync(destination, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Backup integrity check failed: {integrity}");
            }
            var sha = await HashFileAsync(path, ct);
            var info = new FileInfo(path);
            await using var transaction = (SqliteTransaction)await source.BeginTransactionAsync(ct);
            await AuditAsync(source, transaction, actorId, "backup.created", "backup", backupId,
                JsonSerializer.Serialize(new { sha256 = sha, info.Length }), ct);
            await transaction.CommitAsync(ct);
            return new BackupResult(backupId, path, sha, UtcNow, info.Length);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<RestoreResult> RestoreBackupAsync(RestoreRequest request, string actorId, CancellationToken ct)
    {
        var path = ResolveBackupPath(request.BackupId);
        if (!File.Exists(path)) throw new KeyNotFoundException("Backup was not found.");
        var sha = await HashFileAsync(path, ct);
        await using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            // Pooling off so the backup file handle is released on dispose and the
            // .db can be moved/deleted afterwards.
            Pooling = false,
        }.ToString()))
        {
            await verify.OpenAsync(ct);
            var integrity = Convert.ToString(await ScalarAsync(verify, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return new RestoreResult(request.BackupId, false, false, sha, $"Integrity check failed: {integrity}");
        }

        if (request.VerifyOnly)
            return new RestoreResult(request.BackupId, true, false, sha, "Backup verified; no data was changed.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Restore requires maintenance mode.");

        await _writeGate.WaitAsync(ct);
        try
        {
            var safety = DatabasePath + $".pre-restore-{Guid.NewGuid():N}";
            await using (var current = Open())
            {
                await current.OpenAsync(ct);
                await ConfigureConnectionAsync(current, ct);
                var unresolved = Convert.ToInt64(await ScalarAsync(current,
                    """
                    SELECT
                        (SELECT count(*) FROM leases
                          WHERE status IN ('active', 'process-unknown'))
                      + (SELECT count(*) FROM review_attempts
                          WHERE status IN ('leased', 'process-unknown'));
                    """, ct) ?? 0L, CultureInfo.InvariantCulture);
                if (unresolved > 0)
                    throw new TaskServerConflictException("attempt-authority-unresolved", "Restore is blocked while active or process-unknown attempts exist.");

                await using var safetyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = safety,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
                await safetyConnection.OpenAsync(ct);
                current.BackupDatabase(safetyConnection);
            }

            AuthorityReady = false;
            var staging = DatabasePath + ".restore";
            try
            {
                File.Copy(path, staging, overwrite: true);
                DeleteDatabaseSidecars();
                File.Move(staging, DatabasePath, overwrite: true);

                await using (var restored = Open())
                {
                    await restored.OpenAsync(ct);
                    await ConfigureConnectionAsync(restored, ct);
                    await ApplyMigrationsAsync(restored, ct);
                    var integrity = Convert.ToString(await ScalarAsync(restored, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Restored store integrity check failed: {integrity}");
                    _serverId = await GetOrCreateMetaAsync(restored, "server_id", $"srv_{Guid.NewGuid():N}", ct);
                    await using var transaction = (SqliteTransaction)await restored.BeginTransactionAsync(ct);
                    await SetMetaAsync(restored, transaction, "mode", TaskServerMode.Maintenance.ToString(), ct);
                    _mode = TaskServerMode.Maintenance;
                    await AuditAsync(restored, transaction, actorId, "backup.restored", "backup", request.BackupId,
                        JsonSerializer.Serialize(new { sha256 = sha }), ct);
                    await transaction.CommitAsync(ct);
                }

                AuthorityReady = true;
                File.Delete(safety);
                return new RestoreResult(request.BackupId, true, true, sha, "Backup restored with identity and fence continuity.");
            }
            catch (Exception restoreException)
            {
                try
                {
                    DeleteDatabaseSidecars();
                    File.Move(safety, DatabasePath, overwrite: true);
                    await using var rolledBack = Open();
                    await rolledBack.OpenAsync(ct);
                    await ConfigureConnectionAsync(rolledBack, ct);
                    var integrity = Convert.ToString(await ScalarAsync(rolledBack, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Pre-restore safety copy integrity check failed: {integrity}");
                    _serverId = await GetOrCreateMetaAsync(rolledBack, "server_id", $"srv_{Guid.NewGuid():N}", ct);
                    var storedMode = await GetOrCreateMetaAsync(rolledBack, "mode", TaskServerMode.Maintenance.ToString(), ct);
                    _mode = Enum.TryParse<TaskServerMode>(storedMode, true, out var parsed) ? parsed : TaskServerMode.Maintenance;
                    AuthorityReady = true;
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Restore failed and the pre-restore safety copy could not be recovered. Task Server remains not ready.",
                        restoreException,
                        rollbackException);
                }

                throw;
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditRecordDto>> ListAuditAsync(long after, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT sequence, occurred_at, actor_id, action, target_type, target_id, detail_json
              FROM audit WHERE sequence > $after ORDER BY sequence LIMIT 1000;
            """, ("$after", after));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AuditRecordDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new AuditRecordDto(reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return result;
    }

    internal async Task<string> ComputeIntegrityDigestAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var builder = new StringBuilder();
        foreach (var table in new[]
                 {
                     "workspaces", "projects", "tasks", "runs", "events", "artifacts",
                     "audit", "principals", "principal_credentials", "fence_counters",
                     "leases", "runners", "review_subjects",
                     "review_attempts", "review_fence_counters", "review_deliveries",
                     "runner_review_restarts",
                     "result_handoffs", "result_ref_gc",
                     "runner_inventories", "invariant_reports",
                     "runner_reconciliation_actions",
                 })
        {
            var count = Convert.ToInt64(await ScalarAsync(connection, $"SELECT count(*) FROM {table};", ct) ?? 0L, CultureInfo.InvariantCulture);
            builder.Append(table).Append(':').Append(count).Append('\n');
        }
        await using var command = Command(connection, "SELECT id, task_key, version FROM tasks ORDER BY id;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            builder.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('|').Append(reader.GetInt64(2)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    internal async Task ImportLegacyBatchAsync(
        string workspaceName,
        IReadOnlyList<LegacyProjectImport> projects,
        LegacyAuthorityImport authority,
        string actorId,
        CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Legacy import requires maintenance mode.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var workspaceId = DeterministicId("wsp", workspaceName);
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO workspaces(id, name, version, created_at, updated_at)
                VALUES ($id, $name, 1, $now, $now)
                ON CONFLICT(id) DO NOTHING;
                """, ct, transaction, ("$id", workspaceId), ("$name", workspaceName), ("$now", now));

            foreach (var project in projects)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO projects(id, workspace_id, name, task_key_prefix, next_task_number, version, created_at, updated_at)
                    VALUES ($id, $workspace, $name, $prefix, $next, 1, $now, $now)
                    ON CONFLICT(id) DO NOTHING;
                    INSERT INTO orchestrator_contexts(
                        context_key, kind, project_id, task_id, summary, created_at, updated_at, hidden_at)
                    VALUES ($context_key, 'project', $id, NULL, $summary, $now, $now, NULL)
                    ON CONFLICT(context_key) DO NOTHING;
                    """, ct, transaction,
                    ("$id", project.ProjectId), ("$workspace", workspaceId), ("$name", project.Name),
                    ("$prefix", project.Prefix), ("$next", project.NextTaskNumber), ("$now", now),
                    ("$context_key", $"project:{project.Name}"),
                    ("$summary", $"Project chat for {project.Name}"));

                foreach (var task in project.Tasks)
                {
                    await ExecuteAsync(connection, """
                        INSERT INTO tasks(id, project_id, task_key, title, body, state, version, created_at, updated_at)
                        VALUES ($id, $project, $key, $title, $body, $state, 1, $created, $updated)
                        ON CONFLICT(id) DO NOTHING;
                        """, ct, transaction,
                        ("$id", task.TaskId), ("$project", project.ProjectId), ("$key", task.TaskKey),
                        ("$title", task.Title), ("$body", task.Body), ("$state", task.State),
                        ("$created", Iso(task.CreatedAt)), ("$updated", Iso(task.UpdatedAt)));

                    foreach (var legacyEvent in task.Events)
                    {
                        await ExecuteAsync(connection, """
                            INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
                            VALUES ($id, '', $task, $kind, $payload, $key, 0, $occurred)
                            ON CONFLICT(idempotency_key) DO NOTHING;
                            """, ct, transaction,
                            ("$id", legacyEvent.EventId), ("$task", task.TaskId), ("$kind", legacyEvent.Kind),
                            ("$payload", legacyEvent.PayloadJson), ("$key", legacyEvent.IdempotencyKey), ("$occurred", Iso(legacyEvent.OccurredAt)));
                    }

                    foreach (var artifact in task.Artifacts)
                    {
                        await ExecuteAsync(connection, """
                            INSERT INTO artifacts(id, run_id, name, media_type, sha256, content, size_bytes, idempotency_key, fence, created_at)
                            VALUES ($id, '', $name, $media, $sha, $content, $size, $key, 0, $created)
                            ON CONFLICT(idempotency_key) DO NOTHING;
                            """, ct, transaction,
                            ("$id", artifact.ArtifactId), ("$name", artifact.Name), ("$media", artifact.MediaType),
                            ("$sha", artifact.Sha256), ("$content", artifact.Content), ("$size", artifact.Content.LongLength),
                            ("$key", artifact.IdempotencyKey), ("$created", Iso(artifact.CreatedAt)));
                    }
                }
            }

            var tasksByKey = projects.SelectMany(project => project.Tasks)
                .ToDictionary(task => task.TaskKey, task => task, StringComparer.OrdinalIgnoreCase);
            foreach (var identity in authority.RunnerIdentities)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO runners(id, name, host_id, instance_id, runner_version, protocol_version,
                                        capabilities_json, status, registered_at, last_seen_at)
                    VALUES ($id, $name, $id, $id, 'legacy-migration', 1, '[]',
                            'migrated', $registered, $seen)
                    ON CONFLICT(id) DO NOTHING;
                    """, ct, transaction,
                    ("$id", identity.RunnerId), ("$name", identity.Name),
                    ("$registered", Iso(identity.RegisteredAt)),
                    ("$seen", Iso(identity.LastSeenAt ?? identity.RegisteredAt)));
            }
            var runners = authority.CodingAttempts.Select(attempt => (attempt.Lease, "coding-executor"))
                .Concat(authority.ReviewAttempts.Select(attempt => (attempt.Lease, "review-executor")))
                .Where(item => item.Lease is not null)
                .GroupBy(item => item.Lease!.ExecutorId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            foreach (var (lease, role) in runners)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO runners(id, name, host_id, instance_id, runner_version, protocol_version,
                                        capabilities_json, status, registered_at, last_seen_at)
                    VALUES ($id, $name, $host, $instance, 'legacy-migration', 1, $capabilities,
                            'migrated', $at, $at)
                    ON CONFLICT(id) DO NOTHING;
                    """, ct, transaction,
                    ("$id", lease!.ExecutorId), ("$name", lease.ExecutorDisplayName ?? lease.ExecutorId),
                    ("$host", lease.HostId), ("$instance", lease.InstanceId),
                    ("$capabilities", JsonSerializer.Serialize(new[] { role })), ("$at", Iso(lease.AcquiredAt)));
            }

            foreach (var attempt in authority.CodingAttempts)
            {
                if (!tasksByKey.TryGetValue(attempt.TaskKey, out var task))
                    throw new InvalidDataException($"Legacy coding attempt '{attempt.AttemptId}' references unknown task '{attempt.TaskKey}'.");
                var runStatus = attempt.State == "leased" ? "process-unknown" : attempt.State;
                await ExecuteAsync(connection, """
                    INSERT INTO runs(id, task_id, status, runner_id, fence, created_at, started_at,
                                     finished_at, result_sha, repository_id)
                    VALUES ($id, $task, $status, $runner, $fence, $created, $started, $finished, $sha, $repository)
                    ON CONFLICT(id) DO NOTHING;
                    INSERT INTO fence_counters(task_id, last_fence) VALUES ($task, $fence)
                    ON CONFLICT(task_id) DO UPDATE SET last_fence = max(last_fence, excluded.last_fence);
                    """, ct, transaction,
                    ("$id", attempt.AttemptId), ("$task", task.TaskId), ("$status", runStatus),
                    ("$runner", attempt.Lease?.ExecutorId), ("$fence", attempt.LastFence),
                    ("$created", Iso(attempt.CreatedAt)), ("$started", attempt.Lease is null ? null : Iso(attempt.Lease.AcquiredAt)),
                    ("$finished", attempt.TerminalAt is null ? null : Iso(attempt.TerminalAt.Value)),
                    ("$sha", attempt.ResultSha), ("$repository", attempt.RepositoryId));
                if (attempt.Lease is { } lease)
                {
                    var leaseStatus = attempt.State == "leased" ? "process-unknown" : "completed";
                    await ExecuteAsync(connection, """
                        INSERT INTO leases(task_id, lease_id, run_id, runner_id, instance_id, fence,
                                           acquired_at, expires_at, status)
                        VALUES ($task, $lease, $run, $runner, $instance, $fence, $acquired, $expires, $status)
                        ON CONFLICT(lease_id) DO NOTHING;
                        """, ct, transaction,
                        ("$task", task.TaskId), ("$lease", lease.LeaseId), ("$run", attempt.AttemptId),
                        ("$runner", lease.ExecutorId), ("$instance", lease.InstanceId), ("$fence", lease.Fence),
                        ("$acquired", Iso(lease.AcquiredAt)), ("$expires", Iso(lease.ExpiresAt)), ("$status", leaseStatus));
                }
            }
            foreach (var (taskKey, fence) in authority.LastFenceByTask)
            {
                if (!tasksByKey.TryGetValue(taskKey, out var task))
                    throw new InvalidDataException($"Legacy fence counter references unknown task '{taskKey}'.");
                await ExecuteAsync(connection, """
                    INSERT INTO fence_counters(task_id, last_fence) VALUES ($task, $fence)
                    ON CONFLICT(task_id) DO UPDATE SET last_fence = max(last_fence, excluded.last_fence);
                    """, ct, transaction, ("$task", task.TaskId), ("$fence", fence));
            }

            var reviewNumberBySubject = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var attempt in authority.ReviewAttempts.OrderBy(item => item.CreatedAt))
            {
                if (!tasksByKey.TryGetValue(attempt.TaskKey, out var task))
                    throw new InvalidDataException($"Legacy review attempt '{attempt.AttemptId}' references unknown task '{attempt.TaskKey}'.");
                if (!authority.CodingAttempts.Any(run => string.Equals(run.AttemptId, attempt.SourceRunAttemptId, StringComparison.Ordinal)))
                    throw new InvalidDataException($"Legacy review attempt '{attempt.AttemptId}' references unknown run '{attempt.SourceRunAttemptId}'.");
                var subject = attempt.Subject;
                await ExecuteAsync(connection, """
                    INSERT INTO review_subjects(id, task_id, source_run_id, repository_id, repository_url,
                                                expected_result_sha, result_ref, review_policy_hash, plan_json,
                                                idempotency_key, created_at)
                    VALUES ($id, $task, $run, $repository, $url, $sha, $ref, $policy, $plan, $key, $created)
                    ON CONFLICT(id) DO NOTHING;
                    """, ct, transaction,
                    ("$id", subject.SubjectId), ("$task", task.TaskId), ("$run", attempt.SourceRunAttemptId),
                    ("$repository", subject.RepositoryId), ("$url", subject.RepositoryUrl),
                    ("$sha", subject.ExpectedResultSha), ("$ref", subject.ResultRef),
                    ("$policy", subject.ReviewPolicyHash), ("$plan", subject.PlanJson),
                    ("$key", $"legacy-review-subject:{subject.SubjectId}"), ("$created", Iso(subject.CreatedAt)));
                var number = reviewNumberBySubject.GetValueOrDefault(subject.SubjectId) + 1;
                reviewNumberBySubject[subject.SubjectId] = number;
                var status = attempt.State switch
                {
                    "pending" => "queued",
                    "leased" => "process-unknown",
                    _ => attempt.State,
                };
                await ExecuteAsync(connection, """
                    INSERT INTO review_attempts(id, subject_id, task_id, attempt_number, status, executor_id,
                                                instance_id, host_id, lease_id, fence, acquired_at, expires_at,
                                                outcome, failure_classification, reported_at, resource_namespace,
                                                port_base, created_at)
                    VALUES ($id, $subject, $task, $number, $status, $executor, $instance, $host, $lease,
                            $fence, $acquired, $expires, $outcome, $failure, $reported, $resourceNamespace,
                            $portBase, $created)
                    ON CONFLICT(id) DO NOTHING;
                    INSERT INTO review_fence_counters(subject_id, last_fence) VALUES ($subject, $fence)
                    ON CONFLICT(subject_id) DO UPDATE SET last_fence = max(last_fence, excluded.last_fence);
                    """, ct, transaction,
                    ("$id", attempt.AttemptId), ("$subject", subject.SubjectId), ("$task", task.TaskId),
                    ("$number", number), ("$status", status), ("$executor", attempt.Lease?.ExecutorId),
                    ("$instance", attempt.Lease?.InstanceId), ("$host", attempt.Lease?.HostId),
                    ("$lease", attempt.Lease?.LeaseId), ("$fence", attempt.LastFence),
                    ("$acquired", attempt.Lease is null ? null : Iso(attempt.Lease.AcquiredAt)),
                    ("$expires", attempt.Lease is null ? null : Iso(attempt.Lease.ExpiresAt)),
                    ("$outcome", attempt.Outcome), ("$failure", attempt.FailureClassification),
                    ("$reported", attempt.TerminalAt is null ? null : Iso(attempt.TerminalAt.Value)),
                    ("$resourceNamespace", attempt.Lease is null
                        ? null
                        : LegacyReviewResourceNamespace(attempt.AttemptId, attempt.LastFence)),
                    ("$portBase", attempt.Lease is null
                        ? null
                        : LegacyReviewPortBase(attempt.AttemptId, attempt.LastFence)),
                    ("$created", Iso(attempt.CreatedAt)));
            }

            await AuditAsync(connection, transaction, actorId, "legacy.imported", "server", _serverId,
                JsonSerializer.Serialize(new
                {
                    workspaceName,
                    projects = projects.Count,
                    tasks = projects.Sum(p => p.Tasks.Count),
                    runnerIdentities = authority.RunnerIdentities.Count,
                    codingAttempts = authority.CodingAttempts.Count,
                    reviewAttempts = authority.ReviewAttempts.Count,
                    leases = authority.LeaseCount,
                    authority.AuthorityEpoch,
                }), ct);
        }, ct);
    }

    private async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);", ct);
        var storedVersion = Convert.ToInt32(
            await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'schema_version';", ct) ?? 0,
            CultureInfo.InvariantCulture);
        if (storedVersion > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Task Server store schema {storedVersion} is newer than this service supports ({CurrentSchemaVersion}).");

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS principals(
                principal_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK(kind IN ('studio', 'engine', 'runner')),
                scopes_json TEXT NOT NULL,
                runner_id TEXT UNIQUE,
                created_at TEXT NOT NULL,
                revoked_at TEXT,
                last_seen_at TEXT,
                CHECK(kind = 'runner' OR runner_id IS NULL)
            );
            CREATE TABLE IF NOT EXISTS principal_credentials(
                credential_id TEXT PRIMARY KEY,
                principal_id TEXT NOT NULL REFERENCES principals(principal_id) ON DELETE CASCADE,
                secret_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                revoked_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_principal_credentials_principal
                ON principal_credentials(principal_id, revoked_at, expires_at);
            CREATE TABLE IF NOT EXISTS workspaces(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS projects(
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL REFERENCES workspaces(id),
                name TEXT NOT NULL,
                task_key_prefix TEXT NOT NULL UNIQUE,
                next_task_number INTEGER NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tasks(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                task_key TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                body TEXT,
                state TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS orchestrator_contexts(
                context_key TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK(kind IN ('project', 'task')),
                project_id TEXT NOT NULL REFERENCES projects(id),
                task_id TEXT REFERENCES tasks(id),
                summary TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                hidden_at TEXT,
                CHECK((kind = 'project' AND task_id IS NULL) OR (kind = 'task' AND task_id IS NOT NULL))
            );
            CREATE TABLE IF NOT EXISTS orchestrator_context_turns(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                context_key TEXT NOT NULL REFERENCES orchestrator_contexts(context_key),
                turn_id TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('user', 'orchestrator')),
                body TEXT NOT NULL,
                model TEXT,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                error_message TEXT,
                error_detail TEXT,
                attachments_json TEXT,
                receipt_json TEXT,
                payload_sha256 TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runners(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                host_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                runner_version TEXT NOT NULL,
                protocol_version INTEGER NOT NULL,
                capabilities_json TEXT NOT NULL,
                status TEXT NOT NULL,
                registered_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                host_orchestrator_minimum TEXT,
                host_orchestrator_maximum TEXT,
                role_max_parallelism INTEGER,
                effective_max_parallelism INTEGER,
                runtime_capacity_applied_at TEXT,
                runtime_capacity_applied_version INTEGER
            );
            CREATE TABLE IF NOT EXISTS runtime_capacity_settings(
                host_id TEXT PRIMARY KEY,
                max_parallelism INTEGER NOT NULL,
                target_load_percent INTEGER NOT NULL,
                ramp_strategy TEXT NOT NULL,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS host_project_policies(
                host_id TEXT PRIMARY KEY,
                allow_all_projects INTEGER NOT NULL,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS host_allowed_projects(
                host_id TEXT NOT NULL REFERENCES host_project_policies(host_id) ON DELETE CASCADE,
                project_id TEXT NOT NULL REFERENCES projects(id),
                PRIMARY KEY(host_id, project_id)
            );
            CREATE TABLE IF NOT EXISTS runner_capabilities(
                runner_id TEXT NOT NULL REFERENCES runners(id),
                capability_key TEXT NOT NULL,
                category TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                advertised_status TEXT NOT NULL,
                health_state TEXT NOT NULL,
                reason TEXT,
                version TEXT,
                identity_value TEXT,
                detail TEXT,
                signal TEXT,
                credential_expires_at TEXT,
                limited_until TEXT,
                credential_modified_at TEXT,
                advertised_at TEXT NOT NULL,
                fresh_until TEXT NOT NULL,
                generation INTEGER NOT NULL,
                first_failure_at TEXT,
                last_failure_at TEXT,
                cooldown_until TEXT,
                canary_claim_id TEXT,
                consecutive_failures INTEGER NOT NULL DEFAULT 0,
                recovery_history_json TEXT NOT NULL DEFAULT '[]',
                updated_at TEXT NOT NULL,
                PRIMARY KEY(runner_id, capability_key)
            );
            CREATE TABLE IF NOT EXISTS capability_failure_deliveries(
                runner_id TEXT NOT NULL REFERENCES runners(id),
                idempotency_key TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                response_json TEXT NOT NULL,
                received_at TEXT NOT NULL,
                PRIMARY KEY(runner_id, idempotency_key)
            );
            CREATE TABLE IF NOT EXISTS host_admission(
                host_id TEXT PRIMARY KEY,
                automatic_drain_reason TEXT,
                automatic_drain_at TEXT,
                operator_drain_reason TEXT,
                operator_drain_at TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runner_telemetry_latest(
                runner_id TEXT PRIMARY KEY REFERENCES runners(id),
                payload_json TEXT NOT NULL,
                observed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                status TEXT NOT NULL,
                runner_id TEXT REFERENCES runners(id),
                fence INTEGER,
                created_at TEXT NOT NULL,
                started_at TEXT,
                finished_at TEXT
            );
            CREATE TABLE IF NOT EXISTS fence_counters(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                last_fence INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS leases(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                lease_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL UNIQUE REFERENCES runs(id),
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                fence INTEGER NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS events(
                cursor INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                run_id TEXT NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                fence INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                sequence INTEGER
            );
            CREATE TABLE IF NOT EXISTS artifacts(
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                content BLOB,
                size_bytes INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                fence INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                sequence INTEGER,
                archived INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS result_finalizations(
                run_id TEXT PRIMARY KEY REFERENCES runs(id),
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                max_attempts INTEGER NOT NULL,
                artifact_id TEXT REFERENCES artifacts(id),
                artifact_sha256 TEXT,
                error TEXT,
                last_idempotency_key TEXT NOT NULL UNIQUE,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS result_handoffs(
                run_id TEXT PRIMARY KEY REFERENCES runs(id),
                task_id TEXT NOT NULL REFERENCES tasks(id),
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                lease_id TEXT NOT NULL,
                fence INTEGER NOT NULL,
                repository_id TEXT NOT NULL,
                repository_url TEXT,
                source_run_attempt_id TEXT NOT NULL UNIQUE,
                base_sha TEXT NOT NULL,
                result_sha TEXT NOT NULL,
                immutable_remote_ref TEXT,
                source_bundle_digest TEXT,
                artifact_manifest_digest TEXT NOT NULL,
                submodules_json TEXT NOT NULL,
                lfs_objects_json TEXT NOT NULL,
                envelope_digest TEXT NOT NULL UNIQUE,
                sequence INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                acknowledged_at TEXT NOT NULL,
                retain_until TEXT NOT NULL,
                CHECK ((immutable_remote_ref IS NULL) <> (source_bundle_digest IS NULL))
            );
            CREATE TABLE IF NOT EXISTS result_ref_gc(
                run_id TEXT PRIMARY KEY REFERENCES result_handoffs(run_id),
                immutable_remote_ref TEXT NOT NULL,
                status TEXT NOT NULL,
                attempted_at TEXT NOT NULL,
                deleted_at TEXT,
                last_error TEXT
            );
            CREATE TABLE IF NOT EXISTS run_completions(
                run_id TEXT PRIMARY KEY REFERENCES runs(id),
                outcome TEXT NOT NULL,
                summary TEXT,
                envelope_digest TEXT,
                sequence INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                completed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runner_outbox_status(
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                last_sequence INTEGER NOT NULL,
                last_acknowledged_sequence INTEGER NOT NULL,
                backlog_count INTEGER NOT NULL,
                oldest_unacknowledged_sequence INTEGER,
                final_handoff_state TEXT NOT NULL,
                run_id TEXT NOT NULL,
                envelope_digest TEXT,
                observed_at TEXT NOT NULL,
                PRIMARY KEY(runner_id, run_id)
            );
            CREATE TABLE IF NOT EXISTS outbox_receipts(
                run_id TEXT NOT NULL REFERENCES runs(id),
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                received_at TEXT NOT NULL,
                PRIMARY KEY(run_id, sequence)
            );
            CREATE TABLE IF NOT EXISTS flow_definitions(
                project_id TEXT PRIMARY KEY REFERENCES projects(id),
                version INTEGER NOT NULL,
                stages_json TEXT NOT NULL,
                max_reissue_attempts INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS orchestration_runs(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                task_id TEXT NOT NULL REFERENCES tasks(id),
                task_version INTEGER NOT NULL,
                definition_version INTEGER NOT NULL,
                stages_json TEXT NOT NULL,
                max_reissue_attempts INTEGER NOT NULL,
                status TEXT NOT NULL,
                current_stage TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                reissue_attempts INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS orchestration_fence_counters(
                run_id TEXT PRIMARY KEY REFERENCES orchestration_runs(id),
                last_fence INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS orchestration_leases(
                run_id TEXT PRIMARY KEY REFERENCES orchestration_runs(id),
                lease_id TEXT NOT NULL UNIQUE,
                engine_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                fence INTEGER NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS orchestration_stage_results(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL REFERENCES orchestration_runs(id),
                stage TEXT NOT NULL,
                action TEXT NOT NULL,
                output_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                completed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action TEXT NOT NULL,
                target_type TEXT NOT NULL,
                target_id TEXT NOT NULL,
                detail_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runner_inventories(
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                PRIMARY KEY(runner_id, instance_id)
            );
            CREATE TABLE IF NOT EXISTS invariant_reports(
                report_id TEXT PRIMARY KEY,
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                category TEXT NOT NULL,
                detected_at TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runner_reconciliation_actions(
                action_id TEXT PRIMARY KEY,
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                category TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL,
                pid INTEGER,
                run_id TEXT,
                task_key TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS host_reports(
                runner_id TEXT PRIMARY KEY REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                payload_sha256 TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                report_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS work_permits(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL UNIQUE REFERENCES tasks(id),
                policy_version INTEGER NOT NULL,
                expires_at TEXT NOT NULL,
                status TEXT NOT NULL,
                accepted_runner_id TEXT REFERENCES runners(id),
                accepted_instance_id TEXT,
                accepted_at TEXT,
                accept_idempotency_key TEXT UNIQUE,
                run_id TEXT UNIQUE REFERENCES runs(id)
            );
            CREATE TABLE IF NOT EXISTS retention_policies(
                scope TEXT PRIMARY KEY,
                policy_json TEXT NOT NULL,
                version INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS archive_runs(
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                trigger_kind TEXT NOT NULL,
                mode TEXT NOT NULL,
                policy_version INTEGER NOT NULL,
                action_count INTEGER NOT NULL DEFAULT 0,
                applied_bytes INTEGER NOT NULL DEFAULT 0,
                counts_by_rule_json TEXT NOT NULL DEFAULT '{}',
                bytes_by_rule_json TEXT NOT NULL DEFAULT '{}',
                report_json TEXT NOT NULL,
                actor_id TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS archive_manifests(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                archived_at TEXT NOT NULL,
                manifest_json TEXT NOT NULL,
                payload_path TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                total_bytes INTEGER NOT NULL,
                state TEXT NOT NULL,
                restored_at TEXT
            );
            CREATE TABLE IF NOT EXISTS post_step_executions(
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES runs(id),
                step_id TEXT NOT NULL,
                eligible_runner_id TEXT NOT NULL REFERENCES runners(id),
                status TEXT NOT NULL,
                claim_fence INTEGER,
                claimed_instance_id TEXT,
                started_at TEXT,
                finished_at TEXT,
                outcome TEXT,
                artifact_hashes_json TEXT NOT NULL DEFAULT '[]',
                claim_idempotency_key TEXT UNIQUE,
                complete_idempotency_key TEXT UNIQUE
            );
            CREATE TABLE IF NOT EXISTS studio_users(
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                role TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                project_ids_json TEXT NOT NULL DEFAULT '[]',
                disabled INTEGER NOT NULL DEFAULT 0,
                must_change_password INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_sessions(
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES studio_users(id),
                token_hash TEXT NOT NULL,
                csrf_token TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                revoked_at TEXT
            );
            CREATE TABLE IF NOT EXISTS studio_stream_events(
                cursor INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT,
                task_id TEXT,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_project_state ON tasks(project_id, state);
            CREATE INDEX IF NOT EXISTS ix_orchestrator_contexts_project_visible
                ON orchestrator_contexts(project_id, hidden_at, updated_at);
            CREATE INDEX IF NOT EXISTS ix_orchestrator_context_turns_context_sequence
                ON orchestrator_context_turns(context_key, sequence);
            CREATE INDEX IF NOT EXISTS ix_leases_task_status ON leases(task_id, status);
            CREATE INDEX IF NOT EXISTS ix_events_run_cursor ON events(run_id, cursor);
            CREATE INDEX IF NOT EXISTS ix_artifacts_run ON artifacts(run_id);
            CREATE INDEX IF NOT EXISTS ix_result_finalizations_status
                ON result_finalizations(status);
            CREATE INDEX IF NOT EXISTS ix_result_handoffs_retain_until ON result_handoffs(retain_until);
            CREATE INDEX IF NOT EXISTS ix_result_ref_gc_deleted_at ON result_ref_gc(deleted_at);
            CREATE INDEX IF NOT EXISTS ix_runner_outbox_backlog ON runner_outbox_status(backlog_count);
            CREATE INDEX IF NOT EXISTS ix_outbox_receipts_run ON outbox_receipts(run_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_runner_inventory_observed ON runner_inventories(observed_at);
            CREATE INDEX IF NOT EXISTS ix_runner_actions_owner ON runner_reconciliation_actions(runner_id, instance_id);
            CREATE INDEX IF NOT EXISTS ix_runner_capabilities_state ON runner_capabilities(runner_id, health_state, fresh_until);
            CREATE INDEX IF NOT EXISTS ix_host_allowed_projects_project
                ON host_allowed_projects(project_id, host_id);
            CREATE INDEX IF NOT EXISTS ix_orchestration_runs_status_stage
                ON orchestration_runs(status, current_stage, updated_at);
            CREATE INDEX IF NOT EXISTS ix_orchestration_stage_results_run
                ON orchestration_stage_results(run_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_permits_status_expiry
                ON work_permits(status, expires_at);
            CREATE INDEX IF NOT EXISTS ix_post_steps_run_status
                ON post_step_executions(run_id, status);
            CREATE INDEX IF NOT EXISTS ix_archive_runs_started ON archive_runs(started_at);
            CREATE INDEX IF NOT EXISTS ix_archive_manifests_state ON archive_manifests(state);
            """, ct);
        await EnsureColumnAsync(connection, "events", "sequence", "INTEGER", ct);
        await EnsureColumnAsync(connection, "artifacts", "sequence", "INTEGER", ct);
        await RelaxArtifactContentConstraintAsync(connection, ct);
        await EnsureColumnAsync(connection, "tasks", "archive_state", "TEXT", ct);
        await EnsureColumnAsync(connection, "tasks", "archived_at", "TEXT", ct);
        await EnsureColumnAsync(connection, "archive_runs", "counts_by_rule_json", "TEXT NOT NULL DEFAULT '{}'", ct);
        await EnsureColumnAsync(connection, "archive_runs", "bytes_by_rule_json", "TEXT NOT NULL DEFAULT '{}'", ct);
        await EnsureColumnAsync(connection, "runs", "required_capabilities_json", "TEXT NOT NULL DEFAULT '[]'", ct);
        await EnsureColumnAsync(connection, "runs", "canary_capabilities_json", "TEXT NOT NULL DEFAULT '[]'", ct);
        await EnsureColumnAsync(connection, "runners", "host_orchestrator_minimum", "TEXT", ct);
        await EnsureColumnAsync(connection, "runners", "host_orchestrator_maximum", "TEXT", ct);
        await EnsureColumnAsync(connection, "runners", "role_max_parallelism", "INTEGER", ct);
        await EnsureColumnAsync(connection, "runners", "effective_max_parallelism", "INTEGER", ct);
        await EnsureColumnAsync(connection, "runners", "runtime_capacity_applied_at", "TEXT", ct);
        await EnsureColumnAsync(connection, "runners", "runtime_capacity_applied_version", "INTEGER", ct);
        await EnsureColumnAsync(connection, "runner_capabilities", "signal", "TEXT", ct);
        await EnsureColumnAsync(connection, "runner_capabilities", "credential_expires_at", "TEXT", ct);
        await EnsureColumnAsync(connection, "runner_capabilities", "limited_until", "TEXT", ct);
        await EnsureColumnAsync(connection, "runner_capabilities", "credential_modified_at", "TEXT", ct);
        await EnsureColumnAsync(connection, "orchestration_runs", "task_version", "INTEGER NOT NULL DEFAULT 0", ct);
        await ExecuteAsync(connection, """
            INSERT INTO runtime_capacity_settings(
                host_id, max_parallelism, target_load_percent, ramp_strategy,
                version, updated_at)
            SELECT host_id, 2, 80, 'balanced', 1, $now
              FROM runners
             WHERE capabilities_json LIKE '%"executor:coding"%'
             GROUP BY host_id
            ON CONFLICT(host_id) DO NOTHING;
            """, ct, ("$now", Iso(UtcNow)));
        await ExecuteAsync(connection, """
            INSERT INTO orchestrator_contexts(
                context_key, kind, project_id, task_id, summary, created_at, updated_at, hidden_at)
            SELECT 'project:' || name, 'project', id, NULL, 'Project chat for ' || name,
                   created_at, updated_at, NULL
              FROM projects
             WHERE 1 = 1
            ON CONFLICT(context_key) DO NOTHING;
            """, ct);
        await ExecuteAsync(connection, """
            INSERT INTO flow_definitions(
                project_id, version, stages_json, max_reissue_attempts, updated_at)
            SELECT id, 0, $stages, $max_reissues, $now
              FROM projects
             WHERE 1 = 1
            ON CONFLICT(project_id) DO NOTHING;
            """, ct,
            ("$stages", JsonSerializer.Serialize(OrchestrationDefaults.CreateStages())),
            ("$max_reissues", OrchestrationDefaults.MaxReissueAttempts),
            ("$now", Iso(UtcNow)));
        await ExecuteAsync(connection, """
            INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $now)
            ON CONFLICT(version) DO NOTHING;
            """, ct, ("$version", CurrentSchemaVersion), ("$now", Iso(UtcNow)));
        await ApplyReviewMigrationAsync(connection, ct);
        await EnsureColumnAsync(connection, "review_attempts", "required_capabilities_json", "TEXT NOT NULL DEFAULT '[]'", ct);
        await EnsureColumnAsync(connection, "review_attempts", "canary_capabilities_json", "TEXT NOT NULL DEFAULT '[]'", ct);
        await EnsureColumnAsync(connection, "tasks", "rank", "INTEGER NOT NULL DEFAULT 0", ct);
        await ExecuteAsync(connection, """
            CREATE INDEX IF NOT EXISTS ix_tasks_project_state_rank ON tasks(project_id, state, rank);
            CREATE INDEX IF NOT EXISTS ix_studio_sessions_user ON studio_sessions(user_id, revoked_at);
            CREATE INDEX IF NOT EXISTS ix_studio_stream_events_project ON studio_stream_events(project_id, cursor);
            """, ct);
        await SetMetaAsync(connection, null, "schema_version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture), ct);
    }

    /// <summary>
    /// Upgrades a pre-retention store where <c>artifacts.content</c> is <c>NOT NULL</c>. Archiving clears
    /// content to NULL once the cold copy is durable, so the constraint has to be dropped once via a table
    /// rebuild; SQLite has no <c>ALTER COLUMN</c>. Fresh installs already create the relaxed shape and skip
    /// this rebuild entirely.
    /// </summary>
    private static async Task RelaxArtifactContentConstraintAsync(SqliteConnection connection, CancellationToken ct)
    {
        var notNull = false;
        await using (var command = Command(connection, "PRAGMA table_info(artifacts);"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), "content", StringComparison.OrdinalIgnoreCase))
                {
                    notNull = reader.GetInt32(3) == 1;
                    break;
                }
            }
        }
        if (!notNull) return;

        // Build beside the old table rather than renaming it first. SQLite rewrites
        // foreign keys in result_finalizations when a referenced table is renamed;
        // the beside-copy keeps those references pointed at "artifacts".
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", ct);
        try
        {
            await ExecuteAsync(connection, """
                DROP TABLE IF EXISTS artifacts_retention_upgrade;
                CREATE TABLE artifacts_retention_upgrade(
                    id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    content BLOB,
                    size_bytes INTEGER NOT NULL,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    fence INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    sequence INTEGER,
                    archived INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO artifacts_retention_upgrade(
                        id, run_id, name, media_type, sha256, content, size_bytes,
                        idempotency_key, fence, created_at, sequence, archived)
                    SELECT id, run_id, name, media_type, sha256, content, size_bytes,
                           idempotency_key, fence, created_at, sequence, 0
                      FROM artifacts;
                DROP TABLE artifacts;
                ALTER TABLE artifacts_retention_upgrade RENAME TO artifacts;
                CREATE INDEX IF NOT EXISTS ix_artifacts_run ON artifacts(run_id);
                """, ct);
        }
        finally
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", ct);
        }
        var foreignKeyErrors = Convert.ToInt64(
            await ScalarAsync(connection, "SELECT count(*) FROM pragma_foreign_key_check;", ct) ?? 0L,
            CultureInfo.InvariantCulture);
        if (foreignKeyErrors != 0)
            throw new InvalidOperationException($"Artifact retention migration left {foreignKeyErrors} foreign-key violation(s).");
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string declaration,
        CancellationToken ct)
    {
        await using var command = Command(connection, $"PRAGMA table_info({table});");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        await reader.DisposeAsync();
        await ExecuteAsync(
            connection,
            $"ALTER TABLE {table} ADD COLUMN {column} {declaration};",
            ct);
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
        Pooling = false,
    }.ToString());

    private async Task<SqliteConnection> OpenReadyAsync(CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        var connection = Open();
        await connection.OpenAsync(ct);
        await ConfigureConnectionAsync(connection, ct);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", ct);

    private async Task InWriteTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken ct,
        bool requireReady = true)
    {
        if (requireReady && !AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = Open();
            await connection.OpenAsync(ct);
            await ConfigureConnectionAsync(connection, ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await action(connection, transaction);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RequireWritable()
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance)
            throw new TaskServerConflictException("server-not-writable", $"Task Server is in {_mode} mode.");
    }

    private void RequireAdmission()
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode != TaskServerMode.Normal)
            throw new TaskServerConflictException("admission-closed", $"New claims are closed while Task Server is in {_mode} mode.");
    }

    private int NormalizeTtl(int requested)
        => Math.Clamp(requested, _options.MinimumLeaseSeconds, _options.MaximumLeaseSeconds);

    private static void ValidateLeaseReference(LeaseDto lease, string runnerId, string instanceId, string leaseId, long fence)
    {
        if (!string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || lease.Fence != fence)
            throw new TaskServerConflictException("stale-fence", "Lease id, runner instance, or fence does not match current authority.");
    }

    private async Task EnsureLeaseCurrentAsync(SqliteConnection connection, SqliteTransaction transaction, LeaseDto lease, CancellationToken ct)
    {
        if (!string.Equals(lease.Status, "active", StringComparison.Ordinal))
            throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
        if (lease.ExpiresAt <= UtcNow)
            throw new TaskServerConflictException("lease-expired-process-unknown", "Lease expired. Evidence is retained, but authoritative writes are fenced off.");
        var lastFence = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT last_fence FROM fence_counters WHERE task_id = $task;", ct, transaction, ("$task", lease.TaskId)) ?? 0L,
            CultureInfo.InvariantCulture);
        if (lastFence != lease.Fence)
            throw new TaskServerConflictException("stale-fence", "A higher durable fence exists for this task.");
    }

    private async Task EnsureOutboxLeaseCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LeaseDto lease,
        CancellationToken ct)
        => await EnsureOutboxLeaseCurrentAsync(
            connection,
            transaction,
            lease,
            lease.RunnerId,
            lease.InstanceId,
            lease.LeaseId,
            ct);

    private async Task EnsureOutboxLeaseCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LeaseDto lease,
        string? runnerId,
        string? instanceId,
        string? leaseId,
        CancellationToken ct)
    {
        if (lease.Status is not ("active" or "process-unknown"))
            throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
        if (string.Equals(lease.Status, "active", StringComparison.Ordinal)
            && runnerId is null && instanceId is null && leaseId is null)
        {
            await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);
            return;
        }
        if (string.Equals(lease.Status, "process-unknown", StringComparison.Ordinal)
            && (runnerId is null || instanceId is null || leaseId is null))
        {
            throw new TaskServerConflictException(
                "lease-not-active",
                "Lease status is 'process-unknown'; exact outbox authority is required for replay.");
        }
        if (!string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal))
        {
            throw new TaskServerConflictException(
                "stale-fence",
                "Outbox replay must present the exact runner instance and lease authority.");
        }
        var lastFence = Convert.ToInt64(await ScalarAsync(
            connection,
            "SELECT last_fence FROM fence_counters WHERE task_id = $task;",
            ct,
            transaction,
            ("$task", lease.TaskId)) ?? 0L,
            CultureInfo.InvariantCulture);
        if (lastFence != lease.Fence)
            throw new TaskServerConflictException("stale-fence", "A higher durable fence exists for this task.");
        if (string.Equals(lease.Status, "process-unknown", StringComparison.Ordinal))
        {
            await ExecuteAsync(connection, """
                UPDATE leases
                   SET status = 'active', expires_at = $expires
                 WHERE run_id = $run AND status = 'process-unknown';
                UPDATE runs
                   SET status = 'running'
                 WHERE id = $run AND status = 'process-unknown';
                """, ct, transaction,
                ("$expires", Iso(UtcNow.AddSeconds(_options.MaximumLeaseSeconds))),
                ("$run", lease.RunId));
        }
    }

    private static bool RequiresResultEnvelope(string outcome)
        => outcome.Trim().ToLowerInvariant() is "success" or "done" or "noop" or "no-op";

    private static void ValidateImmutableSource(
        string runId,
        long fence,
        ImmutableResultEnvelope envelope)
    {
        if (envelope.ImmutableRemoteRef is null) return;
        var expected = FencedGitRefs.ImmutableResult(
            runId,
            fence,
            envelope.ResultSha);
        if (!string.Equals(envelope.ImmutableRemoteRef, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Immutable result ref must be '{expected}'. Moving task, runner, or other fence-generation refs are not durable result identity.");
        }
    }

    private static void ValidateHandoffReplay(
        StoredResultHandoff existing,
        ResultHandoffRequest request)
    {
        if (!string.Equals(existing.Acknowledgement.RunId, request.Envelope.SourceRunAttemptId, StringComparison.Ordinal)
            || existing.Acknowledgement.AcknowledgedSequence != request.Sequence
            || !string.Equals(existing.Acknowledgement.EnvelopeDigest, request.EnvelopeDigest, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(existing.RunnerId, request.RunnerId, StringComparison.Ordinal)
            || !string.Equals(existing.InstanceId, request.InstanceId, StringComparison.Ordinal)
            || !string.Equals(existing.LeaseId, request.LeaseId, StringComparison.Ordinal)
            || existing.Fence != request.Fence)
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "The result handoff identity is already bound to a different run, sequence, or envelope.");
        }
    }

    private async Task RestoreHandoffReplayAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        ResultHandoffRequest request,
        CancellationToken ct)
    {
        var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                    ?? throw new KeyNotFoundException("Run lease was not found.");
        if (string.Equals(lease.Status, "completed", StringComparison.Ordinal))
            return;
        ValidateLeaseReference(
            lease,
            request.RunnerId,
            request.InstanceId,
            request.LeaseId,
            request.Fence);
        await EnsureOutboxLeaseCurrentAsync(connection, transaction, lease, ct);
    }

    private static void ValidateCompletionReplay(
        StoredRunCompletion existing,
        CompleteRunRequest request)
    {
        if (!string.Equals(existing.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(existing.Run.Status, request.Outcome, StringComparison.Ordinal)
            || !string.Equals(existing.EnvelopeDigest, request.ResultEnvelopeDigest, StringComparison.OrdinalIgnoreCase)
            || existing.Sequence != request.Sequence)
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "The run completion is already bound to a different outcome, sequence, or result envelope.");
        }
    }

    private static async Task<StoredResultHandoff?> ReadResultHandoffAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run_id, sequence, envelope_digest, idempotency_key,
                   acknowledged_at, retain_until, runner_id, instance_id,
                   lease_id, fence, repository_id, source_run_attempt_id,
                   base_sha, result_sha, immutable_remote_ref,
                   source_bundle_digest, artifact_manifest_digest,
                   submodules_json, lfs_objects_json, repository_url
              FROM result_handoffs
             WHERE run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadStoredResultHandoff(reader)
            : null;
    }

    private static async Task<StoredResultHandoff?> ReadResultHandoffByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run_id, sequence, envelope_digest, idempotency_key,
                   acknowledged_at, retain_until, runner_id, instance_id,
                   lease_id, fence, repository_id, source_run_attempt_id,
                   base_sha, result_sha, immutable_remote_ref,
                   source_bundle_digest, artifact_manifest_digest,
                   submodules_json, lfs_objects_json, repository_url
              FROM result_handoffs
             WHERE idempotency_key = $key;
            """, transaction, ("$key", idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadStoredResultHandoff(reader)
            : null;
    }

    private static StoredResultHandoff ReadStoredResultHandoff(SqliteDataReader reader)
        => new(
            new ResultHandoffAck(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                "acknowledged",
                Parse(reader.GetString(4)),
                Parse(reader.GetString(5)),
                false),
            reader.GetString(3),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            new ImmutableResultEnvelope(
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetString(16),
                JsonSerializer.Deserialize<List<ResultDependencyIdentity>>(reader.GetString(17)),
                JsonSerializer.Deserialize<List<ResultDependencyIdentity>>(reader.GetString(18)),
                reader.IsDBNull(19) ? null : reader.GetString(19)));

    private static async Task<StoredRunCompletion?> ReadRunCompletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT r.id, r.task_id, r.status, r.runner_id, r.fence,
                   r.created_at, r.started_at, r.finished_at,
                   c.envelope_digest, c.sequence, c.idempotency_key
              FROM run_completions c
              JOIN runs r ON r.id = c.run_id
             WHERE c.run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var run = new RunDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)));
        return new StoredRunCompletion(
            run,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9),
            reader.GetString(10));
    }

    private async Task RefreshOutboxSummaryAsync(
        SqliteConnection connection,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        _outboxBacklog = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT COALESCE(sum(backlog_count), 0) FROM runner_outbox_status;",
            ct,
            transaction) ?? 0L,
            CultureInfo.InvariantCulture);
        var oldest = await ScalarAsync(
            connection,
            "SELECT min(oldest_unacknowledged_sequence) FROM runner_outbox_status WHERE backlog_count > 0;",
            ct,
            transaction);
        _oldestUnacknowledgedSequence = oldest is null or DBNull
            ? null
            : Convert.ToInt64(oldest, CultureInfo.InvariantCulture);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT final_handoff_state, count(*)
              FROM runner_outbox_status
             GROUP BY final_handoff_state;
            """, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            states[reader.GetString(0)] = reader.GetInt32(1);
        _finalHandoffStates = states;
    }

    private async Task RecordOutboxSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        long? sequence,
        string idempotencyKey,
        string kind,
        CancellationToken ct)
    {
        if (sequence is null) return;
        if (sequence <= 0)
            throw new ArgumentException("Outbox sequence must be positive.");
        var last = Convert.ToInt64(await ScalarAsync(
            connection,
            "SELECT COALESCE(max(sequence), 0) FROM outbox_receipts WHERE run_id = $run;",
            ct,
            transaction,
            ("$run", runId)) ?? 0L,
            CultureInfo.InvariantCulture);
        if (sequence <= last)
        {
            throw new TaskServerConflictException(
                "stale-outbox-sequence",
                $"Outbox sequence {sequence} is not newer than acknowledged sequence {last} for run '{runId}'.");
        }
        await ExecuteAsync(connection, """
            INSERT INTO outbox_receipts(run_id, sequence, kind, idempotency_key, received_at)
            VALUES ($run, $sequence, $kind, $key, $received);
            """, ct, transaction,
            ("$run", runId),
            ("$sequence", sequence),
            ("$kind", kind),
            ("$key", idempotencyKey),
            ("$received", Iso(UtcNow)));
    }

    private sealed record StoredResultHandoff(
        ResultHandoffAck Acknowledgement,
        string IdempotencyKey,
        string RunnerId,
        string InstanceId,
        string LeaseId,
        long Fence,
        ImmutableResultEnvelope Envelope);

    private sealed record StoredRunCompletion(
        RunDto Run,
        string? EnvelopeDigest,
        long Sequence,
        string IdempotencyKey);

    private static async Task ValidateRunnerAsync(SqliteConnection connection, SqliteTransaction transaction, string runnerId, string instanceId, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT instance_id, protocol_version, status, capabilities_json FROM runners WHERE id = $id;", transaction, ("$id", runnerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Runner is not registered.");
        if (!string.Equals(reader.GetString(0), instanceId, StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-instance-stale", "Runner instance id is not current.");
        var protocol = reader.GetInt32(1);
        if (!TaskServerProtocol.Supports(protocol)) throw new TaskServerProtocolException(protocol);
        if (!string.Equals(reader.GetString(2), "active", StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-not-active", "Runner is not active.");
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        if (!capabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal)
            || capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal))
            throw new TaskServerConflictException(
                "coding-capability-required",
                "Runner did not advertise the separately registered coding executor capability.");
    }

    private static string? ExecutorRole(IReadOnlyCollection<string> capabilities)
        => capabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal)
            ? ReviewCapabilities.CodingExecutor
            : capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal)
                ? ReviewCapabilities.ReviewExecutor
                : null;

    private static async Task<(string Prefix, long Next)> ReadProjectCounterAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT task_key_prefix, next_task_number FROM projects WHERE id = $id;", transaction, ("$id", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Project was not found.");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<TaskDto?> ReadTaskAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string identity, CancellationToken ct)
    {
        var sql = projectId == UnscopedProjectToken
            ? """
              SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
                FROM tasks WHERE id = $identity;
              """
            : """
              SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body, archive_state, archived_at
                FROM tasks WHERE project_id = $project AND (id = $identity OR task_key = upper($identity));
              """;
        await using var command = Command(connection, sql, transaction, ("$project", projectId), ("$identity", identity));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    internal static TaskDto ReadTask(SqliteDataReader reader)
    {
        var archiveStateOrdinal = TryGetOrdinal(reader, "archive_state");
        var archivedAtOrdinal = TryGetOrdinal(reader, "archived_at");
        return new TaskDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            Parse(reader.GetString(6)),
            Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            archiveStateOrdinal is { } state && !reader.IsDBNull(state) ? reader.GetString(state) : null,
            archivedAtOrdinal is { } archivedAt && !reader.IsDBNull(archivedAt) ? Parse(reader.GetString(archivedAt)) : null);
    }

    private static int? TryGetOrdinal(SqliteDataReader reader, string columnName)
    {
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            if (string.Equals(reader.GetName(ordinal), columnName, StringComparison.OrdinalIgnoreCase))
                return ordinal;
        }

        return null;
    }

    private static RunDto ReadRun(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Parse(reader.GetString(7)));

    private static async Task<LeaseDto?> ReadLeaseAsync(SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT lease_id, run_id, task_id, runner_id, instance_id, fence, acquired_at, expires_at, status
              FROM leases WHERE run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new LeaseDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), Parse(reader.GetString(6)), Parse(reader.GetString(7)), reader.GetString(8))
            : null;
    }

    private static async Task<RunDto?> ReadRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, task_id, status, runner_id, fence, created_at, started_at, finished_at
              FROM runs WHERE id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRun(reader);
    }

    private static async Task<EventDto?> ReadEventByIdempotencyKeyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT cursor, event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at, sequence
              FROM events WHERE idempotency_key = $key;
            """, transaction, ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEvent(reader) : null;
    }

    private static EventDto ReadEvent(SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetInt64(7), Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt64(9));

    private static void ValidateEventReplay(EventDto? existing, string runId, string taskId, EventIngestRequest request)
    {
        if (existing is null)
            throw new InvalidOperationException("The ingested event could not be read back.");
        if (!string.Equals(existing.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(existing.TaskId, taskId, StringComparison.Ordinal)
            || !string.Equals(existing.Kind, request.Kind, StringComparison.Ordinal)
            || !string.Equals(existing.PayloadJson, request.PayloadJson, StringComparison.Ordinal)
            || existing.Fence != request.Fence
            || existing.Sequence != request.Sequence)
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "The event idempotency key is already bound to a different run or payload.");
        }
    }

    private static async Task<ArtifactDto?> ReadArtifactByIdempotencyKeyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, run_id, name, media_type, sha256, size_bytes, idempotency_key, fence, created_at, sequence
              FROM artifacts WHERE idempotency_key = $key;
            """, transaction, ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadArtifact(reader) : null;
    }

    private static ArtifactDto ReadArtifact(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt64(5), reader.GetString(6), reader.GetInt64(7), Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt64(9));

    private static void ValidateArtifactReplay(
        ArtifactDto? existing,
        string runId,
        ArtifactIngestRequest request,
        string actualSha,
        long sizeBytes)
    {
        if (existing is null)
            throw new InvalidOperationException("The ingested artifact could not be read back.");
        if (!string.Equals(existing.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(existing.Name, request.Name, StringComparison.Ordinal)
            || !string.Equals(existing.MediaType, request.MediaType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Sha256, actualSha, StringComparison.OrdinalIgnoreCase)
            || existing.SizeBytes != sizeBytes
            || existing.Fence != request.Fence
            || existing.Sequence != request.Sequence)
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "The artifact idempotency key is already bound to a different run or payload.");
        }
    }

    private async Task<string> GetOrCreateMetaAsync(SqliteConnection connection, string key, string defaultValue, CancellationToken ct)
    {
        var existing = Convert.ToString(await ScalarAsync(connection, "SELECT value FROM meta WHERE key = $key;", ct, ("$key", key)), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        await ExecuteAsync(connection, "INSERT INTO meta(key, value) VALUES ($key, $value);", ct, ("$key", key), ("$value", defaultValue));
        return defaultValue;
    }

    private static async Task SetMetaAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string value, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO meta(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, ct, transaction, ("$key", key), ("$value", value));

    private async Task AuditAsync(
        SqliteConnection connection, SqliteTransaction transaction, string actorId, string action,
        string targetType, string targetId, string detailJson, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO audit(occurred_at, actor_id, action, target_type, target_id, detail_json)
            VALUES ($at, $actor, $action, $type, $target, $detail);
            """, ct, transaction,
            ("$at", Iso(UtcNow)), ("$actor", string.IsNullOrWhiteSpace(actorId) ? "anonymous-local" : actorId),
            ("$action", action), ("$type", targetType), ("$target", targetId), ("$detail", detailJson));

    private async Task AppendLifecycleEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string taskId,
        long fence,
        string kind,
        object payload,
        CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
            VALUES ($event, $run, $task, $kind, $payload, $key, $fence, $occurred)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """, ct, transaction,
            ("$event", $"evt_{Guid.NewGuid():N}"),
            ("$run", runId),
            ("$task", taskId),
            ("$kind", kind),
            ("$payload", JsonSerializer.Serialize(payload)),
            ("$key", $"task-server:{runId}:{kind}"),
            ("$fence", fence),
            ("$occurred", Iso(UtcNow)));

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
        => Command(connection, sql, null, parameters);

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            if (!string.IsNullOrWhiteSpace(name)) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
        => await ExecuteAsync(connection, sql, ct, null, parameters);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
        => await ScalarAsync(connection, sql, ct, null, parameters);

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteScalarAsync(ct);
    }

    private string ResolveBackupPath(string backupId)
    {
        if (!string.Equals(Path.GetFileName(backupId), backupId, StringComparison.Ordinal)
            || backupId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Backup id is invalid.");
        return Path.Combine(BackupDirectory, backupId.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? backupId : backupId + ".db");
    }

    private void DeleteDatabaseSidecars()
    {
        if (File.Exists(DatabasePath + "-wal")) File.Delete(DatabasePath + "-wal");
        if (File.Exists(DatabasePath + "-shm")) File.Delete(DatabasePath + "-shm");
    }

    private static string SanitizeBackupName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "manual" : name.Trim().ToLowerInvariant();
        var clean = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(clean) ? "manual" : clean[..Math.Min(clean.Length, 40)];
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static string DeterministicId(string prefix, string identity)
        => $"{prefix}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24]}";

    private static string LegacyReviewResourceNamespace(string attemptId, long fence)
        => $"review-{new string(attemptId.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).ToLowerInvariant()}-f{fence}";

    private static int LegacyReviewPortBase(string attemptId, long fence)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{attemptId}:{fence}"));
        var slot = BitConverter.ToUInt16(bytes, 0) % 4000;
        return 24000 + slot * 8;
    }

    private static string StableOrGeneratedId(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{prefix}_{Guid.NewGuid():N}";
        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new ArgumentException("Resource ids may contain only letters, digits, '.', '_', and '-'.");
        return normalized;
    }

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static JsonSerializerOptions CreateOutcomeJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class TaskServerConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class TaskServerProtocolException(int version)
    : Exception($"Protocol {version} is outside the supported range {TaskServerProtocol.MinimumSupported}-{TaskServerProtocol.MaximumSupported}.")
{
    public int Version { get; } = version;
}

internal sealed record LegacyProjectImport(
    string ProjectId,
    string Name,
    string Prefix,
    long NextTaskNumber,
    IReadOnlyList<LegacyTaskImport> Tasks);

internal sealed record LegacyTaskImport(
    string TaskId,
    string TaskKey,
    string Title,
    string? Body,
    string State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<LegacyEventImport> Events,
    IReadOnlyList<LegacyArtifactImport> Artifacts);

internal sealed record LegacyEventImport(
    string EventId,
    string Kind,
    string PayloadJson,
    string IdempotencyKey,
    DateTime OccurredAt);

internal sealed record LegacyArtifactImport(
    string ArtifactId,
    string Name,
    string MediaType,
    byte[] Content,
    string Sha256,
    string IdempotencyKey,
    DateTime CreatedAt);

internal sealed record LegacyAuthorityImport(
    long AuthorityEpoch,
    IReadOnlyDictionary<string, long> LastFenceByTask,
    IReadOnlyList<LegacyRunnerIdentityImport> RunnerIdentities,
    IReadOnlyList<LegacyCodingAttemptImport> CodingAttempts,
    IReadOnlyList<LegacyReviewAttemptImport> ReviewAttempts)
{
    public static LegacyAuthorityImport Empty { get; } = new(0, new Dictionary<string, long>(), [], [], []);
    public int LeaseCount => CodingAttempts.Count(item => item.Lease is not null)
                             + ReviewAttempts.Count(item => item.Lease is not null);
}

internal sealed record LegacyRunnerIdentityImport(
    string RunnerId,
    string Name,
    DateTime RegisteredAt,
    DateTime? LastSeenAt);

internal sealed record LegacyLeaseImport(
    string LeaseId,
    long Fence,
    long AuthorityEpoch,
    string ExecutorId,
    string HostId,
    string InstanceId,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string? ExecutorDisplayName);

internal sealed record LegacyCodingAttemptImport(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string State,
    LegacyLeaseImport? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    string? ResultSha);

internal sealed record LegacyReviewSubjectImport(
    string SubjectId,
    string RepositoryId,
    string ExpectedResultSha,
    string SourceRunAttemptId,
    string ReviewPolicyHash,
    string? RepositoryUrl,
    string? ResultRef,
    string PlanJson,
    DateTime CreatedAt);

internal sealed record LegacyReviewAttemptImport(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string SourceRunAttemptId,
    string State,
    LegacyLeaseImport? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    string? Outcome,
    string? FailureClassification,
    LegacyReviewSubjectImport Subject);
