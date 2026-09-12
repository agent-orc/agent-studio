using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using System.Reflection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ADR-0052 Slice 2: the runner must read each project's
/// <c>MaxParallelism</c> and surface the slot picture on its status so the
/// FE Overview/Timeline can render occupancy + the pick decision without
/// re-deriving it. This is the additive, zero-control-flow-change layer:
/// at <c>MaxParallelism == 1</c> the runner behaves byte-for-byte as before
/// (single sequential slot), so we only assert the *surface* here. The
/// concurrent N-slot execution rewrite is the documented supervised-landing
/// remainder; the pure pick-gate it will call is covered by
/// <c>ParallelSlotPolicyTests</c>.
/// </summary>
public sealed class RunnerSlotWiringTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public RunnerSlotWiringTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-runner-slot-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        _repoRoot = Path.Combine(_workspaceRoot, "repos", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void GetStatus_DefaultsToSingleIdleSlot()
    {
        var (runner, _) = BuildRunner();

        var status = runner.GetStatus();

        Assert.Equal(1, status.MaxParallelism);
        Assert.Equal(0, status.OccupiedSlots);
        Assert.Null(status.LastPickReason);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void GetStatus_ReflectsConfiguredMaxParallelism(int configured)
    {
        var (runner, settings) = BuildRunner();
        settings.SetMaxParallelism(ProjectName, configured);

        var status = runner.GetStatus();

        Assert.Equal(configured, status.MaxParallelism);
        Assert.Equal(0, status.OccupiedSlots);
    }

    /// <summary>
    /// ASS-1753 Directive 1: a backend restart clears the in-memory slot
    /// registry, but the CLI router can still own live runs. Re-booking each
    /// recovered live run must make <c>OccupiedSlots</c> count the genuinely
    /// live runs again - the desync the operator saw was occupied=1 while
    /// three runs were alive. Booking is additive and idempotent: a second
    /// call for the same job is a no-op (no double-count), and two distinct
    /// recovered runs occupy two slots.
    /// </summary>
    [Fact]
    public void RegisterRecoveredRun_BooksLiveRunsIntoSlots_OccupancyMatchesLiveRuns()
    {
        SeedProgressTask("t6a");
        SeedProgressTask("t6b");
        SeedProgressTask("t6c");
        var (runner, settings) = BuildRunner();
        settings.SetMaxParallelism(ProjectName, 3);
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);

        var bookedFirst = runner.RegisterRecoveredRun("t6a", "claude");

        Assert.True(bookedFirst, "first recovery booking must claim a fresh slot");
        Assert.Equal(1, runner.GetStatus().OccupiedSlots);

        // Idempotent: re-booking the same live run must not double-count it.
        var bookedAgain = runner.RegisterRecoveredRun("t6a", "claude");

        Assert.False(bookedAgain, "re-booking an already-booked run must be a no-op");
        Assert.Equal(1, runner.GetStatus().OccupiedSlots);

        // A second genuinely-distinct live run occupies a second slot, so the
        // post-restart occupancy reflects the real number of live runs.
        Assert.True(runner.RegisterRecoveredRun("t6b", "claude"));
        Assert.True(runner.RegisterRecoveredRun("t6c", "codex"));
        Assert.Equal(3, runner.GetStatus().OccupiedSlots);
    }

    /// <summary>
    /// ASS-1753 Directive 1 + 3 together: once a recovered run is re-booked,
    /// the same in-memory facts the endpoint reads (<see cref="ProjectRunner.GetRunActivity"/>)
    /// must classify the task as <c>active</c> - so the 3-progress badge reads
    /// "Run aktiv" instead of the false "kein aktiver Run" the operator saw.
    /// </summary>
    [Fact]
    public void RecoveredRun_ClassifiesAsContinuingAfterRestart()
    {
        SeedProgressTask("t6a");
        var (runner, _) = BuildRunner();

        // Before recovery the slot is empty and the classifier paints the
        // orphan as no-active-run.
        var before = TaskRunActivityClassifier.Classify(
            runner.GetRunActivity("t6a"), execution: null, outcomeIssue: null, DateTime.UtcNow);
        Assert.Equal(TaskRunActivityKinds.NoActiveRun, before.Kind);

        runner.RegisterRecoveredRun("t6a", "claude");

        var after = TaskRunActivityClassifier.Classify(
            runner.GetRunActivity("t6a"), execution: null, outcomeIssue: null, DateTime.UtcNow);
        Assert.Equal(TaskRunActivityKinds.ContinuingAfterRestart, after.Kind);
    }

    [Fact]
    public void RegisterRecoveredRun_RejectsBlankJobId()
    {
        var (runner, _) = BuildRunner();

        Assert.False(runner.RegisterRecoveredRun("", "claude"));
        Assert.False(runner.RegisterRecoveredRun("   ", "claude"));
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);
    }

    private void SeedProgressTask(string id)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            $"{{\"id\":\"{id}\",\"title\":\"Recovered {id}\",\"state\":\"3-progress\",\"agent\":\"codex\",\"cliType\":\"codex\"}}");
    }

    [Fact]
    public void DeferredManual_FlipsOnlyAfterLastTaskFromActiveSnapshotFinishes()
    {
        var (runner, settings) = BuildRunner();
        settings.SetMaxParallelism(ProjectName, 3);
        runner.SetMode("auto-continuous");
        runner.SetActiveJobForTest("task-a");
        runner.SetActiveJobForTest("task-b");

        var request = runner.RequestModeChange("manual", "api-toggle");

        Assert.Equal(ModeChangeOutcome.Deferred, request.Outcome);
        Assert.Equal(2, runner.GetStatus().PendingModeActiveTaskCount);
        Assert.Null(runner.GetStatus().PendingModeWillApplyAfter);

        Assert.True(runner.CompleteActiveJobForTest("task-a"));
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.Equal("manual", runner.GetStatus().PendingMode);
        Assert.Equal(1, runner.GetStatus().PendingModeActiveTaskCount);
        Assert.Equal("task-b", runner.GetStatus().PendingModeWillApplyAfter);

        Assert.True(runner.CompleteActiveJobForTest("task-b"));
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.Null(runner.GetStatus().PendingMode);
        Assert.Equal(0, runner.GetStatus().PendingModeActiveTaskCount);
    }

    [Fact]
    public async Task DeferredManual_WithFreeSlotAndReadyCandidate_DoesNotPick()
    {
        var (runner, settings) = BuildRunner();
        settings.SetMaxParallelism(ProjectName, 2);
        runner.SetMode("auto-continuous");
        var activeFolder = Path.Combine(_watchPath, TaskStates.Progress, "task-active");
        Directory.CreateDirectory(activeFolder);
        File.WriteAllText(Path.Combine(activeFolder, "task.json"),
            "{\"id\":\"task-active\",\"title\":\"Active task\",\"state\":\"3-progress\",\"agent\":\"codex\",\"cliType\":\"codex\"}");
        runner.SetActiveJobForTest("task-active");
        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "task-ready");
        Directory.CreateDirectory(readyFolder);
        File.WriteAllText(Path.Combine(readyFolder, "task.json"),
            "{\"id\":\"task-ready\",\"title\":\"Ready candidate\",\"state\":\"2-ready\",\"agent\":\"codex\",\"cliType\":\"codex\"}");

        runner.RequestModeChange("manual", "api-toggle");
        await runner.TickAsync(CancellationToken.None);

        Assert.Equal(1, runner.GetStatus().OccupiedSlots);
        Assert.Equal("manual", runner.GetStatus().PendingMode);
        Assert.True(Directory.Exists(readyFolder));
    }

    [Fact]
    public void ActiveRunRecord_DoesNotOccupySlot_AfterCliProcessExits()
    {
        var runs = new ActiveRuns();
        Assert.True(runs.TryClaim(new ActiveRun { JobId = "live", Intent = RunIntent.AutoPickup }));
        Assert.Equal(1, runs.Count);

        Assert.True(runs.ReleaseExecutionSlot("live"));

        Assert.Equal(0, runs.Count);
        Assert.True(runs.HasInFlight);
        Assert.True(runs.Contains("live"));
        Assert.False(runs.HoldsExecutionSlot("live"));
        Assert.Null(runs.SingleJobId);
        Assert.Empty(runs.RunningTasks());
        Assert.True(runs.HasFreeSlot(1));
        Assert.False(runs.ReleaseExecutionSlot("live"));
    }

    [Fact]
    public async Task ExecutionSlotRelease_IsObservedExactlyOnce_WhenFinishSignalsRace()
    {
        var runs = new ActiveRuns();
        Assert.True(runs.TryClaim(new ActiveRun { JobId = "live", Intent = RunIntent.AutoPickup }));

        var releases = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => runs.ReleaseExecutionSlot("live"))));

        Assert.Single(releases, released => released);
        Assert.Equal(0, runs.Count);
        Assert.True(runs.Contains("live"));
    }

    [Fact]
    public void RenderPrompt_ForWorktreeRun_RewritesMainCheckoutPathsAndAddsContainmentNotice()
    {
        var (runner, _) = BuildRunner();
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "fix-worktree-paths");
        Directory.CreateDirectory(jobFolder);
        var promptPath = Path.Combine(jobFolder, "prompt.md");
        var authoredPrompt =
            $"Edit {_repoRoot}\\backend\\Services\\Tasks\\TaskStateMachine.cs and run git -C {_repoRoot} status.";
        File.WriteAllText(promptPath, authoredPrompt);
        var worktree = Path.Combine(_workspaceRoot, "worktrees", "fix-worktree-paths");
        Directory.CreateDirectory(worktree);
        var enrichmentContext =
            $"## Prompt enrichment\n\nInspect {_repoRoot}\\docs before changing the runner.";
        var enrichment = new PromptEnrichmentPreparation(
            authoredPrompt,
            PromptEnrichmentService.AppendContext(authoredPrompt, enrichmentContext),
            enrichmentContext,
            new PromptEnrichmentReport());
        var info = new TaskInfo
        {
            Id = "fix-worktree-paths",
            Title = "Fix worktree paths",
            State = TaskStates.Progress,
            FolderPath = jobFolder,
            WatchPath = _watchPath,
            ProjectName = ProjectName,
            Mode = TaskModes.Planning
        };
        var plan = new RunPlan(
            PromptTemplate: RuntimePromptService.RunnerFreshStart,
            PromptVariables: new Dictionary<string, string?>
            {
                ["prompt_path"] = promptPath,
                ["job_folder"] = jobFolder,
                ["user_followup"] = null
            },
            PromptOverride: null,
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "start",
            EventReason: null,
            EventInputSessionId: null,
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

        var rendered = InvokeRenderPrompt(runner, plan, info, worktree, enrichment);

        Assert.Contains("## Worktree containment", rendered);
        Assert.Contains("## Prompt enrichment", rendered);
        Assert.Contains(worktree, rendered);
        Assert.DoesNotContain($"git -C {_repoRoot} status", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"Edit {_repoRoot}\\backend", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"Inspect {_repoRoot}\\docs", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"git -C {worktree} status", rendered);
        Assert.Contains($"Inspect {worktree}\\docs", rendered);
        Assert.Contains($"Working directory: `{worktree}`", rendered);
        Assert.Contains($"Git repository for status/diff/commits: `{worktree}`", rendered);
        var containmentIndex = rendered.IndexOf("## Worktree containment", StringComparison.Ordinal);
        var authoredIndex = rendered.IndexOf("Edit ", StringComparison.Ordinal);
        var modeFramingIndex = rendered.IndexOf("Read-only run", StringComparison.Ordinal);
        var enrichmentIndex = rendered.IndexOf("## Prompt enrichment", StringComparison.Ordinal);
        Assert.True(
            containmentIndex == 0
            && authoredIndex > containmentIndex
            && modeFramingIndex > authoredIndex
            && enrichmentIndex > modeFramingIndex);
    }

    [Fact]
    public void RenderPrompt_FreshStart_PrependsIntakeEnrichedContext()
    {
        var (runner, _) = BuildRunner();
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "git-runner-context");
        Directory.CreateDirectory(jobFolder);
        var promptPath = Path.Combine(jobFolder, "prompt.md");
        File.WriteAllText(promptPath, "Original task body: update runner git handling.");
        var intakeDir = Path.Combine(jobFolder, "intake");
        Directory.CreateDirectory(intakeDir);
        File.WriteAllText(Path.Combine(intakeDir, "enriched-context.md"),
            "## Intake-enriched context\n\n- **Keep git handling in the backend** (`git-handling-api-not-cli`)\n  Git lifecycle work belongs in API/backend orchestration.");

        var info = new TaskInfo
        {
            Id = "git-runner-context",
            Title = "Update runner git handling",
            State = TaskStates.Progress,
            FolderPath = jobFolder,
            WatchPath = _watchPath,
            ProjectName = ProjectName
        };
        var plan = new RunPlan(
            PromptTemplate: RuntimePromptService.RunnerFreshStart,
            PromptVariables: new Dictionary<string, string?>
            {
                ["prompt_path"] = promptPath,
                ["job_folder"] = jobFolder,
                ["user_followup"] = null
            },
            PromptOverride: null,
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "start",
            EventReason: null,
            EventInputSessionId: null,
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

        var rendered = InvokeRenderPrompt(runner, plan, info, _repoRoot);

        Assert.Contains("## Intake-enriched context", rendered);
        Assert.Contains("git-handling-api-not-cli", rendered);
        var enrichedIndex = rendered.IndexOf("## Intake-enriched context", StringComparison.Ordinal);
        var originalIndex = rendered.IndexOf("Original task body", StringComparison.Ordinal);
        Assert.True(enrichedIndex >= 0 && originalIndex > enrichedIndex);
    }

    [Fact]
    public void RenderPrompt_ForWorktreePromptOverride_StillAddsContainmentNotice()
    {
        var (runner, _) = BuildRunner();
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "fix-worktree-override-paths");
        Directory.CreateDirectory(jobFolder);
        var worktree = Path.Combine(_workspaceRoot, "worktrees", "fix-worktree-override-paths");
        Directory.CreateDirectory(worktree);
        var info = new TaskInfo
        {
            Id = "fix-worktree-override-paths",
            Title = "Fix worktree override paths",
            State = TaskStates.Progress,
            FolderPath = jobFolder,
            WatchPath = _watchPath,
            ProjectName = ProjectName
        };
        var plan = new RunPlan(
            PromptTemplate: null,
            PromptVariables: new Dictionary<string, string?>(),
            PromptOverride: $"Run tests in {_repoRoot} and inspect {_repoRoot}\\backend.",
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "start",
            EventReason: null,
            EventInputSessionId: null,
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

        var rendered = InvokeRenderPrompt(runner, plan, info, worktree);

        Assert.Contains("## Worktree containment", rendered);
        Assert.Contains(worktree, rendered);
        Assert.DoesNotContain($"Run tests in {_repoRoot}", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"inspect {_repoRoot}\\backend", rendered, StringComparison.OrdinalIgnoreCase);
    }

    private (ProjectRunner Runner, ProjectSettingsService Settings) BuildRunner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _repoRoot,
            RepositoryPath = _repoRoot
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
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

        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        var runner = new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null);

        return (runner, settings);
    }

    private static string InvokeRenderPrompt(
        ProjectRunner runner,
        RunPlan plan,
        TaskInfo info,
        string runWorkingDir,
        PromptEnrichmentPreparation? promptEnrichment = null)
    {
        var method = typeof(ProjectRunner).GetMethod("RenderPrompt", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ProjectRunner), "RenderPrompt");
        return (string)method.Invoke(
            runner,
            new object?[] { plan, info, runWorkingDir, promptEnrichment })!;
    }
}
