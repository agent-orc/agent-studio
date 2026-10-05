using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the runner-side half of the human-decision-needed routing fix: a
/// <c>human-decision-needed-*</c> card is a marker for a human call, never a
/// unit of agent work. If it comes to rest in <c>2-ready</c> the runner must
/// (a) never auto-pick it (<see cref="ProjectRunner.GetNextReadyJob"/>) and
/// (b) relocate it to <c>5e-escalated</c> (through the HumanReviewEscalation
/// funnel, which routes human-decision escalations to the Escalated lane;
/// the 1b-needs-human-review lane was retired) before the pickup selection
/// runs. Without this guard the card is picked, the agent NOOPs, exit=1 fires
/// within a few seconds, and the cross-slug infra circuit breaker demotes the
/// project to manual.
/// </summary>
public sealed class ProjectRunnerHumanDecisionRoutingTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public ProjectRunnerHumanDecisionRoutingTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-hdn-routing-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void GetNextReadyJob_SkipsHumanDecisionNeededCard()
    {
        WriteJob(TaskStates.Ready, "human-decision-needed-bug-card-delete-button", order: 1);
        WriteJob(TaskStates.Ready, "real-work", order: 2);

        var runner = BuildRunner();
        var next = runner.GetNextReadyJob();

        Assert.NotNull(next);
        Assert.Equal("real-work", next!.Id);
    }

    [Fact]
    public void GetNextReadyJob_ReturnsNull_WhenOnlyHumanDecisionNeededCardsAreReady()
    {
        WriteJob(TaskStates.Ready, "human-decision-needed-a", order: 1);
        WriteJob(TaskStates.Ready, "Human-Decision-Needed-B", order: 2); // case-insensitive

        var runner = BuildRunner();

        Assert.Null(runner.GetNextReadyJob());
    }

    [Fact]
    public void RelocateStrayHumanDecisionCards_MovesReadyCardToEscalated()
    {
        WriteJob(TaskStates.Ready, "human-decision-needed-bug-card-delete-button", order: 1);
        WriteJob(TaskStates.Ready, "real-work", order: 2);

        var runner = BuildRunner();
        InvokeRelocateSweep(runner);

        var hdnInReady = Path.Combine(_watchPath, TaskStates.Ready, "human-decision-needed-bug-card-delete-button");
        var hdnInEscalated = Path.Combine(_watchPath, TaskStates.Escalated, "human-decision-needed-bug-card-delete-button");
        var realInReady = Path.Combine(_watchPath, TaskStates.Ready, "real-work");

        Assert.False(Directory.Exists(hdnInReady), "human-decision-needed card should have left 2-ready");
        Assert.True(Directory.Exists(hdnInEscalated), "human-decision-needed card should have landed in 5e-escalated");
        Assert.True(Directory.Exists(realInReady), "the real-work card must stay in 2-ready untouched");
    }

    [Fact]
    public void RelocateStrayHumanDecisionCards_NoStrayCards_IsNoOp()
    {
        WriteJob(TaskStates.Ready, "real-work", order: 1);

        var runner = BuildRunner();
        InvokeRelocateSweep(runner);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "real-work")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.Escalated)));
    }

    // ===== Helpers =====

    private static void InvokeRelocateSweep(ProjectRunner runner)
    {
        var method = typeof(ProjectRunner).GetMethod(
            "RelocateStrayHumanDecisionCards",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(runner, null);
    }

    private void WriteJob(string state, string slug, int order)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order}," +
            "\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"}");
    }

    private ProjectRunner BuildRunner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(scanner, clients, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var codex = GenericCliExecutionService.ForCodex(
            NullLogger<GenericCliExecutionService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, gemini);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null,
            localCodingAdmission: AllowLocalCodingForTests.Instance);
    }
}
