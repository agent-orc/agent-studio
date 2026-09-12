using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backing store for the Studio P3 "administration and long tail" bundle
/// (docs/studio-route-ownership/index.html): the 24 read-only routes left
/// once P0-P2 delivered every write path and every other projection. Every
/// method here reads a table a P0-P2 bundle already migrated (
/// <c>studio_admin_config</c>/<c>studio_admin_prompts</c>,
/// <c>studio_cli_settings</c>, <c>studio_project_settings</c>,
/// <c>studio_proposals</c>, <c>studio_publish_settings</c>/
/// <c>studio_operations</c>, <c>studio_watch_paths</c>) or the core
/// <c>tasks</c>/<c>runs</c>/<c>run_completions</c> tables, plus one static
/// document (<see cref="ModelRoutingPolicyDocument"/>, vendored alongside
/// <c>backend/Policies/model-routing-policy.v1.json</c>) and a handful of
/// small vendored enum lists that would otherwise require referencing the
/// CLI-runner NuGet package the architecture boundary test forbids. No new
/// table or column is added by this bundle.
/// </summary>
public sealed partial class TaskServerStore
{
    // ---- Admin config + prompt overrides ---------------------------------

    public async Task<OrchestratorConfigDto> GetOrchestratorConfigAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT config_json, updated_at FROM studio_admin_config WHERE config_key = $key;",
            ("$key", OrchestratorConfigKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new OrchestratorConfigDto("{}", DateTime.UtcNow);
        return new OrchestratorConfigDto(reader.GetString(0), Parse(reader.GetString(1)));
    }

