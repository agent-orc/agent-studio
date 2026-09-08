using System.Globalization;
using System.Text.Json;
using AgentStudio.Retention;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>Thin application boundary over the retention policy, plan/apply, run history, and archive routes.</summary>
public sealed class RetentionManagementService(TaskServerStore store)
{
    public Task<RetentionPolicyDto> GetWorkspacePolicyAsync(CancellationToken ct) => store.GetWorkspaceRetentionPolicyDtoAsync(ct);

    public Task<RetentionPolicyDto> UpdateWorkspacePolicyAsync(UpdateRetentionPolicyRequest request, string actorId, CancellationToken ct)
        => store.UpdateWorkspaceRetentionPolicyAsync(request, actorId, ct);

    public Task<RetentionPolicyDto> GetProjectPolicyAsync(string projectId, CancellationToken ct) => store.GetProjectRetentionPolicyDtoAsync(projectId, ct);

    public Task<RetentionPolicyDto> UpdateProjectPolicyAsync(string projectId, UpdateRetentionPolicyRequest request, string actorId, CancellationToken ct)
        => store.UpdateProjectRetentionPolicyAsync(projectId, request, actorId, ct);

    public Task DeleteProjectPolicyAsync(string projectId, long expectedVersion, string actorId, CancellationToken ct)
        => store.DeleteProjectRetentionPolicyAsync(projectId, expectedVersion, actorId, ct);

    public async Task<RetentionPlanDto> PlanAsync(RunRetentionRequest request, string actorId, CancellationToken ct)
        => (await store.PlanRetentionRunAsync(request, actorId, ct)).Plan;

    public Task<RetentionApplyResultDto> ApplyAsync(RunRetentionRequest request, string actorId, CancellationToken ct)
        => store.ApplyRetentionRunAsync(request, actorId, ct);

    public Task<IReadOnlyList<RetentionRunSummaryDto>> ListRunsAsync(CancellationToken ct) => store.ListRetentionRunsAsync(ct);

    public Task<RetentionRunDetailDto?> GetRunAsync(string runId, CancellationToken ct) => store.GetRetentionRunAsync(runId, ct);

    public Task<RetentionArchiveManifestDto?> GetManifestAsync(string taskIdentity, CancellationToken ct) => store.GetRetentionManifestAsync(taskIdentity, ct);

    public Task<RetentionApplyResultDto> ArchiveNowAsync(string taskIdentity, RetentionArchiveTaskRequest request, string actorId, CancellationToken ct)
        => store.ArchiveTaskNowAsync(taskIdentity, request.Stage, actorId, ct);

    public Task RestoreAsync(string taskIdentity, string actorId, CancellationToken ct) => store.RestoreArchivedTaskAsync(taskIdentity, actorId, ct);

    /// <summary>Separate audit action from a scheduled sweep, so the Studio feed can tell an automatic run from an operator-triggered one.</summary>
    public Task EmitRunCompletedAsync(string runId, int appliedActions, long appliedBytes, string actorId, CancellationToken ct)
        => store.EmitRetentionRunCompletedAuditAsync(runId, appliedActions, appliedBytes, actorId, ct);
}

/// <summary>Thrown when artifact content was read after it moved to the cold archive; maps to HTTP 409.</summary>
public sealed class ArtifactArchivedException(string artifactId, string taskId, string taskKey)
    : Exception($"Artifact '{artifactId}' was moved to the cold archive.")
{
    public string ArtifactId { get; } = artifactId;
    public string TaskId { get; } = taskId;
    public string TaskKey { get; } = taskKey;
}

public sealed partial class TaskServerStore
{
    private readonly record struct RetentionPolicyRow(IReadOnlyDictionary<ArtifactClass, RetentionRule> Rules, int Version, DateTime UpdatedAt, string UpdatedBy);

