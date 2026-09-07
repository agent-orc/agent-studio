using System.Text.Json;
using AgentStudio.ModelMigrations;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelMigrationServiceTests : IDisposable
{
    private const string Project = "demo";
    private const string CatalogVersion = "2026-09-06";

    private readonly string _root;
    private readonly string _watchPath;
    private readonly IConfiguration _configuration;
    private readonly WorkspaceRecord _workspace;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ProjectSettingsService _projectSettings;
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceRegistry _workspaces;
    private readonly WorkspaceSettingsService _workspaceSettings;
    private readonly TimelineLog _timeline;
    private readonly OrchestratorLog _operatorFeed;

    public ModelMigrationServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "model-migration-service-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "projects", Project);
        Directory.CreateDirectory(_watchPath);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();

        _workspaces = new WorkspaceRegistry(
            _configuration,
            NullLogger<WorkspaceRegistry>.Instance);
        _workspace = _workspaces.EnsureDefaultWorkspace();
        _projects = new ProjectRegistry(
            _configuration,
            NullLogger<ProjectRegistry>.Instance);
        _projects.EnsureProjectForStorage(_watchPath, Project, _workspace.Id);

        var summaries = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            _configuration);
        _scanner = new TaskScannerService(
            _configuration,
            NullLogger<TaskScannerService>.Instance,
            summaries,
            projectRegistry: _projects);
        new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance)
            .EnsureStateFoldersAndMigrate();
        _mutations = new TaskMutationService(
            _scanner,
            new ClientIdentityStore(_configuration, NullLogger<ClientIdentityStore>.Instance),
            _projects,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        _projectSettings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            _configuration);
        _workspaceSettings = new WorkspaceSettingsService(
            NullLogger<WorkspaceSettingsService>.Instance,
            _configuration);
        _timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        _operatorFeed = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for filesystem-backed tests.
        }
    }

    [Fact]
    public async Task GetStatusAsync_SupersededExplicitTask_EmitsProposal()
    {
        var task = CreateTask("explicit-proposal", modelExplicit: true);
        var service = BuildService(Rule(safeAuto: true));

        var status = await service.GetStatusAsync(taskId: task.Id);

        Assert.Equal(CatalogVersion, status.CatalogVersion);
        Assert.Equal("Token Economy project", status.CatalogSource);
        Assert.DoesNotContain("stub-token-economy", status.CatalogSource, StringComparison.Ordinal);
        var proposal = Assert.Single(status.Proposals);
        Assert.Equal(ModelMigrationScopes.Task, proposal.Scope);
        Assert.Equal(task.Id, proposal.TaskId);
        Assert.True(proposal.Explicit);
        Assert.Equal(ModelIds.ClaudeOpus48, proposal.FromModel);
        Assert.Equal(ModelIds.ClaudeOpus5, proposal.ToModel);
        Assert.Equal("claude-opus/latest-in-family", proposal.Rule);
        Assert.Equal("premium", proposal.CostClassFrom);
        Assert.Equal("premium", proposal.CostClassTo);
        Assert.NotEmpty(proposal.ReasoningLadderFrom!);
        Assert.NotEmpty(proposal.ReasoningLadderTo!);
    }

    [Fact]
    public async Task ApplySafeAtAdmissionAsync_NonExplicitSafeMigration_PersistsAndAudits()
    {
        var task = CreateTask("safe-auto", modelExplicit: false);
        var service = BuildService(Rule(safeAuto: true));

        var migrated = await service.ApplySafeAtAdmissionAsync(task);

        Assert.Equal(ModelIds.ClaudeOpus5, migrated.Model);
        Assert.False(migrated.ModelExplicit);

        var persisted = _scanner.FindJob(task.Id, _watchPath);
        Assert.NotNull(persisted);
        Assert.Equal(ModelIds.ClaudeOpus5, persisted.Model);
        Assert.False(persisted.ModelExplicit);
        using (var document = JsonDocument.Parse(File.ReadAllText(
                   Path.Combine(task.FolderPath, "task.json"))))
        {
            Assert.False(document.RootElement.GetProperty("modelExplicit").GetBoolean());
        }

        var timelineEvent = Assert.Single(
            _timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ModelMigrated);
        Assert.Equal("system", timelineEvent.Actor);
        Assert.Equal(ModelIds.ClaudeOpus48, timelineEvent.Details!["from"]);
        Assert.Equal(ModelIds.ClaudeOpus5, timelineEvent.Details["to"]);
        Assert.Equal("claude-opus/latest-in-family", timelineEvent.Details["rule"]);
        Assert.Equal(CatalogVersion, timelineEvent.Details["catalogVersion"]);
        Assert.Equal("safeAuto", timelineEvent.Details["application"]);

        var feedEntry = Assert.Single(_operatorFeed.Read(_watchPath));
        Assert.Equal(OrchestratorLogKinds.Action, feedEntry.Kind);
        Assert.Equal(OrchestratorLogTopics.ModelMigration, feedEntry.Topic);
        Assert.Equal(task.Id, feedEntry.JobId);
        Assert.Contains(ModelIds.ClaudeOpus48, feedEntry.Summary, StringComparison.Ordinal);
        Assert.Contains(ModelIds.ClaudeOpus5, feedEntry.Summary, StringComparison.Ordinal);
        Assert.Contains(CatalogVersion, feedEntry.Reasoning, StringComparison.Ordinal);
        Assert.Contains("safeAuto", feedEntry.Reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplySafeAtAdmissionAsync_ExplicitTask_IsUnchanged()
    {
        var task = CreateTask("explicit-unchanged", modelExplicit: true);
        var service = BuildService(Rule(safeAuto: true));

        var result = await service.ApplySafeAtAdmissionAsync(task);

        Assert.Equal(ModelIds.ClaudeOpus48, result.Model);
        Assert.True(result.ModelExplicit);
        var persisted = _scanner.FindJob(task.Id, _watchPath);
        Assert.Equal(ModelIds.ClaudeOpus48, persisted!.Model);
        Assert.True(persisted.ModelExplicit);
        Assert.Empty(_timeline.ReadAll(task.FolderPath));
        Assert.Empty(_operatorFeed.Read(_watchPath));
    }

    [Fact]
    public async Task ApplySafeAtAdmissionAsync_WorkspaceAutoApplyDisabled_IsUnchanged()
    {
        var task = CreateTask("workspace-disabled", modelExplicit: false);
        _workspaceSettings.SetAutoApplySafeModelMigrations(_workspace.Id, enabled: false);
        var service = BuildService(Rule(safeAuto: true));

        var result = await service.ApplySafeAtAdmissionAsync(task);

        Assert.Equal(ModelIds.ClaudeOpus48, result.Model);
        Assert.False(result.ModelExplicit);
        Assert.Equal(ModelIds.ClaudeOpus48, _scanner.FindJob(task.Id, _watchPath)!.Model);
        Assert.Empty(_timeline.ReadAll(task.FolderPath));
        Assert.Empty(_operatorFeed.Read(_watchPath));
    }

    [Fact]
    public async Task GetStatusAsync_ProposalOnlyCrossFamilyRule_UsesTargetFamilyAvailability()
    {
        var task = CreateTask("cross-family-proposal", modelExplicit: true);
        var service = BuildService(Rule(
            to: ModelIds.ClaudeSonnet5,
            safeAuto: false));

        var status = await service.GetStatusAsync(taskId: task.Id);

        var proposal = Assert.Single(status.Proposals);
        Assert.Equal(ModelIds.ClaudeSonnet5, proposal.ToModel);
        Assert.Equal(
            $"token-economy/{ModelIds.ClaudeOpus48}-to-{ModelIds.ClaudeSonnet5}",
            proposal.Rule);
    }

    [Fact]
    public async Task GetStatusAsync_CrossProviderProposal_UsesEachModelsReasoningLadder()
    {
        var task = CreateTask("cross-provider-proposal", modelExplicit: true);
        var service = BuildService(Rule(
            to: ModelIds.Gpt55,
            safeAuto: false));

        var status = await service.GetStatusAsync(taskId: task.Id);

        var proposal = Assert.Single(status.Proposals);
        Assert.Equal(
            ModelMetadataRegistry.ThinkingLevelsFor(CliTypes.Claude, ModelIds.ClaudeOpus48),
            proposal.ReasoningLadderFrom);
        Assert.Equal(
            ModelMetadataRegistry.ThinkingLevelsFor(CliTypes.Codex, ModelIds.Gpt55),
            proposal.ReasoningLadderTo);
    }

    [Fact]
    public async Task ApplySafeAtAdmissionAsync_CrossFamilySafeAutoData_FailsClosed()
    {
        var task = CreateTask("cross-family-auto", modelExplicit: false);
        var service = BuildService(Rule(
            to: ModelIds.ClaudeSonnet5,
            safeAuto: true));

        var result = await service.ApplySafeAtAdmissionAsync(task);

        Assert.Equal(ModelIds.ClaudeOpus48, result.Model);
        Assert.False(result.ModelExplicit);
        Assert.Empty(_timeline.ReadAll(task.FolderPath));
        Assert.Empty(_operatorFeed.Read(_watchPath));
    }

    [Fact]
    public async Task UnavailableTargetInFreshCatalog_IsNeitherProposedNorApplied()
    {
        var task = CreateTask("target-unavailable", modelExplicit: false);
        var service = BuildServiceWithAvailableModels(
            [ModelIds.ClaudeSonnet5],
            Rule(safeAuto: true));

        var status = await service.GetStatusAsync(taskId: task.Id);
        var result = await service.ApplySafeAtAdmissionAsync(task);

        Assert.Empty(status.Proposals);
        Assert.Equal(ModelIds.ClaudeOpus48, result.Model);
        Assert.False(result.ModelExplicit);
        Assert.Equal(ModelIds.ClaudeOpus48, _scanner.FindJob(task.Id, _watchPath)!.Model);
        Assert.Empty(_timeline.ReadAll(task.FolderPath));
        Assert.Empty(_operatorFeed.Read(_watchPath));
    }

    [Fact]
    public async Task ApplyAsync_CrossProviderTaskTarget_IsRejectedWithoutFalseAudit()
    {
        var task = CreateTask("cross-provider-apply", modelExplicit: true);
        var service = BuildService(Rule(to: ModelIds.Gpt55, safeAuto: false));

        await Assert.ThrowsAsync<ModelMigrationConflictException>(() => service.ApplyAsync(
            new ApplyModelMigrationRequest
            {
                Scope = ModelMigrationScopes.Task,
                TaskId = task.Id,
                ExpectedFromModel = ModelIds.ClaudeOpus48,
                CatalogVersion = CatalogVersion,
            }));

        var persisted = _scanner.FindJob(task.Id, _watchPath);
        Assert.Equal(ModelIds.ClaudeOpus48, persisted!.Model);
        Assert.True(persisted.ModelExplicit);
        Assert.Empty(_timeline.ReadAll(task.FolderPath));
        Assert.Empty(_operatorFeed.Read(_watchPath));
    }

    [Fact]
    public async Task GetStatusAsync_PlanningOverrideDoesNotHideLegacyTaskProposalWithSameStepId()
    {
        const string stepId = "shared-model-step";
        _projectSettings.SetPipelineStep(
            Project,
            stepId,
            new PipelineStepSetting { Model = ModelIds.ClaudeOpus48 });
        _projectSettings.SetPipelineStep(
            Project,
            PipelineTypes.Planning,
            stepId,
            new PipelineStepSetting { Model = ModelIds.ClaudeOpus5 });
        var service = BuildService(Rule(safeAuto: true));

        var status = await service.GetStatusAsync(project: Project);

        var proposal = Assert.Single(status.Proposals);
        Assert.Equal(ModelMigrationScopes.PipelineStep, proposal.Scope);
        Assert.Equal(PipelineTypes.Task, proposal.PipelineType);
        Assert.Equal(stepId, proposal.StepId);
        Assert.Equal(ModelIds.ClaudeOpus48, proposal.FromModel);
    }

    private TaskInfo CreateTask(string id, bool modelExplicit)
    {
        Assert.NotNull(_mutations.CreateJob(new CreateTaskRequest
        {
            Id = id,
            Title = id,
            WatchPath = _watchPath,
            Agent = CliTypes.Claude,
            CliType = CliTypes.Claude,
            Model = ModelIds.ClaudeOpus48,
            ModelExplicit = modelExplicit,
            ThinkingLevelExplicit = false,
            TargetState = TaskStates.Ready,
        }));
        return _scanner.FindJob(id, _watchPath)
               ?? throw new InvalidOperationException($"Test task '{id}' was not created.");
    }

    private ModelMigrationService BuildService(params ModelMigrationRule[] rules)
        => BuildServiceWithAvailableModels(
            [ModelIds.ClaudeOpus5, ModelIds.ClaudeSonnet5, ModelIds.Gpt55],
            rules);

    private ModelMigrationService BuildServiceWithAvailableModels(
        IReadOnlyList<string> availableModelIds,
        params ModelMigrationRule[] rules)
    {
        var snapshot = new ModelMigrationCatalogSnapshot(
            new ModelMigrationCatalog
            {
                Schema = "model-migrations.v1.schema.json",
                SchemaVersion = 1,
                CatalogVersion = CatalogVersion,
                EvidenceAsOfDate = "2026-09-06",
                DefaultStrategy = "latestInFamily",
                CostClassOrder = ["economy", "standard", "premium"],
                Migrations = rules,
            },
            Source: "stub-token-economy",
            Error: null,
            CapturedAtUtc: DateTime.UtcNow,
            IsStale: false);
        var catalogs = new StubCatalogProvider(snapshot);
        var resolver = new ModelFamilyResolver(
            (_, _) => Task.FromResult(CurrentModelCatalog(availableModelIds)),
            _configuration,
            NullLogger<ModelFamilyResolver>.Instance);
        var configurationPins = new ModelConfigurationPinStore(
            _configuration,
            new TestHostEnvironment(_root),
            NullLogger<ModelConfigurationPinStore>.Instance);
        return new ModelMigrationService(
            catalogs,
            resolver,
            _scanner,
            _mutations,
            _projectSettings,
            _projects,
            _workspaces,
            _workspaceSettings,
            configurationPins,
            _timeline,
            _operatorFeed,
            NullLogger<ModelMigrationService>.Instance);
    }

    private static CliModelCatalog CurrentModelCatalog(IReadOnlyList<string> availableModelIds) => new()
    {
        Models = availableModelIds.Select(AvailableModel).ToList(),
        Source = "test-current-catalog",
        FetchedAt = DateTime.UtcNow,
    };

    private static CliModelInfo AvailableModel(string id) => new()
    {
        Id = id,
        Label = id,
        Available = true,
        Deprecated = false,
    };

    private static ModelMigrationRule Rule(
        string to = ModelIds.ClaudeOpus5,
        bool safeAuto = true) => new()
    {
        From = ModelIds.ClaudeOpus48,
        To = to,
        Family = "claude-opus",
        Vendor = "anthropic",
        GenerationOrder = new ModelMigrationGenerationOrder { From = 408, To = 500 },
        CostClassFrom = "premium",
        CostClassTo = "premium",
        LadderCompatible = true,
        ContextChange = "same",
        SafeAuto = safeAuto,
        Since = "2026-09-06",
        Note = "Test migration",
    };

    private sealed class StubCatalogProvider(ModelMigrationCatalogSnapshot snapshot)
        : IModelMigrationCatalogProvider
    {
        public ModelMigrationCatalogSnapshot GetCatalog(bool refresh = false) => snapshot;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
            ContentRootFileProvider = new PhysicalFileProvider(contentRoot);
        }

        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "OrchestratorApi.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
