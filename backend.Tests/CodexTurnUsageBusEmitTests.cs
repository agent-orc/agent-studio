using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Codex streaming -&gt; agent message bus contract for per-turn
/// token usage (see bug-codex-turn-completed-not-emitted-to-bus-as-token-usage).
/// <para>
/// Before the fix the Codex driver parsed <c>turn.completed</c> frames
/// correctly but never mirrored the parsed usage onto the bus, so
/// <see cref="BusAggregationCache"/>, the project token summary, and the
/// workspace quota strip all reported zero for Codex runs unless the
/// orchestrator happened to fire a Haiku decision turn. The fix routes
/// every <c>turn.completed</c> frame through <see cref="CodexUsageParser"/>
/// and emits a <c>kind:token-usage</c> message attributed to
/// <c>agent:codex</c>; this test pins the resulting bus shape and the
/// aggregation hook so a future refactor cannot silently regress either
/// half of the pipeline.
/// </para>
/// </summary>
public sealed class CodexTurnUsageBusEmitTests : IDisposable
{
    private readonly string _workspace;
    private readonly AgentMessageBusStore _store;
    private readonly AgentMessageBusBridge _bridge;
    private readonly BusAggregationCache _cache;

    public CodexTurnUsageBusEmitTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "codex-turn-bus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        _store = new AgentMessageBusStore();
        _bridge = new AgentMessageBusBridge(_store, config, NullLogger<AgentMessageBusBridge>.Instance);
        _cache = new BusAggregationCache(_store);
        _store.OnAppended = (ws, msg) => _cache.OnAppended(ws, msg);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The exact <c>turn.completed</c> frame the user captured on 2026-05-11
    /// during the first Codex run in Lotta Dashboard. Parsed, then routed
    /// through the bus exactly the way the production path now does. The
    /// emission must show up as a <c>kind:token-usage</c> message attributed
    /// to <c>agent:codex</c> with <c>cached_input_tokens</c> mapped onto
    /// <see cref="AgentMessageTokens.CacheRead"/> and a populated
    /// context-window snapshot for <c>gpt-5-codex</c>.
    /// </summary>
    [Fact]
    public async Task BugFixtureFrame_EmitsTokenUsageMessageWithCodexAgentAttribution()
    {
        var frame = JsonDocument.Parse(
            """{"type":"turn.completed","usage":{"input_tokens":92303,"cached_input_tokens":79232,"output_tokens":830,"reasoning_output_tokens":271}}""")
            .RootElement;

        var parser = new CodexUsageParser();
        Assert.True(parser.TryParse(frame, modelHint: "gpt-5-codex", new CliModelRegistry(), out var usage));

        const string project = "Lotta Dashboard";
        const string jobId = "bug-codex-turn";
        var startedAt = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
        var completedAt = startedAt.AddSeconds(8);
        var runId = AgentMessageBusBridge.DeriveRunId(jobId, startedAt);

        await _bridge.EmitTokenUsageRichAsync(
            project,
            jobId,
            runId,
            AgentMessageBusBridge.ParticipantForCli("codex"),
            topic: "codex-turn",
            usage: usage,
            latency: new AgentMessageLatency(
                RequestedAt: startedAt,
                CompletedAt: completedAt,
                TotalMs: (long)(completedAt - startedAt).TotalMilliseconds));

        var msg = Assert.Single(_store.Recent(_workspace, project, 10));
        Assert.Equal("token-usage", msg.Kind);
        Assert.Equal("agent:codex", msg.ParticipantId);
        Assert.Equal("codex-turn", msg.Topic);
        Assert.Equal(jobId, msg.JobId);
        Assert.Equal(runId, msg.RunId);

        Assert.NotNull(msg.Tokens);
        Assert.Equal("gpt-5-codex", msg.Tokens!.Model);
        Assert.Equal(13071, msg.Tokens.Input);
        Assert.Equal(830, msg.Tokens.Output);
        Assert.Equal(79232, msg.Tokens.CacheRead);
        Assert.True(msg.Tokens.InputIncludesCached);
        Assert.NotNull(msg.Tokens.ContextWindow);
        Assert.Equal(272_000, msg.Tokens.ContextWindow!.TotalSize);
        Assert.Equal(92303, msg.Tokens.ContextWindow.Used);
    }

    /// <summary>
    /// BusAggregationCache.OnAppended is what feeds the token-aggregate
    /// endpoint and the workspace token-usage strip. A Codex-attributed
    /// emit must flow through that hook so the rollup is non-empty even
    /// when no orchestrator-decision turn ever fires for the run.
    /// </summary>
    [Fact]
    public async Task EmittedCodexUsage_IsVisibleToBusAggregationCache()
    {
        var usage = new ParsedTurnUsage(
            Model: "gpt-5-codex",
            Input: 1000,
            Output: 50,
            CacheRead: 200,
            CacheWrite: 0,
            ReasoningOutput: 30,
            ContextWindow: new AgentMessageContextWindow(TotalSize: 272_000, Used: 1200, Remaining: 270_800));

        const string project = "agent-taskboard";
        await _bridge.EmitTokenUsageRichAsync(
            project,
            jobId: "agg-job",
            runId: "agg-run",
            participantId: AgentMessageBusBridge.ParticipantForCli("codex"),
            topic: "codex-turn",
            usage: usage);

        var snapshot = _cache.Aggregate(_workspace, project, since: null, until: null);
        Assert.Equal(1, snapshot.TotalMessages);
        Assert.Equal(1000, snapshot.Totals.Input);
        Assert.Equal(50, snapshot.Totals.Output);
        Assert.Equal(200, snapshot.Totals.CacheRead);
        Assert.Contains(snapshot.ByModel, b => b.Key == "gpt-5-codex");
        Assert.Contains(snapshot.ByParticipant, b => b.Key == "agent:codex" && b.Input == 1000);
    }

    /// <summary>
    /// When a Codex run produces multiple <c>turn.completed</c> frames
    /// (multi-turn tool-use, retry-after-throttle, etc.) the bus must
    /// record one event per frame so the per-turn breakdown survives. The
    /// runner re-arms its TurnCompleted hook on every frame; we lock that
    /// here by emitting twice and verifying both messages land.
    /// </summary>
    [Fact]
    public async Task MultipleTurnsInOneRun_ProduceOneBusEventPerTurnCompleted()
    {
        const string project = "agent-taskboard";
        var first = new ParsedTurnUsage("gpt-5-codex", 100, 10, 0, 0, 0, null);
        var second = new ParsedTurnUsage("gpt-5-codex", 200, 20, 0, 0, 0, null);

        await _bridge.EmitTokenUsageRichAsync(project, "job-x", "run-1",
            AgentMessageBusBridge.ParticipantForCli("codex"), "codex-turn", first);
        await _bridge.EmitTokenUsageRichAsync(project, "job-x", "run-1",
            AgentMessageBusBridge.ParticipantForCli("codex"), "codex-turn", second);

        var msgs = _store.Recent(_workspace, project, 10);
        Assert.Equal(2, msgs.Count);
        Assert.All(msgs, m => Assert.Equal("agent:codex", m.ParticipantId));
        Assert.All(msgs, m => Assert.Equal("codex-turn", m.Topic));
    }
}
