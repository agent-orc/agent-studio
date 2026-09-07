using AgentStudio.Configuration;

namespace AgentStudio.ModelMigrations;

public sealed record ModelConfigurationPinView
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string ConfigKey { get; init; } = "";
    public string CurrentModel { get; init; } = "";
    public ModelMigrationProposal? ModelMigration { get; init; }
}

public sealed record ApplyConfigurationPinRequest
{
    public string ExpectedFrom { get; init; } = "";
    public string ToModel { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
    public string Rule { get; init; } = "";
}

public sealed record ModelConfigurationPinApplyResult(
    string Status,
    ModelConfigurationPinView? Pin = null,
    string? Error = null)
{
    public bool Applied => string.Equals(Status, "applied", StringComparison.Ordinal);
}

/// <summary>
/// Coordinates migration proposals, guarded admission-time writes, and the
/// two audit projections. Catalog parsing and safe-rule validation remain pure
/// in <see cref="ModelMigrationCatalogPolicy"/>.
/// </summary>
public sealed class ModelMigrationCoordinator
{
    private static readonly ModelConfigurationPinDescriptor[] ConfigurationPinDescriptors =
    [
        new("summary", "Summary generation", "ClaudeCli:SummaryModel"),
        new("title", "Title generation", "TitleGeneration:Model"),
        new("prompt-enhancement", "Prompt enhancement", "PromptEnhancement:Model"),
        new("wiki-search", "Wiki search", "WikiSearch:Model"),
        new("soft-reasoning", "Soft reasoning", "Supervisor:SoftReasoningModel"),
        new("proposal-drafting", "Proposal drafting", "ProposalManagement:Model"),
        new("review-decision", "Review decision", "ReviewDecisionOrchestrator:Model"),
        new("review-aspect", "Review aspect", "ReviewDecisionOrchestrator:AspectModel"),
        new("global-orchestrator", "Global orchestrator", "GlobalOrchestrator:Model"),
        new("codex", "Codex default", "CodexCli:Model"),
        new("codex-default", "Codex fallback default", "CodexCli:DefaultModel"),
        new("code-review", "Code review", "CodeReviewStep:DefaultModel"),
        new("task-spawner", "Task spawner", "TaskSpawnerStep:DefaultModel"),
    ];

    private readonly ModelMigrationCatalogService _catalog;
    private readonly ModelRoutingPolicyStateStore _state;
    private readonly TaskMutationService _mutations;
    private readonly TimelineLog _timeline;
    private readonly AgentMessageBusBridge? _bus;
    private readonly IConfiguration _configuration;
    private readonly OrchestratorConfigService _configurationWriter;
    private readonly ILogger<ModelMigrationCoordinator> _logger;
    private readonly Func<string, bool> _isModelAvailable;
    private readonly Func<string, bool> _hasDatedPrice;

    public ModelMigrationCoordinator(
        ModelMigrationCatalogService catalog,
        ModelRoutingPolicyStateStore state,
        TaskMutationService mutations,
        TimelineLog timeline,
        AgentMessageBusBridge? bus,
        IConfiguration configuration,
        OrchestratorConfigService configurationWriter,
        ILogger<ModelMigrationCoordinator> logger,
        Func<string, bool>? isModelAvailable = null,
        Func<string, bool>? hasDatedPrice = null)
    {
        _catalog = catalog;
        _state = state;
        _mutations = mutations;
        _timeline = timeline;
        _bus = bus;
        _configuration = configuration;
        _configurationWriter = configurationWriter;
        _logger = logger;
        _isModelAvailable = isModelAvailable ?? ModelFamilyResolver.IsAvailableInFreshCatalog;
        _hasDatedPrice = hasDatedPrice ?? (model =>
            TokenEconomy.ModelPriceCatalog.Default.ResolvePrice(model, DateTime.UtcNow).Price is not null);
    }

    public ModelMigrationCatalogStatus GetStatus(bool forceRefresh = false)
        => _catalog.GetStatus(forceRefresh);

    public ModelMigrationProposal? GetProposal(string? currentModel)
        => _catalog.FindProposal(currentModel, _isModelAvailable);

    public TaskInfo WithProposal(TaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.ModelExplicit || string.IsNullOrWhiteSpace(task.Model))
            return task with { ModelMigration = null };
        return task with { ModelMigration = GetProposal(task.Model) };
    }

    public IReadOnlyList<ModelConfigurationPinView> GetConfigurationPins()
        => ConfigurationPinDescriptors
            .Select(descriptor => ProjectConfigurationPin(descriptor))
            .Where(pin => pin is not null)
            .Cast<ModelConfigurationPinView>()
            .ToList();

