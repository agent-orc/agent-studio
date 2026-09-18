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

    [Fact]
    public void ClaimedFollowUp_IsStashedUntilMatchingWorkerStart_ThenRecordedInHistory()
    {
        const string slug = "remote-follow-up";
        WriteJob(slug, TaskStates.Ready);
        var harness = Build();
        const string prompt = "Use the already approved recovery branch.";
        var saved = harness.Mutations.SavePendingIntent(
            slug,
            ContinueModes.Steer,
            prompt,
            FollowUpQueueReasons.RemoteExecution,
            activeJobId: null,
            watchPath: _watchPath,
            author: "human:owner")!;
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);

        var stashed = harness.Mutations.ReadAndStashPendingIntent(folder);

        Assert.Equal(saved.Prompt, stashed!.Prompt);
        Assert.False(File.Exists(Path.Combine(folder, "pending-intent.json")));
        Assert.True(File.Exists(Path.Combine(folder, "pending-intent.consumed.json")));
        Assert.Equal(
            PendingIntentAcknowledgeResult.HashMismatch,
            harness.Mutations.AcknowledgeStashedPendingIntent(folder, "wrong", "run-1"));

        var result = harness.Mutations.AcknowledgeStashedPendingIntent(
            folder,
            AgentStudio.TaskServer.Contracts.FollowUpPromptDigest.Compute(prompt),
            "run-1",
            source: "remote-heartbeat");

        Assert.Equal(PendingIntentAcknowledgeResult.Consumed, result);
        Assert.False(File.Exists(Path.Combine(folder, "pending-intent.consumed.json")));
        var receipt = Assert.Single(
            harness.Timeline.ReadAll(folder),
            row => row.Kind == TimelineEventKinds.FollowUpConsumed);
        Assert.Equal("run-1", receipt.RunId);
        Assert.Equal("steer", receipt.Details!["mode"]);
        Assert.Equal("human:owner", receipt.Details["author"]);
    }

    [Fact]
    public void FailedClaim_RollsStashedFollowUpBackToTheQueue()
    {
        const string slug = "failed-claim";
        WriteJob(slug, TaskStates.Ready);
        var harness = Build();
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        harness.Mutations.SavePendingIntent(
            slug, ContinueModes.Continue, "Try this again.", "remote-claim",
            activeJobId: null, watchPath: _watchPath);

        Assert.NotNull(harness.Mutations.ReadAndStashPendingIntent(folder));
        harness.Mutations.RollbackStashedPendingIntent(folder);

        Assert.NotNull(ReadIntent(folder));
        Assert.False(File.Exists(Path.Combine(folder, "pending-intent.consumed.json")));
    }

    [Fact]
    public async Task EnteringTerminalLane_SupersedesQueuedFollowUpIntoHistory()
    {
        const string slug = "completed-with-follow-up";
        WriteJob(slug, TaskStates.Ready);
        var harness = Build();
        harness.Mutations.SavePendingIntent(
            slug, ContinueModes.Continue, "This must not replay.", "operator-continue",
            activeJobId: null, watchPath: _watchPath);
        Assert.NotNull(harness.Mutations.ReadAndStashPendingIntent(
            Path.Combine(_watchPath, TaskStates.Ready, slug)));

        var moved = await harness.Transitions.MoveAsync(
            slug,
            TaskStates.Archive,
            _watchPath,
            suppressProductExecution: true);

        Assert.Equal(MoveJobStatus.Success, moved.Status);
        var folder = Path.Combine(_watchPath, TaskStates.Archive, slug);
        Assert.Null(ReadIntent(folder));
        var receipt = Assert.Single(
            harness.Timeline.ReadAll(folder),
            row => row.Kind == TimelineEventKinds.FollowUpSuperseded);
        Assert.Equal("superseded-by-completion", receipt.Details!["state"]);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("lane_changed")]
    public void StartupReconciliation_UnrelatedLaterActivityLeavesIntentQueued(string eventKind)
    {
        WriteJob("queued", TaskStates.HumanReview);
        var harness = Build();
        var intent = harness.Mutations.SavePendingIntent(
            "queued", ContinueModes.Steer, "Still queued.", "remote-execution",
            activeJobId: null, watchPath: _watchPath)!;
        harness.Sessions.AppendSessionEvent(
            "queued",
            new SessionEvent
            {
                Ts = intent.SavedAt.AddSeconds(1),
                Kind = eventKind,
                Cli = "remote-runner",
                RunAttemptId = "unrelated-event",
                FinishedAt = intent.SavedAt.AddSeconds(2),
                Result = "done",
            },
            _watchPath);

        var outcome = harness.Transitions.ReconcilePendingIntents();

        Assert.Equal(1, outcome.Inspected);
        Assert.Equal(0, outcome.Delivered);
        Assert.Single(outcome.Undecided);
        Assert.Equal("Still queued.", ReadIntent(
            Path.Combine(_watchPath, TaskStates.HumanReview, "queued"))!.Prompt);
    }

    [Fact]
    public void StartupReconciliation_MatchingPromptHashConvertsIntentWithoutTerminalOutcome()
    {
        WriteJob("acknowledged", TaskStates.HumanReview);
        var harness = Build();
        var intent = harness.Mutations.SavePendingIntent(
            "acknowledged", ContinueModes.Steer, "Already delivered.", "remote-execution",
            activeJobId: null, watchPath: _watchPath)!;
        harness.Sessions.AppendSessionEvent(
            "acknowledged",
            new SessionEvent
            {
                Ts = intent.SavedAt.AddSeconds(1),
                Kind = "continue",
                Cli = "remote-runner",
                RunAttemptId = "run-acknowledged",
                StartedPromptSha256 = AgentStudio.TaskServer.Contracts.FollowUpPromptDigest.Compute(intent.Prompt),
            },
            _watchPath);

        var outcome = harness.Transitions.ReconcilePendingIntents();

        Assert.Equal(1, outcome.Inspected);
        Assert.Equal(1, outcome.Delivered);
        Assert.Empty(outcome.Undecided);
        Assert.Empty(outcome.Failures);
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "acknowledged");
        Assert.Null(ReadIntent(folder));
        Assert.Contains(
            harness.Timeline.ReadAll(folder),
            row => row.Kind == TimelineEventKinds.FollowUpConsumed
                   && row.RunId == "run-acknowledged");
    }

    [Fact]
    public void StartupReconciliation_LegacyCodingRunWithTerminalOutcomeConvertsIntent()
    {
        WriteJob("legacy", TaskStates.HumanReview);
        var harness = Build();
        var intent = harness.Mutations.SavePendingIntent(
            "legacy", ContinueModes.Continue, "Legacy delivered prompt.", "remote-execution",
            activeJobId: null, watchPath: _watchPath)!;
        var startedAt = intent.SavedAt.AddSeconds(1);
        harness.Sessions.AppendSessionEvent(
            "legacy",
            new SessionEvent
            {
                Ts = startedAt,
                Kind = "start",
                Cli = "remote-runner",
                RunAttemptId = "run-legacy",
                FinishedAt = startedAt.AddMinutes(2),
                Result = "done",
                Status = "completed",
            },
            _watchPath);

        var outcome = harness.Transitions.ReconcilePendingIntents();

        Assert.Equal(1, outcome.Delivered);
        Assert.Empty(outcome.Undecided);
        Assert.Null(ReadIntent(Path.Combine(_watchPath, TaskStates.HumanReview, "legacy")));
    }

    [Fact]
    public void StartupReconciliation_PreStartClaimFailureLeavesIntentQueued()
    {
        WriteJob("pre-start-failure", TaskStates.HumanReview);
        var harness = Build();
        var intent = harness.Mutations.SavePendingIntent(
            "pre-start-failure", ContinueModes.Continue, "Retry after rollback.", "remote-execution",
            activeJobId: null, watchPath: _watchPath)!;
        harness.Sessions.AppendSessionEvent(
            "pre-start-failure",
            new SessionEvent
            {
                Ts = intent.SavedAt.AddSeconds(1),
                Kind = "start",
                Cli = "remote-runner",
                RunAttemptId = "run-never-started",
                Status = "claim-failed",
            },
            _watchPath);

        var outcome = harness.Transitions.ReconcilePendingIntents();

        Assert.Equal(0, outcome.Delivered);
        Assert.Single(outcome.Undecided);
        Assert.Equal("Retry after rollback.", ReadIntent(
            Path.Combine(_watchPath, TaskStates.HumanReview, "pre-start-failure"))!.Prompt);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed record Harness(
        TaskRunnerService Service,
        TaskScannerService Scanner,
        ProjectSettingsService Settings,
        CliRouter Router,
        TaskMutationService Mutations,
        TaskTransitionService Transitions,
        TaskSessionLog Sessions,
        TimelineLog Timeline);

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
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline);
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
            scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance,
            sessions: sessions);
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
        return new Harness(service, scanner, settings, router, mutations, transitions, sessions, timeline);
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
