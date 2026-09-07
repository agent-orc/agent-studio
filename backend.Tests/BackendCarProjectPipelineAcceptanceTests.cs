using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentStudio.ModelMigrations;
using AgentStudio.TestSupport;
using CodingAgentRunner.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// One non-billable paired acceptance slice for the local CAR rollout. It
/// starts the same Ready card through <see cref="ProjectRunner"/> once per
/// execution engine, feeds both engines the same recorded Claude fixture, and
/// drives the resulting AutoReview card through deterministic post-steps.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class BackendCarProjectPipelineAcceptanceTests : IDisposable
{
    private const string Project = "demo";
    private const string Slug = "car-local-acceptance";
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "backend-car-project-pipeline", Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public BackendCarProjectPipelineAcceptanceTests()
    {
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        InitializeGitRepository();
    }

    [SkippableTheory]
    [InlineData(CliExecutionEngines.Legacy)]
    [InlineData(CliExecutionEngines.Car)]
    public async Task Local_engine_card_reaches_identical_green_post_steps_and_token_cost_ledger(
        string executionEngine)
    {
        RequireNode();
        if (executionEngine == CliExecutionEngines.Legacy)
            Skip.IfNot(OperatingSystem.IsLinux(),
                "the legacy fixture executable currently requires a POSIX shell");
        WriteReadyCard();

        var fixturePath = CliCaptureFixtureLocator.Resolve(
            RepoRoot(),
            "p1-happy-done.claude.fixture");
        var spawner = new FixtureSpawner(fixturePath);
        var harness = BuildHarness(spawner, fixturePath, executionEngine);
        var liveOutput = new ConcurrentQueue<CliOutputLine>();
        harness.Router.OnOutput += (_, _, line) => liveOutput.Enqueue(line);

        harness.Runner.SetMode("auto-continuous");
        await harness.Runner.TickAsync(CancellationToken.None);

        var autoReviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, Slug);
        await WaitUntilAsync(
            () => Directory.Exists(autoReviewFolder),
            $"ProjectRunner did not move the fixture-backed {executionEngine} run to AutoReview.");
        await WaitUntilAsync(
            () => harness.Summary.GetState(TaskIdentity.CreateKey(_watchPath, Slug))?.Status
                  == TaskSummaryStatus.Ready,
            "the deterministic post-run summary did not finish.");

        if (executionEngine == CliExecutionEngines.Car)
        {
            Assert.True(spawner.Spawned, "the injected CAR process spawner was never called");
            Assert.Contains("--output-format", spawner.PreparedArgv);
            Assert.Contains("stream-json", spawner.PreparedArgv);
        }
        else
        {
            Assert.False(spawner.Spawned,
                "the legacy control unexpectedly crossed the CAR process-spawn boundary");
        }
        Assert.Contains(liveOutput, line =>
            line.Text.Contains("Patch applied; the suite is green.", StringComparison.Ordinal));
        Assert.Contains(liveOutput, line =>
            line.Text.Contains("[[TASK_DONE]]", StringComparison.Ordinal));

        var outputLog = Path.Combine(autoReviewFolder, "logs", "cli-output.log");
        Assert.True(File.Exists(outputLog));
        var persistedOutput = await File.ReadAllTextAsync(outputLog);
        Assert.Contains("Patch applied; the suite is green.", persistedOutput, StringComparison.Ordinal);
        Assert.Contains("[[TASK_DONE]]", persistedOutput, StringComparison.Ordinal);

        TaskTokenSummary? cardLedger = null;
        await WaitUntilAsync(() =>
        {
            var perJob = new BusBackedTokenSummaryReader(harness.BusStore, harness.Configuration)
                .SummarizePerJob(Project);
            return perJob.TryGetValue(Slug, out cardLedger);
        }, "the agent turn did not reach the bus-backed token ledger.");
        Assert.NotNull(cardLedger);
        Assert.Equal(1, cardLedger!.Calls);
        Assert.Equal(1_542, cardLedger.InputTokens);
        Assert.Equal(911, cardLedger.OutputTokens);
        Assert.Equal(48_230, cardLedger.CacheReadTokens);
        Assert.Equal(2_010, cardLedger.CacheCreationTokens);
        Assert.Equal(52_693, cardLedger.TotalTokens);
        Assert.True(cardLedger.AllModelsPriced);
        Assert.True(cardLedger.EstimatedApiCostUsd > 0m);
        Assert.Equal("Claude Sonnet 4.5", cardLedger.LastModel);

        var aspectInvocations = 0;
        var buildGate = new PassingBuildGate();
        var review = BuildReviewOrchestrator(harness, buildGate, () =>
            Interlocked.Increment(ref aspectInvocations));
        await review.TickOnceAsync(_workspace, CancellationToken.None);

        var humanReviewFolder = Path.Combine(_watchPath, TaskStates.HumanReview, Slug);
        Assert.True(Directory.Exists(humanReviewFolder),
            "the green completion/build/aspect gates did not promote the card to HumanReview");
        Assert.Equal(1, buildGate.CallCount);
        Assert.True(Volatile.Read(ref aspectInvocations) > 0);
        Assert.True(File.Exists(Path.Combine(
            humanReviewFolder, CompletionAcceptanceRecord.FileName)));

        var pipeline = harness.PipelineLog.Read(humanReviewFolder);
        Assert.NotNull(pipeline);
        Assert.NotNull(pipeline!.CompletedAt);
        var core = Assert.Single(pipeline.Steps, step =>
            step.StepId == PipelineCatalogue.CoreAgentRunStepId);
        Assert.Equal(PipelineStepStatus.Passed, core.Status);
        Assert.Equal(1_542, core.InputTokens);
        Assert.Equal(911, core.OutputTokens);
        Assert.Equal(48_230, core.CacheReadTokens);
        Assert.Equal(2_010, core.CacheCreationTokens);
        Assert.Contains("CLI FOOTER", core.TokenUsageSource, StringComparison.Ordinal);

        Assert.Equal(
            PipelineStepStatus.Passed,
            Assert.Single(pipeline.Steps, step =>
                step.StepId == PipelineCatalogue.OrchestratorReviewStepId).Status);
        Assert.Equal(
            PipelineStepStatus.Passed,
            Assert.Single(pipeline.Steps, step =>
                step.StepId == PipelineCatalogue.BuildTestGateStepId).Status);
        Assert.All(
            pipeline.Steps.Where(step => PipelineCatalogue.AspectStepIds.Contains(step.StepId)),
            step => Assert.Equal(PipelineStepStatus.Passed, step.Status));

        var pipelineCost = PipelineCostCalculator.Summarize(pipeline);
        Assert.Equal(52_693, pipelineCost.TotalTokens);
        Assert.False(pipelineCost.AnyModelUnknown);
        Assert.True(pipelineCost.TotalCostUsd > 0m);
    }

    [SkippableFact]
    public async Task Local_admission_launches_the_model_selected_by_a_safe_migration()
    {
        RequireNode();
        WriteReadyCard(ModelIds.ClaudeOpus48, modelExplicit: false);
        WriteMigrationCatalog();
        var fixturePath = CliCaptureFixtureLocator.Resolve(
            RepoRoot(),
            "p1-happy-done.claude.fixture");
        var spawner = new FixtureSpawner(fixturePath);
        var harness = BuildHarness(
            spawner,
            fixturePath,
            CliExecutionEngines.Car,
            enableSafeModelMigration: true);
        var liveOutput = new ConcurrentQueue<CliOutputLine>();
        harness.Router.OnOutput += (_, _, line) => liveOutput.Enqueue(line);

        harness.Runner.SetMode("auto-continuous");
        await harness.Runner.TickAsync(CancellationToken.None);

        var autoReviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, Slug);
        await WaitUntilAsync(
            () => Directory.Exists(autoReviewFolder),
            "The migrated fixture-backed run did not reach AutoReview.");
        Assert.Contains(liveOutput, line =>
            line.Stream == "system"
            && line.Text.Contains("model=claude-opus-5", StringComparison.Ordinal));
        var migrated = harness.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(migrated);
        Assert.Equal(ModelIds.ClaudeOpus5, migrated.Model);
        Assert.False(migrated.ModelExplicit);
        var audit = Assert.Single(
            harness.Timeline.ReadAll(autoReviewFolder),
            item => item.Kind == TimelineEventKinds.ModelMigrated);
        Assert.Equal(ModelIds.ClaudeOpus48, audit.Details?["from"]);
        Assert.Equal(ModelIds.ClaudeOpus5, audit.Details?["to"]);
    }

    private Harness BuildHarness(
        FixtureSpawner spawner,
        string fixturePath,
        string executionEngine,
        bool enableSafeModelMigration = false)
    {
        var rulesPath = Path.Combine(_workspace, "agent-rules.md");
        File.WriteAllText(rulesPath, "Follow the task and finish with one terminal sentinel.\n");
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["AgentRules:CorePath"] = rulesPath,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["WatchPaths:0:RepositoryPath"] = _watchPath,
            ["ReviewDecisionOrchestrator:Enabled"] = "true",
            ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
            ["ReviewDecisionOrchestrator:AspectsEnabled"] = "true",
            ["ReviewDecisionOrchestrator:MaxParallelReviews"] = "4",
            ["ReviewDecisionOrchestrator:MaxAutoReissueAttempts"] = "3",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            config,
            prompts,
            oneShotRegistry: new CliOneShotRegistry([new SummaryOneShot()]));
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var notifier = new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            notifier,
            NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetCliExecutionEngine(Project, executionEngine);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var cli = GenericCliExecutionService.ForClaude(
            NullLogger<GenericCliExecutionService>.Instance,
            config,
            new CliUsageParserRegistry([new ClaudeUsageParser()]),
            new CliModelRegistry());
        cli.SetCliPath(executionEngine == CliExecutionEngines.Legacy
            ? WriteLegacyFixtureExecutable(fixturePath)
            : "node");
        cli.CarOptionsCustomizer = options => options with { Spawner = spawner };
        var router = new CliRouter(cli);
        var busStore = new AgentMessageBusStore();
        var bus = new AgentMessageBusBridge(
            busStore, config, NullLogger<AgentMessageBusBridge>.Instance);
        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var quotaCache = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quota = new QuotaService(
            NullLogger<QuotaService>.Instance, [], config, quotaCache);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHalt = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(
            config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHalt);
        var pickupOwner = new PickupLockOwner
        {
            Pid = Environment.ProcessId,
            Hostname = Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "car-acceptance-test",
            BackendPort = 0,
        };
        ModelMigrationCoordinator? modelMigrations = null;
        if (enableSafeModelMigration)
        {
            var catalog = new ModelMigrationCatalogService(
                () => _workspace,
                NullLogger<ModelMigrationCatalogService>.Instance);
            var policyState = new ModelRoutingPolicyStateStore(
                config,
                NullLogger<ModelRoutingPolicyStateStore>.Instance);
            modelMigrations = new ModelMigrationCoordinator(
                catalog,
                policyState,
                mutations,
                timeline,
                bus,
                config,
                configurationWriter: null!,
                NullLogger<ModelMigrationCoordinator>.Instance,
                isModelAvailable: _ => true,
                hasDatedPrice: _ => true);
        }

        var runner = new ProjectRunner(
            Project,
            new WatchPathEntry
            {
                Name = Project,
                Path = _watchPath,
                RootPath = _watchPath,
                RepositoryPath = _watchPath,
            },
            NullLogger<ProjectRunner>.Instance,
            scanner,
            states,
            sessions,
            router,
            summary,
            prompts,
            transitions,
            chatLog,
            mutations,
            orchestratorLog,
            new OrchestratorRunner(cli, NullLogger<OrchestratorRunner>.Instance),
            new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance),
            settings,
            quota,
            quotaCaps,
            git,
            pickupFailures,
            infraBreaker,
            taskAccess,
            bus,
            pickupLock: new PickupLockFile(NullLogger<PickupLockFile>.Instance),
            pickupLockOwner: pickupOwner,
            pipelineLog: pipelineLog,
            timeline: timeline,
            modelMigrations: modelMigrations);

        return new Harness(
            config, summary, scanner, states, chatLog, prompts, mutations, git,
            settings, transitions, taskAccess, router, busStore, pipelineLog, timeline, runner);
    }

    private static ReviewDecisionOrchestrator BuildReviewOrchestrator(
        Harness harness,
        IBuildTestGateRunner buildGate,
        Action onAspect)
    {
        // The aspect steps are recorded by AspectRunnerService against its OWN
        // pipeline log, not the orchestrator's. Without this the aspect runs
        // happen but never reach pipeline-execution.json, and Complete()
        // terminalizes the four pre-seeded aspect steps to Skipped.
        var aspectRunner = new AspectRunnerService(
            harness.Prompts,
            NullLogger<AspectRunnerService>.Instance,
            pipelineLog: harness.PipelineLog);
        aspectRunner.CliRunner = (_, _, _, _, _, _) =>
        {
            onAspect();
            return Task.FromResult(
                "[[ASPECT_VERDICT: status=pass; summary=fixture passed]]\n[[TASK_DONE]]");
        };

        return new ReviewDecisionOrchestrator(
            harness.Scanner,
            harness.States,
            harness.TaskAccess,
            harness.ChatLog,
            harness.Prompts,
            aspectRunner,
            new AutoReviewStatusSnapshot(),
            harness.Configuration,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: harness.Git,
            pipelineLog: harness.PipelineLog,
            lintScssRunner: null,
            buildTestGateRunner: buildGate);
    }

    private void WriteReadyCard(
        string model = "claude-sonnet-4-5",
        bool modelExplicit = true)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Ready, Slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"),
            "Run the deterministic local CAR acceptance task.\n");
        File.WriteAllText(Path.Combine(folder, "task.json"),
            $$"""
            {
              "id": "{{Slug}}",
              "title": "Local CAR acceptance",
              "state": "{{TaskStates.Ready}}",
              "order": 1,
              "agent": "claude",
              "cliType": "claude",
              "model": "{{model}}",
              "modelExplicit": {{modelExplicit.ToString().ToLowerInvariant()}},
              "ownerClientId": "local-default"
            }
            """);
    }

    private void WriteMigrationCatalog()
    {
        var path = Path.Combine(_workspace, ModelMigrationCatalogService.CatalogRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "$schema": "model-migrations.v1.schema.json",
          "schemaVersion": 1,
          "catalogVersion": "2026-09-06",
          "evidenceAsOfDate": "2026-09-06",
          "defaultStrategy": "latestInFamily",
          "authority": {
            "rules": "docs/model-migrations.md",
            "routingPolicy": "docs/system/domains/model-routing-policy.md",
            "priceCatalog": "src/TokenEconomy/catalog/model-prices.json"
          },
          "costClassOrder": ["economy", "standard", "premium"],
          "migrations": [{
            "from": "claude-opus-4-8",
            "to": "claude-opus-5",
            "family": "claude-opus",
            "vendor": "anthropic",
            "generationOrder": { "from": 408, "to": 500 },
            "costClassFrom": "premium",
            "costClassTo": "premium",
            "ladderCompatible": true,
            "contextChange": "increase",
            "evidence": {
              "kind": "controlledBenchmark",
              "reference": "benchmarks/result.json",
              "conclusion": "noRegression",
              "summary": "Both models passed the same cases."
            },
            "safeAuto": true,
            "since": "2026-09-06",
            "note": "Fixture migration rule."
          }],
          "taskClassRecommendations": [{}, {}, {}, {}, {}]
        }
        """);
    }

    private void InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "car-acceptance@example.invalid");
        RunGit("config", "user.name", "CAR Acceptance Test");
        File.WriteAllText(Path.Combine(_watchPath, "README.md"), "fixture repository\n");
        RunGit("add", "README.md");
        RunGit("commit", "-q", "-m", "seed");
        RunGit("checkout", "-q", "-b", "develop");
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _watchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git did not start");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), failure);
    }

    private static void RequireNode()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Skip.If(probe == null || !probe.WaitForExit(8_000) || probe.ExitCode != 0,
                "node is not on PATH; backend CAR fixture replay requires Node.js.");
        }
        catch
        {
            Skip.If(true, "node is not on PATH; backend CAR fixture replay requires Node.js.");
        }
    }

    private string WriteLegacyFixtureExecutable(string fixturePath)
    {
        var executablePath = Path.Combine(_workspace, "fixture-claude");
        var fakeCliPath = Path.Combine(
            RepoRoot(), "testdata", "cli-fixtures", "fake-cli.mjs");
        var script = string.Join('\n',
            "#!/bin/sh",
            "if [ \"$1\" = \"--version\" ]; then",
            "  printf '%s\\n' 'fixture-claude 1.0.0'",
            "  exit 0",
            "fi",
            $"export FAKE_CLI_FIXTURE={ShellQuote(fixturePath)}",
            "export FAKE_CLI_NO_STDIN=1",
            $"exec node {ShellQuote(fakeCliPath)} \"$@\"",
            string.Empty);
        File.WriteAllText(executablePath, script);
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return executablePath;
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var current = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln was not found above the test source.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best effort after process shutdown */ }
    }

    private sealed class FixtureSpawner(string fixturePath) : ICliProcessSpawner
    {
        public bool Spawned { get; private set; }
        public IReadOnlyList<string> PreparedArgv { get; private set; } = [];

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            PreparedArgv = startInfo.ArgumentList.ToList();
            startInfo.FileName = "node";
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add(Path.Combine(
                RepoRoot(), "testdata", "cli-fixtures", "fake-cli.mjs"));
            startInfo.ArgumentList.Add(fixturePath);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            Spawned = true;
            return new CliSpawn(
                process,
                startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
                process.StandardOutput,
                process.StandardError);
        }
    }

    private sealed class SummaryOneShot : ICliOneShot
    {
        public string CliType => CliTypes.Claude;

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            const string markdown = """
                ## Summary
                Done and verified by the deterministic CAR fixture.

                Result: Success

                ## Open Items
                None
                """;
            var requestedAt = DateTime.UtcNow;
            var completedAt = requestedAt.AddMilliseconds(1);
            return Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: markdown,
                Stderr: string.Empty,
                Duration: completedAt - requestedAt,
                ParsedText: markdown,
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: requestedAt,
                    CompletedAt: completedAt,
                    TotalMs: 1),
                Error: null));
        }
    }

    private sealed class PassingBuildGate : IBuildTestGateRunner
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Ok,
                ExitCode: 0,
                DurationMs: 5,
                Output: "deterministic build and test gate passed",
                Reason: "build gate passed",
                RanBackendBuild: true,
                RanFrontendBuild: false));
        }
    }

    private sealed record Harness(
        IConfiguration Configuration,
        SummaryGenerationService Summary,
        TaskScannerService Scanner,
        TaskStateMachine States,
        OrchestratorChatLog ChatLog,
        RuntimePromptService Prompts,
        TaskMutationService Mutations,
        GitService Git,
        ProjectSettingsService Settings,
        TaskTransitionService Transitions,
        AgentStudio.TaskAccess.ITaskAccess TaskAccess,
        CliRouter Router,
        AgentMessageBusStore BusStore,
        PipelineExecutionLog PipelineLog,
        TimelineLog Timeline,
        ProjectRunner Runner);
}
