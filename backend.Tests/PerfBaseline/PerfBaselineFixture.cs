using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Shared fixture that materializes a synthetic workspace under tempdir and
/// builds the full runtime graph the polled endpoints traverse. Mirrors the
/// builder in JobsEndpointPerfTests so the two stay calibrated against the
/// same service shape.
/// </summary>
internal sealed class PerfBaselineFixture : IDisposable
{
    public string WatchPath { get; }
    public string ProjectName { get; }
    public IConfiguration Config { get; }
    public TaskScannerService Scanner { get; }
    public TaskRunnerService Runners { get; }
    public CliRouter Router { get; }
    public SummaryGenerationService Summary { get; }
    public TaskStateMachine States { get; }
    public ProjectTokenUsageService TokenUsage { get; }

    public TaskIndexCache? IndexCache { get; }

    public PerfBaselineFixture(int jobCount, string scenarioTag = "perf", bool withCache = false)
    {
        ProjectName = $"perf-{scenarioTag}-{jobCount}";
        WatchPath = Path.Combine(Path.GetTempPath(), $"atp-{scenarioTag}-{Guid.NewGuid():N}");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(WatchPath, state));

        // Distribute jobs across realistic lanes: bulk in archive, a few
        // ready, a couple in progress, some completed.
        var laneMix = new[]
        {
            (TaskStates.Archive,  0.80),
            (TaskStates.Completed, 0.10),
            (TaskStates.Ready,     0.05),
            (TaskStates.AutoReview, 0.03),
            (TaskStates.HumanReview,0.015),
            (TaskStates.Progress,   0.005),
        };
        var written = 0;
        foreach (var (lane, share) in laneMix)
        {
            var count = (int)Math.Round(jobCount * share);
            for (var i = 0; i < count && written < jobCount; i++, written++)
            {
                WriteJob(lane, $"job-{written:D5}", $"Job {written}");
            }
        }
        // Pad out any remainder into archive.
        while (written < jobCount)
        {
            WriteJob(TaskStates.Archive, $"job-{written:D5}", $"Job {written}");
            written++;
        }

        Config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = WatchPath
            })
            .Build();

        Summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, Config);
        Scanner = new TaskScannerService(Config, NullLogger<TaskScannerService>.Instance, Summary);
        if (withCache)
        {
            IndexCache = new TaskIndexCache(Scanner, NullLogger<TaskIndexCache>.Instance, Config);
            Scanner.SetIndexCache(IndexCache);
        }
        States = new TaskStateMachine(Scanner, NullLogger<TaskStateMachine>.Instance);
        var sessions = new TaskSessionLog(Scanner, NullLogger<TaskSessionLog>.Instance);
        var mutations = new TaskMutationService(Scanner, new ClientIdentityStore(Config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(Config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);

        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, Config);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, Config);
        var codex = GenericCliExecutionService.ForCodex(NullLogger<GenericCliExecutionService>.Instance, Config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, Config);
        Router = new CliRouter(claude, codex, gemini);

        var contextUsageParser = new ContextUsageParser();
        var prompts = new RuntimePromptService(Config, NullLogger<RuntimePromptService>.Instance);
        var projectSettings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, Config);
        var git = new GitService(NullLogger<GitService>.Instance, Scanner, Config, prompts);
        var transitions = new TaskTransitionService(Scanner, States, mutations, git, projectSettings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var orchestratorRunner = new OrchestratorRunner(NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var globalStore = new GlobalOrchestratorSessionStore(Config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, Scanner, Config);

        var quotaCacheStore = new QuotaCacheStore(Config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), Config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, Config);
        var pickupFailures = new PickupFailureLog(Config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(Config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(Config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var indexCache = new TaskIndexCache(Scanner, NullLogger<TaskIndexCache>.Instance, Config);
        Scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            Scanner, mutations, States, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        Runners = new TaskRunnerService(
            Config, NullLogger<TaskRunnerService>.Instance, Scanner, States, mutations, sessions,
            Router, contextUsageParser, Summary, prompts, transitions, projectSettings,
            quotaService, quotaCaps,
            chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, globalBoot, git, pickupFailures, infraBreaker, taskAccess);

        TokenUsage = new ProjectTokenUsageService(orchestratorLog, Scanner);
    }

    private void WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(WatchPath, state, slug);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            id = slug,
            title,
            state,
            order = 1,
            agent = "claude",
            cliType = "claude"
        });
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    public void Dispose()
    {
        try { Directory.Delete(WatchPath, recursive: true); } catch { /* best-effort */ }
    }
}