    public async Task<PromptOverrideListResponse> ListPromptOverridesAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var names = new List<string>();
        await using (var command = Command(connection, "SELECT name FROM studio_admin_prompts ORDER BY name;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));

        var items = new List<PromptOverrideDto>();
        foreach (var name in names)
        {
            var item = await ReadPromptOverrideAsync(connection, null, name, ct);
            if (item is not null) items.Add(item);
        }
        return new PromptOverrideListResponse(items);
    }

    public async Task<PromptOverrideDto?> GetPromptOverrideAsync(string name, CancellationToken ct)
    {
        var promptName = RequirePromptName(name);
        await using var connection = await OpenReadyAsync(ct);
        return await ReadPromptOverrideAsync(connection, null, promptName, ct);
    }

    public async Task<PromptOverrideCoverageResponse> GetPromptOverrideCoverageAsync(CancellationToken ct)
    {
        var list = await ListPromptOverridesAsync(ct);
        var reviewed = list.Items.Count(item => item.LastReviewedAt is not null);
        return new PromptOverrideCoverageResponse(list.Items.Count, reviewed, list.Items.Count - reviewed);
    }

    // ---- Auto-review status ------------------------------------------------

    public async Task<AutoReviewStatusResponse> GetAutoReviewStatusAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body
              FROM tasks WHERE state = $state ORDER BY updated_at DESC;
            """, ("$state", StudioTaskLanes.AutoReview));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<AutoReviewActiveItemDto>();
        while (await reader.ReadAsync(ct))
        {
            var task = ReadTask(reader);
            items.Add(new AutoReviewActiveItemDto(task.TaskId, task.TaskKey, task.Title, task.ProjectId, task.UpdatedAt));
        }
        return new AutoReviewStatusResponse(items, items.Count);
    }

    // ---- CLI / quota settings ----------------------------------------------

    public async Task<CliSettingsDto> GetCliSettingsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT economy_mode, quota_caps_json, quota_model_routes_json, quota_wait_policy_json, updated_at
              FROM studio_cli_settings WHERE id = 1;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new CliSettingsDto(false, null, null, null, DateTime.UtcNow);
        return new CliSettingsDto(
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Parse(reader.GetString(4)));
    }

    public async Task<ModelRoutingPolicyDto> GetModelRoutingPolicyAsync(CancellationToken ct)
    {
        var document = ModelRoutingPolicyDocument.Value;
        var cli = await GetCliSettingsAsync(ct);
        return new ModelRoutingPolicyDto(document.Version, document.WikiPath, document.Tiers, document.TaskTypeDefaults, cli.EconomyMode);
    }

    /// <summary>
    /// Policy-only recommendation for one task type - see the doc comment on
    /// <see cref="ModelRoutingRecommendationDto"/> for the scope-down versus
    /// the legacy live-CLI-probe route.
    /// </summary>
    public Task<ModelRoutingRecommendationDto> GetModelRoutingRecommendationAsync(string? taskType, CancellationToken ct)
    {
        var document = ModelRoutingPolicyDocument.Value;
        var normalized = string.IsNullOrWhiteSpace(taskType) ? "chore" : taskType.Trim().ToLowerInvariant();
        var match = document.TaskTypeDefaults.FirstOrDefault(d => string.Equals(d.TaskType, normalized, StringComparison.OrdinalIgnoreCase))
            ?? document.TaskTypeDefaults.First(d => string.Equals(d.TaskType, "chore", StringComparison.OrdinalIgnoreCase));
        var tier = document.Tiers.FirstOrDefault(t => string.Equals(t.Id, match.Tier, StringComparison.OrdinalIgnoreCase))
            ?? document.Tiers[0];
        return Task.FromResult(new ModelRoutingRecommendationDto(normalized, tier.Id, tier.Model, tier.ThinkingLevel, match.Score));
    }

    // ---- Per-project resolved settings -------------------------------------

    public async Task<StudioProjectSettingsDto> GetProjectSettingsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        return await ReadProjectSettingsAsync(connection, null, project.ProjectId, ct);
    }

    public async Task<ResolvedOptionListDto> GetProjectCliContextModeAsync(string projectIdentity, CancellationToken ct)
    {
        var settings = await GetProjectSettingsAsync(projectIdentity, ct);
        return new ResolvedOptionListDto(settings.CliContextMode, CliContextModesAvailable);
    }

    public async Task<ResolvedOptionListDto> GetProjectCliModeAsync(string projectIdentity, CancellationToken ct)
    {
        var settings = await GetProjectSettingsAsync(projectIdentity, ct);
        return new ResolvedOptionListDto(settings.CliMode, CliPermissionModesAvailable);
    }

    public async Task<ResolvedOptionListDto> GetProjectLaneSortStrategyAsync(string projectIdentity, CancellationToken ct)
    {
        var settings = await GetProjectSettingsAsync(projectIdentity, ct);
        return new ResolvedOptionListDto(settings.LaneSortStrategy, LaneSortStrategiesAvailable);
    }

    public async Task<ProjectQuotaWaitPolicyDto> GetProjectQuotaWaitPolicyAsync(string projectIdentity, CancellationToken ct)
    {
        var settings = await GetProjectSettingsAsync(projectIdentity, ct);
        return new ProjectQuotaWaitPolicyDto(settings.QuotaWaitPolicyJson, settings.UpdatedAt);
    }

    public async Task<AllProjectSettingsResponse> ListAllProjectSettingsAsync(CancellationToken ct)
    {
        var projects = await ListProjectsAsync(null, ct);
        await using var connection = await OpenReadyAsync(ct);
        var result = new Dictionary<string, StudioProjectSettingsDto>(StringComparer.Ordinal);
        foreach (var project in projects)
            result[project.ProjectId] = await ReadProjectSettingsAsync(connection, null, project.ProjectId, ct);
        return new AllProjectSettingsResponse(result);
    }

    // ---- Proposals evidence -------------------------------------------------

    /// <summary>
    /// Finds the proposal that reported <paramref name="relPath"/> as its
    /// evidence pointer and returns its metadata and raw payload. There is
    /// no durable byte-content store for evidence images (see
    /// <see cref="ProposalEvidenceDto"/>), so a caller that needs the actual
    /// image still has no source once this returns - only whether a
    /// proposal claims that path exists.
    /// </summary>
    public async Task<ProposalEvidenceDto?> GetProposalEvidenceAsync(string projectIdentity, string relPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            throw new ArgumentException("An evidence path is required.");
        var proposals = await ListStudioProposalsAsync(projectIdentity, ct);
        foreach (var proposal in proposals.Proposals)
        {
            if (TryReadEvidenceRelPath(proposal.Payload, out var candidate)
                && string.Equals(candidate, relPath, StringComparison.Ordinal))
                return new ProposalEvidenceDto(proposal.Id, candidate, proposal.Payload);
        }
        return null;
    }

    private static readonly string[] EvidencePathPropertyNames = ["evidenceScreenshot", "evidencePath", "relPath"];

    private static bool TryReadEvidenceRelPath(JsonElement payload, out string relPath)
    {
        relPath = "";
        if (payload.ValueKind != JsonValueKind.Object) return false;
        foreach (var propertyName in EvidencePathPropertyNames)
        {
            if (payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                relPath = value.GetString() ?? "";
                return relPath.Length > 0;
            }
        }
        return false;
    }

    // ---- Publish panel / run ------------------------------------------------

    private static string ResolvePublishOperationKind(string targetId) => targetId switch
    {
        "package" => StudioOperationKinds.PublishPackage,
        "website" => StudioOperationKinds.PublishWebsite,
        _ => throw new ArgumentException($"Unknown publish target '{targetId}'."),
    };

    public async Task<PublishPanelDto> GetPublishPanelAsync(string projectIdentity, string targetId, CancellationToken ct)
    {
        var kind = ResolvePublishOperationKind(targetId);
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var automationRaw = await ScalarAsync(connection,
            "SELECT automation_enabled FROM studio_publish_settings WHERE project_id = $project;",
            ct, ("$project", project.ProjectId));
        var automationEnabled = automationRaw is not null && Convert.ToInt64(automationRaw) != 0;
        var latest = await GetLatestCompletedStudioOperationAsync(kind, project.ProjectId, ct);
        return new PublishPanelDto(
            targetId, automationEnabled, latest?.OperationId, latest?.Status, latest?.ResultJson, latest?.CompletedAt);
    }

    public async Task<PublishRunStatusDto> GetPublishRunAsync(string projectIdentity, string targetId, CancellationToken ct)
    {
        var kind = ResolvePublishOperationKind(targetId);
        var project = await RequireProjectAsync(projectIdentity, ct);
        var nonTerminalId = await FindMostRecentNonTerminalOperationAsync(kind, project.ProjectId, ct);
        if (nonTerminalId is not null)
        {
            var running = await RequireStudioOperationAsync(nonTerminalId, ct);
            return new PublishRunStatusDto(targetId, running.Status, running.OperationId, running.ResultJson, running.Error, running.CompletedAt);
        }
        var latest = await GetLatestCompletedStudioOperationAsync(kind, project.ProjectId, ct);
        if (latest is null)
            throw new KeyNotFoundException($"No publish workflow has been triggered for target '{targetId}'.");
        return new PublishRunStatusDto(targetId, latest.Status, latest.OperationId, latest.ResultJson, latest.Error, latest.CompletedAt);
    }

    // ---- Review decisions pending -------------------------------------------

    /// <summary>
    /// Tasks in the durable human-review lane whose most recent run
    /// completion carried a needs-input message - the AGT-2773 escalation
    /// channel's durable successor to the legacy route's live scan of
    /// per-job CLI output logs for an unresolved
    /// <c>[[TASK_NEEDS_INPUT:...]]</c> sentinel in the auto-review lane.
    /// </summary>
    public async Task<ReviewDecisionsPendingResponse> GetReviewDecisionsPendingAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT t.id, t.project_id, t.task_key, t.title, t.state, t.version, t.created_at, t.updated_at, t.body,
                   c.needs_input_message, c.completed_at
              FROM tasks t
              JOIN runs r ON r.task_id = t.id
              JOIN run_completions c ON c.run_id = r.id
             WHERE t.project_id = $project AND t.state = $state
               AND c.needs_input_message IS NOT NULL
               AND c.completed_at = (
                   SELECT MAX(c2.completed_at)
                     FROM runs r2 JOIN run_completions c2 ON c2.run_id = r2.id
                    WHERE r2.task_id = t.id
               )
             ORDER BY c.completed_at DESC;
            """, ("$project", project.ProjectId), ("$state", StudioTaskLanes.HumanReview));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ReviewDecisionPendingItemDto>();
        while (await reader.ReadAsync(ct))
        {
            var task = ReadTask(reader);
            items.Add(new ReviewDecisionPendingItemDto(
                task.TaskId, task.TaskKey, task.Title, reader.GetString(9), Parse(reader.GetString(10))));
        }
        return new ReviewDecisionsPendingResponse(items);
    }

    // ---- Wiki grading status -------------------------------------------------

    public async Task<WikiGradingStatusDto> GetWikiGradingStatusAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var nonTerminalId = await FindMostRecentNonTerminalOperationAsync(StudioOperationKinds.WikiGradingRun, project.ProjectId, ct);
        if (nonTerminalId is not null)
        {
            var running = await RequireStudioOperationAsync(nonTerminalId, ct);
            return new WikiGradingStatusDto(running.Status, running.OperationId, running.ResultJson, running.Error, running.CompletedAt);
        }
        var latest = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.WikiGradingRun, project.ProjectId, ct);
        return latest is null
            ? new WikiGradingStatusDto("never-run", null, null, null, null)
            : new WikiGradingStatusDto(latest.Status, latest.OperationId, latest.ResultJson, latest.Error, latest.CompletedAt);
    }

    // ---- Pipeline catalogue ---------------------------------------------------

    private static readonly string[] PipelineTypesAvailable = ["task", "bug", "feature", "planning"];

    private static readonly PipelineCatalogueStepDto[] StandardCodingPipelineSteps =
    [
        new("pre-loop-guard", "Auto-mode loop guard", "pre"),
        new("pre-model-qualification", "Model qualification", "pre"),
        new("pre-prompt-enrichment", "Prompt enrichment", "pre"),
        new("core-agent-run", "Coding agent run", "core"),
        new("aspect-requirement-fit", "Aspect: requirement fit", "post"),
        new("aspect-code-quality", "Aspect: code quality", "post"),
        new("aspect-documentation-impact", "Aspect: documentation impact", "post"),
        new("aspect-tests-and-evidence", "Aspect: tests and evidence", "post"),
        new("post-git-commit-attribution", "Git commit attribution", "post"),
        new("post-build-test-gate", "Build and test gate", "post"),
        new("post-code-review-grade", "Code review quality grade", "post"),
    ];

    private static readonly PipelineCatalogueStepDto[] ReadOnlyPlanningPipelineSteps =
    [
        new("pre-model-qualification", "Model qualification", "pre"),
        new("core-agent-run", "Coding agent run", "core"),
        new("post-abort-review", "Post-run review", "post"),
    ];

    /// <summary>
    /// The static per-type step catalogue - see the doc comment on
    /// <see cref="PipelineCatalogueTypeDto"/> for the scope-down versus the
    /// legacy route's live stack detection and per-project overrides.
    /// </summary>
    public Task<PipelineCatalogueResponse> GetPipelineCatalogueAsync(string? pipelineType, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pipelineType)
            && !PipelineTypesAvailable.Contains(pipelineType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown pipeline type '{pipelineType}'.");
        var selected = string.IsNullOrWhiteSpace(pipelineType)
            ? PipelineTypesAvailable
            : [PipelineTypesAvailable.First(type => string.Equals(type, pipelineType, StringComparison.OrdinalIgnoreCase))];
        var result = selected
            .Select(type => new PipelineCatalogueTypeDto(
                type,
                string.Equals(type, "planning", StringComparison.Ordinal)
                    ? ReadOnlyPlanningPipelineSteps
                    : StandardCodingPipelineSteps))
            .ToList();
        return Task.FromResult(new PipelineCatalogueResponse(result));
    }

    // ---- Global search (task domain) -----------------------------------------

    /// <summary>
    /// Task-domain-only search, optionally scoped to one project - see the
    /// doc comment on <see cref="StudioSearchResponse"/> for the split from
    /// the legacy mixed <c>/api/search</c> contract.
    /// </summary>
    public async Task<StudioSearchResponse> SearchTasksAsync(
        string? projectIdentity, string? query, int? limit, CancellationToken ct)
    {
        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length < 2)
            return new StudioSearchResponse(trimmed, []);
        string? projectId = null;
        if (!string.IsNullOrWhiteSpace(projectIdentity))
            projectId = (await RequireProjectAsync(projectIdentity, ct)).ProjectId;
        var effectiveLimit = limit is > 0 and <= 100 ? limit.Value : 20;
        var pattern = "%" + trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body
              FROM tasks
             WHERE ($project IS NULL OR project_id = $project)
               AND (task_key LIKE $pattern ESCAPE '\'
                OR title LIKE $pattern ESCAPE '\'
                OR body LIKE $pattern ESCAPE '\')
             ORDER BY updated_at DESC
             LIMIT $limit;
            """, ("$project", projectId), ("$pattern", pattern), ("$limit", effectiveLimit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<StudioSearchTaskItemDto>();
        while (await reader.ReadAsync(ct))
        {
            var task = ReadTask(reader);
            items.Add(new StudioSearchTaskItemDto(task.TaskId, task.TaskKey, task.Title, task.ProjectId, task.State));
        }
        return new StudioSearchResponse(trimmed, items);
    }

    // ---- Watch paths -----------------------------------------------------------

    public async Task<WatchPathListResponse> ListWatchPathsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT name, project_id, pattern, created_at FROM studio_watch_paths ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<WatchPathDto>();
        while (await reader.ReadAsync(ct))
            items.Add(new WatchPathDto(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), Parse(reader.GetString(3))));
        return new WatchPathListResponse(items);
    }

    // ---- Vendored static data --------------------------------------------------

    private static readonly string[] CliContextModesAvailable = ["clean", "shared"];
    private static readonly string[] CliPermissionModesAvailable = ["yolo", "workspace-write", "read-only", "custom"];
    private static readonly string[] LaneSortStrategiesAvailable = ["lane-entry", "manual", "newest-first", "oldest-first", "last-activity"];

    private sealed record ModelRoutingPolicyDocumentData(
        string Version,
        string WikiPath,
        IReadOnlyList<ModelRoutingTierDto> Tiers,
        IReadOnlyList<ModelRoutingTaskTypeDefaultDto> TaskTypeDefaults);

    private static readonly Lazy<ModelRoutingPolicyDocumentData> ModelRoutingPolicyDocument = new(LoadModelRoutingPolicyDocument);

    private static ModelRoutingPolicyDocumentData LoadModelRoutingPolicyDocument()
    {
        var assembly = typeof(TaskServerStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(name => name.EndsWith("model-routing-policy.v1.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var tiers = root.GetProperty("tiers").EnumerateArray()
            .Select(tier => new ModelRoutingTierDto(
                tier.GetProperty("id").GetString()!,
                tier.GetProperty("rank").GetInt32(),
                tier.GetProperty("model").GetString()!,
                tier.GetProperty("thinkingLevel").GetString()!,
                tier.GetProperty("estimatedSavingsPercent").GetInt32()))
            .ToList();
        var taskTypeDefaults = root.GetProperty("taskTypeDefaults").EnumerateObject()
            .Select(property => new ModelRoutingTaskTypeDefaultDto(
                property.Name,
                property.Value.GetProperty("tier").GetString()!,
                property.Value.TryGetProperty("hardFloorTier", out var hardFloor) ? hardFloor.GetString() : null,
                property.Value.GetProperty("score").GetInt32()))
            .ToList();
        return new ModelRoutingPolicyDocumentData(
            root.GetProperty("version").GetString()!,
            root.GetProperty("wikiPath").GetString()!,
            tiers,
            taskTypeDefaults);
    }
}
