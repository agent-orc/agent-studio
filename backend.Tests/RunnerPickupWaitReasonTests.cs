using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3579: the runner's own reasons for not picking a Ready card reach the
/// status DTO as typed facts, and the live-status projection names them on the
/// card. Before this the mode and slot gates returned without any fact and the
/// candidate skips wrote only the free-text <c>LastPickReason</c>, which no
/// frontend file read.
/// </summary>
public sealed class RunnerPickupWaitReasonTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly string _repoRoot;

    public RunnerPickupWaitReasonTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-pickup-wait-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("manual", RunnerPickupWaitCodes.ManualMode, "Auto-pickup is off")]
    [InlineData("paused", RunnerPickupWaitCodes.Paused, "The runner is paused")]
    public async Task ModeGate_IsRecordedAndNamedOnEveryQueuedCard(string mode, string code, string detail)
    {
        WriteJob(TaskStates.Ready, "first", order: 1);
        WriteJob(TaskStates.Ready, "second", order: 2);
        var runner = BuildRunner();
        runner.SetMode(mode);

        await runner.TickAsync(CancellationToken.None);

        var status = runner.GetStatus();
        Assert.Equal(code, status.PickupGate?.Code);
        Assert.Contains(detail, status.PickupGate!.Detail);
        Assert.Empty(status.PickupSkips);
        foreach (var (job, position) in new[] { ("first", 1), ("second", 2) })
        {
            var queue = TaskLiveStatusProjection.RunnerQueueFor(job, status);
            Assert.NotNull(queue);
            Assert.Equal(code, queue!.ReasonCode);
            Assert.Equal(status.PickupGate.Detail, queue.Reason);
            Assert.Equal(position, queue.Position);
        }
    }

    [Fact]
    public async Task SlotGate_FullSlotsAreRecordedWithTheSlotCount()
    {
        WriteJob(TaskStates.Progress, "already-running", order: 1);
        WriteJob(TaskStates.Ready, "waiting", order: 1);
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetActiveJobForTest("already-running", "codex");

        await runner.TickAsync(CancellationToken.None);

        var status = runner.GetStatus();
        Assert.Equal(1, status.OccupiedSlots);
        Assert.Equal(1, status.MaxParallelism);
        Assert.Equal(RunnerPickupWaitCodes.SlotsFull, status.PickupGate?.Code);
        Assert.Equal("All 1 runner slot is occupied.", status.PickupGate!.Detail);
        Assert.Equal(RunnerPickupWaitCodes.SlotsFull, TaskLiveStatusProjection.RunnerQueueFor("waiting", status)?.ReasonCode);
    }

    [Fact]
    public async Task QuotaDefer_EverySkippedCandidateCarriesThePlannerReason()
    {
        WriteJob(TaskStates.Ready, "first", order: 1);
        WriteJob(TaskStates.Ready, "second", order: 2);
        var runner = BuildRunner(modelSelection: new WaitingModelSelection("codex 5h window exhausted until the next reset"));
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        var status = runner.GetStatus();
        Assert.Null(status.PickupGate);
        Assert.Equal(new[] { "first", "second" }, status.PickupSkips.Select(skip => skip.JobId));
        Assert.All(status.PickupSkips, skip =>
        {
            Assert.Equal(RunnerPickupWaitCodes.QuotaDefer, skip.Code);
            Assert.Equal("codex 5h window exhausted until the next reset", skip.Detail);
        });
        var queue = TaskLiveStatusProjection.RunnerQueueFor("second", status);
        Assert.Equal(RunnerPickupWaitCodes.QuotaDefer, queue?.ReasonCode);
        Assert.Equal(2, queue?.Position);
        Assert.StartsWith("quota-defer: second:", status.LastPickReason);
    }

    [Fact]
    public async Task LoadThrottle_SkipDetailCarriesNoPerSampleNumbers()
    {
        WriteJob(TaskStates.Ready, "first", order: 1);
        var throttle = new FixedLoadThrottle(new LoadThrottleDecision(true, 97.3, TimeSpan.FromSeconds(75)));
        var runner = BuildRunner(loadThrottle: throttle);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);
        var first = Assert.Single(runner.GetStatus().PickupSkips);
        throttle.Current = new LoadThrottleDecision(true, 99.1, TimeSpan.FromSeconds(90));
        await runner.TickAsync(CancellationToken.None);
        var second = Assert.Single(runner.GetStatus().PickupSkips);

        Assert.Equal(RunnerPickupWaitCodes.LoadThrottle, first.Code);
        // An unchanged wait keeps the board card bytes (and its ETag) stable.
        Assert.Equal(first, second);
        Assert.DoesNotContain("97", first.Detail);
    }

    [Fact]
    public async Task ProviderLimit_SkipNamesTheCliAndTheResetTime()
    {
        WriteJob(TaskStates.Ready, "first", order: 1);
        // Far in the future, so the reset never passes and no recovery probe starts.
        var until = new DateTime(2999, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        var limits = new ProviderLimitRegistry();
        limits.Record(new ProviderLimitStatus("codex", until.AddHours(-5), until, "usage limit", ResetTimeReported: true));
        var runner = BuildRunner(providerLimits: limits);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        var skip = Assert.Single(runner.GetStatus().PickupSkips);
        Assert.Equal(RunnerPickupWaitCodes.ProviderLimit, skip.Code);
        Assert.Equal($"The codex account is limited until {until:O}.", skip.Detail);
    }

    [Fact]
    public async Task ClosedGate_HidesSkipsFromTheLastOpenPass()
    {
        WriteJob(TaskStates.Ready, "first", order: 1);
        var runner = BuildRunner(modelSelection: new WaitingModelSelection("quota exhausted"));
        runner.SetMode("auto-continuous");
        await runner.TickAsync(CancellationToken.None);
        Assert.Single(runner.GetStatus().PickupSkips);

        runner.SetMode("manual");
        await runner.TickAsync(CancellationToken.None);

        var status = runner.GetStatus();
        Assert.Equal(RunnerPickupWaitCodes.ManualMode, status.PickupGate?.Code);
        Assert.Empty(status.PickupSkips);
    }

    [Fact]
    public void RunnerQueue_GateOutranksSkip_AndUnqueuedCardGetsNone()
    {
        var status = new ProjectRunnerStatus
        {
            ProjectName = ProjectName,
            Mode = "auto-continuous",
            QueuedJobIds = ["a", "b"],
            PickupSkips = [new RunnerPickupSkip("b", RunnerPickupWaitCodes.QuotaDefer, "quota")],
        };
        var gated = status with
        {
            PickupGate = new RunnerPickupWait(RunnerPickupWaitCodes.SlotsFull, "All 2 runner slots are occupied."),
        };

        Assert.Null(TaskLiveStatusProjection.RunnerQueueFor("a", status)!.ReasonCode);
        Assert.Equal(RunnerPickupWaitCodes.QuotaDefer, TaskLiveStatusProjection.RunnerQueueFor("b", status)!.ReasonCode);
        Assert.Equal(RunnerPickupWaitCodes.SlotsFull, TaskLiveStatusProjection.RunnerQueueFor("b", gated)!.ReasonCode);
        Assert.Null(TaskLiveStatusProjection.RunnerQueueFor("held-elsewhere", gated));
    }

    [Theory]
    [InlineData("manual", RunnerPickupWaitCodes.ManualMode)]
    [InlineData("paused", RunnerPickupWaitCodes.Paused)]
    public void RunnerQueue_UsesModeBeforeTheNextPickupTick(string mode, string code)
    {
        var status = new ProjectRunnerStatus
        {
            ProjectName = ProjectName,
            Mode = mode,
            QueuedJobIds = ["remote-ready"],
        };

        foreach (var snapshot in new[]
        {
            status,
            status with { PickupGate = new RunnerPickupWait(RunnerPickupWaitCodes.SlotsFull,
                "Previous pickup tick found all runner slots occupied.") },
        })
        {
            var queue = TaskLiveStatusProjection.RunnerQueueFor("remote-ready", snapshot);
            Assert.Equal(code, queue?.ReasonCode);
            Assert.NotNull(queue?.Reason);
            Assert.Equal(1, queue.Position);
        }
    }

    private sealed class WaitingModelSelection(string reason) : IModelSelectionService
    {
        public ModelSelectionDecision Select(ModelSelectionCall call)
        {
            var pins = call.Request.Pins;
            var route = new SelectionRoute(pins.CliType ?? "codex", pins.Model, pins.ThinkingLevel);
            var result = new SelectionResult
            {
                Outcome = SelectionOutcome.Primary,
                Route = route,
                Primary = route,
                Rule = SelectionRules.Pin,
                Reason = reason,
            };
            var plan = new QuotaAdmissionPlan(
                QuotaAdmissionOutcome.Wait, route.CliType, route.Model, route.ThinkingLevel,
                IsFallback: false, reason, NextResetAt: null, Projection: null);
            return new ModelSelectionDecision(result, plan);
        }

        public bool IsOperatorSwitchActive(string? cliType) => false;
    }

    private sealed class FixedLoadThrottle(LoadThrottleDecision current) : ILoadThrottleGate
    {
        public LoadThrottleDecision Current { get; set; } = current;
        public bool WasRecentlyActive => Current.Throttle;
        public Task WaitUntilReadyAsync(string reason, CancellationToken ct) => Task.CompletedTask;
    }

    private void WriteJob(string state, string slug, int order)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order},\"agent\":\"codex\",\"cliType\":\"codex\",\"ownerClientId\":\"local-default\"}}");
    }

    private ProjectRunner BuildRunner(
        IModelSelectionService? modelSelection = null,
        ILoadThrottleGate? loadThrottle = null,
        ProviderLimitRegistry? providerLimits = null)
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
            orchestratorLog, new OrchestratorRunner(NullLogger<OrchestratorRunner>.Instance),
            new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance),
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null,
            loadThrottle: loadThrottle,
            providerLimits: providerLimits,
            localCodingAdmission: AllowLocalCodingForTests.Instance,
            modelSelection: modelSelection);
    }
}
