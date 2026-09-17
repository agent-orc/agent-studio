using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectRunnerPickupAtomicityTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public ProjectRunnerPickupAtomicityTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-pickup-atomicity-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        InitializeGitRepository();
    }

    // AGT-2858: this fixture seeds a Git repository, whose read-only objects
    // defeat a plain recursive delete on Windows.
    public void Dispose() => TestTempRoot.TryDelete(_workspaceRoot);

    [Fact]
    public async Task AutoPickupSpawnFailure_RevertsReady_RemovesLock_AndFreesSlot()
    {
        WriteJob(TaskStates.Ready, "job-a");
        var cli = new FailingCliService();
        var runner = BuildRunner(cli);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "job-a");
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "job-a");
        Assert.True(cli.StartCalled, "the fake CLI should have reached the spawn boundary");
        Assert.Equal(CliExecutionEngines.Car, cli.ExecutionEngineAtStart);
        Assert.True(cli.PickupLockExistedAtStart,
            "the pickup lock must be stamped on the actual 3-progress folder before StartAsync is called");
        Assert.True(Directory.Exists(readyFolder), "failed spawn must return the task to 2-ready immediately");
        Assert.False(Directory.Exists(progressFolder), "failed spawn must not leave a zombie in 3-progress");
        Assert.False(File.Exists(Path.Combine(readyFolder, PickupLockFile.LockFileName)),
            "the pickup lock must be removed when the run never starts");
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);

        Assert.False(File.Exists(Path.Combine(readyFolder, "logs", "session-events.jsonl")),
            "a rejected spawn must not create a historical run boundary");
        var timelinePath = Path.Combine(readyFolder, "logs", "timeline.jsonl");
        Assert.False(File.Exists(timelinePath)
            && File.ReadAllText(timelinePath).Contains(TimelineEventKinds.AgentRunStarted, StringComparison.Ordinal),
            "agent_run_started must be durable only after process confirmation");

        var cliOutput = Path.Combine(readyFolder, "logs", "cli-output.log");
        Assert.True(File.Exists(cliOutput), "spawn failures should leave a diagnostic in cli-output.log");
        Assert.Contains("spawn failed", File.ReadAllText(cliOutput), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodingRun_HandsThePreparationCacheBindingToTheCliLaunch()
    {
        // TE-52: the coding run's agent runs this repository's own build, test
        // and lint commands, so the cache folders repository preparation restored
        // into must be part of the CLI launch environment.
        WritePreparationContract();
        WriteJob(TaskStates.Ready, "job-prepared");
        var cli = new FailingCliService();
        var runner = BuildRunner(cli);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        Assert.True(cli.StartCalled, "the fake CLI should have reached the spawn boundary");
        Assert.NotNull(cli.EnvironmentAtStart);
        Assert.Contains("NUGET_PACKAGES", cli.EnvironmentAtStart!.Keys);
        Assert.Contains("xunit.analyzers/1.4.0/xunit.analyzers.nupkg", cli.PackagesVisibleAtStart);
        // Freeing the slot releases the per-run copy; the shared entries remain.
        Assert.False(Directory.Exists(cli.EnvironmentAtStart["NUGET_PACKAGES"]));
    }

    [Fact]
    public async Task AutoPickupAdmissionFault_RevertsReady_RemovesLock_AndFreesSlot()
    {
        WriteJob(TaskStates.Ready, "job-fault");
        var cli = new FailingCliService(throwOnStart: true);
        var runner = BuildRunner(cli, CliExecutionEngines.Legacy);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "job-fault");
        Assert.True(cli.StartCalled, "fault injection must reach the process-start boundary");
        Assert.Equal(CliExecutionEngines.Legacy, cli.ExecutionEngineAtStart);
        Assert.True(Directory.Exists(readyFolder), "an admission exception must self-heal back to Ready");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "job-fault")),
            "an admission exception must never leave Progress without an active run");
        Assert.False(File.Exists(Path.Combine(readyFolder, PickupLockFile.LockFileName)),
            "fault recovery must release durable ownership so the bounded wake can retry");
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);
        Assert.False(File.Exists(Path.Combine(readyFolder, "logs", "session-events.jsonl")),
            "an admission fault must not create a historical run boundary");
    }

    [Fact]
    public async Task ImmediateCliFinish_WaitsForDurableStartHandshakeBeforeFinalization()
    {
        WriteJob(TaskStates.Ready, "job-fast-finish");
        var cli = new ImmediateFinishCliService();
        var runner = BuildRunner(cli);
        runner.SetMode("auto-continuous");

        var tick = runner.TickAsync(CancellationToken.None);
        await cli.FinishRaised.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Keep StartAsync suspended after it raised OnFinished. Without the
        // handoff gate, the background finalizer releases the execution slot
        // and writes post-processing state while admission is still waiting.
        await Task.Delay(200);
        Assert.Equal(1, runner.GetStatus().OccupiedSlots);
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "job-fast-finish");
        Assert.True(Directory.Exists(progressFolder));
        Assert.False(File.Exists(Path.Combine(progressFolder, "logs", "session-events.jsonl")));

        cli.AllowStartReturn.TrySetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(10));

        var sessionEvents = Path.Combine(progressFolder, "logs", "session-events.jsonl");
        Assert.True(File.Exists(sessionEvents), "the confirmed start must be durable before finalization is released");
        Assert.Contains("\"kind\"", File.ReadAllText(sessionEvents), StringComparison.OrdinalIgnoreCase);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (runner.GetStatus().OccupiedSlots != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);

        SessionEvent? recorded = null;
        var closeoutDeadline = DateTime.UtcNow.AddSeconds(10);
        while (recorded?.FinishedAt is null && DateTime.UtcNow < closeoutDeadline)
        {
            recorded = JsonSerializer.Deserialize<SessionEvent>(
                File.ReadLines(sessionEvents).Single(),
                TaskJsonFile.ReadOpts);
            if (recorded?.FinishedAt is null) await Task.Delay(20);
        }
        Assert.NotNull(recorded);
        Assert.Equal(RunStatuses.Stopped, recorded!.Status);
        Assert.Equal(TerminalRunOutcomeKinds.Interrupted, recorded.Result);
        Assert.Equal(-1, recorded.ExitCode);
        Assert.Equal(0.01, recorded.DurationSeconds);
        Assert.NotNull(recorded.FinishedAt);
    }

    [Fact]
    public async Task ProviderLimit_HoldsWithoutEscalation_ThenProbeResumesSameCard()
    {
        WriteJob(TaskStates.Ready, "job-provider-limit");
        var cli = new ProviderLimitCliService();
        var registry = new ProviderLimitRegistry();
        var runner = BuildRunner(cli, providerLimits: registry, quotaProbes: [new HealthyQuotaProbe()]);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "job-provider-limit");
        await WaitUntilAsync(() =>
            File.Exists(Path.Combine(progressFolder, QuotaWaitMarker.FileName))
            && runner.GetStatus().OccupiedSlots == 0);

        Assert.True(Directory.Exists(progressFolder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "job-provider-limit")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl")));
        Assert.False(runner.ProviderClaimsAllowedForTest("claude", DateTime.UtcNow));
        Assert.True(runner.ProviderClaimsAllowedForTest("codex", DateTime.UtcNow));

        // The simulated response reports a near-future reset. Once due, this
        // tick starts the single coalesced recovery probe without admitting
        // Claude on the strength of the timestamp alone.
        var resetAt = Assert.Single(registry.Active(DateTime.UtcNow)).LimitedUntil;
        var untilReset = resetAt - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
        if (untilReset > TimeSpan.Zero) await Task.Delay(untilReset);
        var recoveryDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!runner.ProviderClaimsAllowedForTest("claude", DateTime.UtcNow)
               && DateTime.UtcNow < recoveryDeadline)
        {
            await runner.TickAsync(CancellationToken.None);
            await Task.Delay(20);
        }
        Assert.True(runner.ProviderClaimsAllowedForTest("claude", DateTime.UtcNow));

        // Normal scheduler ticks resume the same Progress card after the
        // durable marker projection observes that recovery.
        var resumeDeadline = DateTime.UtcNow.AddSeconds(10);
        while (cli.StartCount < 2 && DateTime.UtcNow < resumeDeadline)
        {
            await runner.TickAsync(CancellationToken.None);
            await Task.Delay(20);
        }
        Assert.Equal(2, cli.StartCount);
    }

    [Fact]
    public async Task ProviderLimit_ClaudeCircuitStillAdmitsCodexCard()
    {
        WriteJob(TaskStates.Ready, "job-codex", cliType: CliTypes.Codex);
        var cli = new ImmediateFinishCliService();
        var registry = new ProviderLimitRegistry();
        registry.Record(new ProviderLimitStatus(
            CliTypes.Claude,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1),
            "claude: limited until reset",
            ResetTimeReported: true));
        var runner = BuildRunner(cli, providerLimits: registry);
        runner.SetMode("auto-continuous");
        cli.AllowStartReturn.SetResult();

        await runner.TickAsync(CancellationToken.None);
        await cli.FinishRaised.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(runner.ProviderClaimsAllowedForTest(CliTypes.Codex, DateTime.UtcNow));
    }

    private void WriteJob(string state, string slug, int order = 1, string cliType = CliTypes.Claude)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Do the task.");
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order}," +
            $"\"agent\":\"claude\",\"cliType\":\"{cliType}\",\"ownerClientId\":\"local-default\"}}");
    }

    /// <summary>
    /// Gives the fixture repository a repository preparation contract whose
    /// prepare restores one package into the executor-owned NuGet folder, the
    /// minimal stand-in for `dotnet restore`.
    /// </summary>
    private void WritePreparationContract()
    {
        WriteRepositoryFile("packages.lock.json", "{\"version\":1,\"dependencies\":{}}");
        WriteRepositoryFile(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [dotnet]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
              lint:
            testSuites:
            cachePaths: [bin]
            capabilities: [linux]
            environment:
              CI: "true"
            """);
        WriteRepositoryFile(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            mkdir -p "$NUGET_PACKAGES/xunit.analyzers/1.4.0"
            printf nupkg > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/xunit.analyzers.nupkg"
            """);
        RunGit("add", "packages.lock.json", ".agent-studio");
        RunGit("commit", "-q", "-m", "chore: repository preparation contract");
    }

    private void WriteRepositoryFile(string relative, string content)
    {
        var path = Path.Combine(_watchPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Pickup Atomicity Test");
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
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git did not start");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }

    private ProjectRunner BuildRunner(
        ICliExecutionService cli,
        string? executionEngine = null,
        ProviderLimitRegistry? providerLimits = null,
        IReadOnlyList<IQuotaProbe>? quotaProbes = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
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
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        if (executionEngine is not null)
            settings.SetCliExecutionEngine(ProjectName, executionEngine);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var router = new CliRouter(cli);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(
            NullLogger<QuotaService>.Instance,
            quotaProbes ?? Array.Empty<IQuotaProbe>(),
            config,
            quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var pickupLock = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var pickupLockOwner = new PickupLockOwner
        {
            Pid = Environment.ProcessId,
            Hostname = Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "test",
            BackendPort = 0
        };

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess,
            bus: null,
            pickupLock: pickupLock,
            pickupLockOwner: pickupLockOwner,
            providerLimits: providerLimits);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(predicate(), "condition was not reached before the test deadline");
    }

    private sealed class FailingCliService : ICliExecutionService
    {
        private readonly bool _throwOnStart;

        public FailingCliService(bool throwOnStart = false)
        {
            _throwOnStart = throwOnStart;
        }

        public string CliType => CliTypes.Claude;
        public bool StartCalled { get; private set; }
        public bool PickupLockExistedAtStart { get; private set; }
        public string? ExecutionEngineAtStart { get; private set; }

        /// <summary>Preparation cache binding this launch was handed (TE-52).</summary>
        public IReadOnlyDictionary<string, string>? EnvironmentAtStart { get; private set; }

        /// <summary>
        /// Files visible under the bound NuGet folder at launch time. Captured
        /// here because the run releases its per-run folder as soon as the slot
        /// is freed, which for a rejected spawn is immediately.
        /// </summary>
        public IReadOnlyList<string> PackagesVisibleAtStart { get; private set; } = [];

        public string GetCliPath() => "fake-claude";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => (true, "test", path ?? GetCliPath());

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
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default)
        {
            StartCalled = true;
            ExecutionEngineAtStart = executionEngine;
            EnvironmentAtStart = environment;
            PackagesVisibleAtStart = environment is not null
                                     && environment.TryGetValue("NUGET_PACKAGES", out var packages)
                                     && Directory.Exists(packages)
                ? Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(packages, path).Replace('\\', '/'))
                    .ToArray()
                : [];
            PickupLockExistedAtStart = !string.IsNullOrWhiteSpace(jobFolderPath)
                && File.Exists(Path.Combine(jobFolderPath, PickupLockFile.LockFileName));
            if (_throwOnStart) throw new InvalidOperationException("injected admission fault");
            return Task.FromResult<(CliExecution?, string?)>((null, "fake spawn failure"));
        }

        public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => false;
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

    private sealed class ImmediateFinishCliService : ICliExecutionService
    {
        public string CliType => CliTypes.Claude;
        public TaskCompletionSource FinishRaised { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStartReturn { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string GetCliPath() => "fake-claude";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null)
            => (true, "test", path ?? GetCliPath());

        public async Task<(CliExecution? Execution, string? Error)> StartAsync(
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
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default)
        {
            var started = new CliExecution
            {
                JobId = jobId,
                TaskKey = jobKey,
                ProcessId = Environment.ProcessId,
                StartedAt = DateTime.UtcNow,
                Status = RunStatuses.Running,
                Model = model,
                ThinkingLevel = thinkingLevel,
            };
            OnStarted?.Invoke(jobKey, started);
            OnFinished?.Invoke(jobKey, started with
            {
                Status = RunStatuses.Stopped,
                ExitCode = -1,
                DurationSeconds = 0.01,
                RunOutcome = TerminalRunOutcomeKinds.Interrupted,
            });
            FinishRaised.TrySetResult();
            await AllowStartReturn.Task.WaitAsync(ct);
            return (started, null);
        }

        public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => false;
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

    private sealed class ProviderLimitCliService : ICliExecutionService
    {
        private readonly Dictionary<string, List<CliOutputLine>> _output = new(StringComparer.Ordinal);

        public string CliType => CliTypes.Claude;
        public int StartCount { get; private set; }
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
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default)
        {
            StartCount++;
            var started = new CliExecution
            {
                JobId = jobId,
                TaskKey = jobKey,
                ProcessId = Environment.ProcessId,
                StartedAt = DateTime.UtcNow,
                Status = RunStatuses.Running,
            };
            OnStarted?.Invoke(jobKey, started);
            var text = StartCount == 1
                ? $"You've hit your session limit (resetsAt={DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds()})"
                : "[[TASK_DONE]]";
            var line = new CliOutputLine { Timestamp = DateTime.UtcNow, Stream = "stderr", Text = text };
            _output[jobKey] = [line];
            OnOutput?.Invoke(jobKey, line);
            OnFinished?.Invoke(jobKey, started with
            {
                Status = StartCount == 1 ? RunStatuses.Failed : RunStatuses.Completed,
                ExitCode = StartCount == 1 ? 1 : 0,
                DurationSeconds = 0.01,
                RunOutcome = StartCount == 1 ? TerminalRunOutcomeKinds.Failed : TerminalRunOutcomeKinds.Success,
            });
            return Task.FromResult<(CliExecution?, string?)>((started, null));
        }

        public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => false;
        public bool SendInput(string jobKey, string input) => false;
        public List<CliOutputLine> GetOutput(string jobKey)
            => _output.TryGetValue(jobKey, out var output) ? output : [];
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

    private sealed class HealthyQuotaProbe : IQuotaProbe
    {
        public string CliType => CliTypes.Claude;

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) => Task.FromResult(new QuotaSnapshot
        {
            CliType = CliTypes.Claude,
            FetchedAt = DateTime.UtcNow,
            Windows =
            [
                new QuotaWindow
                {
                    Label = "five-hour",
                    UsedPct = 0,
                    ResetAt = DateTime.UtcNow.AddHours(5),
                },
            ],
            Source = "simulated-recovery",
        });
    }
}
