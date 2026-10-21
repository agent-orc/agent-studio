using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Task execution mode (coding|planning|research|concept)
/// kind. Foundation slice: the field round-trips through task.json + the scanner
/// and the web-access default is mode-derived. Pipeline behaviour (read-only
/// git-step skip) + the create-modal + promote flow are separate slices.
/// </summary>
public class TaskModeTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "test-project";

    public TaskModeTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "task-mode-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TaskModes_Normalize_DefaultsToCoding_AndDetectsReadOnly()
    {
        Assert.Equal(TaskModes.Coding, TaskModes.Normalize(null));
        Assert.Equal(TaskModes.Coding, TaskModes.Normalize(""));
        Assert.Equal(TaskModes.Coding, TaskModes.Normalize("nonsense"));
        Assert.Equal(TaskModes.Planning, TaskModes.Normalize("PLANNING"));
        Assert.Equal(TaskModes.Research, TaskModes.Normalize("research"));
        Assert.Equal(TaskModes.Concept, TaskModes.Normalize("CONCEPT"));
        Assert.False(TaskModes.IsReadOnly("coding"));
        Assert.True(TaskModes.IsReadOnly("planning"));
        Assert.True(TaskModes.IsReadOnly("research"));
        Assert.True(TaskModes.IsReadOnly("concept"));
        Assert.False(TaskModes.IsReportOnly("concept"));
        Assert.True(TaskModes.IsConcept("concept"));
    }

    [Fact]
    public void CreateJob_DefaultModeIsCoding_NoWebAccess()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest { Id = "plain", Title = "Plain", WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("plain", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskModes.Coding, info!.Mode);
        Assert.False(info.AllowWebAccess);
    }

    [Fact]
    public void CreateJob_PlanningMode_IsPersistedAndScanned_WebOffByDefault()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest { Id = "plan-1", Title = "Plan", Mode = TaskModes.Planning, WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("plan-1", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskModes.Planning, info!.Mode);
        Assert.False(info.AllowWebAccess); // planning defaults web off
    }

    [Fact]
    public void CreateJob_ResearchMode_DefaultsWebAccessOn()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest { Id = "res-1", Title = "Res", Mode = TaskModes.Research, WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("res-1", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskModes.Research, info!.Mode);
        Assert.True(info.AllowWebAccess); // research defaults web on
    }

    [Fact]
    public void CreateJob_ConceptMode_IsPersistedAndDefaultsWebAccessOff()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "concept-1",
            Title = "Concept",
            Mode = TaskModes.Concept,
            WatchPath = _watchPath,
            TargetState = TaskStates.Ready,
        });

        var info = scanner.FindJob("concept-1", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskModes.Concept, info!.Mode);
        Assert.False(info.AllowWebAccess);
    }

    [Fact]
    public void CreateJob_WebAccessOverride_WinsOverModeDefault()
    {
        var (_, scanner, mutations) = Build();
        // research, but the caller explicitly turns web access off
        mutations.CreateJob(new CreateTaskRequest { Id = "res-2", Title = "Res2", Mode = TaskModes.Research, AllowWebAccess = false, WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("res-2", _watchPath);
        Assert.NotNull(info);
        Assert.False(info!.AllowWebAccess);
    }

    [Fact]
    public void ExplicitTaskKind_DefaultsToCodingMode()
    {
        var (_, scanner, mutations) = Build();
        // an explicit task kind with no mode defaults to coding
        mutations.CreateJob(new CreateTaskRequest { Id = "explicit-task", Title = "Task", Kind = TaskKinds.Task, WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("explicit-task", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskKinds.Task, info!.Kind);
        Assert.Equal(TaskModes.Coding, info.Mode);
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
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
