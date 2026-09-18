using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2029 scheduler gate: <see cref="ProjectRunner.GetNextReadyJob"/> must not
/// pull a 2-ready card whose waits-on (dependsOn) targets are unfulfilled, must
/// resolve fulfillment CROSS-PROJECT (a target completing in another project
/// unblocks it), must skip a blocked card to the next pickable one rather than
/// halting, and must never deadlock on a dependency cycle.
/// </summary>
public sealed class WaitsOnPickupGateTests : IDisposable
{
    private const string App = "app";   // the runner's project
    private const string Lib = "lib";   // a second project holding the dependency
    private readonly string _workspaceRoot;
    private readonly string _appWatch;
    private readonly string _libWatch;

    public WaitsOnPickupGateTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-waitson-gate-" + Guid.NewGuid().ToString("N"));
        _appWatch = Path.Combine(_workspaceRoot, "projects", App);
        _libWatch = Path.Combine(_workspaceRoot, "projects", Lib);
        foreach (var wp in new[] { _appWatch, _libWatch })
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(wp, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void UnfulfilledDependency_BlocksPickup()
    {
        // consumer waits on LIB-1, which is still in 2-ready (open).
        WriteJob(_libWatch, TaskStates.Ready, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1, dependsOn: new[] { "LIB-1" });

        Assert.Null(BuildAppRunner().GetNextReadyJob());
    }

    [Fact]
    public void BlockedCard_IsSkipped_NextPickableIsChosen()
    {
        // consumer is blocked; a sibling with no dependency is pickable. The
        // gate must skip the blocked card, not halt the whole lane.
        WriteJob(_libWatch, TaskStates.Ready, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1, dependsOn: new[] { "LIB-1" });
        WriteJob(_appWatch, TaskStates.Ready, "free", "APP-2", order: 2);

        var next = BuildAppRunner().GetNextReadyJob();

        Assert.NotNull(next);
        Assert.Equal("free", next!.Id);
    }

    [Fact]
    public void RunnerQueue_ExcludesBlockedCards_FromPositionsOfPickableSiblings()
    {
        WriteJob(_libWatch, TaskStates.Ready, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1, dependsOn: new[] { "LIB-1" });
        WriteJob(_appWatch, TaskStates.Ready, "first", "APP-2", order: 2);
        WriteJob(_appWatch, TaskStates.Ready, "second", "APP-3", order: 3);

        var queued = BuildAppRunner().GetStatus().QueuedJobIds;

        Assert.Equal(new[] { "first", "second" }, queued);
        Assert.Equal(1, queued.IndexOf("first") + 1);
        Assert.Equal(2, queued.IndexOf("second") + 1);
        Assert.DoesNotContain("consumer", queued);
    }

    [Fact]
    public void FulfilledDependency_CrossProject_AllowsPickup()
    {
        // Same shape, but LIB-1 has reached 6-completed in the OTHER project.
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1, dependsOn: new[] { "LIB-1" });

        var next = BuildAppRunner().GetNextReadyJob();

        Assert.NotNull(next);
        Assert.Equal("consumer", next!.Id);
    }

    [Fact]
    public void ReleaseGate_TerminalDependencyWithoutReleasedFlag_BlocksPickup()
    {
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1,
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        Assert.Null(BuildAppRunner().GetNextReadyJob());
    }

