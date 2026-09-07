using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end guard for AGT-2747: "a continue/steer must never be lost".
///
/// <para>
/// The 2026-09-07 incident: <c>POST /api/tasks/{id}/continue</c> accepted a
/// follow-up for a card sitting in <c>4-auto-review</c>, answered
/// <c>200 {"status":"started"}</c>, and spawned a local run; the lane watchdog
/// killed the fresh process within a second ("active job moved out of
/// 3-progress") and nothing had persisted the prompt. Three steers were lost
/// in one night. These tests drive the real service graph so a regression on
/// any of the three legs - admission, preservation, honest response - fails
/// here rather than in production at 02:30.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class FollowUpAdmissionAndPreservationTests : IDisposable
{
    private const string ProjectName = "demo";
    private const string RemoteRunnerId = "agent-runner-01";

    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public FollowUpAdmissionAndPreservationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-followup-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        InitializeGitRepository();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── 1. Admission: a non-runnable lane queues instead of spawning ──────────

    [Fact]
    public async Task ContinueOnAutoReviewCard_Queues_PersistsIntent_AndPromotesToReadyTop()
    {
        WriteJob(TaskStates.AutoReview, "steered");
        WriteJob(TaskStates.Ready, "other", order: 5);
        var stack = Build();

        var response = await stack.Runners.ContinueJobAsync(
            "steered", "Please redo the retention table.", _watchPath, mode: ContinueModes.Steer);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, response.Queued!.Reason);
        Assert.Equal(TaskStates.AutoReview, response.Queued.PromotedFromState);
        Assert.False(stack.Cli.StartCalled, "a queued follow-up must never spawn a local process");

        var queued = stack.Scanner.FindJob("steered", _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Equal(1, response.Queued.Position);
        Assert.Equal(ContinueModes.Steer, queued.PendingIntent!.Mode);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, queued.PendingIntent.SavedReason);
        Assert.Contains("retention table", queued.PendingIntent.Prompt, StringComparison.Ordinal);

        // The card must sort ahead of the plain Ready card so the next pickup
        // takes the steer, not unrelated work.
        Assert.True(
            queued.Order < stack.Scanner.FindJob("other", _watchPath)!.Order,
            "a queued follow-up is promoted to the top of 2-ready");

        Assert.Contains(
            stack.Timeline.ReadAll(queued.FolderPath),
            e => e.Kind == TimelineEventKinds.FollowUpPreserved
                 && e.Details?.GetValueOrDefault("reason") == FollowUpQueueReasons.LaneNotRunnable
                 && e.Details?.GetValueOrDefault("mode") == ContinueModes.Steer);

        // The operator's text also has to survive for a remote runner, which
        // reads prompt.md and never sees pending-intent.json.
        Assert.Contains(
            "retention table",
            File.ReadAllText(Path.Combine(queued.FolderPath, "prompt.md")),
            StringComparison.Ordinal);
    }

    // ── 2. Admission: a remote-routed project queues instead of spawning ──────

    [Fact]
    public async Task ContinueOnRemoteConfiguredCard_Queues_WithoutLocalProcess()
    {
        WriteJob(TaskStates.Progress, "remote-card");
        var stack = Build();
        stack.Settings.SetExecutionSettings(ProjectName, pickupMode: null, executionLocation: RemoteRunnerId);

        var response = await stack.Runners.ContinueJobAsync(
            "remote-card", "Check the archive run.", _watchPath, mode: ContinueModes.Continue);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, response.Queued!.Reason);
        Assert.False(stack.Cli.StartCalled,
            "the local backend must not run a card another host owns");

        var queued = stack.Scanner.FindJob("remote-card", _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, queued.PendingIntent!.SavedReason);
    }

    [Fact]
    public async Task StartOnRemoteConfiguredCard_Queues_WithoutPendingIntent()
    {
        WriteJob(TaskStates.Ready, "remote-start");
        var stack = Build();
        stack.Settings.SetExecutionSettings(ProjectName, pickupMode: null, executionLocation: RemoteRunnerId);

        var response = await stack.Runners.StartJobAsync("remote-start", _watchPath);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, response.Queued!.Reason);
        Assert.False(stack.Cli.StartCalled);

        // A start carries no operator text, so there is nothing to persist -
        // the promotion alone hands the card to the remote runner.
        var queued = stack.Scanner.FindJob("remote-start", _watchPath)!;
        Assert.Null(queued.PendingIntent);
        Assert.False(File.Exists(Path.Combine(queued.FolderPath, "pending-intent.json")));
    }

    [Fact]
    public async Task ContinueOnCardWithDeliveryAwaitingReview_Queues()
    {
        WriteJob(TaskStates.Progress, "delivered", phase: LifecyclePhases.AwaitingReview);
        var stack = Build();

        var response = await stack.Runners.ContinueJobAsync(
            "delivered", "One more thing.", _watchPath, mode: ContinueModes.Continue);

        Assert.Equal("queued", response.Status);
        Assert.Equal(FollowUpQueueReasons.DeliveryUnderReview, response.Queued!.Reason);
        Assert.False(stack.Cli.StartCalled);
    }

    // ── 3. The runnable lanes still start locally ────────────────────────────

    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public async Task ContinueOnRunnableLane_StillStartsLocally(string lane)
    {
        WriteJob(lane, "runnable");
        var stack = Build();

        var response = await stack.Runners.ContinueJobAsync(
            "runnable", "Keep going.", _watchPath, mode: ContinueModes.Continue);

        Assert.Equal("started", response.Status);
        Assert.True(stack.Cli.StartCalled, $"a follow-up on '{lane}' must still spawn a local run");
        Assert.NotNull(response.Execution);

        // Admission-before-spawn guarantees the run is physically in 3-progress,
        // so the lane watchdog's "moved out of 3-progress" can never fire for it.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "runnable")));
    }

    [Fact]
    public async Task StartOnLocalReadyCard_StillStartsLocally()
    {
        WriteJob(TaskStates.Ready, "local-start");
        var stack = Build();

        var response = await stack.Runners.StartJobAsync("local-start", _watchPath);

        Assert.Equal("started", response.Status);
        Assert.True(stack.Cli.StartCalled);
    }

    // ── 4. A kill never loses the follow-up ──────────────────────────────────

    [Fact]
    public async Task WatchdogStopDuringLocalFollowUp_LeavesPendingIntentAndTimelineEvent()
    {
        WriteJob(TaskStates.Progress, "killed");
        var stack = Build();

        var response = await stack.Runners.ContinueJobAsync(
            "killed", "Fix the archive threshold.", _watchPath, mode: ContinueModes.Steer);
        Assert.Equal("started", response.Status);

        // Reproduce the incident: something moves the card out of 3-progress
        // while the follow-up run is live, and the lane watchdog clears it.
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "killed");
        var reviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, "killed");
        Directory.Move(progressFolder, reviewFolder);

        Assert.True(stack.Runner.ReconcileActiveJobAgainstDisk(),
            "the lane watchdog should have cleared the latch for the moved card");

        var intentPath = Path.Combine(reviewFolder, "pending-intent.json");
        Assert.True(File.Exists(intentPath),
            "a killed run that still owed the operator an answer must leave the follow-up behind");

        var intent = JsonSerializer.Deserialize<PendingIntent>(
            File.ReadAllText(intentPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(ContinueModes.Steer, intent.Mode);
        Assert.Contains("archive threshold", intent.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("run-stopped:", intent.SavedReason, StringComparison.Ordinal);
        Assert.Contains("4-auto-review", intent.SavedReason, StringComparison.Ordinal);

        // Exactly one row: a lane move reaches the runner twice (OnJobMoved hook
        // plus watcher reconciliation), and the operator should see one event.
        var preserved = Assert.Single(
            stack.Timeline.ReadAll(reviewFolder),
            e => e.Kind == TimelineEventKinds.FollowUpPreserved);
        Assert.Equal(ContinueModes.Steer, preserved.Details?.GetValueOrDefault("mode"));
    }

    /// <summary>
    /// The interlocked latch behind that single row. The two clear paths can
    /// interleave before either release lands, so the guard cannot rely on the
    /// active-job latch already being gone.
    /// </summary>
    [Fact]
    public void FollowUpPreservation_IsClaimedExactlyOncePerRun()
    {
        var run = new ActiveRun { JobId = "once", Intent = RunIntent.UserContinue, Followup = "steer" };

        Assert.True(run.TryClaimFollowUpPreservation(), "the first kill preserves");
        Assert.False(run.TryClaimFollowUpPreservation(), "the racing second kill must not preserve again");
    }

    [Fact]
    public async Task PlainStartKilledByWatchdog_LeavesNoPendingIntent()
    {
        WriteJob(TaskStates.Progress, "plain");
        var stack = Build();

        var response = await stack.Runners.StartJobAsync("plain", _watchPath);
        Assert.Equal("started", response.Status);

        var reviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, "plain");
        Directory.Move(Path.Combine(_watchPath, TaskStates.Progress, "plain"), reviewFolder);
        stack.Runner.ReconcileActiveJobAgainstDisk();

        Assert.False(File.Exists(Path.Combine(reviewFolder, "pending-intent.json")),
            "a manual start owes the operator no answer, so nothing is queued behind it");
    }

    /// <summary>
    /// Preservation is bound to the live execution latch, and the latch is
    /// released the moment the CLI process exits (before any post-processing
    /// lane move). That binding is what stops an already-answered follow-up
    /// from being resurrected when the runner itself moves a finished card to
    /// <c>4-auto-review</c>. Asserted here as "a second watchdog pass over a
    /// run this runner no longer holds writes nothing".
    /// </summary>
    [Fact]
    public async Task WatchdogPassOverARunTheRunnerNoLongerHolds_WritesNothing()
    {
        WriteJob(TaskStates.Progress, "twice");
        var stack = Build();

        await stack.Runners.ContinueJobAsync(
            "twice", "Answer this once.", _watchPath, mode: ContinueModes.Steer);

        var reviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, "twice");
        Directory.Move(Path.Combine(_watchPath, TaskStates.Progress, "twice"), reviewFolder);
        Assert.True(stack.Runner.ReconcileActiveJobAgainstDisk());

        var intentPath = Path.Combine(reviewFolder, "pending-intent.json");
        Assert.True(File.Exists(intentPath));
        File.Delete(intentPath);

        Assert.False(stack.Runner.ReconcileActiveJobAgainstDisk(),
            "the latch is gone, so there is no run left to preserve");
        Assert.False(File.Exists(intentPath),
            "a follow-up must be re-persisted exactly once, by the kill that interrupted it");
    }

    // ── 5. Honest response: a run that dies in the start window is not "started" ──

    [Fact]
    public async Task RunKilledInsideTheStartWindow_Answers409_AndKeepsTheIntent()
    {
        WriteJob(TaskStates.Progress, "racing");
        var stack = Build();

        // The 2026-09-07 race: something takes the card off this runner while
        // the follow-up process is coming up. This is the production
        // TaskTransitionService.OnJobMoved hook, which clears the latch and
        // kills the CLI the moment a card leaves 3-progress.
        stack.Cli.OnStartHook = () => stack.Runners.ClearActiveJobForProject(
            ProjectName, "racing", "job moved out of 3-progress externally (3-progress -> 4-auto-review)");

        var failure = await Assert.ThrowsAsync<TaskOperationException>(
            () => stack.Runners.ContinueJobAsync(
                "racing", "Do not lose this steer.", _watchPath, mode: ContinueModes.Steer));

        Assert.Equal(409, failure.Status);
        Assert.Contains("stays saved", failure.Message, StringComparison.Ordinal);

        var folder = Path.Combine(_watchPath, TaskStates.Progress, "racing");
        Assert.True(File.Exists(Path.Combine(folder, "pending-intent.json")),
            "a 409 is only honest if the follow-up really is still queued");
    }

    // ── 6. Consumption proof ─────────────────────────────────────────────────

    [Fact]
    public async Task AutoPickupConsumingASavedIntent_KeepsTheConsumedCopyAndRecordsTheRun()
    {
        WriteJob(TaskStates.AutoReview, "consumed");
        var stack = Build();

        await stack.Runners.ContinueJobAsync(
            "consumed", "Redo the dilution table.", _watchPath, mode: ContinueModes.Steer);

        stack.Runner.SetMode("auto-continuous");
        await stack.Runner.TickAsync(CancellationToken.None);

        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "consumed");
        Assert.True(Directory.Exists(progressFolder), "the promoted card should have been picked up");
        Assert.True(stack.Cli.StartCalled);
        Assert.Contains("dilution table", stack.Cli.LastPrompt ?? "", StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(progressFolder, "pending-intent.json")),
            "a consumed intent is no longer owed");
        Assert.True(File.Exists(Path.Combine(progressFolder, "pending-intent.consumed.json")),
            "the consumed copy is retained as proof the steer actually ran");

        var consumed = Assert.Single(
            stack.Timeline.ReadAll(progressFolder),
            e => e.Kind == TimelineEventKinds.FollowUpConsumed);
        Assert.Equal(ContinueModes.Steer, consumed.Details?.GetValueOrDefault("mode"));
        Assert.Equal("local", consumed.Details?.GetValueOrDefault("executedBy"));
        // A fresh session leaves runId null, so the run's start marker is the
        // correlator that answers "which run took my steer?".
        Assert.True(
            DateTime.TryParse(consumed.Details?.GetValueOrDefault("runStartedAt"), out _),
            "follow_up_consumed must always identify its run");
    }

    /// <summary>
    /// Two follow-ups in a row: the first queues (wrong lane), which moves the
    /// card to Ready, so the second one starts locally. The run must deliver
    /// both and leave nothing queued behind - admission already appended the
    /// first one's text to prompt.md, so a surviving intent would replay it as
    /// a redundant extra run.
    /// </summary>
    [Fact]
    public async Task SecondFollowUpThatStartsLocally_AlsoDeliversTheQueuedOne()
    {
        WriteJob(TaskStates.AutoReview, "double");
        var stack = Build();

        var first = await stack.Runners.ContinueJobAsync(
            "double", "FIRST steer.", _watchPath, mode: ContinueModes.Steer);
        Assert.Equal("queued", first.Status);

        var second = await stack.Runners.ContinueJobAsync(
            "double", "SECOND steer, supersedes.", _watchPath, mode: ContinueModes.Steer);
        Assert.Equal("started", second.Status);

        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "double");
        Assert.False(File.Exists(Path.Combine(progressFolder, "pending-intent.json")),
            "the queued follow-up rode along in prompt.md; leaving it queued would replay it");
        Assert.True(File.Exists(Path.Combine(progressFolder, "pending-intent.consumed.json")));

        var prompt = File.ReadAllText(Path.Combine(progressFolder, "prompt.md"));
        Assert.Contains("FIRST steer.", prompt, StringComparison.Ordinal);
        Assert.Contains("SECOND steer, supersedes.", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart guard: an intent that another producer saved without
    /// writing it into <c>prompt.md</c> (here a steer-timeout auto-answer) is
    /// NOT swept up by a user continue - its words exist nowhere else.
    /// </summary>
    [Fact]
    public async Task UserContinue_DoesNotConsumeAnIntentFromAnotherProducer()
    {
        WriteJob(TaskStates.Ready, "foreign");
        var stack = Build();
        stack.Mutations.SavePendingIntent(
            "foreign", ContinueModes.Continue, "Auto-answer: the branch is already integrated.",
            reason: "steer-timeout-auto-answer", activeJobId: null, watchPath: _watchPath);

        var response = await stack.Runners.ContinueJobAsync(
            "foreign", "Unrelated operator follow-up.", _watchPath, mode: ContinueModes.Continue);
        Assert.Equal("started", response.Status);

        var intentPath = Path.Combine(_watchPath, TaskStates.Progress, "foreign", "pending-intent.json");
        Assert.True(File.Exists(intentPath),
            "an auto-answer lives only in pending-intent.json; consuming it here would lose it");
        Assert.Contains("already integrated", File.ReadAllText(intentPath), StringComparison.Ordinal);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private void WriteJob(string state, string slug, int order = 1, string? phase = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Do the task.");
        var phaseField = phase is null ? string.Empty : $",\"phase\":\"{phase}\"";
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order}," +
            $"\"agent\":\"claude\",\"cliType\":\"{CliTypes.Claude}\",\"ownerClientId\":\"local-default\"{phaseField}}}");
    }

    private void InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Follow-up Admission Test");
        File.WriteAllText(Path.Combine(_watchPath, "README.md"), "test repository");
        RunGit("add", "README.md");
        RunGit("commit", "-q", "-m", "seed");
        RunGit("checkout", "-q", "-b", "develop");
    }

    private void RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _watchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git did not start");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }

    private sealed record Stack(
        TaskRunnerService Runners,
        ProjectRunner Runner,
        TaskScannerService Scanner,
        TimelineLog Timeline,
        ProjectSettingsService Settings,
        TaskMutationService Mutations,
        RecordingCliService Cli);

    private Stack Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot,
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath,
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var cli = new RecordingCliService();
        var router = new CliRouter(cli);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var quotaCache = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quota = new QuotaService(
            NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCache);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(
            config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var globalStore = new GlobalOrchestratorSessionStore(
            config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, scanner, config);

        var runner = new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quota, quotaCaps, git, pickupFailures, infraBreaker, taskAccess,
            timeline: timeline);

        var runners = new TaskRunnerService(
            config, NullLogger<TaskRunnerService>.Instance, scanner, states, mutations, sessions,
            router, new ContextUsageParser(), summary, prompts, transitions, settings,
            quota, quotaCaps, chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions,
            globalBoot, git, pickupFailures, infraBreaker, taskAccess,
            timeline: timeline);
        runners.RegisterRunnerForTest(ProjectName, runner);

        return new Stack(runners, runner, scanner, timeline, settings, mutations, cli);
    }

    /// <summary>
    /// A CLI that reports a live process and then stays quiet, so a run remains
    /// "started" until the test decides how it ends. <see cref="StartCalled"/> is
    /// the load-bearing assertion: a queued follow-up must never reach here.
    /// </summary>
    private sealed class RecordingCliService : ICliExecutionService
    {
        public string CliType => CliTypes.Claude;
        public bool StartCalled { get; private set; }
        public string? LastPrompt { get; private set; }
        public RunStopReason? LastStopReason { get; private set; }
        /// <summary>Fires while the process is "coming up", so a test can race a lane move against the spawn.</summary>
        public Action? OnStartHook { get; set; }

        public string GetCliPath() => "fake-claude";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null)
            => (true, "test", path ?? GetCliPath());

        public Task<(CliExecution? Execution, string? Error)> StartAsync(
            string jobId,
            string jobKey,
            string prompt,
            string workingDirectory,
            string? sessionName = null,
            bool resumeSession = false,
            string? model = null,
            string? thinkingLevel = null,
            string? jobFolderPath = null,
            string? permissionMode = null,
            string? contextMode = null,
            string? executionEngine = null,
            CancellationToken ct = default)
        {
            StartCalled = true;
            LastPrompt = prompt;
            var execution = new CliExecution
            {
                JobId = jobId,
                TaskKey = jobKey,
                ProcessId = Environment.ProcessId,
                StartedAt = DateTime.UtcNow,
                Status = RunStatuses.Running,
                Model = model,
                ThinkingLevel = thinkingLevel,
            };
            OnStarted?.Invoke(jobKey, execution);
            // Fires with the process nominally live, so a test can race a
            // watchdog against the rest of the start sequence.
            OnStartHook?.Invoke();
            return Task.FromResult<(CliExecution?, string?)>((execution, null));
        }

        public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop)
        {
            LastStopReason = reason;
            return true;
        }

        public bool SendInput(string jobKey, string input) => false;
        public List<CliOutputLine> GetOutput(string jobKey) => [];
        public void DiscardPersistedOutput(string jobKey) { }
        public void ReleaseOutputResources(string jobKey) { }
        public CliExecution? GetExecution(string jobKey) => null;
        public SessionUsage? GetLastUsage(string jobKey) => null;
        public bool IsRunningForProject(string rootPath) => false;
        public DateTime? GetLastStreamedAt(string jobKey) => null;
        public WatchdogState GetWatchdogState(string jobKey) => WatchdogState.Healthy;
        public void SetWatchdogState(string jobKey, WatchdogState state) { }
        public void ReattachOnStartup() { }
        public Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(new CliModelCatalog { Models = [], Source = "fake", FetchedAt = DateTime.UtcNow });
        public bool IsCompatibleSessionName(string? sessionName) => true;

        public event Action<string, CliOutputLine>? OnOutput;
        public event Action<string, CliExecution>? OnStarted;
        public event Action<string, CliExecution>? OnFinished;
        public event Action<string, CliRunEvent>? OnRunEvent;
    }
}
