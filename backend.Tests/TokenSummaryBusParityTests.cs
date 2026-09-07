using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Phase-5 parity test for <see cref="TokenSummaryService"/>. Drives a
/// realistic mix of orchestrator-log entries through both the legacy
/// reader (<c>orchestrator.jsonl</c>) and the Phase-4 bus-backed reader
/// (<see cref="BusBackedTokenSummaryReader"/>) and asserts byte-identical
/// output across every numeric field plus the <c>byModel</c> list.
///
/// <para>
/// This is the regression guard that lets
/// <see cref="TokenAggregationService.LifetimeSummary"/> switch off
/// <see cref="TokenSummaryService.Summarize(string, string)"/> for the
/// bus-backed read path. The two readers can drift in subtle ways: the
/// (unknown)-model fallback, model-key trim/casing, the
/// <c>AllModelsPriced</c> latch when zero LLM calls are recorded, and
/// the per-model sort order. The parity assertion compares each field
/// individually so any divergence on a real record fails fast.
/// </para>
/// </summary>
public sealed class TokenSummaryBusParityTests : IDisposable
{
    private readonly string _workspace;
    private const string ProjectName = "agent-taskboard";
    private readonly string _watchPath;

    public TokenSummaryBusParityTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "token-summary-parity-" + Guid.NewGuid().ToString("N"));
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
    public async Task Summarize_MultiModelMixedPricing_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var day1 = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

        var entries = new[]
        {
            MakeEntry("claude-opus-4-7",   100_000, 10_000, day1),
            MakeEntry("claude-opus-4-7",    50_000,  5_000, day1.AddMinutes(10)),
            MakeEntry("claude-haiku-4-5",  200_000, 20_000, day1.AddHours(1)),
            // Unpriced model -> AllModelsPriced=false on both sides
            MakeEntry("future-experimental",   50,      5, day1.AddHours(2)),
            // Mixed-case model key: legacy uses OrdinalIgnoreCase bucket map
            MakeEntry("Claude-Opus-4-7",     20_000,  2_000, day1.AddHours(3)),
        };
        await WriteAllAsync(log, bridge, store, entries);

        var legacy = TokenSummaryService.Summarize(ProjectName, log.Read(_watchPath));
        var bus    = BusBackedTokenSummaryReader.SummarizeFromStore(store, _workspace, ProjectName);

        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task Summarize_OnlyUnpricedModel_AllModelsPricedFalse()
    {
        var (log, bridge, store) = BuildStack();
        var t = new DateTime(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc);

        var entries = new[]
        {
            MakeEntry("future-experimental", 1_000, 100, t),
            MakeEntry("future-experimental", 2_000, 200, t.AddMinutes(5)),
        };
        await WriteAllAsync(log, bridge, store, entries);

        var legacy = TokenSummaryService.Summarize(ProjectName, log.Read(_watchPath));
        var bus    = BusBackedTokenSummaryReader.SummarizeFromStore(store, _workspace, ProjectName);

        Assert.False(legacy.AllModelsPriced);
        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task Summarize_UnknownModelFallback_PicksSameKey()
    {
        // Bus messages with no model -> legacy maps to "(unknown)"; the
        // bus reader must do the same. Drive the missing-model case from
        // the recorder so the parity assertion catches any divergence on
        // the (unknown) fallback.
        var (log, bridge, store) = BuildStack();
        var t = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc);

        var entries = new[]
        {
            MakeEntry(model: null,             5_000, 500, t),
            MakeEntry("",                      1_000, 100, t.AddMinutes(5)),
            MakeEntry("claude-haiku-4-5",  10_000, 1_000, t.AddMinutes(10)),
        };
        await WriteAllAsync(log, bridge, store, entries);

        var legacy = TokenSummaryService.Summarize(ProjectName, log.Read(_watchPath));
        var bus    = BusBackedTokenSummaryReader.SummarizeFromStore(store, _workspace, ProjectName);

        Assert.Contains(legacy.ByModel, m => m.Model == "(unknown)");
        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task Summarize_CacheReadAndCreationFields_SurviveRoundTrip()
    {
        var (log, bridge, store) = BuildStack();
        var t = new DateTime(2026, 5, 4, 14, 0, 0, DateTimeKind.Utc);

        var entries = new[]
        {
            MakeEntry("claude-opus-4-7", 30_000, 3_000, t,            cacheRead: 5_000, cacheCreate:    400),
            MakeEntry("claude-opus-4-7", 50_000, 4_500, t.AddMinutes(5), cacheRead: 1_200, cacheCreate:    700),
            MakeEntry("claude-haiku-4-5", 80_000, 9_000, t.AddMinutes(10), cacheRead:   600, cacheCreate:  10_000),
        };
        await WriteAllAsync(log, bridge, store, entries);

        var legacy = TokenSummaryService.Summarize(ProjectName, log.Read(_watchPath));
        var bus    = BusBackedTokenSummaryReader.SummarizeFromStore(store, _workspace, ProjectName);

        Assert.True(legacy.TotalCacheReadTokens > 0);
        Assert.True(legacy.TotalCacheCreationTokens > 0);
        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task Summarize_EmptyState_BothReadersReturnZeros()
    {
        var (log, _bridge, store) = BuildStack();
        // No writes.
        await Task.CompletedTask;

        var legacy = TokenSummaryService.Summarize(ProjectName, log.Read(_watchPath));
        var bus    = BusBackedTokenSummaryReader.SummarizeFromStore(store, _workspace, ProjectName);

        Assert.Equal(0, legacy.OrchestratorLlmCalls);
        Assert.Equal(0, bus.OrchestratorLlmCalls);
        Assert.False(legacy.AllModelsPriced);
        Assert.False(bus.AllModelsPriced);
        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task SummarizePerJob_AttributesEachEntryToItsJob()
    {
        var (log, bridge, store) = BuildStack();
        var t = new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Utc);

        var entries = new[]
        {
            MakeEntry("claude-opus-4-7", 10_000, 1_000, t,                jobId: "job-a"),
            MakeEntry("claude-opus-4-7", 20_000, 2_000, t.AddMinutes(5),  jobId: "job-a"),
            MakeEntry("claude-haiku-4-5", 5_000, 500, t.AddMinutes(10),   jobId: "job-b"),
            MakeEntry("claude-haiku-4-5", 3_000, 300, t.AddMinutes(15),   jobId: null),  // no job id -> skipped
        };
        await WriteAllAsync(log, bridge, store, entries);

        var legacy = TokenSummaryService.SummarizePerJob(log.Read(_watchPath));
        var bus    = BusBackedTokenSummaryReader.SummarizePerJobFromStore(store, _workspace, ProjectName);

        Assert.Equal(legacy.Count, bus.Count);
        foreach (var (jobId, legacyTotal) in legacy)
        {
            Assert.True(bus.TryGetValue(jobId, out var busTotal), $"bus missing jobId {jobId}");
            Assert.Equal(legacyTotal.Calls,                  busTotal!.Calls);
            Assert.Equal(legacyTotal.InputTokens,            busTotal.InputTokens);
            Assert.Equal(legacyTotal.OutputTokens,           busTotal.OutputTokens);
            Assert.Equal(legacyTotal.CacheReadTokens,        busTotal.CacheReadTokens);
            Assert.Equal(legacyTotal.CacheCreationTokens,    busTotal.CacheCreationTokens);
            Assert.Equal(legacyTotal.TotalTokens,            busTotal.TotalTokens);
            Assert.Equal(legacyTotal.LastModel,              busTotal.LastModel);
            Assert.Equal(legacyTotal.Entries.Count,          busTotal.Entries.Count);
        }
    }

    [Fact]
    public async Task InstanceSummarizePerJob_IncludesAgentTokenUsageAndPrefersRunModel()
    {
        var (_, bridge, store) = BuildStack();
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

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var reader = new BusBackedTokenSummaryReader(store, config);

        var perJob = reader.SummarizePerJob(ProjectName);
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

    private static void AssertEquivalent(TokenSummary a, TokenSummary b)
    {
        Assert.Equal(a.Project,                         b.Project);
        Assert.Equal(a.OrchestratorEntries,             b.OrchestratorEntries);
        Assert.Equal(a.OrchestratorLlmCalls,            b.OrchestratorLlmCalls);
        Assert.Equal(a.TotalInputTokens,                b.TotalInputTokens);
        Assert.Equal(a.TotalOutputTokens,               b.TotalOutputTokens);
        Assert.Equal(a.TotalCacheReadTokens,            b.TotalCacheReadTokens);
        Assert.Equal(a.TotalCacheCreationTokens,        b.TotalCacheCreationTokens);
        Assert.Equal(a.EstimatedApiCostUsd,             b.EstimatedApiCostUsd);
        Assert.Equal(a.AllModelsPriced,                 b.AllModelsPriced);

        Assert.Equal(a.ByModel.Count, b.ByModel.Count);
        for (var i = 0; i < a.ByModel.Count; i++)
        {
            Assert.Equal(a.ByModel[i].Model,                 b.ByModel[i].Model);
            Assert.Equal(a.ByModel[i].Calls,                 b.ByModel[i].Calls);
            Assert.Equal(a.ByModel[i].InputTokens,           b.ByModel[i].InputTokens);
            Assert.Equal(a.ByModel[i].OutputTokens,          b.ByModel[i].OutputTokens);
            Assert.Equal(a.ByModel[i].CacheReadTokens,       b.ByModel[i].CacheReadTokens);
            Assert.Equal(a.ByModel[i].CacheCreationTokens,   b.ByModel[i].CacheCreationTokens);
            Assert.Equal(a.ByModel[i].EstimatedApiCostUsd,   b.ByModel[i].EstimatedApiCostUsd);
            Assert.Equal(a.ByModel[i].ModelPriced,           b.ByModel[i].ModelPriced);
        }
    }

    private (OrchestratorLog log, AgentMessageBusBridge bridge, AgentMessageBusStore store) BuildStack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        return (log, bridge, store);
    }

    private async Task WriteAllAsync(OrchestratorLog log, AgentMessageBusBridge bridge, AgentMessageBusStore store, IReadOnlyList<OrchestratorLogEntry> entries)
    {
        foreach (var e in entries)
        {
            log.Append(_watchPath, e);
            // Mirror onto the bus using the same shape ProjectRunner uses
            // for orchestrator-driven turns. The bus emit is fire-and-forget
            // in production; we await here to keep the parity test stable.
            if (e.TokenUsage != null)
            {
                await bridge.EmitTokenUsageAsync(
                    project: ProjectName,
                    jobId: e.JobId,
                    participantId: AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
                    topic: e.Topic,
                    usage: e.TokenUsage,
                    createdAt: e.Ts);
            }
        }
        await WaitForBusCountAsync(store, entries.Count(x => x.TokenUsage != null));
    }

    private async Task WaitForBusCountAsync(AgentMessageBusStore store, int expected, int timeoutMs = 5_000)
    {
        var participant = AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var got = store.Query(_workspace, ProjectName, new AgentMessageQuery(
                ParticipantId: participant,
                Kind: "token-usage")).Count;
            if (got >= expected) return;
            await Task.Delay(25);
        }
        var final = store.Query(_workspace, ProjectName, new AgentMessageQuery(
            ParticipantId: participant,
            Kind: "token-usage")).Count;
        Assert.Fail($"Bus did not reach {expected} orchestrator token-usage messages within {timeoutMs}ms (got {final}).");
    }

    private static OrchestratorLogEntry MakeEntry(
        string? model,
        int input,
        int output,
        DateTime ts,
        int cacheRead = 0,
        int cacheCreate = 0,
        string? jobId = null)
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
