using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression test for the multi-second lag the user hit on /api/tasks and
/// /api/tasks/grouped after the auto-loop snapshot was folded onto every
/// TaskInfo.
///
/// <para>
/// Root cause that this test pins down: <c>WithRuntime</c> looked up the
/// auto-loop state via <c>TaskRunnerService.GetStuckLoopStateForJob(jobId,
/// watchPath)</c>, which called <c>TaskScannerService.FindJob</c>, which
/// performed a full <c>ScanAllJobs</c> (disk walk + JSON parse) on every
/// invocation. With ~150 jobs that meant the grouped endpoint did 150
/// full disk rescans per HTTP call, taking 7-15 seconds. The frontend
/// polls grouped jobs every 5 seconds, so the UI was permanently
/// blocked behind the previous poll.
/// </para>
///
/// <para>
/// The contract being locked: enriching N TaskInfos with WithRuntime must
/// be O(N) in cheap in-memory lookups, with no per-job disk I/O. We
/// assert the runtime overlay phase completes in &lt; 1 second on a
/// realistic board of 200 jobs. That ceiling is generous (the real fix
/// brings it under 50 ms); we leave headroom for a slow CI runner.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public class JobsEndpointPerfTests : IDisposable
{
    private readonly string _watchPath;

    public JobsEndpointPerfTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-perf-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void WithRuntime_Over200Jobs_FinishesWellUnderOneSecond()
    {
        // Arrange — populate the watch path with 200 jobs split across the
        // archive lane (the bulk of a real-world board accumulates there).
        const int jobCount = 200;
        const string projectName = "perf-test";
        for (var i = 0; i < jobCount; i++)
        {
            WriteJob(TaskStates.Archive, $"job-{i:D4}");
        }

        var (router, runners) = BuildRuntime(projectName);
        var scanner = BuildScanner();

        // Warm one scan so the JIT / file system cache are settled — we are
        // measuring the overlay, not the first-touch cost.
        var jobs = scanner.ScanAllJobs();
        Assert.Equal(jobCount, jobs.Count);
        _ = jobs.Select(j => TaskEndpointHelpersAccessor.WithRuntime(j, router, runners)).ToList();

        // Act — measure the overlay only. Even if ScanAllJobs gets faster
        // later, the regression we are guarding against was inside the
        // overlay (per-job FindJob causing a full rescan).
        var sw = Stopwatch.StartNew();
        var enriched = jobs.Select(j => TaskEndpointHelpersAccessor.WithRuntime(j, router, runners)).ToList();
        sw.Stop();

        // Assert — generous ceiling. The pre-fix path took ~7-15s for 144
        // jobs; the post-fix path is &lt; 50ms. 1000ms catches the regression
        // on any reasonable CI runner without flaking on slow ones.
        Assert.Equal(jobCount, enriched.Count);
        Assert.True(
            sw.ElapsedMilliseconds < 1000,
            $"WithRuntime over {jobCount} jobs took {sw.ElapsedMilliseconds} ms; " +
            "the auto-loop / summary lookups must be O(1) per job and never re-scan disk. " +
            "If this assertion fires, look at TaskRunnerService.GetStuckLoopStateForJob and " +
            "any other helper that might be calling TaskScannerService.FindJob inside the " +
            "per-job overlay loop.");
    }

    [Fact]
    public void BuildTokenLookup_UsesCanonicalAggregatorOncePerProject()
    {
        var jobs = new[]
        {
            MakeJob("job-a", "project-a", Path.Combine(_watchPath, "a")),
            MakeJob("job-b", "project-a", Path.Combine(_watchPath, "a")),
            MakeJob("job-a", "project-b", Path.Combine(_watchPath, "b")),
        };
        var tokens = new FakeTokenAggregator(new Dictionary<string, Dictionary<string, TaskTokenSummary>>(StringComparer.OrdinalIgnoreCase)
        {
            ["project-a"] = new(StringComparer.Ordinal)
            {
                ["job-a"] = new TaskTokenSummary { TotalTokens = 10 },
                ["job-b"] = new TaskTokenSummary { TotalTokens = 20 },
            },
            ["project-b"] = new(StringComparer.Ordinal)
            {
                ["job-a"] = new TaskTokenSummary { TotalTokens = 30 },
            },
        });

        var lookup = TaskEndpointHelpersAccessor.BuildTokenLookup(jobs, tokens);

        Assert.Equal(2, tokens.Calls.Count);
        Assert.Contains(tokens.Calls, c => c.ProjectName == "project-a" && c.WatchPath == jobs[0].WatchPath);
        Assert.Contains(tokens.Calls, c => c.ProjectName == "project-b" && c.WatchPath == jobs[2].WatchPath);
        Assert.Equal(10, lookup[jobs[0].TaskKey].TotalTokens);
        Assert.Equal(20, lookup[jobs[1].TaskKey].TotalTokens);
        Assert.Equal(30, lookup[jobs[2].TaskKey].TotalTokens);
    }

    [Fact]
    public void GroupedPath_Over300JobsWithWarmBusProjection_FinishesUnderVerifierBudget()
    {
        const int jobCount = 300;
        const int messageCount = 50_000;
        const string projectName = "grouped-warm-bus";
        var workspace = Path.Combine(Path.GetTempPath(), "atp-bus-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var jobs = Enumerable.Range(0, jobCount)
                .Select(i => MakeJob($"job-{i:D4}", projectName, _watchPath))
                .ToList();

            WriteBusTokenMessages(workspace, projectName, jobCount, messageCount);
            var store = new AgentMessageBusStore();
            var warmed = store.WarmProject(workspace, projectName);
            Assert.Equal(messageCount, warmed);
            var tokens = BuildRealTokenAggregator(workspace, store);

            // This is the grouped endpoint's expensive enrichment path:
            // /api/tasks/grouped -> BuildTokenLookup -> WorkspacePerJob ->
            // BusBackedTokenSummaryReader.SummarizePerJob. Startup warmup
            // must make this a memory-only projection pass, not a request-
            // thread JSONL cold load that can exceed UpdateVerifier's 10s
            // per-attempt timeout.
            var sw = Stopwatch.StartNew();
            var lookup = TaskEndpointHelpersAccessor.BuildTokenLookup(jobs, tokens);
            sw.Stop();

            Assert.Equal(jobCount, lookup.Count);
            Assert.True(sw.ElapsedMilliseconds < 5_000,
                $"Grouped token lookup over {jobCount} jobs and {messageCount} warmed bus messages took {sw.ElapsedMilliseconds} ms; "
                + "the grouped endpoint must stay below the post-restart verifier budget.");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task JobsGroupedEndpoint_Over300SyntheticJobs_ReturnsWithinFiveSecondRegressionBudget()
    {
        const int jobCount = 300;
        const int messageCount = 50_000;
        const string projectName = "grouped-http-warm-bus";
        var workspace = Path.Combine(Path.GetTempPath(), "atp-grouped-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            for (var i = 0; i < jobCount; i++)
            {
                WriteJob(TaskStates.Progress, $"job-{i:D4}");
            }
            WriteBusTokenMessages(workspace, projectName, jobCount, messageCount);

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Test");
                    b.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = workspace,
                            ["WatchPaths:0:Name"] = projectName,
                            ["WatchPaths:0:Path"] = _watchPath,
                            ["WatchPaths:0:RootPath"] = _watchPath,
                        });
                    });
                });

            using var client = factory.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync("/api/tasks/grouped", timeout.Token);
            sw.Stop();

            response.EnsureSuccessStatusCode();
            Assert.Contains(response.Headers, h => h.Key == "Server-Timing"
                                                   && h.Value.Any(v => v.StartsWith("task-op;dur=", StringComparison.Ordinal)));
            var grouped = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(cancellationToken: timeout.Token);
            Assert.NotNull(grouped);
            Assert.Contains(grouped!.Keys, key => string.Equals(key, "progress", StringComparison.OrdinalIgnoreCase));
            Assert.True(sw.ElapsedMilliseconds < 5_000,
                $"/api/tasks/grouped over {jobCount} jobs and {messageCount} warmed bus messages took {sw.ElapsedMilliseconds} ms; "
                + "the grouped endpoint must stay below the post-restart verifier regression budget.");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task TaskListEndpoints_ColdAndHeadChurn_StartNoGitProcessAndReturnUnderOneSecond()
    {
        var root = Path.Combine(Path.GetTempPath(), "task-list-git-perf-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var jobs = Path.Combine(root, "jobs");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(workspace);

        try
        {
            RunGit(repo, "init", "-q", "-b", "main");
            RunGit(repo, "config", "user.email", "test@example.com");
            RunGit(repo, "config", "user.name", "Task List Test");
            File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
            RunGit(repo, "add", "README.md");
            RunGit(repo, "commit", "-q", "-m", "seed");
            RunGit(repo, "branch", "develop");
            var anchor = RunGit(repo, "rev-parse", "HEAD").Trim();

            var taskFolder = Path.Combine(jobs, TaskStates.Completed, "task-1");
            Directory.CreateDirectory(taskFolder);
            File.WriteAllText(
                Path.Combine(taskFolder, "task.json"),
                JsonSerializer.Serialize(new
                {
                    id = "task-1",
                    key = "PERF-1",
                    title = "Task list Git projection performance",
                    state = TaskStates.Completed,
                    order = 1,
                    commits = new[]
                    {
                        new
                        {
                            sha = anchor,
                            shortSha = anchor[..7],
                            message = "seed",
                            filesChanged = 1,
                            files = new[] { "README.md" },
                        },
                    },
                }));

            var telemetry = new StructuredTelemetryLoggerProvider();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureLogging(logging => logging.AddProvider(telemetry));
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = workspace,
                            ["WatchPaths:0:Name"] = "task-list-perf",
                            ["WatchPaths:0:Path"] = jobs,
                            ["WatchPaths:0:RootPath"] = repo,
                            ["WatchPaths:0:RepositoryPath"] = repo,
                        });
                    });
                });

            using var client = factory.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var readiness = await client.GetAsync("/healthz", timeout.Token);
            readiness.EnsureSuccessStatusCode();
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync("/api/tasks", timeout.Token);
            stopwatch.Stop();

            response.EnsureSuccessStatusCode();
            var rollup = Assert.Single(telemetry.Rollups("tasks/list"));
            Assert.Equal(0, rollup.Spawns);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"GET /api/tasks took {stopwatch.ElapsedMilliseconds}ms; the list budget is under 1000ms.");

            var initialRefresh = await telemetry.WaitForRollupAsync(
                "tasks/list-refresh",
                expectedCount: 1,
                timeout.Token);
            Assert.True(initialRefresh[0].Spawns > 0);

            File.WriteAllText(Path.Combine(repo, "head-churn.txt"), "new HEAD\n");
            RunGit(repo, "add", "head-churn.txt");
            RunGit(repo, "commit", "-q", "-m", "test: move HEAD");
            await Task.Delay(TaskListGitProjectionCache.RefreshInterval + TimeSpan.FromMilliseconds(250), timeout.Token);

            stopwatch.Restart();
            using var churnResponse = await client.GetAsync("/api/tasks", timeout.Token);
            stopwatch.Stop();
            churnResponse.EnsureSuccessStatusCode();
            var listRollups = telemetry.Rollups("tasks/list");
            Assert.Equal(2, listRollups.Count);
            Assert.All(listRollups, item => Assert.Equal(0, item.Spawns));
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"GET /api/tasks after HEAD churn took {stopwatch.ElapsedMilliseconds}ms; the list budget is under 1000ms.");

            using var groupedResponse = await client.GetAsync("/api/tasks/grouped", timeout.Token);
            groupedResponse.EnsureSuccessStatusCode();
            Assert.Equal(0, Assert.Single(telemetry.Rollups("tasks/grouped")).Spawns);

            // AGT-2726: both board reads carry the background index stamp so a
            // client can tell "as of when" without asking git anything. /grouped
            // carries it in the body (its response is already an object);
            // /api/tasks answers with a bare array, so its stamp travels in
            // headers and the array contract is untouched.
            var groupedBody = await groupedResponse.Content
                .ReadFromJsonAsync<Dictionary<string, JsonElement>>(cancellationToken: timeout.Token);
            Assert.NotNull(groupedBody);
            Assert.Contains(groupedBody!.Keys, key => string.Equals(key, "gitStateAt", StringComparison.OrdinalIgnoreCase));
            var staleKey = Assert.Single(
                groupedBody.Keys,
                key => string.Equals(key, "gitStateStale", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                groupedBody[staleKey].ValueKind,
                new[] { JsonValueKind.True, JsonValueKind.False });

            Assert.True(churnResponse.Headers.Contains(GitStateHeaders.At));
            Assert.Contains(
                churnResponse.Headers.GetValues(GitStateHeaders.Stale),
                value => value is "true" or "false");

            var refreshes = await telemetry.WaitForRollupAsync(
                "tasks/list-refresh",
                expectedCount: 2,
                timeout.Token);
            Assert.True(refreshes[1].Spawns > 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void WithRuntime_OutsideProgress_ClearsExecutionOverlay()
    {
        // Single-source-of-truth contract (Lane > Execution-Status >
        // Default): the wire overlay may only surface a CLI Execution
        // snapshot for a task that is actually in 3-progress. A task that
        // has moved on to 4-auto-review / 5-human-review / 6-completed /
        // 7-archive must come back with Execution == null even when the CLI
        // driver still holds a live "running" snapshot for that TaskKey —
        // the driver retains a ProcInfo ~30 min post-exit, and a foreign
        // backend can keep one alive across a move. Without the lane gate
        // the per-card pill renders that stale snapshot as a misleading
        // "Running" badge on a card that is not executing in this lane.
        const string projectName = "lane-gate";
        var (router, runners) = BuildRuntime(projectName);

        // Swap the Claude driver (the router's default route) for a fake
        // that always reports a live "running" execution, so the assertion
        // exercises the state gate rather than an empty _processes dict that
        // would return null for every lane anyway.
        var sentinel = new CliExecution
        {
            JobId = "task-7",
            TaskKey = $"{_watchPath}::task-7",
            ProcessId = 4242,
            StartedAt = DateTime.UtcNow,
            Status = "running",
        };
        InjectExecutionDriver(router, sentinel);

        TaskInfo At(string state) => MakeJob("task-7", projectName, _watchPath) with
        {
            State = state,
            CliType = CliTypes.Claude,
        };

        // 3-progress → overlay surfaces the live running snapshot.
        var progress = TaskEndpointHelpersAccessor.WithRuntime(At(TaskStates.Progress), router, runners);
        Assert.NotNull(progress.Execution);
        Assert.Equal("running", progress.Execution!.Status);

        // Every lane past 3-progress → overlay clears it to null.
        foreach (var state in new[] { TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Completed, TaskStates.Archive })
        {
            var enriched = TaskEndpointHelpersAccessor.WithRuntime(At(state), router, runners);
            Assert.True(
                enriched.Execution is null,
                $"Execution must be null for state '{state}', but the overlay surfaced status '{enriched.Execution?.Status}'.");
        }
    }

    private static void InjectExecutionDriver(CliRouter router, CliExecution execution)
    {
        // CliRouter._byType is the private cli-type → driver map consulted by
        // Get(). Replace the Claude entry (matches CliType = "claude" jobs)
        // with a fake so GetExecution returns a known live snapshot.
        var field = typeof(CliRouter).GetField("_byType",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var byType = (System.Collections.IDictionary)field.GetValue(router)!;
        byType[CliTypes.Claude] = new FakeRunningCliService(execution);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static TaskInfo MakeJob(string id, string projectName, string watchPath) => new()
    {
        Id = id,
        TaskKey = $"{watchPath}::{id}",
        Title = id,
        State = TaskStates.Progress,
        ProjectName = projectName,
        WatchPath = watchPath,
        FolderPath = Path.Combine(watchPath, TaskStates.Progress, id),
    };

    private static void WriteBusTokenMessages(string workspace, string projectName, int jobCount, int messageCount)
    {
        var day = new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc);
        var path = AgentMessageBusPaths.DayFile(workspace, projectName, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        using var writer = new StreamWriter(stream);
        var participant = AgentMessageBusBridge.ParticipantOrchestratorFor(projectName);
        for (var i = 0; i < messageCount; i++)
        {
            var msg = new AgentMessage
            {
                Id = "01HXYZ0000000000000000G" + i.ToString("D5"),
                CreatedAt = day.AddSeconds(i),
                ParticipantId = participant,
                Role = "actor",
                Kind = "token-usage",
                Project = projectName,
                JobId = $"job-{i % jobCount:D4}",
                Summary = "perf token sample",
                Tokens = new AgentMessageTokens(
                    Input: 100 + i % 17,
                    Output: 20 + i % 7,
                    CacheRead: i % 5,
                    CacheWrite: i % 3,
                    Model: "claude-sonnet-4"),
            };
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(msg, AgentMessageBusStore.SerializerOptions));
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000), "git did not exit within 15 seconds.");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        return output;
    }

    private static ITokenAggregator BuildRealTokenAggregator(string workspace, AgentMessageBusStore store)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = workspace,
            })
            .Build();
        var busCache = new BusAggregationCache(store);
        store.OnAppended = busCache.OnAppended;
        var summaryCache = new TokenSummaryCacheStore(config, NullLogger<TokenSummaryCacheStore>.Instance);
        return new TokenAggregationService(
            busCache,
            config,
            summaryCache,
            new BusBackedAdHocUsageReader(store, config),
            new BusBackedWorkspaceTimelineReader(store, config),
            new BusBackedProjectTokenUsageReader(
                store,
                config,
                new JobStatsMetadataCache(BuildScannerFor(workspace), config, NullLogger<JobStatsMetadataCache>.Instance),
                new ProjectTokenReceiptReader()));
    }

    private static TaskScannerService BuildScannerFor(string watchPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "grouped-warm-bus",
                ["WatchPaths:0:Path"] = watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private TaskScannerService BuildScanner()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private (CliRouter router, TaskRunnerService runners) BuildRuntime(string projectName)
    {
        var config = BuildConfig(projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);

        // Minimal CliRouter wired with all four drivers - the overlay only
        // calls router.Get(...).GetExecution(), which returns null when no
        // process is registered. That's exactly what we want for the perf
        // assertion: a fast no-op lookup.
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var codex = GenericCliExecutionService.ForCodex(NullLogger<GenericCliExecutionService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, gemini);

        var contextUsageParser = new ContextUsageParser();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var projectSettings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, projectSettings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var globalStore = new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, scanner, config);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var runners = new TaskRunnerService(
            config, NullLogger<TaskRunnerService>.Instance, scanner, states, mutations, sessions,
            router, contextUsageParser, summary, prompts, transitions, projectSettings,
            quotaService, quotaCaps,
            chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, globalBoot, git, pickupFailures, infraBreaker, taskAccess);
        return (router, runners);
    }

    private IConfiguration BuildConfig(string projectName = "perf-test")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = projectName,
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
    }
}