    internal async Task<RetentionPolicy> GetActiveRetentionPolicyAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadActiveRetentionPolicyAsync(connection, null, ct);
    }

    private static async Task<RetentionPolicy> ReadActiveRetentionPolicyAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var workspaceRow = await ReadRetentionPolicyRowAsync(connection, transaction, "workspace", ct);
        var basePolicy = workspaceRow is null
            ? RetentionPolicy.Default()
            : new RetentionPolicy
            {
                Version = workspaceRow.Value.Version,
                UpdatedAt = workspaceRow.Value.UpdatedAt,
                UpdatedBy = workspaceRow.Value.UpdatedBy,
                WorkspaceDefaults = workspaceRow.Value.Rules,
            };
        return basePolicy with { ProjectOverrides = await ReadProjectRetentionOverridesAsync(connection, transaction, ct) };
    }

    private static async Task<Dictionary<string, ProjectRetentionOverride>> ReadProjectRetentionOverridesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var overrides = new Dictionary<string, ProjectRetentionOverride>(StringComparer.OrdinalIgnoreCase);
        await using var command = Command(connection, "SELECT scope, policy_json FROM retention_policies WHERE scope LIKE 'project:%';", transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var scope = reader.GetString(0);
            var projectId = scope["project:".Length..];
            var rules = JsonSerializer.Deserialize<Dictionary<ArtifactClass, RetentionRule>>(reader.GetString(1), RetentionJson) ?? [];
            overrides[projectId] = new ProjectRetentionOverride { Rules = rules };
        }
        return overrides;
    }

    private static async Task<RetentionPolicyRow?> ReadRetentionPolicyRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scope, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT policy_json, version, updated_at, updated_by FROM retention_policies WHERE scope = $scope;", transaction, ("$scope", scope));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var rules = JsonSerializer.Deserialize<Dictionary<ArtifactClass, RetentionRule>>(reader.GetString(0), RetentionJson) ?? [];
        return new RetentionPolicyRow(rules, reader.GetInt32(1), Parse(reader.GetString(2)), reader.GetString(3));
    }

    internal async Task<RetentionPolicyDto> GetWorkspaceRetentionPolicyDtoAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var row = await ReadRetentionPolicyRowAsync(connection, null, "workspace", ct);
        if (row is null)
        {
            var defaults = RetentionPolicy.Default();
            return new RetentionPolicyDto("workspace", 0, defaults.UpdatedAt.UtcDateTime, defaults.UpdatedBy,
                defaults.WorkspaceDefaults.Values.Select(ToRetentionRuleDto).ToList());
        }
        return new RetentionPolicyDto("workspace", row.Value.Version, row.Value.UpdatedAt, row.Value.UpdatedBy,
            row.Value.Rules.Values.Select(ToRetentionRuleDto).ToList());
    }

    internal async Task<RetentionPolicyDto> UpdateWorkspaceRetentionPolicyAsync(
        UpdateRetentionPolicyRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var rules = ValidateWorkspaceRetentionRules(request.Rules);
        RetentionPolicyDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadRetentionPolicyRowAsync(connection, transaction, "workspace", ct);
            var expected = existing?.Version ?? 0;
            if (expected != request.ExpectedVersion)
                throw new TaskServerConflictException("resource-version-mismatch",
                    $"Expected retention policy version {request.ExpectedVersion}, current version is {expected}.");

            var overrides = await ReadProjectRetentionOverridesAsync(connection, transaction, ct);
            ValidateRetentionPolicy(new RetentionPolicy { WorkspaceDefaults = rules, ProjectOverrides = overrides });

            var now = UtcNow;
            var version = expected + 1;
            await ExecuteAsync(connection, """
                INSERT INTO retention_policies(scope, policy_json, version, updated_at, updated_by)
                VALUES ('workspace', $json, $version, $now, $actor)
                ON CONFLICT(scope) DO UPDATE SET
                    policy_json = excluded.policy_json, version = excluded.version,
                    updated_at = excluded.updated_at, updated_by = excluded.updated_by;
                """, ct, transaction,
                ("$json", JsonSerializer.Serialize(rules, RetentionJson)), ("$version", version), ("$now", Iso(now)), ("$actor", actorId));
            await AuditAsync(connection, transaction, actorId, "retention-policy.updated", "workspace", "workspace",
                JsonSerializer.Serialize(new { request.ExpectedVersion, version }), ct);
            updated = new RetentionPolicyDto("workspace", version, now, actorId, rules.Values.Select(ToRetentionRuleDto).ToList());
        }, ct);
        return updated!;
    }

    internal async Task<RetentionPolicyDto> GetProjectRetentionPolicyDtoAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var row = await ReadRetentionPolicyRowAsync(connection, null, $"project:{projectId}", ct);
        return row is null
            ? new RetentionPolicyDto(projectId, 0, DateTime.UnixEpoch, "workspace-default", [])
            : new RetentionPolicyDto(projectId, row.Value.Version, row.Value.UpdatedAt, row.Value.UpdatedBy,
                row.Value.Rules.Values.Select(ToRetentionRuleDto).ToList());
    }

    internal async Task<RetentionPolicyDto> UpdateProjectRetentionPolicyAsync(
        string projectId, UpdateRetentionPolicyRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var rules = request.Rules.Select(ToRetentionRule).ToDictionary(rule => rule.ArtifactClass);
        var scope = $"project:{projectId}";
        RetentionPolicyDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadRetentionPolicyRowAsync(connection, transaction, scope, ct);
            var expected = existing?.Version ?? 0;
            if (expected != request.ExpectedVersion)
                throw new TaskServerConflictException("resource-version-mismatch",
                    $"Expected retention policy version {request.ExpectedVersion}, current version is {expected}.");

            var workspaceRow = await ReadRetentionPolicyRowAsync(connection, transaction, "workspace", ct);
            var workspaceDefaults = workspaceRow?.Rules ?? RetentionPolicy.Default().WorkspaceDefaults;
            ValidateRetentionPolicy(new RetentionPolicy
            {
                WorkspaceDefaults = workspaceDefaults,
                ProjectOverrides = new Dictionary<string, ProjectRetentionOverride> { [projectId] = new() { Rules = rules } },
            });

            var now = UtcNow;
            var version = expected + 1;
            await ExecuteAsync(connection, """
                INSERT INTO retention_policies(scope, policy_json, version, updated_at, updated_by)
                VALUES ($scope, $json, $version, $now, $actor)
                ON CONFLICT(scope) DO UPDATE SET
                    policy_json = excluded.policy_json, version = excluded.version,
                    updated_at = excluded.updated_at, updated_by = excluded.updated_by;
                """, ct, transaction,
                ("$scope", scope), ("$json", JsonSerializer.Serialize(rules, RetentionJson)), ("$version", version), ("$now", Iso(now)), ("$actor", actorId));
            await AuditAsync(connection, transaction, actorId, "retention-policy.project-updated", "project", projectId,
                JsonSerializer.Serialize(new { request.ExpectedVersion, version }), ct);
            updated = new RetentionPolicyDto(projectId, version, now, actorId, rules.Values.Select(ToRetentionRuleDto).ToList());
        }, ct);
        return updated!;
    }

    internal async Task DeleteProjectRetentionPolicyAsync(string projectId, long expectedVersion, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var scope = $"project:{projectId}";
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadRetentionPolicyRowAsync(connection, transaction, scope, ct);
            if (existing is null) return;
            if (existing.Value.Version != expectedVersion)
                throw new TaskServerConflictException("resource-version-mismatch",
                    $"Expected retention policy version {expectedVersion}, current version is {existing.Value.Version}.");
            await ExecuteAsync(connection, "DELETE FROM retention_policies WHERE scope = $scope;", ct, transaction, ("$scope", scope));
            await AuditAsync(connection, transaction, actorId, "retention-policy.project-reset", "project", projectId, "{}", ct);
        }, ct);
    }

    internal async Task<(string RunId, RetentionPlanDto Plan)> PlanRetentionRunAsync(RunRetentionRequest request, string actorId, CancellationToken ct)
    {
        var policy = await GetActiveRetentionPolicyAsync(ct);
        var inventory = FilterRetentionInventory(await EnumerateRetentionInventoryAsync(ct), request.Project, request.TaskKey);
        var plan = new RetentionPlanner().Plan(inventory, policy, UtcNow);
        var dto = ToRetentionPlanDto(plan);
        var runId = await RecordArchiveRunAsync("manual", "plan", policy.Version, dto, 0, 0, [], [], actorId, ct);
        return (runId, dto);
    }

    internal async Task<RetentionApplyResultDto> ApplyRetentionRunAsync(RunRetentionRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var policy = await GetActiveRetentionPolicyAsync(ct);
        var leasedTaskIds = await GetTaskIdsWithActiveLeasesAsync(ct);
        var inventory = FilterRetentionInventory(await EnumerateRetentionInventoryAsync(ct), request.Project, request.TaskKey)
            .Where(item => !leasedTaskIds.Contains(item.StoreKey)).ToList();
        var plan = new RetentionPlanner().Plan(inventory, policy, UtcNow);
        var result = await new RetentionExecutor(new SqliteRetentionStore(this)).ApplyAsync(plan, policy, ct);
        var dto = ToRetentionPlanDto(plan);
        var runId = await RecordArchiveRunAsync(
            "manual", "apply", policy.Version, dto, result.AppliedActions, result.AppliedBytes, result.Errors, result.Warnings, actorId, ct);
        return new RetentionApplyResultDto(runId, dto, result.AppliedActions, result.AppliedBytes, result.Errors, result.Warnings);
    }

    /// <summary>Runs the active policy without the writable-mode gate, for the legacy import hook (which imports under Maintenance).</summary>
    internal async Task<RetentionApplyResultDto> ApplyRetentionDuringImportAsync(string actorId, CancellationToken ct)
    {
        var policy = await GetActiveRetentionPolicyAsync(ct);
        var inventory = await EnumerateRetentionInventoryAsync(ct);
        var plan = new RetentionPlanner().Plan(inventory, policy, UtcNow);
        var result = await new RetentionExecutor(new SqliteRetentionStore(this)).ApplyAsync(plan, policy, ct);
        var dto = ToRetentionPlanDto(plan);
        await RecordArchiveRunAsync(
            "import", "apply", policy.Version, dto, result.AppliedActions, result.AppliedBytes, result.Errors, result.Warnings, actorId, ct);
        return new RetentionApplyResultDto(string.Empty, dto, result.AppliedActions, result.AppliedBytes, result.Errors, result.Warnings);
    }

    internal async Task<RetentionApplyResultDto> ArchiveTaskNowAsync(string taskIdentity, int? requestedStage, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var policy = await GetActiveRetentionPolicyAsync(ct);
        var inventory = await EnumerateRetentionInventoryAsync(ct);
        var task = inventory.SingleOrDefault(item =>
                string.Equals(item.StoreKey, taskIdentity, StringComparison.Ordinal)
                || string.Equals(item.TaskKey, taskIdentity, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Task '{taskIdentity}' was not found.");
        var heavyRule = policy.RuleFor(task.Project, ArtifactClass.HeavyWorkingData);
        var heavy = task.Files.Where(file => file.Classification.ArtifactClass == ArtifactClass.HeavyWorkingData).ToList();
        var stage = requestedStage ?? (heavy.Count > 0 ? 1 : 2);
        var action = stage >= 2
            ? new RetentionAction(RetentionActionKind.ArchiveTask, heavyRule.Id, task,
                task.Files.Where(file => file.Classification.ArtifactClass is ArtifactClass.Evidence or ArtifactClass.HeavyWorkingData).ToList(),
                task.Files.Where(file => file.Classification.ArtifactClass is ArtifactClass.Evidence or ArtifactClass.HeavyWorkingData)
                    .Sum(file => file.Size),
                2, "operator requested immediate archive")
            : new RetentionAction(RetentionActionKind.ArchiveHeavy, heavyRule.Id, task, heavy, heavy.Sum(file => file.Size), 1,
                "operator requested immediate archive");
        var plan = new RetentionPlan(UtcNow, policy.Version, action.Files.Count == 0 ? [] : [action]);
        var result = await new RetentionExecutor(new SqliteRetentionStore(this)).ApplyAsync(plan, policy, ct);
        var dto = ToRetentionPlanDto(plan);
        var runId = await RecordArchiveRunAsync(
            "manual", "apply", policy.Version, dto, result.AppliedActions, result.AppliedBytes, result.Errors, result.Warnings, actorId, ct);
        return new RetentionApplyResultDto(runId, dto, result.AppliedActions, result.AppliedBytes, result.Errors, result.Warnings);
    }

    internal async Task RestoreArchivedTaskAsync(string taskIdentity, string actorId, CancellationToken ct)
    {
        RequireWritable();
        string taskId;
        string taskKey;
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, "SELECT id, task_key FROM tasks WHERE id = $identity OR task_key = upper($identity);",
                         ("$identity", taskIdentity)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Task '{taskIdentity}' was not found.");
            taskId = reader.GetString(0);
            taskKey = reader.GetString(1);
        }
        await new SqliteRetentionStore(this).RestoreAsync(taskKey, ct);
        await InWriteTransactionAsync(async (connection, transaction)
            => await AuditAsync(connection, transaction, actorId, "retention.restored", "task", taskId, "{}", ct), ct);
    }

    internal async Task<IReadOnlyList<RetentionRunSummaryDto>> ListRetentionRunsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var result = new List<RetentionRunSummaryDto>();
        await using var command = Command(connection, """
            SELECT id, started_at, finished_at, trigger_kind, mode, policy_version, action_count, applied_bytes, actor_id
              FROM archive_runs ORDER BY started_at DESC;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new RetentionRunSummaryDto(
                reader.GetString(0), Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetString(8)));
        return result;
    }

    internal async Task<RetentionRunDetailDto?> GetRetentionRunAsync(string runId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, started_at, finished_at, trigger_kind, mode, policy_version, action_count, applied_bytes, actor_id, report_json
              FROM archive_runs WHERE id = $id;
            """, ("$id", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var summary = new RetentionRunSummaryDto(
            reader.GetString(0), Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
            reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetString(8));
        using var report = JsonDocument.Parse(reader.GetString(9));
        var plan = JsonSerializer.Deserialize<RetentionPlanDto>(report.RootElement.GetProperty("plan").GetRawText(), RetentionJson)!;
        var errors = JsonSerializer.Deserialize<List<string>>(report.RootElement.GetProperty("errors").GetRawText(), RetentionJson) ?? [];
        var warnings = JsonSerializer.Deserialize<List<string>>(report.RootElement.GetProperty("warnings").GetRawText(), RetentionJson) ?? [];
        return new RetentionRunDetailDto(summary, plan, errors, warnings);
    }

    internal async Task<RetentionArchiveManifestDto?> GetRetentionManifestAsync(string taskIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT t.id, t.task_key, p.name, am.archived_at, am.manifest_json, am.total_bytes, am.state, am.restored_at
              FROM tasks t
              JOIN projects p ON p.id = t.project_id
              JOIN archive_manifests am ON am.task_id = t.id
             WHERE t.id = $identity OR t.task_key = upper($identity);
            """, ("$identity", taskIdentity));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var envelope = JsonSerializer.Deserialize<RetentionManifestEnvelope>(reader.GetString(4), RetentionJson)!;
        var stages = envelope.Stages.Select(stage => new RetentionArchiveStageDto(
            stage.Stage, stage.ArchivedAt.UtcDateTime, stage.PayloadPath, stage.PayloadSha256, stage.TotalBytes,
            stage.Files.Select(file => new RetentionArchiveFileDto(file.RelativePath, file.Size, file.Sha256)).ToList())).ToList();
        return new RetentionArchiveManifestDto(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)), reader.GetString(6),
            reader.GetInt64(5), reader.IsDBNull(7) ? null : Parse(reader.GetString(7)), stages);
    }

    internal async Task EmitRetentionRunCompletedAuditAsync(
        string runId, int appliedActions, long appliedBytes, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(runId)) return;
        await InWriteTransactionAsync(async (connection, transaction)
            => await AuditAsync(connection, transaction, actorId, "retention.run.completed", "archive-run", runId,
                JsonSerializer.Serialize(new { appliedActions, appliedBytes }), ct), ct);
    }

    private async Task<HashSet<string>> GetTaskIdsWithActiveLeasesAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT DISTINCT task_id FROM leases WHERE status IN ('active','process-unknown');");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    private async Task<string> RecordArchiveRunAsync(
        string trigger, string mode, int policyVersion, RetentionPlanDto plan,
        int appliedActions, long appliedBytes, IReadOnlyList<string> errors, IReadOnlyList<string> warnings, string actorId, CancellationToken ct)
    {
        var id = $"arun_{Guid.NewGuid():N}";
        var startedAt = UtcNow;
        var reportJson = JsonSerializer.Serialize(new { plan, errors, warnings }, RetentionJson);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO archive_runs(id, started_at, finished_at, trigger_kind, mode, policy_version, action_count, applied_bytes, report_json, actor_id)
                VALUES ($id, $started, $finished, $trigger, $mode, $version, $actions, $bytes, $report, $actor);
                """, ct, transaction,
                ("$id", id), ("$started", Iso(startedAt)), ("$finished", Iso(UtcNow)), ("$trigger", trigger), ("$mode", mode),
                ("$version", policyVersion), ("$actions", plan.ActionCount), ("$bytes", appliedBytes), ("$report", reportJson), ("$actor", actorId));
            await AuditAsync(connection, transaction, actorId, $"retention.{mode}", "archive-run", id,
                JsonSerializer.Serialize(new { trigger, plan.ActionCount, plan.TotalBytes, appliedActions, appliedBytes, errors = errors.Count }), ct);
        }, ct);
        return id;
    }

    private static RetentionPlanDto ToRetentionPlanDto(RetentionPlan plan)
    {
        var actionable = plan.Actions.Where(action => action.Kind != RetentionActionKind.RefuseOversize).ToList();
        return new RetentionPlanDto(
            plan.PlannedAt.UtcDateTime, plan.PolicyVersion,
            actionable.Count, actionable.Sum(action => action.Bytes), plan.AffectedTasks,
            plan.Actions.Select(ToRetentionActionDto).ToList());
    }

    private static RetentionActionDto ToRetentionActionDto(RetentionAction action) => new(
        action.Kind.ToString(), action.RuleId, action.Task.Project, action.Task.TaskKey, action.Task.StoreKey,
        action.Stage, action.Bytes, action.Files.Count, action.Reason);

    private static RetentionRuleDto ToRetentionRuleDto(RetentionRule rule) => new(
        rule.Id, rule.ArtifactClass.ToString(), rule.HotCapBytesPerFile, rule.HotBudgetBytesPerTask, rule.RefuseAboveBytes,
        rule.ArchiveAfterDaysTerminal, rule.ArchiveTaskAfterDaysTerminal, rule.DeleteAfterDays, rule.DeleteArchiveAfterDaysTerminal,
        rule.DeleteArchiveEnabled, rule.NeverArchiveLanes.Order(StringComparer.OrdinalIgnoreCase).ToList());

    private static RetentionRule ToRetentionRule(RetentionRuleDto dto) => new()
    {
        Id = dto.Id,
        ArtifactClass = Enum.Parse<ArtifactClass>(dto.ArtifactClass, ignoreCase: true),
        HotCapBytesPerFile = dto.HotCapBytesPerFile,
        HotBudgetBytesPerTask = dto.HotBudgetBytesPerTask,
        RefuseAboveBytes = dto.RefuseAboveBytes,
        ArchiveAfterDaysTerminal = dto.ArchiveAfterDaysTerminal,
        ArchiveTaskAfterDaysTerminal = dto.ArchiveTaskAfterDaysTerminal,
        DeleteAfterDays = dto.DeleteAfterDays,
        DeleteArchiveAfterDaysTerminal = dto.DeleteArchiveAfterDaysTerminal,
        DeleteArchiveEnabled = dto.DeleteArchiveEnabled,
        NeverArchiveLanes = new HashSet<string>(dto.NeverArchiveLanes, StringComparer.OrdinalIgnoreCase),
    };

    private static Dictionary<ArtifactClass, RetentionRule> ValidateWorkspaceRetentionRules(IReadOnlyList<RetentionRuleDto> dtos)
    {
        var rules = new Dictionary<ArtifactClass, RetentionRule>();
        foreach (var dto in dtos)
        {
            var rule = ToRetentionRule(dto);
            rules[rule.ArtifactClass] = rule;
        }
        var missing = Enum.GetValues<ArtifactClass>().Where(value => !rules.ContainsKey(value)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Workspace retention policy is missing rules for: {string.Join(", ", missing)}.");
        return rules;
    }

    private static void ValidateRetentionPolicy(RetentionPolicy candidate)
    {
        try
        {
            candidate.Validate();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(exception.Message);
        }
    }
}
