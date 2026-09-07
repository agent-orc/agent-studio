namespace AgentStudio.ModelMigrations;

/// <summary>
/// Detects operator-visible migration proposals and applies catalog-approved
/// changes. Token Economy owns the migration decision; Studio owns current
/// CLI availability, persisted pins, workspace policy, and the audit trail.
/// </summary>
public sealed class ModelMigrationService
{
    private const string CatalogSourceDisplay = "Token Economy project";

    private readonly IModelMigrationCatalogProvider _catalogs;
    private readonly ModelFamilyResolver _families;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ProjectSettingsService _projectSettings;
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceRegistry _workspaces;
    private readonly WorkspaceSettingsService _workspaceSettings;
    private readonly ModelConfigurationPinStore _configurationPins;
    private readonly TimelineLog _timeline;
    private readonly OrchestratorLog _operatorFeed;
    private readonly ILogger<ModelMigrationService> _logger;

    public ModelMigrationService(
        IModelMigrationCatalogProvider catalogs,
        ModelFamilyResolver families,
        TaskScannerService scanner,
        TaskMutationService mutations,
        ProjectSettingsService projectSettings,
        ProjectRegistry projects,
        WorkspaceRegistry workspaces,
        WorkspaceSettingsService workspaceSettings,
        ModelConfigurationPinStore configurationPins,
        TimelineLog timeline,
        OrchestratorLog operatorFeed,
        ILogger<ModelMigrationService> logger)
    {
        _catalogs = catalogs;
        _families = families;
        _scanner = scanner;
        _mutations = mutations;
        _projectSettings = projectSettings;
        _projects = projects;
        _workspaces = workspaces;
        _workspaceSettings = workspaceSettings;
        _configurationPins = configurationPins;
        _timeline = timeline;
        _operatorFeed = operatorFeed;
        _logger = logger;
    }