    [Fact]
    public void RunnerQueue_ExcludesUnsatisfiableArchivedGate_FromIdleDetection()
    {
        WriteJob(_libWatch, TaskStates.Archive, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1,
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        var runner = BuildAppRunner();

        Assert.Empty(runner.GetStatus().QueuedJobIds);
        Assert.Null(runner.GetNextReadyJob());
    }

    [Fact]
    public void ReleaseGate_ExplicitReleasedFlag_AllowsPickup()
    {
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1", order: 1, released: true);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1,
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        Assert.Equal("consumer", BuildAppRunner().GetNextReadyJob()!.Id);
    }

    [Fact]
    public void ArchivedDependency_CrossProject_AllowsPickup()
    {
        // Fulfilled includes the terminal 7-archive lane, which ScanAllJobs
        // omits - the gate must still resolve it via the archive-inclusive scan.
        WriteJob(_libWatch, TaskStates.Archive, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1, dependsOn: new[] { "LIB-1" });

        var next = BuildAppRunner().GetNextReadyJob();

        Assert.NotNull(next);
        Assert.Equal("consumer", next!.Id);
    }

    [Fact]
    public void DependencyCycle_DoesNotDeadlock_SkipsBoth()
    {
        // APP-1 waits on APP-2 waits on APP-1: a config error that can never be
        // fulfilled. The runner must report+skip, never hang. A pickable sibling
        // is still chosen, proving the tick moved on.
        WriteJob(_appWatch, TaskStates.Ready, "a", "APP-1", order: 1, dependsOn: new[] { "APP-2" });
        WriteJob(_appWatch, TaskStates.Ready, "b", "APP-2", order: 2, dependsOn: new[] { "APP-1" });

        Assert.Null(BuildAppRunner().GetNextReadyJob());

        WriteJob(_appWatch, TaskStates.Ready, "free", "APP-3", order: 3);
        Assert.Equal("free", BuildAppRunner().GetNextReadyJob()!.Id);
    }

    // AGT-2818: an archived, never released release-gate target can never open
    // the gate. The runner must report that as loudly as a cycle - once per card,
    // not once per tick - and must keep skipping the card either way.
    [Fact]
    public void UnsatisfiableReleaseGate_BlocksPickup_AndWarnsOncePerCard()
    {
        WriteJob(_libWatch, TaskStates.Archive, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1,
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        var logger = new RecordingLogger();
        var runner = BuildAppRunner(logger);

        // Three ticks, exactly as an idle runner would produce.
        Assert.Null(runner.GetNextReadyJob());
        Assert.Null(runner.GetNextReadyJob());
        Assert.Null(runner.GetNextReadyJob());

        var warnings = logger.Warnings
            .Where(message => message.Contains("unsatisfiable waits-on gate", StringComparison.Ordinal))
            .ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("APP-1", warning, StringComparison.Ordinal);
        Assert.Contains("LIB-1", warning, StringComparison.Ordinal);
        Assert.Contains("archived", warning, StringComparison.OrdinalIgnoreCase);
        // Both ways out are named in the warning, neither is taken.
        Assert.Contains("release", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("releaseGate", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SatisfiableReleaseGate_IsHeldQuietly_WithoutTheUnsatisfiableWarning()
    {
        // 6-completed, not archived: an ordinary wait, and it must not borrow
        // the configuration-error warning.
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1,
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        var logger = new RecordingLogger();
        Assert.Null(BuildAppRunner(logger).GetNextReadyJob());

        Assert.DoesNotContain(
            logger.Warnings,
            message => message.Contains("unsatisfiable waits-on gate", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsatisfiableReleaseGate_DoesNotHaltTheLane()
    {
        WriteJob(_libWatch, TaskStates.Archive, "dep", "LIB-1", order: 1);
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", order: 1,
            dependsOn: new[] { "LIB-1" }, releaseGate: true);
        WriteJob(_appWatch, TaskStates.Ready, "free", "APP-2", order: 2);

        Assert.Equal("free", BuildAppRunner().GetNextReadyJob()!.Id);
    }

    private void WriteJob(
        string watchPath,
        string state,
        string slug,
        string key,
        int order,
        string[]? dependsOn = null,
        bool releaseGate = false,
        bool released = false)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var refs = dependsOn is { Length: > 0 }
            ? $",\"references\":{{\"dependsOn\":[{string.Join(",", dependsOn.Select(k => releaseGate ? $"{{\"key\":\"{k}\",\"releaseGate\":true}}" : $"\"{k}\""))}]}}"
            : "";
        var release = released ? ",\"released\":true" : "";
        var json =
            $"{{\"id\":\"{slug}\",\"key\":\"{key}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"order\":{order},\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"{release}{refs}}}";
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    private ProjectRunner BuildAppRunner(ILogger<ProjectRunner>? logger = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = App,
                ["WatchPaths:0:Path"] = _appWatch,
                ["WatchPaths:0:RootPath"] = _appWatch,
                ["WatchPaths:0:RepositoryPath"] = _appWatch,
                ["WatchPaths:1:Name"] = Lib,
                ["WatchPaths:1:Path"] = _libWatch,
                ["WatchPaths:1:RootPath"] = _libWatch,
                ["WatchPaths:1:RepositoryPath"] = _libWatch,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = App,
            Path = _appWatch,
            RootPath = _appWatch,
            RepositoryPath = _appWatch
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(scanner, clients, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
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
        var codex = GenericCliExecutionService.ForCodex(
            NullLogger<GenericCliExecutionService>.Instance, config, codexDiscovery,
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

        return new ProjectRunner(
            App, entry,
            logger ?? NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null);
    }

    /// <summary>
    /// Captures warning-level lines so a "reported once, not per tick" contract
    /// can be asserted on the formatted message rather than on a log provider.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ProjectRunner>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
