using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every fold receives an explicit "now"; fixture dates never meet the wall clock.

/// <summary>
/// Runtime read path of the project token surfaces: the merged ledger
/// (bus history, durable task receipts, chat fallback receipts) read by
/// <see cref="TokenAggregationService"/> and folded by the shared
/// pure folds.
/// </summary>
public sealed class ProjectTokenLedgerReaderTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string ProjectName = "agent-taskboard";

    public ProjectTokenLedgerReaderTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "project-token-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _watchPath = Path.Combine(_workspace, "watched");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly DateTime Now = new(2026, 5, 11, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildSummary_Last7d_IsAnExactRollingWindow()
    {
        var jobsById = BuildJobs(("real-job", "Implement dashboard"));
        var entries = new[]
        {
            MakeEntry("claude-opus-4-7", 100, 10, Now.AddDays(-7), jobId: "real-job"),
            MakeEntry("claude-opus-4-7", 200, 20, Now.AddDays(-7).AddTicks(-1), jobId: "real-job"),
            MakeEntry("claude-opus-4-7", 300, 30, Now.AddHours(-1), jobId: "real-job"),
            MakeEntry("claude-opus-4-7", 400, 40, Now.AddTicks(1), jobId: "real-job"),
        };

        var result = ProjectTokenUsageFold.Summary(
            ProjectName, entries, jobsById, Now);

        Assert.Equal(440, result.Last7dTotalTokens);
        Assert.Equal(440, result.Last7dJobTokens);
        Assert.Equal(0, result.Last7dSupportingTokens);
        Assert.Equal(0, result.Last7dOrchestratorTokens);
        Assert.Equal(2, result.Last7dCalls);
    }

    [Fact]
    public async Task InstanceReader_UsesStatsMetadata_ForArchivedSupportingJob()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        WriteJob(TaskStates.Archive, "support-archived", "Security audit of archived token flow");
        var (bridge, store) = BuildStack();
        await bridge.EmitTokenUsageAsync(
            project: ProjectName,
            jobId: "support-archived",
            participantId: "support:security-audit",
            topic: "security-audit",
            usage: new OrchestratorTokenUsage
            {
                Model = "claude-haiku-4-5",
                InputTokens = 10_000,
                OutputTokens = 1_000,
            },
            createdAt: Now);
        Assert.Single(store.Query(_workspace, ProjectName, new AgentMessageQuery(
            ParticipantId: "support:security-audit",
            Kind: "token-usage")));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = "60",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var hotCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(hotCache);
        Assert.DoesNotContain(scanner.ScanAllJobs(), j => j.State == TaskStates.Archive);

        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        var reader = TokenTestServices.Aggregator(store, config, stats, new ProjectTokenReceiptReader());

        var result = reader.ProjectSummary(ProjectName, _watchPath, Now);

        Assert.True(result.HasData);
        Assert.Equal(11_000, result.LifetimeTotalTokens);
        Assert.Equal(11_000, result.LifetimeSupportingTokens);
        Assert.Equal(0, result.LifetimeJobTokens);
    }

    [Fact]
    public void InstanceReader_MergesLegacyBusAndFlatLayoutReceipt_WithoutDoubleCounting()
    {
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var store = new AgentMessageBusStore();
        var historicalAt = new DateTime(2026, 7, 11, 18, 0, 0, DateTimeKind.Utc);
        var currentAt = now.AddHours(-2);

        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "token-aggregation", "hybrid-old-new");
        var busPath = AgentMessageBusPaths.DayFile(_workspace, ProjectName, historicalAt);
        Directory.CreateDirectory(Path.GetDirectoryName(busPath)!);
        File.Copy(Path.Combine(fixtureRoot, "legacy-token-usage.jsonl"), busPath);

        var taskFolder = Path.Combine(_watchPath, "tasks", "002", "AGT-2542");
        Directory.CreateDirectory(taskFolder);
        File.Copy(Path.Combine(fixtureRoot, "task.json"), Path.Combine(taskFolder, "task.json"));

        var reader = BuildInstanceReader(store);
        var summary = reader.ProjectSummary(ProjectName, _watchPath, now);
        var heatmap = reader.ProjectHeatmap(ProjectName, _watchPath, 30, now);
        var perJob = reader.WorkspacePerJob(ProjectName, _watchPath);

        Assert.Equal(6_600, summary.LifetimeTotalTokens);
        Assert.Equal(5_500, summary.Last24hTotalTokens);
        Assert.Equal(5_500, summary.Last7dTotalTokens);
        Assert.Equal(3, summary.LifetimeCalls);
        Assert.Equal("complete", summary.Freshness.Status);
        Assert.Equal(currentAt.ToString("o"), summary.Freshness.AsOf);
        Assert.Contains("historical-token-bus", summary.Freshness.Sources);
        Assert.Contains("task-token-receipts", summary.Freshness.Sources);
        Assert.Contains(heatmap.Jobs, job => job.JobId == "AGT-2542" && job.Total == 5_500);
        Assert.Equal(5_500, perJob["AGT-2542"].TotalTokens);
    }

    [Fact]
    public async Task InstanceReader_ReportsPartialData_WhenReceiptCannotBeParsed()
    {
        var (bridge, store) = BuildStack();
        await bridge.EmitTokenUsageAsync(
            ProjectName, "AGT-2146", AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName), "agent-run",
            new OrchestratorTokenUsage { InputTokens = 100, OutputTokens = 10 },
            Now);
        await WaitForBusCountAsync(store, 1);

        var taskFolder = Path.Combine(_watchPath, "tasks", "002", "AGT-2542");
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(Path.Combine(taskFolder, "task.json"), "{ \"id\": \"AGT-2542\", \"tokenSummary\": ");

        var result = BuildInstanceReader(store).ProjectSummary(ProjectName, _watchPath, Now);

        Assert.Equal("partial", result.Freshness.Status);
        Assert.NotNull(result.Freshness.Warning);
        Assert.Contains("could not be read", result.Freshness.Warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(110, result.LifetimeTotalTokens);
    }

    [Fact]
    public async Task PerJob_IncludesAgentTokenUsageAndPrefersRunModel()
    {
        var (bridge, store) = BuildStack();
        const string jobId = "job-model-panel";
        var startedAt = new DateTime(2026, 6, 9, 8, 0, 0, DateTimeKind.Utc);

        await bridge.EmitTokenUsageRichAsync(
            ProjectName,
            jobId,
            AgentMessageBusBridge.DeriveRunId(jobId, startedAt),
            AgentMessageBusBridge.ParticipantForCli("codex"),
            "codex-turn",
            new ParsedTurnUsage("gpt-5-codex", 10_000, 800, 2_000, 0, null, null),
            createdLatency(startedAt, startedAt.AddSeconds(12)));

        await bridge.EmitTokenUsageAsync(
            ProjectName,
            jobId,
            AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
            "orchestrator-decision",
            new OrchestratorTokenUsage
            {
                Model = "claude-haiku-4-5",
                InputTokens = 1_000,
                OutputTokens = 100,
            },
            createdAt: startedAt.AddMinutes(5));

        await WaitForBusCountAsync(store, 2);
        var perJob = BuildInstanceReader(store).WorkspacePerJob(ProjectName, _watchPath);
        var summary = perJob[jobId];

        Assert.Equal(13_900, summary.TotalTokens);
        Assert.Equal("GPT-5 Codex", summary.LastModel);
        Assert.Equal(2, summary.Entries.Count);
        Assert.Contains(summary.Entries, e => e.Model == "gpt-5-codex" && e.DisplayModel == "GPT-5 Codex");
        Assert.Contains(summary.Entries, e => e.Model == "claude-haiku-4-5" && e.DisplayModel == "Claude Haiku 4.5");

        static AgentMessageLatency createdLatency(DateTime requestedAt, DateTime completedAt)
            => new(
                RequestedAt: requestedAt,
                CompletedAt: completedAt,
                TotalMs: (long)(completedAt - requestedAt).TotalMilliseconds);
    }

    private (AgentMessageBusBridge bridge, AgentMessageBusStore store) BuildStack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        return (bridge, store);
    }

    private TokenAggregationService BuildInstanceReader(AgentMessageBusStore store)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();
        var generation = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, generation);
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        return TokenTestServices.Aggregator(store, config, stats, new ProjectTokenReceiptReader());
    }

    private static IReadOnlyDictionary<string, TaskInfo> BuildJobs(params (string Id, string Title)[] jobs)
    {
        var map = new Dictionary<string, TaskInfo>(StringComparer.Ordinal);
        foreach (var (id, title) in jobs)
        {
            map[id] = new TaskInfo
            {
                Id = id,
                Title = title,
                State = "3-progress",
                ProjectName = ProjectName,
                WatchPath = "(test)",
                FolderPath = $"(test)/{id}",
            };
        }
        return map;
    }

    private void WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            System.Text.Json.JsonSerializer.Serialize(new { id = slug, title, state, order = 1, agent = "claude" }));
    }

    private async Task WaitForBusCountAsync(AgentMessageBusStore store, int expected, int timeoutMs = 5_000)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < timeoutMs)
        {
            var got = store.Query(_workspace, ProjectName, new AgentMessageQuery(Kind: "token-usage")).Count;
            if (got >= expected) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Bus did not reach {expected} token-usage messages within {timeoutMs}ms.");
    }

    private static OrchestratorLogEntry MakeEntry(string? model, int input, int output, DateTime ts, int cacheRead = 0, int cacheCreate = 0, string? jobId = null)
        => new()
        {
            Ts = ts,
            Kind = OrchestratorLogKinds.Decision,
            Topic = "orchestrator-decision",
            Summary = "parity entry",
            JobId = jobId,
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheCreationTokens = cacheCreate,
            },
        };
}
