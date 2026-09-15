using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end over a temp workspace for the AGT-2825 continue guard: a
/// concept or planning card must refuse an implementation-flavored continue
/// prompt with <c>409</c> before any side effect (AGT-2795 - the run started
/// in concept mode, produced no code, and silently overwrote the card's
/// Result with an escalation summary instead of being refused up front). An
/// explicit <c>modeOverride</c> bypasses the guard and reaches the normal
/// admission/spawn path. Mirrors the harness in
/// <see cref="FollowUpAdmissionServiceTests"/>, which covers the adjacent
/// (and unaffected) lane/phase/remote admission contract.
/// </summary>
public sealed class ConceptContinueGuardServiceTests : IDisposable
{
    private const string ProjectName = "demo";
    private const string ImplementationPrompt = "Bump the demo route count from 81 to 83.";

    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public ConceptContinueGuardServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-concept-continue-guard-" + Guid.NewGuid().ToString("N"));
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
    public async Task ConceptCard_ImplementationPrompt_NoOverride_Rejects409_WithoutTouchingTheCard()
    {
        var folder = WriteJob("repurposed-concept", TaskStates.Ready, TaskModes.Concept);
        var statusPath = Path.Combine(folder, "status.md");
        File.WriteAllText(statusPath, "# Status\n- Result: Success\n- delivered the original implementation\n");
        var statusBefore = File.ReadAllText(statusPath);
        var harness = Build();

        var ex = await Assert.ThrowsAsync<TaskOperationException>(() =>
            harness.Service.ContinueJobAsync("repurposed-concept", ImplementationPrompt, _watchPath));

        Assert.Equal(409, ex.Status);
        Assert.Contains("concept", ex.Message, StringComparison.Ordinal);
        Assert.Contains("promote-concept", ex.Message, StringComparison.Ordinal);

        // Nothing about the card changed: the previous Result was never
        // replaced (it wasn't even read), no continuation note or pending
        // intent was written, and prompt.md gained no follow-up history.
        Assert.Equal(statusBefore, File.ReadAllText(statusPath));
        Assert.False(File.Exists(Path.Combine(folder, "pending-intent.json")));
        Assert.False(Directory.Exists(Path.Combine(folder, "results", "history")));
        Assert.DoesNotContain(ImplementationPrompt, File.ReadAllText(Path.Combine(folder, "prompt.md")));
    }

    [Fact]
    public async Task PlanningCard_ImplementationPrompt_NoOverride_Rejects409_NamingPromoteToCoding()
    {
        WriteJob("repurposed-planning", TaskStates.Ready, TaskModes.Planning);
        var harness = Build();

        var ex = await Assert.ThrowsAsync<TaskOperationException>(() =>
            harness.Service.ContinueJobAsync("repurposed-planning", ImplementationPrompt, _watchPath));

        Assert.Equal(409, ex.Status);
        Assert.Contains("planning", ex.Message, StringComparison.Ordinal);
        Assert.Contains("promote-to-coding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConceptCard_DecisionAnswerPrompt_NoOverride_IsNotRejected()
    {
        // The actual shape of a legitimate concept continue: answering the
        // escalation decisions the concept run raised, not asking for code.
        // The only local runner slot is occupied first (the same trick
        // FollowUpAdmissionServiceTests uses) so a guard pass reaches the
        // deterministic "project busy -> queued" admission outcome instead
        // of spawning a real CLI process on the test host.
        WriteJob("concept-decisions", TaskStates.Ready, TaskModes.Concept);
        WriteJob("slot-hog", TaskStates.Progress, TaskModes.Coding, order: 2);
        var harness = Build();
        ClaimRun(ResolveRunner(harness.Service), "slot-hog", Path.Combine(_watchPath, TaskStates.Progress, "slot-hog"));

        var response = await harness.Service.ContinueJobAsync(
            "concept-decisions", "D1: use the direct-merge approach for the retry policy.", _watchPath);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.ProjectBusy, response.Queued!.Reason);
    }

    [Fact]
    public async Task ConceptCard_ImplementationPrompt_WithModeOverride_BypassesTheGuard()
    {
        // Same slot-occupied trick: modeOverride: true must reach the normal
        // admission path (proven by "queued/project-busy") instead of the
        // guard's 409.
        WriteJob("override-concept", TaskStates.Ready, TaskModes.Concept);
        WriteJob("slot-hog", TaskStates.Progress, TaskModes.Coding, order: 2);
        var harness = Build();
        ClaimRun(ResolveRunner(harness.Service), "slot-hog", Path.Combine(_watchPath, TaskStates.Progress, "slot-hog"));

        var response = await harness.Service.ContinueJobAsync(
            "override-concept", ImplementationPrompt, _watchPath, modeOverride: true);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.ProjectBusy, response.Queued!.Reason);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static ProjectRunner ResolveRunner(TaskRunnerService service)
    {
        var field = typeof(TaskRunnerService).GetField(
            "_runners", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var runners = (System.Collections.Concurrent.ConcurrentDictionary<string, ProjectRunner>)field.GetValue(service)!;
        return runners[ProjectName];
    }

    /// <summary>Occupies the runner's only slot, mirroring what a real run registers just before spawning the CLI.</summary>
    private static void ClaimRun(ProjectRunner runner, string jobId, string jobFolder)
    {
        var field = typeof(ProjectRunner).GetField(
            "_activeRuns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var runs = (ActiveRuns)field.GetValue(runner)!;
        var run = new ActiveRun
        {
            JobId = jobId,
            JobFolder = jobFolder,
            CliType = CliTypes.Claude,
            Intent = RunIntent.UserContinue,
        };
        Assert.True(runs.TryClaim(run));
    }

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

    private string WriteJob(string slug, string state, string mode, int order = 1)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $@"{{""id"":""{slug}"",""title"":""{slug}"",""state"":""{state}"",""order"":{order},""agent"":""claude"",""mode"":""{mode}""}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nOriginal task.\n");
        return dir;
    }
}