internal sealed record StructuredTelemetryRollup(string Label, int Spawns, long GitMs, long WallMs);

internal sealed class StructuredTelemetryLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<StructuredTelemetryRollup> _rollups = new();

    public ILogger CreateLogger(string categoryName) => new Logger(_rollups);

    public IReadOnlyList<StructuredTelemetryRollup> Rollups(string label)
        => _rollups.Where(rollup => string.Equals(rollup.Label, label, StringComparison.Ordinal)).ToList();

    public async Task<IReadOnlyList<StructuredTelemetryRollup>> WaitForRollupAsync(
        string label,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var matches = Rollups(label);
            if (matches.Count >= expectedCount) return matches;
            await Task.Delay(20, cancellationToken);
        }
    }

    public void Dispose()
    {
    }

    private sealed class Logger(
        System.Collections.Concurrent.ConcurrentQueue<StructuredTelemetryRollup> rollups) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields) return;
            var label = Field(fields, "Label")?.ToString();
            if (string.IsNullOrWhiteSpace(label)) return;
            rollups.Enqueue(new StructuredTelemetryRollup(
                label,
                Convert.ToInt32(Field(fields, "Spawns")),
                Convert.ToInt64(Field(fields, "GitMs")),
                Convert.ToInt64(Field(fields, "WallMs"))));
        }

        private static object? Field(IReadOnlyList<KeyValuePair<string, object?>> fields, string name)
        {
            foreach (var field in fields)
                if (field.Key == name) return field.Value;
            return null;
        }
    }
}