    public async Task<ModelMigrationStatus> GetStatusAsync(
        string? project = null,
        string? taskId = null,
        string? workspaceId = null,
        CancellationToken ct = default)
    {
        var snapshot = _catalogs.GetCatalog();
        var effectiveWorkspaceId = ResolveWorkspaceId(workspaceId, project);
        var autoApply = AutoApplyEnabled(effectiveWorkspaceId);
        if (snapshot.Catalog is null)
        {
            return new ModelMigrationStatus
            {
                CatalogVersion = "unavailable",
                CatalogSource = CatalogSourceDisplay,
                CatalogStale = snapshot.IsStale,
                CatalogError = snapshot.Error,
                AutoApplySafe = autoApply,
            };
        }

        var rules = await AvailableRulesAsync(snapshot.Catalog, ct);
        var proposals = new List<ModelMigrationProposal>();
        AddTaskProposals(proposals, rules, snapshot.Catalog.CatalogVersion, project, taskId);
        AddPipelineProposals(proposals, rules, snapshot.Catalog.CatalogVersion, project);
        AddConfigurationProposals(proposals, rules, snapshot.Catalog.CatalogVersion);

        return new ModelMigrationStatus
        {
            CatalogVersion = snapshot.Catalog.CatalogVersion,
            CatalogSource = CatalogSourceDisplay,
            CatalogStale = snapshot.IsStale,
            CatalogError = snapshot.Error,
            AutoApplySafe = autoApply,
            Proposals = proposals
                .OrderBy(proposal => proposal.Scope, StringComparer.Ordinal)
                .ThenBy(proposal => proposal.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(proposal => proposal.TaskId ?? proposal.StepId ?? proposal.ConfigKey,
                    StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    /// <summary>
    /// Applies one safe migration before a run makes its model and quota
    /// decisions. Explicit card pins are always returned unchanged.
    /// </summary>
    public async Task<TaskInfo> ApplySafeAtAdmissionAsync(TaskInfo task, CancellationToken ct = default)
    {
        if (task.ModelExplicit || string.IsNullOrWhiteSpace(task.Model)) return task;

        var workspaceId = ResolveWorkspaceId(null, task.ProjectName, task.WatchPath);
        if (!AutoApplyEnabled(workspaceId)) return task;

        var snapshot = _catalogs.GetCatalog();
        var catalog = snapshot.Catalog;
        if (catalog is null) return task;

        var rule = catalog.Migrations.FirstOrDefault(candidate =>
            candidate.SafeAuto
            && IsSameFamilyMigration(candidate)
            && !SameModel(candidate.From, candidate.To)
            && SameModel(candidate.From, task.Model));
        if (rule is null || !await IsTargetCurrentAndAvailableAsync(rule, ct)) return task;

        if (!_mutations.ApplyAutomaticModelMigration(task.Id, rule.From, rule.To, task.WatchPath))
            return _scanner.FindJob(task.Id, task.WatchPath) ?? task;

        WriteAudit(task, rule, catalog.CatalogVersion, "safeAuto");
        _logger.LogInformation(
            "model-migration-applied-at-admission taskId={TaskId} from={FromModel} to={ToModel} rule={Rule} catalogVersion={CatalogVersion}",
            task.Id,
            rule.From,
            rule.To,
            RuleId(rule),
            catalog.CatalogVersion);
        return _scanner.FindJob(task.Id, task.WatchPath) ?? task with
        {
            Model = rule.To,
            ModelExplicit = false,
            ThinkingLevel = task.ThinkingLevelExplicit
                ? task.ThinkingLevel
                : ModelMetadataRegistry.DefaultThinkingLevelForCli(task.CliType, rule.To),
        };
    }

    public async Task<ModelMigrationApplyResult> ApplyAsync(
        ApplyModelMigrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = _catalogs.GetCatalog(refresh: true);
        var catalog = snapshot.Catalog
            ?? throw new InvalidOperationException("The Token Economy migration catalog is unavailable.");
        if (!string.Equals(catalog.CatalogVersion, request.CatalogVersion, StringComparison.Ordinal))
            throw new ModelMigrationConflictException("The migration catalog changed. Refresh the proposal and try again.");

        var rule = catalog.Migrations.FirstOrDefault(candidate =>
            SameModel(candidate.From, request.ExpectedFromModel)
            && !SameModel(candidate.From, candidate.To))
            ?? throw new ModelMigrationConflictException("The requested migration is no longer in the catalog.");
        if (!await IsTargetCurrentAndAvailableAsync(rule, ct))
            throw new ModelMigrationConflictException("The target model is not the current available model for its family.");

        var applied = request.Scope switch
        {
            ModelMigrationScopes.Task => ApplyTask(request, rule, catalog.CatalogVersion),
            ModelMigrationScopes.PipelineStep => ApplyPipelineStep(request, rule),
            ModelMigrationScopes.Configuration => ApplyConfiguration(request, rule),
            _ => throw new ArgumentException($"Unsupported migration scope '{request.Scope}'.", nameof(request)),
        };
        if (!applied)
            throw new ModelMigrationConflictException("The model pin changed after this proposal was loaded. Refresh and try again.");

        return new ModelMigrationApplyResult
        {
            Applied = true,
            FromModel = rule.From,
            ToModel = rule.To,
            Rule = RuleId(rule),
            CatalogVersion = catalog.CatalogVersion,
        };
    }

    public bool SetAutoApply(bool enabled, string? workspaceId, out string effectiveWorkspaceId)
    {
        effectiveWorkspaceId = ResolveWorkspaceId(workspaceId)
            ?? throw new KeyNotFoundException("No workspace is registered.");
        if (_workspaces.Find(effectiveWorkspaceId) is null)
            throw new KeyNotFoundException($"Unknown workspaceId '{effectiveWorkspaceId}'.");
        _workspaceSettings.SetAutoApplySafeModelMigrations(effectiveWorkspaceId, enabled);
        return enabled;
    }

    private async Task<IReadOnlyList<ModelMigrationRule>> AvailableRulesAsync(
        ModelMigrationCatalog catalog,
        CancellationToken ct)
    {
        var result = new List<ModelMigrationRule>();
        foreach (var rule in catalog.Migrations)
        {
            if (SameModel(rule.From, rule.To)) continue;
            if (await IsTargetCurrentAndAvailableAsync(rule, ct)) result.Add(rule);
        }
        return result;
    }

    private async Task<bool> IsTargetCurrentAndAvailableAsync(ModelMigrationRule rule, CancellationToken ct)
    {
        var targetFamily = ModelMetadataRegistry.FamilyFor(rule.To);
        if (targetFamily is null) return false;
        try
        {
            var current = await _families.ResolveAsync(targetFamily, ct);
            return SameModel(current, rule.To);
        }
        catch (ModelFamilyUnavailableException ex)
        {
            _logger.LogInformation(
                "model-migration-target-unavailable target={TargetModel} family={Family} cli={CliType}",
                rule.To,
                ex.FamilyId,
                ex.CliType);
            return false;
        }
    }

    private void AddTaskProposals(
        ICollection<ModelMigrationProposal> output,
        IReadOnlyList<ModelMigrationRule> rules,
        string catalogVersion,
        string? project,
        string? taskId)
    {
        foreach (var task in _scanner.ScanAllJobs())
        {
            if (!string.IsNullOrWhiteSpace(taskId)
                && !string.Equals(task.Id, taskId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchesProject(task, project)) continue;
            var rule = FindRule(rules, task.Model);
            if (rule is null) continue;
            output.Add(BuildProposal(rule, catalogVersion) with
            {
                Scope = ModelMigrationScopes.Task,
                ProjectName = task.ProjectName,
                TaskId = task.Id,
                Explicit = task.ModelExplicit,
            });
        }
    }

    private void AddPipelineProposals(
        ICollection<ModelMigrationProposal> output,
        IReadOnlyList<ModelMigrationRule> rules,
        string catalogVersion,
        string? project)
    {
        foreach (var (projectName, settings) in _projectSettings.GetAll())
        {
            if (!MatchesProject(projectName, project)) continue;
            if (settings.PipelineStepsByType is not null)
            {
                foreach (var (pipelineType, steps) in settings.PipelineStepsByType)
                    AddPipelineStepProposals(output, rules, catalogVersion, projectName, pipelineType, steps);
            }

            if (settings.PipelineSteps is not null)
            {
                AddPipelineStepProposals(
                    output,
                    rules,
                    catalogVersion,
                    projectName,
                    PipelineTypes.Task,
                    settings.PipelineSteps,
                    skipKeys: settings.PipelineStepsByType?
                        .FirstOrDefault(entry => string.Equals(
                            entry.Key,
                            PipelineTypes.Task,
                            StringComparison.OrdinalIgnoreCase))
                        .Value?
                        .Keys
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private static void AddPipelineStepProposals(
        ICollection<ModelMigrationProposal> output,
        IReadOnlyList<ModelMigrationRule> rules,
        string catalogVersion,
        string projectName,
        string pipelineType,
        IReadOnlyDictionary<string, PipelineStepSetting> steps,
        IReadOnlySet<string>? skipKeys = null)
    {
        foreach (var (stepId, setting) in steps)
        {
            if (skipKeys?.Contains(stepId) == true) continue;
            var rule = FindRule(rules, setting.Model);
            if (rule is null) continue;
            output.Add(BuildProposal(rule, catalogVersion) with
            {
                Scope = ModelMigrationScopes.PipelineStep,
                ProjectName = projectName,
                PipelineType = PipelineTypes.Normalize(pipelineType),
                StepId = stepId,
                Explicit = true,
            });
        }
    }

    private void AddConfigurationProposals(
        ICollection<ModelMigrationProposal> output,
        IReadOnlyList<ModelMigrationRule> rules,
        string catalogVersion)
    {
        foreach (var (key, value) in _configurationPins.GetPins())
        {
            var rule = FindRule(rules, value);
            if (rule is null) continue;
            output.Add(BuildProposal(rule, catalogVersion) with
            {
                Scope = ModelMigrationScopes.Configuration,
                ConfigKey = key,
                Explicit = true,
            });
        }
    }

    private bool ApplyTask(ApplyModelMigrationRequest request, ModelMigrationRule rule, string catalogVersion)
    {
        if (string.IsNullOrWhiteSpace(request.TaskId))
            throw new ArgumentException("taskId is required for task migrations.", nameof(request));
        var task = _scanner.FindJob(request.TaskId, ResolveWatchPath(request.ProjectName));
        if (task is null || !SameModel(task.Model, rule.From)) return false;
        if (!ModelMetadataRegistry.IsCompatibleWithCli(task.CliType, rule.To)) return false;
        if (!_mutations.SetJobModel(task.Id, rule.To, task.WatchPath)) return false;
        WriteAudit(task, rule, catalogVersion, "operator");
        return true;
    }

    private bool ApplyPipelineStep(ApplyModelMigrationRequest request, ModelMigrationRule rule)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName)
            || string.IsNullOrWhiteSpace(request.PipelineType)
            || string.IsNullOrWhiteSpace(request.StepId))
        {
            throw new ArgumentException(
                "projectName, pipelineType, and stepId are required for pipeline-step migrations.",
                nameof(request));
        }

        var currentSettings = _projectSettings.Get(request.ProjectName);
        var current = PipelineTypeSettings.ForType(currentSettings, request.PipelineType)
            ?.PipelineSteps?
            .FirstOrDefault(entry => string.Equals(entry.Key, request.StepId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(current?.Key)
            || current.Value.Value is null
            || !SameModel(current.Value.Value.Model, rule.From)) return false;

        _projectSettings.SetPipelineStep(
            request.ProjectName,
            request.PipelineType,
            current.Value.Key,
            current.Value.Value with { Model = rule.To });
        return true;
    }

    private bool ApplyConfiguration(ApplyModelMigrationRequest request, ModelMigrationRule rule)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigKey))
            throw new ArgumentException("configKey is required for configuration migrations.", nameof(request));
        return _configurationPins.TryApply(request.ConfigKey, rule.From, rule.To);
    }

    private void WriteAudit(
        TaskInfo task,
        ModelMigrationRule rule,
        string catalogVersion,
        string application)
    {
        var ruleId = RuleId(rule);
        _timeline.Append(
            task.FolderPath,
            TimelineEventKinds.ModelMigrated,
            "system",
            $"Model migrated from {rule.From} to {rule.To}.",
            details: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from"] = rule.From,
                ["to"] = rule.To,
                ["rule"] = ruleId,
                ["catalogVersion"] = catalogVersion,
                ["application"] = application,
            });
        _operatorFeed.Append(task.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Action,
            Topic = OrchestratorLogTopics.ModelMigration,
            Summary = $"Model migrated from {rule.From} to {rule.To} for {task.Id}.",
            Reasoning = $"Rule {ruleId}, Token Economy catalog {catalogVersion}, application {application}.",
            JobId = task.Id,
        });
    }

    private static ModelMigrationProposal BuildProposal(ModelMigrationRule rule, string catalogVersion)
    {
        var sourceCliType = CliTypeForModel(rule.From, rule.Vendor);
        var targetCliType = CliTypeForModel(rule.To, rule.Vendor);
        return new ModelMigrationProposal
        {
            FromModel = rule.From,
            ToModel = rule.To,
            Rule = RuleId(rule),
            CatalogVersion = catalogVersion,
            CostClassFrom = rule.CostClassFrom,
            CostClassTo = rule.CostClassTo,
            ReasoningLadderFrom = ModelMetadataRegistry.ThinkingLevelsFor(sourceCliType, rule.From).ToList(),
            ReasoningLadderTo = ModelMetadataRegistry.ThinkingLevelsFor(targetCliType, rule.To).ToList(),
        };
    }

    private static string CliTypeForModel(string model, string fallbackVendor)
    {
        var family = ModelMetadataRegistry.FamilyFor(model);
        return family is not null
            ? ModelFamilies.CliTypeFor(family)
            : string.Equals(fallbackVendor, "anthropic", StringComparison.OrdinalIgnoreCase)
                ? CliTypes.Claude
                : CliTypes.Codex;
    }

    private static ModelMigrationRule? FindRule(
        IEnumerable<ModelMigrationRule> rules,
        string? model)
        => rules.FirstOrDefault(rule => SameModel(rule.From, model));

    private static bool IsSameFamilyMigration(ModelMigrationRule rule)
    {
        var sourceFamily = ModelMetadataRegistry.FamilyFor(rule.From);
        var targetFamily = ModelMetadataRegistry.FamilyFor(rule.To);
        return sourceFamily is not null
               && string.Equals(sourceFamily, targetFamily, StringComparison.OrdinalIgnoreCase);
    }

    private static string RuleId(ModelMigrationRule rule)
        => IsSameFamilyMigration(rule)
            ? $"{ModelMigrationFamilyMap.ToStudioFamily(rule.Family)}/latest-in-family"
            : string.Concat(
                "token-economy/",
                ModelMetadataRegistry.NormalizeId(rule.From),
                "-to-",
                ModelMetadataRegistry.NormalizeId(rule.To));

    private bool MatchesProject(TaskInfo task, string? requested)
        => string.IsNullOrWhiteSpace(requested)
           || MatchesProject(task.ProjectName, requested)
           || MatchesProject(_projects.FindByStorageLocation(task.WatchPath), requested);

    private bool MatchesProject(string actual, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return true;
        if (string.Equals(actual, requested.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return MatchesProject(_projects.FindByIdOrDisplayName(actual), requested)
               || MatchesProject(_projects.FindByShortCode(actual), requested);
    }

    private static bool MatchesProject(ProjectRecord? project, string? requested)
        => project is not null
           && !string.IsNullOrWhiteSpace(requested)
           && (string.Equals(project.Id, requested.Trim(), StringComparison.OrdinalIgnoreCase)
               || string.Equals(project.DisplayName, requested.Trim(), StringComparison.OrdinalIgnoreCase)
               || string.Equals(project.ShortCode, requested.Trim(), StringComparison.OrdinalIgnoreCase));

    private string? ResolveWatchPath(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return null;
        var record = _projects.FindByIdOrDisplayName(project)
                     ?? _projects.FindByShortCode(project);
        return record?.StorageLocation
               ?? _scanner.GetWatchPaths().FirstOrDefault(entry =>
                   string.Equals(entry.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;
    }

    private string? ResolveWorkspaceId(
        string? requested,
        string? project = null,
        string? watchPath = null)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        var projectRecord = !string.IsNullOrWhiteSpace(watchPath)
            ? _projects.FindByStorageLocation(watchPath)
            : null;
        projectRecord ??= _projects.FindByIdOrDisplayName(project)
                          ?? _projects.FindByShortCode(project);
        if (!string.IsNullOrWhiteSpace(projectRecord?.WorkspaceId)) return projectRecord.WorkspaceId;
        return _workspaces.List().FirstOrDefault(workspace => workspace.IsDefault)?.Id
               ?? _workspaces.List().FirstOrDefault()?.Id;
    }

    private bool AutoApplyEnabled(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId)
           || (_workspaceSettings.Get(workspaceId).AutoApplySafeModelMigrations ?? true);

    private static bool SameModel(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(
               ModelMetadataRegistry.NormalizeId(left),
               ModelMetadataRegistry.NormalizeId(right),
               StringComparison.OrdinalIgnoreCase);
}

public static class ModelMigrationScopes
{
    public const string Task = "task";
    public const string PipelineStep = "pipelineStep";
    public const string Configuration = "configuration";
}

public sealed record ModelMigrationProposal
{
    public string Scope { get; init; } = "";
    public string? ProjectName { get; init; }
    public string? TaskId { get; init; }
    public string? PipelineType { get; init; }
    public string? StepId { get; init; }
    public string? ConfigKey { get; init; }
    public string FromModel { get; init; } = "";
    public string ToModel { get; init; } = "";
    public string Rule { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
    public bool Explicit { get; init; }
    public string? CostClassFrom { get; init; }
    public string? CostClassTo { get; init; }
    public List<string>? ReasoningLadderFrom { get; init; }
    public List<string>? ReasoningLadderTo { get; init; }
}

public sealed record ModelMigrationStatus
{
    public string CatalogVersion { get; init; } = "";
    public string CatalogSource { get; init; } = "";
    public bool CatalogStale { get; init; }
    public string? CatalogError { get; init; }
    public bool AutoApplySafe { get; init; } = true;
    public List<ModelMigrationProposal> Proposals { get; init; } = [];
}

public sealed record ApplyModelMigrationRequest
{
    public string Scope { get; init; } = "";
    public string? ProjectName { get; init; }
    public string? TaskId { get; init; }
    public string? PipelineType { get; init; }
    public string? StepId { get; init; }
    public string? ConfigKey { get; init; }
    public string ExpectedFromModel { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
}

public sealed record SetAutoApplyModelMigrationsRequest
{
    public bool Enabled { get; init; }
    public string? WorkspaceId { get; init; }
}

public sealed record ModelMigrationApplyResult
{
    public bool Applied { get; init; }
    public string FromModel { get; init; } = "";
    public string ToModel { get; init; } = "";
    public string Rule { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
}

public sealed class ModelMigrationConflictException(string message) : Exception(message);
