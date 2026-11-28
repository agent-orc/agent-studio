using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Acceptance tests for the agent-defaults materialization feature:
///   1. CreateJob materializes owner-client defaults (cliType, model, agent)
///   2. agent:"human" is preserved (deliberate manual-task marker)
///   3. BackfillAgentDefaults migrates legacy triple idempotently
///   4. Auto-pickup skips agent:"human" jobs
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public class AgentDefaultsMaterializationTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "test-project";

    public AgentDefaultsMaterializationTests()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-agent-defaults-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        ModelMetadataRegistry.SetDetectedCodexDefault(null);
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void CreateJob_MaterializesOwnerDefaults_WhenCliTypeAndModelOmitted()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-1",
            Title = "Test",
            WatchPath = _watchPath,
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-1", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("claude", info!.Agent);
        Assert.Equal("claude", info.CliType);
        Assert.Equal("claude-opus-4-7", info.Model);
        Assert.Equal("high", info.ThinkingLevel);
    }

    [Fact]
    public void CreateJob_MaterializesOwnerThinkingLevel_WhenValid()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7", defaultThinkingLevel: "xhigh");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-thinking",
            Title = "Test thinking",
            WatchPath = _watchPath,
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-thinking", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("xhigh", info!.ThinkingLevel);
    }

    [Fact]
    public void CreateJob_NormalizesForeignThinkingLevel_ToModelDefault()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-sonnet-4-6", defaultThinkingLevel: "max");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-sonnet",
            Title = "Test sonnet",
            WatchPath = _watchPath,
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-sonnet", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("high", info!.ThinkingLevel);
    }

    [Fact]
    public void CreateJob_PreservesExplicitCliTypeAndModel()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-2",
            Title = "Test",
            WatchPath = _watchPath,
            CliType = "codex",
            Model = "o3",
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-2", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("codex", info!.Agent);
        Assert.Equal("codex", info.CliType);
        Assert.Equal("o3", info.Model);
    }

    [Fact]
    public void CreateJob_ExplicitCliTypeWithoutModel_UsesThatCliDefault()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-8");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-codex-default",
            Title = "Codex default",
            WatchPath = _watchPath,
            Agent = "codex",
            CliType = "codex",
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-codex-default", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("codex", info!.Agent);
        Assert.Equal("codex", info.CliType);
        Assert.Equal(ModelIds.Gpt62Sol, info.Model);
        Assert.Equal("medium", info.ThinkingLevel);
    }

    [Fact]
    public void CreateJob_Codex_KeepsPolicyDefault_WhenLocalCliReportsAnOlderModel()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-8");
        ModelMetadataRegistry.SetDetectedCodexDefault(ModelIds.Gpt56Sol);

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-codex-56",
            Title = "Codex gpt-5.6",
            WatchPath = _watchPath,
            Agent = "codex",
            CliType = "codex",
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-codex-56", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("codex", info!.CliType);
        Assert.Equal(ModelIds.Gpt62Sol, info.Model);
        Assert.Equal("medium", info.ThinkingLevel);
    }

    [Fact]
    public void CreateJob_ExplicitCliTypeWithForeignModel_RemapsToCliDefault()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-8");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "task-codex-foreign",
            Title = "Codex foreign",
            WatchPath = _watchPath,
            CliType = "codex",
            Model = "claude-opus-4-8",
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("task-codex-foreign", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("codex", info!.Agent);
        Assert.Equal("codex", info.CliType);
        Assert.Equal(ModelIds.Gpt62Sol, info.Model);
    }

    [Fact]
    public void CreateJob_PreservesAgentHuman_AsDeliberateManualTask()
    {
        var (_, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7");

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "manual-task",
            Title = "Manual",
            WatchPath = _watchPath,
            Agent = "human",
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("manual-task", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("human", info!.Agent);
    }

    [Fact]
    public void CreateJob_UsesStandardRoute_WhenOwnerHasNoDefaults()
    {
        var (_, scanner, mutations) = Build(defaultCliType: null, defaultModel: null);

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "no-defaults",
            Title = "No defaults",
            WatchPath = _watchPath,
            TargetState = TaskStates.Ready
        });

        var info = scanner.FindJob("no-defaults", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("codex", info!.Agent);
        Assert.Equal("codex", info.CliType);
        Assert.Equal(ModelIds.Gpt62Sol, info.Model);
        Assert.Equal("medium", info.ThinkingLevel);
    }

    [Fact]
    public void BackfillAgentDefaults_MigratesLegacyTriple()
    {
        var (machine, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7");
        machine.EnsureStateFoldersAndMigrate();

        var jobDir = Path.Combine(_watchPath, TaskStates.Ready, "legacy-job");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "task.json"), """
            {
              "id": "legacy-job",
              "title": "Legacy",
              "state": "2-ready",
              "order": 10,
              "agent": "human",
              "ownerClientId": "local-default"
            }
            """);

        var count = mutations.BackfillAgentDefaults();

        Assert.Equal(1, count);
        var info = scanner.FindJob("legacy-job", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("claude", info!.Agent);
        Assert.Equal("claude", info.CliType);
        Assert.Equal("claude-opus-4-7", info.Model);
        Assert.Equal("high", info.ThinkingLevel);
    }

    [Fact]
    public void BackfillAgentDefaults_IsIdempotent()
    {
        var (machine, scanner, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7");
        machine.EnsureStateFoldersAndMigrate();

        var jobDir = Path.Combine(_watchPath, TaskStates.Ready, "idem-job");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "task.json"), """
            {
              "id": "idem-job",
              "title": "Idempotent",
              "state": "2-ready",
              "order": 10,
              "agent": "human",
              "ownerClientId": "local-default"
            }
            """);

        var first = mutations.BackfillAgentDefaults();
        Assert.Equal(1, first);

        var second = mutations.BackfillAgentDefaults();
        Assert.Equal(0, second);
    }

    [Fact]
    public void BackfillAgentDefaults_SkipsJobsWithExplicitCliType()
    {
        var (machine, _, mutations) = Build(defaultCliType: "claude", defaultModel: "claude-opus-4-7");
        machine.EnsureStateFoldersAndMigrate();

        var jobDir = Path.Combine(_watchPath, TaskStates.Ready, "explicit-job");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "task.json"), """
            {
              "id": "explicit-job",
              "title": "Explicit",
              "state": "2-ready",
              "order": 10,
              "agent": "human",
              "cliType": "codex",
              "ownerClientId": "local-default"
            }
            """);

        var count = mutations.BackfillAgentDefaults();
        Assert.Equal(0, count);
    }

    [Fact]
    public void AgentTypes_IsAutoPickupEligible_ReturnsFalseForHuman()
    {
        Assert.False(AgentTypes.IsAutoPickupEligible("human"));
        Assert.False(AgentTypes.IsAutoPickupEligible("Human"));
        Assert.False(AgentTypes.IsAutoPickupEligible("HUMAN"));
        Assert.True(AgentTypes.IsAutoPickupEligible("claude"));
        Assert.True(AgentTypes.IsAutoPickupEligible("codex"));
        Assert.True(AgentTypes.IsAutoPickupEligible("gemini"));
        Assert.True(AgentTypes.IsAutoPickupEligible(null));
        Assert.True(AgentTypes.IsAutoPickupEligible(""));
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build(
        string? defaultCliType, string? defaultModel, string? defaultThinkingLevel = null)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();

        if (defaultCliType != null || defaultModel != null)
            clients.SetDefaults(DefaultClientIdentity.Id, defaultCliType, defaultModel, defaultThinkingLevel: defaultThinkingLevel);

        var mutations = new TaskMutationService(scanner, clients, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        machine.EnsureStateFoldersAndMigrate();
        return (machine, scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
