using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Tasks;
using AgentStudio.Tokens;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The per-project token snapshot (merged bus history + every task.json receipt,
/// archive included) is the heaviest per-request enrichment on the board polls.
/// It is memoized against the <see cref="TaskIndexCache"/> snapshot generation:
/// a warm poll where nothing changed reuses the snapshot, and a new receipt
/// becomes visible within one generation (the next mutation/watcher/TTL rescan).
/// </summary>
// clock-independent: receipt dates are fixture data; assertions compare cache generations and token totals.
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
        // Publish an initial generation so the reader has a stable version stamp.
        indexCache.GetSnapshot();

        var first = reader.WorkspacePerJob(ProjectName, _watchPath);
        Assert.True(first.TryGetValue("AGT-1", out var firstSummary));
        var firstTotal = firstSummary!.TotalTokens;
        Assert.True(firstTotal > 0);

        // Rewrite the receipt with a much larger total WITHOUT invalidating the
        // index cache: the generation is unchanged, so the memoized snapshot must
        // still report the original total.
        WriteReceipt("AGT-1", totalTokens: 99_000, inputTokens: 90_000, outputTokens: 9_000);
        var cached = reader.WorkspacePerJob(ProjectName, _watchPath);
        Assert.Equal(firstTotal, cached["AGT-1"].TotalTokens);

        // Advancing the generation (a mutation-class refresh) makes the new
        // receipt visible on the next read.
        indexCache.ForceRefresh();
        var refreshed = reader.WorkspacePerJob(ProjectName, _watchPath);
        Assert.True(refreshed["AGT-1"].TotalTokens > firstTotal);
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
