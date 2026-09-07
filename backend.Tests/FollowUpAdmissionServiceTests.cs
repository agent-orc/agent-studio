using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end over a temp workspace for the follow-up admission contract
/// (AGT-2747): a user follow-up is never accepted with <c>started</c> and then
/// thrown away. Every non-runnable case answers <c>queued</c>, persists the
/// prompt as <c>pending-intent.json</c>, and promotes the card to the top of
/// <c>2-ready</c>; a run that is killed while carrying an unconsumed follow-up
/// writes it back before the process dies.
/// </summary>
public sealed class FollowUpAdmissionServiceTests : IDisposable
{
    private const string ProjectName = "demo";

    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public FollowUpAdmissionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-followup-admission-" + Guid.NewGuid().ToString("N"));
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
    public async Task ContinueOnAutoReviewCard_IsQueued_WithPersistedIntentAtTopOfReady()
    {
        // The 2026-09-07 reproduction: AGT-2743 sat in 4-auto-review with a
        // pending delivery and the steer was answered 200 "started", then killed.
        WriteJob("steered", TaskStates.AutoReview, phase: LifecyclePhases.AwaitingReview);
        WriteJob("other-ready", TaskStates.Ready, order: 10);
        var harness = Build();

        var response = await harness.Service.ContinueJobAsync(
            "steered", "Please use a direct merge, not a squash.", _watchPath,
            cliTypeOverride: CliTypes.Codex, mode: ContinueModes.Steer);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, response.Queued!.Reason);
        Assert.Equal(TaskStates.AutoReview, response.Queued.PromotedFromState);
        Assert.Null(response.Execution);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "steered");
        Assert.True(Directory.Exists(readyFolder), "the card must move to 2-ready so a run can pick it up");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "steered")));

        var intent = ReadIntent(readyFolder);
        Assert.NotNull(intent);
        Assert.Equal(ContinueModes.Steer, intent!.Mode);
        Assert.Contains("direct merge", intent.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, intent.SavedReason);

        // Top of Ready: strictly ahead of the card that was already queued.
        harness.Scanner.InvalidateCache();
        var steered = harness.Scanner.FindJob("steered", _watchPath)!;
        var other = harness.Scanner.FindJob("other-ready", _watchPath)!;
        Assert.True(steered.Order < other.Order, $"steered order {steered.Order} must beat {other.Order}");

        Assert.Contains(TimelineEventKinds.FollowUpQueued, ReadTimelineKinds(readyFolder));
    }

    [Fact]
    public async Task ContinueOnRemoteConfiguredCard_IsQueued_WithoutALocalProcess()
    {
        WriteJob("remote-card", TaskStates.Progress, phase: LifecyclePhases.LoopWaiting);
        var harness = Build();
        harness.Settings.SetExecutionRunner(ProjectName, "agent-runner-01", remoteExecutionEnabled: true);

        var response = await harness.Service.ContinueJobAsync(
            "remote-card", "Keep the cardinality one-to-one.", _watchPath, mode: ContinueModes.Steer);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, response.Queued!.Reason);
        Assert.Null(response.Execution);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "remote-card");
        var intent = ReadIntent(readyFolder);
        Assert.NotNull(intent);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, intent!.SavedReason);

        // The remote runner reads prompt.md when it claims the card, so the
        // follow-up has to be in there too.
        Assert.Contains("one-to-one", File.ReadAllText(Path.Combine(readyFolder, "prompt.md")), StringComparison.OrdinalIgnoreCase);

        // Nothing was handed to a local CLI.
        Assert.Null(harness.Router.Get(CliTypes.Claude).GetExecution(TaskIdentity.CreateKey(_watchPath, "remote-card")));
    }

    [Fact]
    public async Task ContinueOnCardWithDeliveryUnderReview_IsQueued()
    {
        // Still physically in 3-progress, but the auto-review worker owns it -
        // exactly the race that produced "active job moved out of 3-progress".
        WriteJob("post-processing", TaskStates.Progress, phase: LifecyclePhases.PostProcessingRunning);
        var harness = Build();

        var response = await harness.Service.ContinueJobAsync(
            "post-processing", "One more thing before you finish.", _watchPath);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.DeliveryUnderReview, response.Queued!.Reason);
        Assert.NotNull(ReadIntent(Path.Combine(_watchPath, TaskStates.Ready, "post-processing")));
    }

    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public async Task RunnableLanes_ReachTheRunner_InsteadOfTheAdmissionQueue(string lane)
    {
        // The admission must stay out of the way of the ordinary local path. To
        // prove the follow-up got past it without spawning a CLI on the test
        // host, the runner's only slot is occupied first: the refusal that comes
        // back is then the runner's busy check, not the lane guard.
        WriteJob("runnable", lane, phase: lane == TaskStates.Progress ? LifecyclePhases.ExecutionRunning : null);
        WriteJob("slot-hog", TaskStates.Progress, phase: LifecyclePhases.ExecutionRunning, order: 2);
        var harness = Build();
        ClaimRun(
            ResolveRunner(harness.Service), "slot-hog",
            Path.Combine(_watchPath, TaskStates.Progress, "slot-hog"),
            followup: null, mode: null);

        var response = await harness.Service.ContinueJobAsync("runnable", "Carry on.", _watchPath);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.ProjectBusy, response.Queued!.Reason);
    }

    [Fact]
    public void WatchdogStopDuringLocalFollowUp_PreservesTheIntentAndTheTimelineEvent()
    {
        const string slug = "killed-mid-steer";
        WriteJob(slug, TaskStates.Progress, phase: LifecyclePhases.ExecutionRunning);
        var harness = Build();
        var runner = ResolveRunner(harness.Service);
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, slug);

        ClaimRun(runner, slug, progressFolder, "Do NOT squash the commits.", ContinueModes.Steer);

        // The exact call the lane watchdog makes when it finds the active job
        // outside 3-progress.
        var cleared = runner.ClearActiveJobIfMatches(
            slug, "active job moved out of 3-progress (now in 4-auto-review)");

        Assert.True(cleared);
        var intent = ReadIntent(progressFolder);
        Assert.NotNull(intent);
        Assert.Equal(ContinueModes.Steer, intent!.Mode);
        Assert.Contains("squash", intent.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(FollowUpQueueReasons.IsRunStopped(intent.SavedReason), intent.SavedReason);
        Assert.Contains("moved out of 3-progress", intent.SavedReason, StringComparison.Ordinal);
        Assert.Contains(TimelineEventKinds.FollowUpPreserved, ReadTimelineKinds(progressFolder));
    }

    [Fact]
    public void StopAfterTheCliExited_DoesNotResurrectAConsumedFollowUp()
    {
        // A run whose CLI already finished has delivered the follow-up. The
        // trailing lane move to 4-auto-review must not re-save it as pending.
        const string slug = "finished-run";
        WriteJob(slug, TaskStates.Progress, phase: LifecyclePhases.ExecutionRunning);
        var harness = Build();
        var runner = ResolveRunner(harness.Service);
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, slug);

        var run = ClaimRun(runner, slug, progressFolder, "Already delivered.", ContinueModes.Continue);
        run.TryReleaseExecutionSlot();

        runner.ClearActiveJobIfMatches(slug, "active job moved out of 3-progress (now in 4-auto-review)");

        Assert.Null(ReadIntent(progressFolder));
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed record Harness(
        TaskRunnerService Service,
        TaskScannerService Scanner,
        ProjectSettingsService Settings,
        CliRouter Router);

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
            // The start window is only meaningful against a real spawn; these
            // tests assert the admission and the preservation instead.
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
        return new Harness(service, scanner, settings, router);
    }

    private static ProjectRunner ResolveRunner(TaskRunnerService service)
    {
        var field = typeof(TaskRunnerService).GetField(
            "_runners", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var runners = (System.Collections.Concurrent.ConcurrentDictionary<string, ProjectRunner>)field.GetValue(service)!;
        return runners[ProjectName];
    }

    /// <summary>
    /// Puts the runner into the state a local follow-up run leaves behind: one
    /// claimed slot carrying the user's prompt and mode. Mirrors what
    /// <c>RunCliAsync</c> registers just before it spawns the CLI.
    /// </summary>
    private static ActiveRun ClaimRun(ProjectRunner runner, string jobId, string jobFolder, string? followup, string? mode)
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
            Followup = followup,
            FollowupMode = mode,
        };
        Assert.True(runs.TryClaim(run));
        return run;
    }

    private void WriteJob(string slug, string state, string? phase = null, int order = 1)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var phaseField = phase is null ? string.Empty : $@",""phase"":""{phase}""";
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $@"{{""id"":""{slug}"",""title"":""{slug}"",""state"":""{state}"",""order"":{order},""agent"":""claude""{phaseField}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nOriginal task.\n");
    }

    private static PendingIntent? ReadIntent(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "pending-intent.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<PendingIntent>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;
    }

    private static IReadOnlyList<string> ReadTimelineKinds(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "logs", "timeline.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString() ?? string.Empty)
            .ToList();
    }
}
