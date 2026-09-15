using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end over a temp workspace for <see cref="ContinueModeGuardPolicy"/>
/// wired into <see cref="TaskRunnerService.ContinueJobAsync"/> (AGT-2795): a
/// concept or planning card whose follow-up reads as a code-change request is
/// refused before anything is written, and an explicit mode override lets the
/// same continue proceed like any other.
/// </summary>
public sealed class ContinueModeGuardServiceTests : IDisposable
{
    private const string ProjectName = "demo";
    private const string ImplementationPrompt = "Fix the demo route count, change it from 81 to 83.";

    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public ContinueModeGuardServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-continue-mode-guard-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "test teardown is best-effort"); }
    }

    [Fact]
    public async Task ConceptCard_ImplementationPrompt_Rejected409_AndWritesNothing()
    {
        // The AGT-2795 reproduction: a card repurposed into a concept card
        // carries an implementation fix prompt into /continue.
        WriteJob("agt-2795", TaskStates.Progress, mode: TaskModes.Concept);
        var harness = Build();
        var promptBefore = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Progress, "agt-2795", "prompt.md"));

        var ex = await Assert.ThrowsAsync<TaskOperationException>(() =>
            harness.Service.ContinueJobAsync("agt-2795", ImplementationPrompt, _watchPath));

        Assert.Equal(409, ex.Status);
        Assert.Contains("'concept' mode", ex.Message);
        Assert.Contains("/api/tasks/agt-2795/promote-concept", ex.Message);

        // Boundary validation before mutation: the rejected continue must not
        // have appended a continuation note, written a pending intent, or
        // touched prompt.md.
        var jobDir = Path.Combine(_watchPath, TaskStates.Progress, "agt-2795");
        Assert.Equal(promptBefore, File.ReadAllText(Path.Combine(jobDir, "prompt.md")));
        Assert.False(File.Exists(Path.Combine(jobDir, "pending-intent.json")));
    }

    [Fact]
    public async Task PlanningCard_ImplementationPrompt_Rejected409_NamesPromoteToCoding()
    {
        WriteJob("plan-card", TaskStates.Progress, mode: TaskModes.Planning);
        var harness = Build();

        var ex = await Assert.ThrowsAsync<TaskOperationException>(() =>
            harness.Service.ContinueJobAsync("plan-card", "Implement the retry limit change.", _watchPath));

        Assert.Equal(409, ex.Status);
        Assert.Contains("'planning' mode", ex.Message);
        Assert.Contains("/api/tasks/plan-card/promote-to-coding", ex.Message);
    }

    [Fact]
    public async Task ConceptCard_ImplementationPrompt_WithMatchingModeOverride_ProceedsPastTheGuard()
    {
        WriteJob("agt-2795-override", TaskStates.Progress, mode: TaskModes.Concept);
        var harness = Build();

        // The lane is runnable and local, so a continue that clears the guard
        // reaches the normal admission path instead of throwing.
        var response = await harness.Service.ContinueJobAsync(
            "agt-2795-override", ImplementationPrompt, _watchPath, modeOverride: TaskModes.Concept);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task ConceptCard_ConceptualPrompt_NeverGuarded()
    {
        WriteJob("agt-2795-discuss", TaskStates.Progress, mode: TaskModes.Concept);
        var harness = Build();

        var response = await harness.Service.ContinueJobAsync(
            "agt-2795-discuss", "What do you think of Option B for the retention window?", _watchPath);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task CodingCard_ImplementationPrompt_NeverGuarded()
    {
        WriteJob("coding-card", TaskStates.Progress, mode: TaskModes.Coding);
        var harness = Build();

        var response = await harness.Service.ContinueJobAsync(
            "coding-card", ImplementationPrompt, _watchPath);

        Assert.NotNull(response);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed record Harness(TaskRunnerService Service, TaskScannerService Scanner);

    private Harness Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Runner:Id"] = "test-backend@test-host",
            ["Runner:FollowUpStartWindowMs"] = "0",
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var codex = GenericCliExecutionService.ForCodex(
            NullLogger<GenericCliExecutionService>.Instance, config,
            new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config),
            new CliUsageParserRegistry([new CodexUsageParser()]),
            new CliModelRegistry());
        var antigravity = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, antigravity);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var globalStore = new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, scanner, config);
        var quotaCache = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quota = new QuotaService(NullLogger<QuotaService>.Instance, [], config, quotaCache);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(
            config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance,
            new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance));
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
        var timeline = new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);

        // A persisted orchestrator session makes ProjectRunner's boot a no-op,
        // so activating the runner never reaches for a real CLI.
        orchestratorSessions.Write(_watchPath, new OrchestratorSession(
            SessionId: "test-session",
            Model: "test-model",
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: string.Empty,
            BootReplyPreview: string.Empty,
            CumulativeInputTokens: 0,
            CumulativeOutputTokens: 0,
            CumulativeCacheReadTokens: 0,
            CumulativeCacheCreationTokens: 0,
            Calls: 1,
            LastUsedAt: DateTime.UtcNow,
            LastError: null));

        var service = new TaskRunnerService(
            config, NullLogger<TaskRunnerService>.Instance, scanner, states, mutations, sessions,
            router, new ContextUsageParser(), summary, prompts, transitions, settings,
            quota, quotaCaps, chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions,
            globalBoot, git, pickupFailures, infraBreaker, taskAccess,
            timeline: timeline,
            runnerIdentity: RunnerIdentity.Resolve(config));

        Assert.True(service.EnsureRunner(scanner.GetWatchPaths().First()), "the test runner must activate");
        return new Harness(service, scanner);
    }

    private void WriteJob(string slug, string state, string mode, int order = 1)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $@"{{""id"":""{slug}"",""title"":""{slug}"",""state"":""{state}"",""order"":{order},""agent"":""claude"",""mode"":""{mode}""}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nOriginal task.\n");
    }
}