internal sealed class FakeTokenAggregator : ITokenAggregator
{
    private readonly IReadOnlyDictionary<string, Dictionary<string, TaskTokenSummary>> _perProject;
    public List<(string ProjectName, string WatchPath)> Calls { get; } = [];

    public FakeTokenAggregator(IReadOnlyDictionary<string, Dictionary<string, TaskTokenSummary>> perProject)
    {
        _perProject = perProject;
    }

    public TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null, CancellationToken ct = default) => throw new NotImplementedException();
    public ProjectTokenUsageSummary ProjectSummary(string projectName, string watchPath, DateTime? nowUtc = null) => throw new NotImplementedException();
    public ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null) => throw new NotImplementedException();
    public IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit) => throw new NotImplementedException();
    public ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId) => throw new NotImplementedException();
    public TokenSummary LifetimeSummary(string projectName, string watchPath) => throw new NotImplementedException();
    public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects) => throw new NotImplementedException();
    public TokenSummaryAggregate? CachedWorkspaceAggregate() => throw new NotImplementedException();
    public TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null) => throw new NotImplementedException();
    public AdHocUsageAggregate AdHocAggregate(DateTime? since = null) => throw new NotImplementedException();

    public Dictionary<string, TaskTokenSummary> WorkspacePerJob(string projectName, string watchPath)
    {
        Calls.Add((projectName, watchPath));
        return _perProject.TryGetValue(projectName, out var perJob)
            ? perJob
            : new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal);
    }
}