    public ModelConfigurationPinApplyResult ApplyConfigurationPin(
        string id,
        ApplyConfigurationPinRequest request)
    {
        var descriptor = ConfigurationPinDescriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return new("not-found", Error: $"Unknown model configuration pin '{id}'.");

        var pin = ProjectConfigurationPin(descriptor, includeWithoutProposal: true);
        if (pin is null)
        {
            return new(
                "stale",
                Error: "The model configuration pin was removed. Refresh and try again.");
        }
        var proposal = pin.ModelMigration;
        if (proposal is null
            || !string.Equals(
                ModelMetadataRegistry.NormalizeId(pin.CurrentModel),
                ModelMetadataRegistry.NormalizeId(request.ExpectedFrom),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                ModelMetadataRegistry.NormalizeId(proposal.To),
                ModelMetadataRegistry.NormalizeId(request.ToModel),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(proposal.CatalogVersion, request.CatalogVersion, StringComparison.Ordinal)
            || !string.Equals(proposal.Rule, request.Rule, StringComparison.Ordinal)
            || proposal.TargetAvailable != true)
        {
            return new(
                "stale",
                pin,
                "The model migration proposal is stale or its target is unavailable. Refresh and try again.");
        }

        if (!_configurationWriter.ApplyModelOverride(descriptor.ConfigKey, proposal.To))
        {
            return new(
                "stale",
                pin,
                "A higher-precedence configuration source keeps this pin read-only. Update that source directly.");
        }
        var effectiveModel = _configuration[descriptor.ConfigKey]?.Trim();
        if (!string.Equals(
                ModelMetadataRegistry.NormalizeId(effectiveModel),
                ModelMetadataRegistry.NormalizeId(proposal.To),
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "stale",
                pin,
                "The effective model did not change after configuration reload. Refresh and try again.");
        }
        var applied = new ModelConfigurationPinView
        {
            Id = descriptor.Id,
            Label = descriptor.Label,
            ConfigKey = descriptor.ConfigKey,
            CurrentModel = effectiveModel!,
            ModelMigration = null,
        };
        _logger.LogInformation(
            "model-configuration-pin-migrated pin={Pin} key={ConfigKey} from={From} to={To} rule={Rule} catalogVersion={CatalogVersion}",
            descriptor.Id,
            descriptor.ConfigKey,
            proposal.From,
            proposal.To,
            proposal.Rule,
            proposal.CatalogVersion);
        return new("applied", applied);
    }

    public async Task<TaskInfo> ApplyAtAdmissionAsync(
        TaskInfo task,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_state.AutoModelMigrationsEnabled
            || task.ModelExplicit
            || string.IsNullOrWhiteSpace(task.Model))
        {
            return task;
        }

        var proposal = GetProposal(task.Model);
        if (proposal is not { SafeAutoCandidate: true, TargetAvailable: true })
            return task;
        if (!ModelMetadataRegistry.IsCompatibleWithCli(task.CliType, proposal.To))
        {
            _logger.LogWarning(
                "model-migration-skipped-cli-mismatch task={Task} cli={Cli} from={From} to={To}",
                task.TaskKey,
                task.CliType,
                proposal.From,
                proposal.To);
            return task;
        }
        if (!_hasDatedPrice(proposal.To))
        {
            _logger.LogWarning(
                "model-migration-skipped-price-unknown task={Task} from={From} to={To} catalogVersion={CatalogVersion}",
                task.TaskKey,
                proposal.From,
                proposal.To,
                proposal.CatalogVersion);
            return task;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["from"] = proposal.From,
            ["to"] = proposal.To,
            ["rule"] = proposal.Rule,
            ["catalogVersion"] = proposal.CatalogVersion,
        };
        var auditAttempted = false;
        var auditWritten = false;
        var migrated = _mutations.MigrateNonExplicitModel(
            task.Id,
            task.WatchPath,
            proposal.From,
            proposal.To,
            writeAudit: candidate =>
            {
                auditAttempted = true;
                return auditWritten = _timeline.Append(
                    candidate.FolderPath,
                    TimelineEventKinds.ModelMigrated,
                    TimelineActors.Orchestrator,
                    $"Model migrated from {proposal.From} to {proposal.To}.",
                    details: details);
            });
        if (migrated is null)
        {
            if (auditAttempted && !auditWritten)
            {
                _logger.LogWarning(
                    "model-migration-audit-write-failed-and-rolled-back task={Task} from={From} to={To}",
                    task.TaskKey,
                    proposal.From,
                    proposal.To);
            }
            return task;
        }

        if (_bus is not null)
        {
            await _bus.EmitModelMigratedAsync(
                migrated,
                proposal.From,
                proposal.To,
                proposal.Rule,
                proposal.CatalogVersion,
                ct).ConfigureAwait(false);
        }
        _logger.LogInformation(
            "model-migrated task={Task} from={From} to={To} rule={Rule} catalogVersion={CatalogVersion}",
            migrated.TaskKey,
            proposal.From,
            proposal.To,
            proposal.Rule,
            proposal.CatalogVersion);
        return migrated with { ModelMigration = null };
    }

    private ModelConfigurationPinView? ProjectConfigurationPin(
        ModelConfigurationPinDescriptor descriptor,
        bool includeWithoutProposal = false)
    {
        var current = _configuration[descriptor.ConfigKey]?.Trim();
        if (string.IsNullOrWhiteSpace(current)) return null;
        var proposal = GetProposal(current);
        if (!includeWithoutProposal && proposal is null) return null;
        return new ModelConfigurationPinView
        {
            Id = descriptor.Id,
            Label = descriptor.Label,
            ConfigKey = descriptor.ConfigKey,
            CurrentModel = current,
            ModelMigration = proposal,
        };
    }

    private sealed record ModelConfigurationPinDescriptor(
        string Id,
        string Label,
        string ConfigKey);
}
