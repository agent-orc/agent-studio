using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SupervisorEngineModeTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "supervisor-engine-mode-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(
        new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task Orchestrator_prep_processes_a_preparation_card_with_operator_profile_on_engine()
    {
        var config = Configuration();
        var scanner = Scanner(config);
        var settings = Settings(config);
        settings.SetPipelineStep(Project, PipelineCatalogue.PreOrchestratorPrepStepId,
            new PipelineStepSetting { Enabled = true });
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var defaults = new OrchestratorDefaultsProvider(settings,
            new WorkspaceSettingsService(NullLogger<WorkspaceSettingsService>.Instance, config), registry);
        var folder = Path.Combine(_workspace, "projects", Project, TaskStates.Preparation, "prep-card");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            "{\"id\":\"prep-card\",\"title\":\"Prepare task\",\"state\":\"1-preparation\",\"order\":1}");
        File.WriteAllText(Path.Combine(folder, "prompt.md"),
            "Implement a cache refresh after a lane transition. Verify the next read returns the moved card.");
        var service = new OrchestratorPrepHostedService(scanner,
            new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance), settings, defaults,
            new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance),
            new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance),
            config, NullLogger<OrchestratorPrepHostedService>.Instance);

        await service.TickOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_workspace, "projects", Project, TaskStates.Ready, "prep-card"))
            || File.Exists(Path.Combine(folder, "orchestrator-prep.json")));
    }

    [Fact]
    public async Task Meta_cycle_writes_a_report_with_operator_profile_on_engine()
    {
        var config = Configuration();
        var scanner = Scanner(config);
        var settings = Settings(config);
        var interventions = Interventions(config);
        var service = new MetaCycleHostedService(null!, scanner, interventions, settings,
            new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance), config,
            null!, NullLogger<MetaCycleHostedService>.Instance, _clock);
        service.StatusProviderOverride = Status;
        var cycleConfig = MetaCycleConfig.Defaults() with { Enabled = true, CycleLengthN = 1 };

        Assert.Null(await service.EvaluateProjectAsync(_workspace, Project,
            Status().Projects[Project], [], cycleConfig, CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var job = new TaskInfo
        {
            Id = "closed-card", ProjectName = Project,
            State = TaskStates.Completed,
            FolderPath = Path.Combine(_workspace, "projects", Project, TaskStates.Completed, "closed-card"),
            WatchPath = Path.Combine(_workspace, "projects", Project),
            LastActivity = _clock.GetUtcNow().UtcDateTime,
        };

        var report = await service.EvaluateProjectAsync(_workspace, Project,
            Status().Projects[Project], [job], cycleConfig, CancellationToken.None);

        Assert.NotNull(report);
        Assert.True(File.Exists(SupervisorLogPaths.MetaCycleReportFile(_workspace, Project, report.CycleId)));
    }

    [Fact]
    public async Task Auto_intervention_dispatches_an_advisory_with_operator_profile_on_engine()
    {
        var config = Configuration();
        var store = new SupervisorAdvisoryStore();
        await store.AppendAsync(_workspace, Project, new SupervisorAdvisory(
            _clock.GetUtcNow().UtcDateTime, Project, SupervisorSeverity.High,
            SupervisorSource.HardCheck, "quota-critical", "capacity exhausted"));
        var service = new AutoInterventionHostedService(null!, Interventions(config), null!, store,
            config, NullLogger<AutoInterventionHostedService>.Instance);
        service.StatusProviderOverride = Status;

        await service.TickOnceAsync(_workspace, CancellationToken.None);

        var log = File.ReadAllText(SupervisorLogPaths.InterventionsFile(_workspace, Project));
        Assert.Contains("quota-critical", log);
        Assert.Contains("kind\":1", log);
    }

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = Path.Combine(_workspace, "projects", Project),
            ["WatchPaths:0:RootPath"] = Path.Combine(_workspace, "projects", Project),
            ["Orchestration:Transitions:OperatorDecision"] = "Engine",
            ["Supervisor:MetaCyclePauseQuiescenceTimeoutSeconds"] = "0",
            ["Supervisor:MetaCycleResumeVerifyMaxAttempts"] = "1",
        }).Build();

    private static RunnerStatus Status() => new()
    {
        Projects = new Dictionary<string, ProjectRunnerStatus>
        {
            [Project] = new() { ProjectName = Project, Mode = "auto-continuous" },
        },
    };

    private static TaskScannerService Scanner(IConfiguration config) => new(config,
        NullLogger<TaskScannerService>.Instance,
        new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));

    private static ProjectSettingsService Settings(IConfiguration config)
        => new(NullLogger<ProjectSettingsService>.Instance, config);

    private SupervisorInterventionService Interventions(IConfiguration config)
        => new(null!, null!, null!, config,
            NullLogger<SupervisorInterventionService>.Instance, _clock);
}