/// <summary>
/// Minimal <see cref="ICliExecutionService"/> stub that reports a fixed live
/// execution for any key. Only <see cref="CliType"/> and
/// <see cref="GetExecution"/> are exercised by the wire overlay; every other
/// member throws so an accidental call shows up loudly rather than silently
/// returning a default.
/// </summary>
internal sealed class FakeRunningCliService : ICliExecutionService
{
    private readonly CliExecution _execution;
    public FakeRunningCliService(CliExecution execution) => _execution = execution;

    public string CliType => CliTypes.Claude;
    public CliExecution? GetExecution(string jobKey) => _execution;

    public string GetCliPath() => throw new NotImplementedException();
    public bool IsAvailable() => throw new NotImplementedException();
    public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => throw new NotImplementedException();
    public Task<(CliExecution? Execution, string? Error)> StartAsync(string jobId, string jobKey, string prompt, string workingDirectory, string? sessionName = null, bool resumeSession = false, string? model = null, string? thinkingLevel = null, string? jobFolderPath = null, string? permissionMode = null, string? contextMode = null, string? executionEngine = null, CancellationToken ct = default) => throw new NotImplementedException();
    public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => throw new NotImplementedException();
    public bool SendInput(string jobKey, string input) => throw new NotImplementedException();
    public List<CliOutputLine> GetOutput(string jobKey) => throw new NotImplementedException();
    public void DiscardPersistedOutput(string jobKey) => throw new NotImplementedException();
    public void ReleaseOutputResources(string jobKey) { }
    public SessionUsage? GetLastUsage(string jobKey) => throw new NotImplementedException();
    public bool IsRunningForProject(string rootPath) => throw new NotImplementedException();
    public DateTime? GetLastStreamedAt(string jobKey) => throw new NotImplementedException();
    public WatchdogState GetWatchdogState(string jobKey) => throw new NotImplementedException();
    public void SetWatchdogState(string jobKey, WatchdogState state) => throw new NotImplementedException();
    public void ReattachOnStartup() { }
    public Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default) => throw new NotImplementedException();
    public bool IsCompatibleSessionName(string? sessionName) => throw new NotImplementedException();

    public event Action<string, CliOutputLine>? OnOutput;
    public event Action<string, CliExecution>? OnStarted;
    public event Action<string, CliExecution>? OnFinished;
    public event Action<string, CliRunEvent>? OnRunEvent;
}

