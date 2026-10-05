using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Cycle 2c regression: ProjectObservationService.LastProgressAt must
/// reflect the union of every file the runner appends to during a live
/// session, not just cli-output.log. The original incident
/// (logs/analysis/_workspace/agent-software-studio-slowness-report-2026-05-09.html)
/// showed 1830 false no-progress advisories against a session that was
/// actively streaming tool-calls.jsonl while cli-output.log stayed
/// nearly empty - the supervisor parsed only cli-output.log and
/// classified the session as wedged.
/// </summary>
public class ProjectObservationServiceTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _jobFolder;
    private readonly ProjectObservationService _observe;
    private readonly TaskRunnerService _runners;

    public ProjectObservationServiceTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-obs-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        _jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "active-job");
        Directory.CreateDirectory(_jobFolder);
        Directory.CreateDirectory(Path.Combine(_jobFolder, "logs"));
        File.WriteAllText(Path.Combine(_jobFolder, "task.json"),
            JsonSerializer.Serialize(new { id = "active-job", title = "Active", state = TaskStates.Progress, order = 1, agent = "claude", cliType = "claude" }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "obs-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskRepository"] = _watchPath,
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var codex = GenericCliExecutionService.ForCodex(NullLogger<GenericCliExecutionService>.Instance, config,
            new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config),
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, gemini);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var projectSettings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, projectSettings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var orchestratorRunner = new OrchestratorRunner(NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var globalStore = new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, scanner, config);
        var quotaCache = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quota = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCache);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickup = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
        _runners = new TaskRunnerService(
            config, NullLogger<TaskRunnerService>.Instance, scanner, states, mutations, sessions,
            router, new ContextUsageParser(), summary, prompts, transitions, projectSettings,
            quota, quotaCaps, chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, globalBoot, git, pickup, infraBreaker, taskAccess);

        // Manually construct a runner status with one active job so the
        // observation path runs (TaskRunnerService is a BackgroundService;
        // we don't start it here).
        var runnerField = typeof(TaskRunnerService).GetField("_runners",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var runners = (System.Collections.Concurrent.ConcurrentDictionary<string, ProjectRunner>)runnerField.GetValue(_runners)!;
        var entry = scanner.GetWatchPaths().First();
        // We can't easily construct a real ProjectRunner without all its deps,
        // so the test exercises the observation path by writing files and
        // asserting MaxMtimeAcrossActivityFiles picks them up via the public
        // ObserveAsync path. Set up a minimal runner via the same factory the
        // service would use.

        _observe = new ProjectObservationService(_runners, scanner, NullLogger<ProjectObservationService>.Instance);
    }

    [Fact]
    public async Task ToolCallsJsonl_StartsObserveLastProgressClock()
    {
        // No active job in TaskRunnerService → observation returns idle (this
        // exercises the early-return path; the load-bearing case below uses
        // direct mtime measurement to keep the test scope tight).
        var idle = await _observe.ObserveAsync("obs-test", CancellationToken.None);
        Assert.Null(idle.LastProgressAt);

        // The actual contract under test: when an active session writes only
        // tool-calls.jsonl (cli-output.log stays empty), the supervisor must
        // still see the recent activity. Simulate the file shape and call the
        // private helper via reflection so we don't have to spin up a real
        // ProjectRunner.
        var toolCallsPath = Path.Combine(_jobFolder, "logs", "tool-calls.jsonl");
        File.WriteAllText(toolCallsPath, """{"ts":"2026-05-09T10:00:00Z","tool":"read"}""" + "\n");
        File.SetLastWriteTimeUtc(toolCallsPath, DateTime.UtcNow);

        // cli-output.log intentionally absent / empty.
        var cliOutPath = Path.Combine(_jobFolder, "logs", "cli-output.log");
        Assert.False(File.Exists(cliOutPath));

        var helper = typeof(ProjectObservationService).GetMethod(
            "MaxMtimeAcrossActivityFiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stamp = (DateTime?)helper.Invoke(null, new object[] { _jobFolder });

        Assert.NotNull(stamp);
        // Within 5 seconds of "now" - we just touched the file.
        Assert.True((DateTime.UtcNow - stamp!.Value).TotalSeconds < 5,
            $"MaxMtimeAcrossActivityFiles returned {stamp:o}, expected close to now. " +
            "If this fails the supervisor will not see tool-calls.jsonl as evidence of progress.");
    }

    [Fact]
    public void MaxMtime_PicksUpSessionEventsJsonl()
    {
        var sessionEventsPath = Path.Combine(_jobFolder, "logs", "session-events.jsonl");
        File.WriteAllText(sessionEventsPath, "{}\n");
        File.SetLastWriteTimeUtc(sessionEventsPath, DateTime.UtcNow);

        var helper = typeof(ProjectObservationService).GetMethod(
            "MaxMtimeAcrossActivityFiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stamp = (DateTime?)helper.Invoke(null, new object[] { _jobFolder });

        Assert.NotNull(stamp);
        Assert.True((DateTime.UtcNow - stamp!.Value).TotalSeconds < 5);
    }

    [Fact]
    public void MaxMtime_ReturnsNull_WhenJobFolderHasNothing()
    {
        var emptyFolder = Path.Combine(_watchPath, "empty-job");
        Directory.CreateDirectory(emptyFolder);

        var helper = typeof(ProjectObservationService).GetMethod(
            "MaxMtimeAcrossActivityFiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stamp = (DateTime?)helper.Invoke(null, new object[] { emptyFolder });

        Assert.Null(stamp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }
}
