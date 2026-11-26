using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RunnerDisplayedPickupOrderTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly string _repoRoot;

    public RunnerDisplayedPickupOrderTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-display-pick-" + Guid.NewGuid().ToString("N"));
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
    public void SingleSlot_PicksFirstVisibleReadyBeforeProgress()
    {
        WriteJob(TaskStates.Progress, "progress-would-have-won-before", order: 1);
        WriteJob(TaskStates.Ready, "visible-ready-head", order: 10);

        var (runner, settings) = BuildRunner();
        settings.SetDispatchOverlapWindow(ProjectName, OverlapWindows.InFlight);
        runner.SetMode("auto-continuous");

        var picked = InvokeDisplayedPicker(runner, slotMax: 1);

        Assert.NotNull(picked);
        Assert.Equal("visible-ready-head", picked!.Id);
        Assert.Equal("display-order: 2-ready visible-ready-head", runner.GetStatus().LastPickReason);
    }

    [Fact]
    public void ParallelSlot_SkipsOnlyConflictingVisibleHeadAndExplainsDeviation()
    {
        WriteJob(TaskStates.Ready, "conflicts-with-running", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.");
        WriteJob(TaskStates.Ready, "non-conflicting-next", order: 2,
            prompt: "Edit frontend/src/app/features/task-list/task-list.component.ts.");

        var (runner, settings) = BuildRunner();
        settings.SetDispatchOverlapWindow(ProjectName, OverlapWindows.InFlight);
        settings.SetMaxParallelism(ProjectName, 2);
        runner.SetMode("auto-continuous");
        runner.SetActiveJobForTest("already-running", "codex", ["backend/services"]);

        var picked = InvokeDisplayedPicker(runner, slotMax: 2);

        Assert.NotNull(picked);
        Assert.Equal("non-conflicting-next", picked!.Id);
        var reason = runner.GetStatus().LastPickReason;
        Assert.NotNull(reason);
        Assert.Contains("skipped earlier conflicting candidate", reason);
        Assert.Contains("conflicts-with-running", reason);
    }

    [Fact]
    public void InFlightWindow_SkipsReadyCardOverlappingACardInReview()
    {
        // DS-0: the card in review holds no slot but is exposed until it
        // integrates; the default in-flight window compares with it.
        WriteJob(TaskStates.AutoReview, "still-in-review", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.");
        WriteJob(TaskStates.Ready, "overlaps-review", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.");
        WriteJob(TaskStates.Ready, "disjoint-next", order: 2,
            prompt: "Edit frontend/src/app/features/task-list/task-list.component.ts.");

        var (runner, settings) = BuildRunner();
        settings.SetDispatchOverlapWindow(ProjectName, OverlapWindows.InFlight);
        runner.SetMode("auto-continuous");

        var picked = InvokeDisplayedPicker(runner, slotMax: 1);

        Assert.NotNull(picked);
        Assert.Equal("disjoint-next", picked!.Id);
        var skip = Assert.Single(runner.GetStatus().PickupSkips);
        Assert.Equal("overlaps-review", skip.JobId);
        Assert.Equal(RunnerPickupWaitCodes.ParallelConflict, skip.Code);
        Assert.Equal(
            "scope conflict with 'still-in-review' on 'backend/services/runner/projectrunner.cs' (window in-flight, 4-auto-review)",
            skip.Detail);
        Assert.Equal(
            "display-order: 2-ready disjoint-next; skipped earlier conflicting candidate(s): overlaps-review: "
            + "scope conflict with 'still-in-review' on 'backend/services/runner/projectrunner.cs' (window in-flight, 4-auto-review)",
            runner.GetStatus().LastPickReason);
    }

    [Fact]
    public void RunningWindow_KeepsTodaysRunningOnlyComparison()
    {
        WriteJob(TaskStates.AutoReview, "still-in-review", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.");
        WriteJob(TaskStates.Ready, "overlaps-review", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.");

        var (runner, settings) = BuildRunner();
        settings.SetDispatchOverlapWindow(ProjectName, OverlapWindows.Running);
        runner.SetMode("auto-continuous");

        var picked = InvokeDisplayedPicker(runner, slotMax: 1);

        Assert.Equal("overlaps-review", picked?.Id);
        Assert.Equal("display-order: 2-ready overlaps-review", runner.GetStatus().LastPickReason);
        Assert.Empty(runner.GetStatus().PickupSkips);
    }

    [Fact]
    public void InFlightWindow_IgnoresAnIntegratedCardInHumanReview()
    {
        // clock-independent: the integration record date is fixture data; no start is recorded, so any date counts.
        WriteJob(TaskStates.HumanReview, "already-integrated", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.",
            extraJson: ",\"integrationRecords\":[{\"id\":\"rec-1\",\"classification\":\"integrated-verified\",\"recordedAtUtc\":\"2026-11-20T10:00:00Z\"}]");
        WriteJob(TaskStates.Ready, "overlaps-integrated", order: 1,
            prompt: "Edit backend/Services/Runner/ProjectRunner.cs.");

        var (runner, settings) = BuildRunner();
        settings.SetDispatchOverlapWindow(ProjectName, OverlapWindows.InFlight);
        runner.SetMode("auto-continuous");

        var picked = InvokeDisplayedPicker(runner, slotMax: 1);

        Assert.Equal("overlaps-integrated", picked?.Id);
        Assert.Equal("display-order: 2-ready overlaps-integrated", runner.GetStatus().LastPickReason);
    }

    private static TaskInfo? InvokeDisplayedPicker(ProjectRunner runner, int slotMax)
    {
        var method = typeof(ProjectRunner).GetMethod("PickNextDisplayedCandidate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(runner, [slotMax]) as TaskInfo;
    }

    private void WriteJob(string state, string slug, int order, string? prompt = null, string? kind = null,
        string extraJson = "")
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var kindField = kind != null ? $",\"kind\":\"{kind}\"" : string.Empty;
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order},\"agent\":\"codex\",\"cliType\":\"codex\",\"ownerClientId\":\"local-default\"{kindField}{extraJson}}}");
        if (prompt != null) File.WriteAllText(Path.Combine(dir, "prompt.md"), prompt);
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

        var orchestratorRunner = new OrchestratorRunner(NullLogger<OrchestratorRunner>.Instance);
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
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null,
            localCodingAdmission: AllowLocalCodingForTests.Instance,
            overlapGuard: new AgentStudio.Runner.OverlapDispatchGuard(new OverlapClock(),
                NullLogger<AgentStudio.Runner.OverlapDispatchGuard>.Instance,
                new AgentStudio.Runner.OverlapPredictionLedger(Path.Combine(_workspaceRoot, "overlap-predictions"))));

        return (runner, settings);
    }
    private sealed class OverlapClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

}