/// <summary>
/// TaskEndpointHelpers.WithRuntime is internal; this thin accessor lets the
/// regression test reach it without making the helper public on its own.
/// Lives in the test project so the production surface stays unchanged.
/// </summary>
internal static class TaskEndpointHelpersAccessor
{
    public static TaskInfo WithRuntime(TaskInfo job, CliRouter router, TaskRunnerService runners)
    {
        // Reflection over the internal helper. Keeps the production access
        // modifier honest while still letting the test call into it.
        var t = typeof(AgentStudio.Tasks.TaskCrudEndpoints).Assembly
            .GetType("AgentStudio.Tasks.TaskEndpointHelpers")!;
        var m = t.GetMethod("WithRuntime",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            new[] { typeof(TaskInfo), typeof(CliRouter), typeof(TaskRunnerService) })!;
        return (TaskInfo)m.Invoke(null, new object[] { job, router, runners })!;
    }

    public static Dictionary<string, TaskTokenSummary> BuildTokenLookup(IEnumerable<TaskInfo> jobs, ITokenAggregator tokens)
    {
        var t = typeof(AgentStudio.Tasks.TaskCrudEndpoints).Assembly
            .GetType("AgentStudio.Tasks.TaskEndpointHelpers")!;
        var m = t.GetMethod("BuildTokenLookup",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            new[] { typeof(IEnumerable<TaskInfo>), typeof(ITokenAggregator) })!;
        return (Dictionary<string, TaskTokenSummary>)m.Invoke(null, new object[] { jobs, tokens })!;
    }
}
