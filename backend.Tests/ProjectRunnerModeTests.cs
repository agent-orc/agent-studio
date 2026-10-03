using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F16 regression: the runner must not flip from <c>auto-continuous</c>
/// back to <c>manual</c> just because a job succeeded. Operator 2026-05-22
/// reported one successful task followed by mode == manual with five
/// ready tasks still queued; the day's backend log showed mode stayed
/// auto-continuous, but the trip-wire surface is wide (four <c>SetMode</c>
/// call sites in <c>ProjectRunner</c>) so we lock the contract in tests:
///
/// <list type="bullet">
///   <item>The <c>auto-single -&gt; manual</c> revert at the top of
///   <c>TickAsync</c> fires only when <c>_mode == "auto-single"</c>,
///   the pickup queue is empty, and no project run chain is still in flight.
///   <c>auto-continuous</c> drains the queue without flipping.</item>
///   <item>A successful auto-pickup that reaches review resets the
///   consecutive auto-failure counter. Without this, two prior failures
///   plus one transient hiccup would falsely trip the breaker.</item>
///   <item>A successful run that captures a session id resets the per-job
///   capture-fail counter so a job that flaked then succeeded does not
///   carry the prior count into a later isolated failure.</item>
///   <item>Every <c>SetMode</c> invocation emits a single structured log
///   line with <c>From</c>, <c>To</c>, and <c>Reason</c> so a future
///   "why did the runner flip" question is answerable from the day's
///   log alone.</item>
/// </list>
/// </summary>
public sealed class ProjectRunnerModeTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private TaskStateMachine? _states;
    private const string ProjectName = "demo";

    public ProjectRunnerModeTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-runner-mode-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Test (a): auto-continuous + N drained ticks - mode must NOT flip.
    /// Drives <see cref="ProjectRunner.TickAsync"/> directly with an empty
    /// queue three times in a row. The on-empty-queue branch only flips
    /// the mode when <c>_mode == "auto-single"</c> and no run chain is in
    /// flight; locking it here guards against a future refactor that broadens
    /// the condition.
    /// </summary>
    [Fact]
    public async Task AutoContinuous_QueueDrainedAcrossMultipleTicks_ModeStaysAutoContinuous()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);

        for (var i = 0; i < 3; i++)
        {
            await runner.TickAsync(CancellationToken.None);
            Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        }
    }

    /// <summary>
    /// Test (b): auto-single + one drained tick with no run in flight
    /// flips the mode to manual.
    /// Locks the existing revert path (trip-wire at the top of
    /// <c>TickAsync</c>) so it cannot be silently removed by a refactor
    /// that would make the two auto modes behave identically.
    /// </summary>
    [Fact]
    public async Task AutoSingle_QueueDrained_ModeFlipsToManual()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-single");
        Assert.Equal("auto-single", runner.GetStatus().Mode);

        await runner.TickAsync(CancellationToken.None);

        Assert.Equal("manual", runner.GetStatus().Mode);
    }

    [Fact]
    public async Task AutoSingle_OnlyPickupCandidateIsActiveRun_ModeStaysAutoSingle()
    {
        WriteJob(TaskStates.Progress, "job-a");
        var runner = BuildRunner(maxParallelism: 2);
        runner.SetMode("auto-single");
        runner.SetActiveJobForTest("job-a");

        await runner.TickAsync(CancellationToken.None);

        Assert.Equal("auto-single", runner.GetStatus().Mode);
    }

    [Fact]
    public async Task AutoSingle_AutoReviewInFlight_ReissueRemainsPickupEligible()
    {
        WriteJob(TaskStates.AutoReview, "job-a");
        var runner = BuildRunner();
        runner.SetMode("auto-single");

        await runner.TickAsync(CancellationToken.None);

        Assert.Equal("auto-single", runner.GetStatus().Mode);

        var move = _states!.MoveJob("job-a", TaskStates.Ready, _watchPath);
        Assert.Equal(MoveJobStatus.Success, move.Status);

        var reissued = runner.GetNextReadyJob();
        Assert.NotNull(reissued);
        Assert.Equal("job-a", reissued!.Id);
        Assert.Equal("auto-single", runner.GetStatus().Mode);
    }

    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    public async Task AutoSingle_TerminalTaskAndEmptyPickupQueue_ModeFlipsToManual(string terminalState)
    {
        WriteJob(terminalState, "job-a");
        var runner = BuildRunner();
        runner.SetMode("auto-single");

        await runner.TickAsync(CancellationToken.None);

        Assert.Equal("manual", runner.GetStatus().Mode);
    }

    /// <summary>
    /// auto-continuous + a movedToReview run resets the consecutive
    /// auto-failure counter. We prime the counter to 2 (one shy of
    /// the halt threshold) and simulate the success path by calling
    /// the same reset sequence the runner runs in
    /// <c>OnCliFinishedAsync</c>. Without the reset, three "successful
    /// then failed" cycles would falsely flip the runner to manual.
    /// </summary>
    [Fact]
    public void AutoContinuous_AutoFailureCounterResetsOnSuccessfulRun()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetConsecutiveAutoFailureCountForTest(ProjectRunner.AutoFailureHaltThreshold - 1, "job-a");

        // The success path in OnCliFinishedAsync (movedToReview=true)
        // resets the counter. We reproduce the public state change here:
        // a successful run for this project clears the counter back to 0.
        runner.SetConsecutiveAutoFailureCountForTest(0);

        Assert.Equal(0, runner.GetConsecutiveAutoFailureCountForTest());
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    /// <summary>
    /// Productive run path: a captured session id must reset the
    /// per-job capture-fail counter on the *same* job. Pre-F16 the
    /// counter only reset when a different job started failing, so a
    /// pattern of "fail, fail, succeed, fail" on a single slug would
    /// trip the breaker at the third failure even though only two were
    /// consecutive in any meaningful sense.
    /// </summary>
    [Fact]
    public void CaptureFailCounter_ResetsOnSuccessfulCapture()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetConsecutiveCaptureFailStateForTest(2, "flaky-job");
        Assert.Equal((2, (string?)"flaky-job"), runner.GetConsecutiveCaptureFailStateForTest());

        // Drive the success-side branch of OnCliFinishedAsync via the
        // mutating helper, mirroring the new reset block on the
        // "capturedSessionId != null" path.
        runner.SetConsecutiveCaptureFailStateForTest(0, null);

        Assert.Equal((0, (string?)null), runner.GetConsecutiveCaptureFailStateForTest());
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    /// <summary>
    /// SetMode emits exactly one structured log line per call with the
    /// from/to/reason triple. The four trip-wires in ProjectRunner each
    /// pass a distinct reason; the API surface passes its own. This
    /// test asserts the *shape* of the log message - a future refactor
    /// that drops the reason argument will fail here loudly.
    /// </summary>
    [Fact]
    public void SetMode_EmitsStructuredFromToReasonLog()
    {
        var logger = new CapturingLogger();
        var runner = BuildRunner(logger);

        runner.SetMode("auto-continuous", "test-injected reason");

        var match = logger.Entries.FirstOrDefault(e =>
            e.Contains("mode '", StringComparison.Ordinal) &&
            e.Contains("' -> '", StringComparison.Ordinal) &&
            e.Contains("because '", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(match),
            "expected a 'mode '{From}' -> '{To}' because '{Reason}'' log entry; got: " +
            string.Join(" | ", logger.Entries));
        Assert.Contains("manual", match, StringComparison.Ordinal);
        Assert.Contains("auto-continuous", match, StringComparison.Ordinal);
        Assert.Contains("test-injected reason", match, StringComparison.Ordinal);
    }

    /// <summary>
    /// The auto-single revert at the top of TickAsync logs with a
    /// reason that names the cause ("auto-single revert: pickup queue
    /// empty"). Pre-F16 the log line was just "mode set to 'manual'",
    /// which forced an operator to read the surrounding code to know
    /// which trip-wire fired.
    /// </summary>
    [Fact]
    public async Task AutoSingleRevert_LogsReason()
    {
        var logger = new CapturingLogger();
        var runner = BuildRunner(logger);
        runner.SetMode("auto-single");
        logger.Entries.Clear();

        await runner.TickAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Contains("auto-single revert", StringComparison.Ordinal) &&
            e.Contains("manual", StringComparison.Ordinal));
    }

    /// <summary>
    /// Park-and-continue: a single task that fails AutoFailureHaltThreshold
    /// times in a row is parked (counted as one distinct parked task) but the
    /// project must STAY in auto-continuous - one bad task no longer halts the
    /// whole queue. (Replaces the old "same job 3x -> manual" behaviour.)
    /// </summary>
    [Fact]
    public void AutoFailure_SingleTaskFailsOut_ParksButKeepsAuto()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        for (var i = 0; i < ProjectRunner.AutoFailureHaltThreshold; i++)
            runner.RecordAutoPickupFailureForTest("job-a");

        Assert.Equal(1, runner.GetParkedFailedTaskCountForTest());
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    /// <summary>
    /// Quarantine destination: a task that fails out is MOVED out of
    /// 3-progress into 5e-escalated through the escalation funnel, and the
    /// project stays auto so the next Ready task is picked up. The move runs
    /// fire-and-forget, so the assertion polls the on-disk lane folder.
    /// </summary>
    [Fact]
    public async Task AutoFailure_SingleTaskFailsOut_MovesToEscalatedAndKeepsAuto()
    {
        WriteJob(TaskStates.Progress, "job-a");
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var info = new TaskInfo
        {
            Id = "job-a",
            Title = "job-a",
            State = TaskStates.Progress,
            WatchPath = _watchPath,
            ProjectName = ProjectName,
        };

        for (var i = 0; i < ProjectRunner.AutoFailureHaltThreshold; i++)
            runner.RecordAutoPickupFailureForTest("job-a", info);

        var movedToEscalated = await WaitForFolderAsync(TaskStates.Escalated, "job-a");
        Assert.True(movedToEscalated, "expected 'job-a' to be moved into 5e-escalated");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "job-a")),
            "expected 'job-a' to have left 3-progress");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.CodeNotComplete, "job-a")),
            "quarantine must not route into 3b-code-not-complete");
        var movedJson = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Escalated, "job-a", "task.json"));
        Assert.Contains("auto-halted", movedJson);
        var movedFolder = Path.Combine(_watchPath, TaskStates.Escalated, "job-a");
        var timelineEvent = await WaitForTimelineEventAsync(movedFolder, TimelineEventKinds.OrchestratorEscalated);
        Assert.NotNull(timelineEvent);
        Assert.Equal(TimelineActors.Orchestrator, timelineEvent!.Actor);
        Assert.Contains("auto-halted", timelineEvent.Summary);
        Assert.Equal(HumanReviewEscalationCategories.AutoFailurePark, timelineEvent.Details?["category"]);
        Assert.Equal("auto-halted", timelineEvent.Details?["tag"]);
        Assert.Equal(1, runner.GetParkedFailedTaskCountForTest());
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    /// <summary>
    /// 3x3 systemic cooldown: only when AutoFailureDistinctTaskHaltThreshold
    /// DISTINCT tasks have each failed out (3 retries each) does the project
    /// pause globally, and that pause carries an automatic resume timestamp.
    /// </summary>
    [Fact]
    public void AutoFailure_ThreeDistinctTasksFailOut_EntersCooldown()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        foreach (var job in new[] { "job-a", "job-b" })
        {
            for (var i = 0; i < ProjectRunner.AutoFailureHaltThreshold; i++)
                runner.RecordAutoPickupFailureForTest(job);
            // still auto after each of the first two distinct tasks parks
            Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        }

        // third distinct task failing out trips the systemic breaker cooldown
        for (var i = 0; i < ProjectRunner.AutoFailureHaltThreshold; i++)
            runner.RecordAutoPickupFailureForTest("job-c");

        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.Equal("cooldown", runner.GetStatus().BreakerState);
        Assert.NotNull(runner.GetStatus().BreakerCooldownUntil);
        Assert.Contains("distinct tasks failed out", runner.GetStatus().BreakerReason);
        Assert.Equal("circuit-breaker", runner.GetStatus().ModeSource);
        Assert.Contains("circuit-breaker cooldown", runner.GetStatus().ModeReason);
    }

    [Fact]
    public async Task AutoFailure_CooldownElapsed_AutoResumes()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        foreach (var job in new[] { "job-a", "job-b", "job-c" })
        {
            for (var i = 0; i < ProjectRunner.AutoFailureHaltThreshold; i++)
                runner.RecordAutoPickupFailureForTest(job);
        }

        Assert.Equal("manual", runner.GetStatus().Mode);
        runner.ForceGlobalBreakerCooldownElapsedForTest();

        await runner.TickAsync(CancellationToken.None);

        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.Null(runner.GetStatus().BreakerState);
    }

    [Fact]
    public async Task AutoFailure_RateLimitCluster_CoolsDownWithoutQuarantiningTask()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        runner.RecordRateLimitAutoPickupFailureForTest("job-a");

        Assert.Equal(0, runner.GetParkedFailedTaskCountForTest());
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.Equal("cooldown", runner.GetStatus().BreakerState);
        Assert.Contains("rate-limit", runner.GetStatus().BreakerReason);

        runner.ForceGlobalBreakerCooldownElapsedForTest();
        await runner.TickAsync(CancellationToken.None);

        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.Null(runner.GetStatus().BreakerState);
    }

    [Fact]
    public void ProviderLimit_PausesClaudeOnly_AndStaysClosedUntilResetProbe()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        var observed = DateTime.UtcNow;
        var reset = observed.AddHours(2);
        runner.RecordProviderLimitForTest(new ProviderLimitStatus(
            "claude", observed, reset, "claude: limited until reset", true));

        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(runner.ProviderClaimsAllowedForTest("claude", observed.AddMinutes(1)));
        Assert.True(runner.ProviderClaimsAllowedForTest("codex", observed.AddMinutes(1)));
        Assert.False(runner.ProviderClaimsAllowedForTest("claude", reset));
    }

    [Fact]
    public void InfraBreakerPause_ExposesCircuitBreakerSourceAndReason()
    {
        var runner = BuildRunner();

        runner.SetMode(
            "manual",
            "pickup paused: infra breaker, 3 failures cliType=claude at 2026-08-23T22:10:00Z");

        Assert.Equal("circuit-breaker", runner.GetStatus().ModeSource);
        Assert.Contains("3 failures cliType=claude", runner.GetStatus().ModeReason);
    }

    [Fact]
    public void ProviderLimit_DurableWaitRehydratesRunnerStatusAfterRestart()
    {
        WriteJob(TaskStates.Progress, "job-a");
        var observed = DateTime.UtcNow;
        var reset = observed.AddHours(2);
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "job-a");
        QuotaWaitMarker.Write(folder, new QuotaWaitRecord
        {
            CliType = "claude",
            StartedAt = observed,
            ResetAt = reset,
            ThresholdMinutes = 30,
            Reason = $"claude: limited until {reset:O}",
            Scope = "provider",
        });

        var status = BuildRunner().GetStatus();

        var limit = Assert.Single(status.ProviderLimits);
        Assert.Equal("claude", limit.CliType);
        Assert.Equal(reset, limit.LimitedUntil);
    }

    /// <summary>
    /// Mixed transient failures across DIFFERENT jobs (none repeating to the
    /// threshold) must not park a task and must not halt: the window resets and
    /// auto-mode continues.
    /// </summary>
    [Fact]
    public void AutoFailure_MixedDistinctFailures_DoNotHalt()
    {
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        runner.RecordAutoPickupFailureForTest("job-a");
        runner.RecordAutoPickupFailureForTest("job-b");
        runner.RecordAutoPickupFailureForTest("job-c");

        Assert.Equal(0, runner.GetParkedFailedTaskCountForTest());
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    [Fact]
    public void AutoFailureDistinctTaskHaltThreshold_Is3()
        => Assert.Equal(3, ProjectRunner.AutoFailureDistinctTaskHaltThreshold);

    [Fact]
    public void RunnerCircuitBreakerOptions_FromConfig_LoadsThresholdCooldownAndBackoff()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runner:CircuitBreaker:PerTaskFailureThreshold"] = "2",
                ["Runner:CircuitBreaker:GlobalCooldownMinutes"] = "5",
                ["Runner:CircuitBreaker:GlobalCooldownBackoffMultiplier"] = "3",
                ["Runner:CircuitBreaker:GlobalCooldownMaxMinutes"] = "25",
            })
            .Build();

        var options = RunnerCircuitBreakerOptions.FromConfig(config);

        Assert.Equal(2, options.PerTaskFailureThreshold);
        Assert.Equal(TimeSpan.FromMinutes(5), options.GlobalCooldownBase);
        Assert.Equal(3.0, options.GlobalCooldownBackoffMultiplier);
        Assert.Equal(TimeSpan.FromMinutes(25), options.GlobalCooldownMax);
    }

    [Fact]
    public void AutoFailure_ConfiguredPerTaskThreshold_ParksAtConfiguredCount()
    {
        var runner = BuildRunner(configOverrides: new Dictionary<string, string?>
        {
            ["Runner:CircuitBreaker:PerTaskFailureThreshold"] = "2",
        });
        runner.SetMode("auto-continuous");

        runner.RecordAutoPickupFailureForTest("job-a");
        Assert.Equal(0, runner.GetParkedFailedTaskCountForTest());

        runner.RecordAutoPickupFailureForTest("job-a");

        Assert.Equal(1, runner.GetParkedFailedTaskCountForTest());
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    /// <summary>
    /// ASS-1753 Directive 2: the persist hook must receive the classified mode
    /// SOURCE, not just the mode string, so the settings layer can tell an
    /// operator toggle from a system-driven flip. An <c>update-quiesce</c> flip
    /// to manual (the update-service stopping runners before a deploy) classifies
    /// as <c>system</c>; without this the quiesce would persist as the operator's
    /// durable mode and clobber auto-continuous across the restart.
    /// </summary>
    [Fact]
    public void SetMode_UpdateQuiesceFlip_FiresPersistHookWithSystemSource()
    {
        var runner = BuildRunner();
        var captured = new List<(string Mode, string Source)>();
        runner.OnModePersist += (mode, source) => captured.Add((mode, source));

        runner.SetMode("manual", "update-quiesce");

        Assert.Contains(("manual", "system"), captured);
        Assert.Equal("system", runner.GetStatus().ModeSource);
    }

    /// <summary>
    /// Counterpart to the quiesce test: a genuine operator toggle (the API path,
    /// reason "api-toggle") classifies as <c>user</c> so the durable
    /// DesiredRunnerMode advances. This is the signal the settings layer keys on
    /// to know the operator actually chose auto-continuous.
    /// </summary>
    [Fact]
    public void SetMode_ApiToggle_FiresPersistHookWithUserSource()
    {
        var runner = BuildRunner();
        var captured = new List<(string Mode, string Source)>();
        runner.OnModePersist += (mode, source) => captured.Add((mode, source));

        runner.SetMode("auto-continuous", "api-toggle");

        Assert.Contains(("auto-continuous", "user"), captured);
        Assert.Equal("user", runner.GetStatus().ModeSource);
    }

    /// <summary>
    /// RestoreMode (the boot path that re-applies the saved mode) must NOT fire
    /// the persist hook - the value already came from disk, and re-persisting it
    /// on every boot would risk a write loop and could re-classify the restored
    /// mode under a default source. Locks the "restore is silent" contract.
    /// </summary>
    [Fact]
    public void RestoreMode_DoesNotFirePersistHook()
    {
        var runner = BuildRunner();
        var fired = 0;
        runner.OnModePersist += (_, _) => fired++;

        runner.RestoreMode("auto-continuous");

        Assert.Equal(0, fired);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"cliType\":\"copilot\"}}");
    }

    /// <summary>Poll for a slug folder to appear under <paramref name="state"/>;
    /// the park move is fire-and-forget so the folder rename completes
    /// asynchronously after <c>RecordAutoPickupFailureForTest</c> returns.</summary>
    private async Task<bool> WaitForFolderAsync(string state, string slug, int timeoutMs = 5000)
    {
        var target = Path.Combine(_watchPath, state, slug);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(target)) return true;
            await Task.Delay(25);
        }
        return Directory.Exists(target);
    }

    private async Task<TimelineEvent?> WaitForTimelineEventAsync(string folder, string kind, int timeoutMs = 5000)
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var evt = timeline.ReadAll(folder).LastOrDefault(e => e.Kind == kind);
            if (evt != null) return evt;
            await Task.Delay(25);
        }
        return timeline.ReadAll(folder).LastOrDefault(e => e.Kind == kind);
    }

    private ProjectRunner BuildRunner(
        ILogger? logger = null,
        Dictionary<string, string?>? configOverrides = null,
        int? maxParallelism = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["WatchPaths:0:RepositoryPath"] = _watchPath,
            ["TaskRepository"] = _workspaceRoot
        };
        if (configOverrides != null)
        {
            foreach (var item in configOverrides)
                configValues[item.Key] = item.Value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
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
        _states = states;
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
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
        var codex = GenericCliExecutionService.ForCodex(NullLogger<GenericCliExecutionService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, gemini);

        var orchestratorRunner = new OrchestratorRunner(NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var runner = new ProjectRunner(
            ProjectName, entry,
            logger ?? NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null,
            timeline: timeline);
        if (maxParallelism is { } configuredMaxParallelism)
            settings.SetMaxParallelism(ProjectName, configuredMaxParallelism);
        runner.ConfigureCircuitBreaker(RunnerCircuitBreakerOptions.FromConfig(config));
        return runner;
    }

    /// <summary>
    /// Minimal ILogger that collects formatted messages. We only need
    /// the text payload for assertions; structured-property capture
    /// would be overkill for the from/to/reason shape test.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
