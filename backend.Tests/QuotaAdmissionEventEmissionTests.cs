using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2055 requirement 3 ("+ Feed-Zeile", never a silent switch) and
/// requirement 7 ("LOG-EVENTS fuer alles ... mit Zahlen"): the pre-launch
/// load-steering decision must be teed to the task timeline AND the global
/// orchestrator feed (the data source of the load-distribution view), carrying
/// the burn-rate / projection numbers - while the healthy "launch primary"
/// decision stays a silent, log-only normal path. This exercises the real
/// <see cref="ProjectRunner"/> emit wiring (<c>EmitQuotaAdmissionDecision</c>)
/// end-to-end onto disk, not just the pure planner.
/// </summary>
public sealed class QuotaAdmissionEventEmissionTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly string _repoRoot;

    public QuotaAdmissionEventEmissionTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-admission-emit-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        _repoRoot = Path.Combine(_workspaceRoot, "repos", ProjectName);
        Directory.CreateDirectory(_repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── req 3 + 7: a quiet-wait steering decision reaches both surfaces ──
    [Fact]
    public void WaitDecision_WritesTimelineAndLoadDistributionFeed()
    {
        var (runner, timeline) = BuildRunner();
        var info = MakeJob("wait-job");
        var reset = new DateTime(2026, 7, 10, 16, 59, 0, DateTimeKind.Utc);
        var plan = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.Wait, "claude", Model: null, ThinkingLevel: null,
            IsFallback: false, Reason: "waiting: all quotas exhausted (claude Weekly at 100%), next reset 16:59",
            NextResetAt: reset, Projection: null);

        Emit(runner, info, plan);

        var events = timeline.ReadAll(info.FolderPath);
        var evt = Assert.Single(events, e => e.Kind == TimelineEventKinds.QuotaAdmissionDecision);
        Assert.Contains("all quotas exhausted", evt.Summary);
        Assert.Equal("Wait", evt.Details!["outcome"]);

        var feed = ReadFeed().Where(f => f.JobId == info.Id).ToList();
        var line = Assert.Single(feed, f => f.Topic == OrchestratorLogTopics.LoadDistribution);
        Assert.Equal(OrchestratorLogKinds.Decision, line.Kind);
        Assert.Contains("all quotas exhausted", line.Summary);
        Assert.Contains("next reset", line.Reasoning);   // remaining-time hint even without a projection
    }

    // ── req 3 + 7: a pre-start model switch is documented, with the numbers ──
    [Fact]
    public void SwitchDecision_DocumentsPreLaunchSwitch_WithProjectionNumbers()
    {
        var (runner, timeline) = BuildRunner();
        var info = MakeJob("switch-job");
        var reset = new DateTime(2026, 7, 10, 16, 59, 0, DateTimeKind.Utc);
        var projection = new QuotaProjection(
            WindowLabel: "5-hour", CurrentUsedPct: 60, ProjectedUsedPct: 120,
            BurnRatePctPerHour: 24, HoursRemaining: 2.5, CapPct: 95,
            ResetAt: reset, BreachesBeforeReset: true);
        var plan = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchFallback, "codex", "gpt-5.3-codex", ThinkingLevel: null,
            IsFallback: true,
            Reason: "model switched pre-launch: claude -> codex/gpt-5.3-codex, reason: claude 5-hour projected to reach the 95% cap before reset",
            NextResetAt: reset, Projection: projection);

        Emit(runner, info, plan);

        var evt = Assert.Single(
            timeline.ReadAll(info.FolderPath), e => e.Kind == TimelineEventKinds.QuotaAdmissionDecision);
        Assert.Contains("model switched pre-launch", evt.Summary);
        Assert.Equal("true", evt.Details!["isFallback"]);
        Assert.Equal("codex", evt.Details["cli"]);

        var line = Assert.Single(
            ReadFeed().Where(f => f.JobId == info.Id), f => f.Topic == OrchestratorLogTopics.LoadDistribution);
        Assert.Contains("model switched pre-launch", line.Summary);
        // The numbers behind the decision (req 7): burn rate, remaining budget, time.
        Assert.Contains("burn", line.Reasoning);
        Assert.Contains("%/h", line.Reasoning);
        Assert.Contains("budget left", line.Reasoning);
        Assert.Contains("to reset", line.Reasoning);
    }

    // The healthy normal launch stays silent on the task surface but remains
    // an admission boundary in the durable feed for usage attribution.
    [Fact]
    public void LaunchPrimaryDecision_IsSilentOnTimelineAndPersistsBoundary()
    {
        var (runner, timeline) = BuildRunner();
        var info = MakeJob("primary-job");
        var plan = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchPrimary, "claude", "claude-opus", ThinkingLevel: null,
            IsFallback: false, Reason: "launch: quota ok", NextResetAt: null, Projection: null);

        Emit(runner, info, plan);

        Assert.Empty(timeline.ReadAll(info.FolderPath));
        var boundary = Assert.Single(ReadFeed(), f => f.JobId == info.Id);
        Assert.Null(boundary.BetterCandidates);
    }

    [Fact]
    public void BetterCandidateDecision_ProjectsNoteToTimelineAndFeedWithoutSwitchingRoute()
    {
        var (runner, timeline) = BuildRunner();
        var info = MakeJob("candidate-job");
        var note = CandidateNote();
        var plan = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchPrimary, "codex", "gpt-5.6-sol", "max",
            IsFallback: false, Reason: "launch: quota ok", NextResetAt: null, Projection: null)
        {
            BetterCandidates = note,
        };

        Emit(runner, info, plan);

        Assert.Equal("gpt-5.6-sol", plan.Model);
        Assert.False(plan.IsFallback);
        var evt = Assert.Single(timeline.ReadAll(info.FolderPath));
        Assert.Contains("gpt-6-astra", evt.Details!["betterCandidates"]);
        Assert.Equal(note.MatrixUrl, evt.Details["matrixUrl"]);
        var feed = Assert.Single(ReadFeed(), entry => entry.JobId == info.Id);
        Assert.Equal("gpt-6-astra", Assert.Single(feed.BetterCandidates!.Candidates).Model);
    }

    [Fact]
    public void SuspiciousProjection_LaunchesPrimaryButEmitsDiagnosticWarning()
    {
        var (runner, timeline) = BuildRunner();
        var info = MakeJob("projection-warning-job");
        var reset = new DateTime(2026, 7, 11, 16, 9, 0, DateTimeKind.Utc);
        var start = new DateTime(2026, 7, 11, 11, 9, 0, DateTimeKind.Utc);
        var warning = new QuotaProjectionWarning(
            "5-hour", "5-hour projected/used ratio exceeds 4 near window start",
            reset, start, 0.2, 19, 95);
        var plan = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchPrimary, "codex", "gpt-5.3-codex", null,
            false, "launch: quota projection ignored", null, null, warning);

        Emit(runner, info, plan);

        var evt = Assert.Single(timeline.ReadAll(info.FolderPath));
        Assert.Equal("0.2", evt.Details!["elapsedFraction"]);
        Assert.Equal(reset.ToString("o"), evt.Details["resetAt"]);
        Assert.Equal(start.ToString("o"), evt.Details["assumedStart"]);
        Assert.Contains("ratio exceeds 4", evt.Details["projectionWarning"]);
        var line = Assert.Single(ReadFeed().Where(f => f.JobId == info.Id));
        Assert.Contains("projection ignored", line.Reasoning);
    }

    // ── MS-5: the local launch asks the selection service ──
    [Fact]
    public void Local_launch_admission_asks_the_model_selection_service()
    {
        var spy = new SpyModelSelectionService();
        var (runner, _) = BuildRunner(spy);
        var info = MakeJob("spy-job") with { Model = "claude-opus-5", ThinkingLevel = "high", TaskType = "feature" };

        var method = typeof(ProjectRunner).GetMethod(
            "PlanQuotaAdmission", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ProjectRunner), "PlanQuotaAdmission");
        var plan = (QuotaAdmissionPlan)method.Invoke(runner, new object[] { info })!;

        var call = Assert.Single(spy.Calls);
        Assert.Equal("local-coding-launch", call.ExecutionPath);
        Assert.Equal(AgentStudio.TaskServer.Contracts.SelectionConsumers.Coding, call.Request.Consumer);
        Assert.Equal(ProjectName, call.Request.Project);
        Assert.Equal("spy-job", call.Request.TaskKey);
        Assert.Equal(new AgentStudio.TaskServer.Contracts.SelectionPins("claude", "claude-opus-5", "high"), call.Request.Pins);
        Assert.Equal("feature", call.Request.TaskType);
        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
        Assert.Equal("launch: spy", plan.Reason);
    }

    [Fact]
    public void Selection_ids_support_parallel_admission_writes_and_timeline_reads()
    {
        var (runner, _) = BuildRunner();
        var field = typeof(ProjectRunner).GetField("_selectionIdsByJob",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ids = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string, string>>(
            field.GetValue(runner));
        Parallel.For(0, 1_000, index =>
        {
            var key = $"job-{index % 32}";
            ids[key] = $"selection-{index}";
            Assert.NotNull(ids.GetValueOrDefault(key));
        });
        Assert.Equal(32, ids.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static void Emit(ProjectRunner runner, TaskInfo info, QuotaAdmissionPlan plan)
    {
        var method = typeof(ProjectRunner).GetMethod(
            "EmitQuotaAdmissionDecision", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ProjectRunner), "EmitQuotaAdmissionDecision");
        method.Invoke(runner, new object[] { info, plan });
    }

    private List<OrchestratorLogEntry> ReadFeed()
    {
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        return log.Read(_watchPath);
    }

    private TaskInfo MakeJob(string id)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Ready, id);
        Directory.CreateDirectory(folder);
        return new TaskInfo
        {
            Id = id,
            Title = id,
            State = TaskStates.Ready,
            FolderPath = folder,
            WatchPath = _watchPath,
            ProjectName = ProjectName,
            CliType = "claude",
        };
    }

    private static BetterCandidateNote CandidateNote() => new()
    {
        CurrentModel = "gpt-5.6-sol",
        CurrentThinkingLevel = "max",
        CapabilityClass = "CodingAgent",
        EvidenceSnapshot = "1:deepswe-v1.1:1:2026-09-02:2",
        EvaluatedAtUtc = new DateTime(2026, 9, 12, 23, 0, 0, DateTimeKind.Utc),
        MatrixUrl = BetterCandidateService.MatrixUrl,
        Candidates =
        [
            new BetterCandidate
            {
                Model = "gpt-6-astra",
                BenchmarkType = "deepswe-v1.1",
                BenchmarkName = "DeepSWE v1.1",
                ScoreDelta = 1.1m,
                CostDeltaUsd = -4.96m,
                EvidenceAgeDays = 10,
            },
        ],
    };

    private (ProjectRunner Runner, TimelineLog Timeline) BuildRunner(IModelSelectionService? modelSelection = null)
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
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
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
        var quotaFallback = new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        var runner = new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess,
            timeline: timeline, quotaFallback: quotaFallback,
            localCodingAdmission: AllowLocalCodingForTests.Instance,
            modelSelection: modelSelection);

        return (runner, timeline);
    }
}
