

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract <c>BusAggregationCache</c> ships to the
/// <c>/token-aggregate</c> endpoint: append-time O(1) updates, correct
/// sum/group dimensions, since/until fallback that does not re-scan the
/// whole projection.
/// </summary>
public class BusAggregationCacheTests : IDisposable
{
    private readonly string _workspace;

    public BusAggregationCacheTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "bus-agg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Aggregate_SumsByModelParticipantAndDay()
    {
        var store = new AgentMessageBusStore();
        var cache = new BusAggregationCache(store);
        store.OnAppended = cache.OnAppended;

        var project = "agent-taskboard";
        var d1 = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc);

        await store.AppendAsync(_workspace, TokenMsg("01HX0000000000000000000001", project, "agent:claude",
            "claude-sonnet-4-6", input: 1000, output: 200, cacheRead: 5000, createdAt: d1));
        await store.AppendAsync(_workspace, TokenMsg("01HX0000000000000000000002", project, "agent:claude",
            "claude-sonnet-4-6", input: 500, output: 100, cacheRead: 4500, createdAt: d1));
        await store.AppendAsync(_workspace, TokenMsg("01HX0000000000000000000003", project, "agent:codex",
            "gpt-5-codex", input: 800, output: 90, cacheRead: 0, createdAt: d2));

        var agg = cache.Aggregate(_workspace, project, since: null, until: null);

        Assert.Equal(3, agg.TotalMessages);
        Assert.Equal(2300, agg.Totals.Input);
        Assert.Equal(390, agg.Totals.Output);
        Assert.Equal(9500, agg.Totals.CacheRead);

        Assert.Equal(2, agg.ByModel.Count);
        var claudeBucket = agg.ByModel.Single(b => b.Key == "claude-sonnet-4-6");
        Assert.Equal(1500, claudeBucket.Input);
        Assert.Equal(300, claudeBucket.Output);
        Assert.Equal(9500, claudeBucket.CacheRead);
        Assert.Equal(2, claudeBucket.Messages);

        var codexBucket = agg.ByModel.Single(b => b.Key == "gpt-5-codex");
        Assert.Equal(800, codexBucket.Input);

        Assert.Equal(2, agg.ByParticipant.Count);
        Assert.Equal(2, agg.ByDay.Count);
        Assert.Equal("2026-05-10", agg.ByDay[0].Key);
        Assert.Equal("2026-05-11", agg.ByDay[1].Key);
    }

    [Fact]
    public async Task Aggregate_SinceUntilFiltersUseProjection()
    {
        var store = new AgentMessageBusStore();
        var cache = new BusAggregationCache(store);
        store.OnAppended = cache.OnAppended;

        var project = "p";
        var d1 = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc);
        var d3 = new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc);

        await store.AppendAsync(_workspace, TokenMsg("01HX0000000000000000000010", project, "agent:claude",
            "claude-sonnet-4-6", input: 100, output: 10, createdAt: d1));
        await store.AppendAsync(_workspace, TokenMsg("01HX0000000000000000000011", project, "agent:claude",
            "claude-sonnet-4-6", input: 200, output: 20, createdAt: d2));
        await store.AppendAsync(_workspace, TokenMsg("01HX0000000000000000000012", project, "agent:claude",
            "claude-sonnet-4-6", input: 300, output: 30, createdAt: d3));

        var middleOnly = cache.Aggregate(_workspace, project,
            since: d2.AddMinutes(-5), until: d2.AddMinutes(5));

        Assert.Equal(1, middleOnly.TotalMessages);
        Assert.Equal(200, middleOnly.Totals.Input);
        Assert.Equal(d2.AddMinutes(-5), middleOnly.Since);
    }

    [Fact]
    public async Task OnAppended_IgnoresMessagesWithoutTokens()
    {
        var store = new AgentMessageBusStore();
        var cache = new BusAggregationCache(store);
        store.OnAppended = cache.OnAppended;

        await store.AppendAsync(_workspace, new AgentMessage
        {
            Id = "01HX0000000000000000000020",
            CreatedAt = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc),
            ParticipantId = "runtime",
            Role = "system",
            Kind = "lifecycle",
            Project = "p",
            Summary = "no-tokens lifecycle event",
        });

        var agg = cache.Aggregate(_workspace, "p", since: null, until: null);
        Assert.Equal(0, agg.TotalMessages);
        Assert.Equal(0, agg.Totals.Input);
    }

    [Fact]
    public async Task LegacyOpenAiRow_IsNormalizedBeforeProjectAggregation()
    {
        var store = new AgentMessageBusStore();
        var cache = new BusAggregationCache(store);
        store.OnAppended = cache.OnAppended;

        await store.AppendAsync(_workspace, TokenMsg(
            "01HX0000000000000000000030", "p", "agent:codex",
            "gpt-5.6-sol", input: 100, output: 5, cacheRead: 80));

        var aggregate = cache.Aggregate(_workspace, "p", since: null, until: null);

        Assert.Equal(20, aggregate.Totals.Input);
        Assert.Equal(80, aggregate.Totals.CacheRead);
        Assert.Equal(105, aggregate.Totals.Input + aggregate.Totals.CacheRead + aggregate.Totals.Output);
    }

    private static AgentMessage TokenMsg(
        string id, string project, string participantId, string model,
        long input, long output, long cacheRead = 0, DateTime? createdAt = null)
        => new()
        {
            Id = id,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            ParticipantId = participantId,
            Role = "evidence",
            Kind = "token-usage",
            Project = project,
            Summary = $"tokens in={input} out={output}",
            Tokens = new AgentMessageTokens(
                Input: input,
                Output: output,
                CacheRead: cacheRead == 0 ? null : cacheRead,
                Model: model),
        };
}
