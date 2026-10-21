using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using AgentStudio.Tasks;
using AgentStudio.Tokens;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The independent detail usage resource caches each card separately while
/// board token aggregation retains its generation-based project snapshot.
/// </summary>
public sealed class TokenAggregationServiceCacheTests : IDisposable
{
    private const string ProjectName = "agent-taskboard";
    private readonly string _workspace;
    private readonly string _watchPath;

    public TokenAggregationServiceCacheTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "token-snapshot-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _watchPath = Path.Combine(_workspace, "watched");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void BuildPerJob_MemoizesSnapshotPerGeneration_AndRefreshesWhenGenerationAdvances()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        var reader = TokenTestServices.Aggregator(
            new AgentMessageBusStore(), config, stats, new ProjectTokenReceiptReader(), indexCache);

        WriteReceipt("AGT-1", totalTokens: 5_500, inputTokens: 5_000, outputTokens: 500);
        indexCache.GetSnapshot();

        var first = reader.WorkspacePerJob(ProjectName, _watchPath);
        Assert.True(first.TryGetValue("AGT-1", out var firstSummary));
        var firstTotal = firstSummary!.TotalTokens;
        Assert.True(firstTotal > 0);

        WriteReceipt("AGT-1", totalTokens: 99_000, inputTokens: 90_000, outputTokens: 9_000);
        var cached = reader.WorkspacePerJob(ProjectName, _watchPath);
        Assert.Equal(firstTotal, cached["AGT-1"].TotalTokens);

        indexCache.ForceRefresh();
        var refreshed = reader.WorkspacePerJob(ProjectName, _watchPath);
        Assert.True(refreshed["AGT-1"].TotalTokens > firstTotal);
    }

    [Fact]
    public void DetailUsageCache_InvalidatesOnlyThePublishedCard()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var store = new AgentMessageBusStore();
        var ledger = new ProjectTokenLedger(store, config, new ProjectTokenReceiptReader(), indexCache);
        var reader = new TaskTokenUsageCache(ledger, TokenTestServices.Pricing(), indexCache,
            new FakeTimeProvider());

        WriteReceipt("AGT-1", totalTokens: 5_500, inputTokens: 5_000, outputTokens: 500);
        WriteReceipt("AGT-2", totalTokens: 2_200, inputTokens: 2_000, outputTokens: 200);
        // Publish resident card facts before the first usage read.
        indexCache.GetSnapshot();

        var firstSummary = reader.Get(ProjectName, _watchPath, "AGT-1");
        var otherSummary = reader.Get(ProjectName, _watchPath, "AGT-2");
        Assert.NotNull(firstSummary);
        Assert.Equal(1, reader.ProjectLoadCount(ProjectName, _watchPath));
        var firstTotal = firstSummary!.TotalTokens;
        Assert.True(firstTotal > 0);
        var folder = Path.Combine(_watchPath, "3-progress", "AGT-1");
        Assert.True(TaskJsonFile.UpdateField(folder, "heartbeatAt",
            "2026-10-21T08:00:00Z", NullLogger.Instance));
        scanner.PublishCoreFromKnownFolder(folder);
        Assert.Same(firstSummary, reader.Get(ProjectName, _watchPath, "AGT-1"));
        Assert.Equal(1, reader.ProjectLoadCount(ProjectName, _watchPath));

        // A disk rewrite alone does not invalidate the resident card entry.
        WriteReceipt("AGT-1", totalTokens: 99_000, inputTokens: 90_000, outputTokens: 9_000);
        var cached = reader.Get(ProjectName, _watchPath, "AGT-1");
        Assert.Equal(firstTotal, cached?.TotalTokens);
        var heartbeat = indexCache.GetDetailFacts("AGT-1", _watchPath).Info!;
        indexCache.PublishCore(heartbeat with { Title = "Heartbeat" });
        Assert.Same(firstSummary, reader.Get(ProjectName, _watchPath, "AGT-1"));
        Assert.Equal(1, reader.ProjectLoadCount(ProjectName, _watchPath));

        // The durable writer republishes only this card. No board rescan is
        // required before the separate usage resource sees the new receipt.
        scanner.PublishCoreFromKnownFolder(folder);
        var refreshed = reader.Get(ProjectName, _watchPath, "AGT-1");
        Assert.True(refreshed!.TotalTokens > firstTotal);
        Assert.Equal(2, reader.ProjectLoadCount(ProjectName, _watchPath));
        Assert.Same(otherSummary, reader.Get(ProjectName, _watchPath, "AGT-2"));
    }

    [Fact]
    public async Task DetailUsageCache_InvalidatesCardOnBusAppend()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(index);
        index.GetSnapshot();
        var store = new AgentMessageBusStore();
        var ledger = new ProjectTokenLedger(store, config, new ProjectTokenReceiptReader(), index);
        var usage = new TaskTokenUsageCache(ledger, TokenTestServices.Pricing(), index,
            new FakeTimeProvider());
        store.OnAppended = (_, message) => usage.OnTokenUsageAppended(message);
        Assert.Null(usage.Get(ProjectName, _watchPath, "AGT-1"));

        await store.AppendAsync(_workspace, new AgentMessage
        {
            Id = "token-row-01", CreatedAt = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc),
            ParticipantId = "agent:codex", Role = "actor", Kind = "token-usage",
            Project = ProjectName, JobId = "AGT-1", Tokens = new AgentMessageTokens(100, 20, Model: "gpt-6"),
        });

        var refreshed = usage.Get(ProjectName, _watchPath, "AGT-1");
        Assert.NotNull(refreshed);
        Assert.Equal(100, refreshed.InputTokens);

        var folder = Path.Combine(_watchPath, TaskStates.Progress, "AGT-1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "AGT-1", key = "external-card-key", title = "Card", state = TaskStates.Progress,
            order = 1,
        }));
        scanner.PublishCoreFromFolder(folder, _watchPath, ProjectName, TaskStates.Progress);
        usage.OnTokenUsageAppended(new AgentMessage
        {
            Id = "token-row-02", CreatedAt = new DateTime(2026, 10, 21, 8, 1, 0, DateTimeKind.Utc),
            ParticipantId = "agent:codex", Role = "actor", Kind = "token-usage",
            Project = ProjectName, JobId = "external-card-key",
        });
        usage.Get(ProjectName, _watchPath, "AGT-1");
        Assert.Equal(3, usage.ProjectLoadCount(ProjectName, _watchPath));
    }

    [Fact]
    public async Task DetailUsageCache_ConcurrentCardChangesShareOneProjectLoad()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(index);
        var ledger = new ProjectTokenLedger(new AgentMessageBusStore(), config,
            new ProjectTokenReceiptReader(), index);
        var usage = new TaskTokenUsageCache(ledger, TokenTestServices.Pricing(), index,
            new FakeTimeProvider());
        WriteReceipt("AGT-1", 5500, 5000, 500);
        WriteReceipt("AGT-2", 2200, 2000, 200);
        index.GetSnapshot();
        Assert.NotNull(usage.Get(ProjectName, _watchPath, "AGT-1"));
        Assert.Equal(1, usage.ProjectLoadCount(ProjectName, _watchPath));

        foreach (var id in new[] { "AGT-1", "AGT-2" })
            usage.OnTokenUsageAppended(new AgentMessage
            {
                Id = "change-" + id, Kind = "token-usage", Project = ProjectName,
                JobId = id, CreatedAt = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc),
                ParticipantId = "agent:codex", Role = "actor",
            });
        var reads = await Task.WhenAll(
            Task.Run(() => usage.Get(ProjectName, _watchPath, "AGT-1")),
            Task.Run(() => usage.Get(ProjectName, _watchPath, "AGT-2")));
        Assert.All(reads, Assert.NotNull);
        Assert.Equal(2, usage.ProjectLoadCount(ProjectName, _watchPath));
    }

    [Fact]
    public async Task DetailUsageCache_ReceiptPublishedDuringWalkGetsFollowUpLoad()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(index);
        WriteReceipt("AGT-1", 5500, 5000, 500);
        index.GetSnapshot();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var walks = 0;
        var ledger = new ProjectTokenLedger(new AgentMessageBusStore(), config,
            new ProjectTokenReceiptReader(), index);
        var usage = new TaskTokenUsageCache(ledger, TokenTestServices.Pricing(), index,
            new FakeTimeProvider(), beforeProjectWalk: () =>
            {
                if (Interlocked.Increment(ref walks) != 1) return;
                entered.Set();
                release.Wait();
            });

        var first = usage.GetAsync(ProjectName, _watchPath, "AGT-1");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            WriteReceipt("AGT-1", 99000, 90000, 9000);
            scanner.PublishCoreFromKnownFolder(Path.Combine(_watchPath, TaskStates.Progress, "AGT-1"));
            var second = usage.GetAsync(ProjectName, _watchPath, "AGT-1");
            release.Set();
            var refreshed = await second;
            Assert.Equal(99000, refreshed?.TotalTokens);
            Assert.Equal(2, usage.ProjectLoadCount(ProjectName, _watchPath));
            await first;
            Assert.Equal(99000, usage.Get(ProjectName, _watchPath, "AGT-1")?.TotalTokens);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void DetailUsageCache_DuplicateLaneIdKeepsFirstReceipt()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(index);
        WriteReceipt("AGT-1", 5500, 5000, 500);
        var duplicate = Path.Combine(_watchPath, TaskStates.Ready, "AGT-1");
        Directory.CreateDirectory(duplicate);
        File.Copy(Path.Combine(_watchPath, TaskStates.Progress, "AGT-1", "task.json"),
            Path.Combine(duplicate, "task.json"));
        index.GetSnapshot();
        Assert.Equal(2, index.GetAllResidentTasks().Count(task => task.Id == "AGT-1"));
        var ledger = new ProjectTokenLedger(new AgentMessageBusStore(), config,
            new ProjectTokenReceiptReader(), index);
        var usage = new TaskTokenUsageCache(ledger, TokenTestServices.Pricing(), index,
            new FakeTimeProvider());

        Assert.NotNull(usage.Get(ProjectName, _watchPath, "AGT-1"));
        Assert.Equal(1, usage.ProjectLoadCount(ProjectName, _watchPath));
    }

    [Fact]
    public void DetailUsageCache_PrunesAbsentCardAfterLaterCompletedRescan()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(index);
        var ledger = new ProjectTokenLedger(new AgentMessageBusStore(), config,
            new ProjectTokenReceiptReader(), index);
        var usage = new TaskTokenUsageCache(ledger, TokenTestServices.Pricing(), index,
            new FakeTimeProvider());
        index.GetSnapshot();
        usage.Get(ProjectName, _watchPath, "AGT-gone");
        usage.OnTokenUsageAppended(new AgentMessage
        {
            Id = "gone-usage", Kind = "token-usage", Project = ProjectName,
            JobId = "AGT-gone", CreatedAt = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc),
            ParticipantId = "agent:codex", Role = "actor",
        });
        Assert.Equal(1, usage.EntryCount);
        Assert.Equal(1, usage.BusRevisionCount);

        index.ForceRefresh();

        Assert.Equal(0, usage.EntryCount);
        Assert.Equal(0, usage.BusRevisionCount);
    }

    private IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = "600",
            })
            .Build();

    private void WriteReceipt(string id, long totalTokens, long inputTokens, long outputTokens)
    {
        var dir = Path.Combine(_watchPath, "3-progress", id);
        Directory.CreateDirectory(dir);
        var payload = new
        {
            id,
            title = id,
            state = "3-progress",
            order = 1,
            tokenSummary = new
            {
                calls = 1,
                inputTokens,
                outputTokens,
                cacheReadTokens = 0,
                cacheCreationTokens = 0,
                totalTokens,
                allModelsPriced = true,
                lastModel = "claude-opus-4-8",
                lastUpdate = "2026-08-09T10:00:00Z",
                entries = new[]
                {
                    new
                    {
                        ts = "2026-08-09T00:00:00Z",
                        model = "claude-opus-4-8",
                        participantId = "agent:claude",
                        inputTokens,
                        outputTokens,
                        cacheReadTokens = 0,
                        cacheCreationTokens = 0,
                        modelPriced = true,
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "task.json"),
            System.Text.Json.JsonSerializer.Serialize(payload));
    }
}
