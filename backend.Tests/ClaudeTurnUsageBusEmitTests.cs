using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Claude streaming -&gt; agent message bus contract for per-turn
/// token usage (see bug-agent-run-metriken-keine-tokens).
/// <para>
/// The CORE coding-agent run is a <c>claude</c> CLI process. Its
/// <c>result</c> frame already parses cleanly via
/// <see cref="ClaudeUsageParser"/>, but before the fix the Claude driver
/// never mirrored that parsed usage onto the bus and
/// <c>ProjectRunner.MirrorAgentTurnUsageToBus</c> short-circuited for every
/// non-Codex CLI. So the Overview reported "no token activity recorded" for
/// the agent run even though Haiku aspect turns (which flow through the
/// orchestrator) showed tokens. This test pins the bus shape for a
/// Claude-attributed emit - <c>agent:claude</c>, <c>claude-turn</c>, the
/// token mapping, and the aggregation hook - so the wiring cannot silently
/// regress back to Codex-only.
/// </para>
/// </summary>
public sealed class ClaudeTurnUsageBusEmitTests : IDisposable
{
    private readonly string _workspace;
    private readonly AgentMessageBusStore _store;
    private readonly AgentMessageBusBridge _bridge;
    private readonly BusAggregationCache _cache;

    public ClaudeTurnUsageBusEmitTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "claude-turn-bus-" + Guid.NewGuid().ToString("N"));
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
    /// A representative stream-json <c>result</c> frame from the task-agent
    /// path (claude-opus-4-8). The frame omits a top-level <c>model</c> - the
    /// real stream-json result frame does - so the parser must fall back to
    /// the run's model hint to size the context window. The emission must show
    /// up as a <c>kind:token-usage</c> message attributed to
    /// <c>agent:claude</c> with <c>cache_read_input_tokens</c> mapped onto
    /// <see cref="AgentMessageTokens.CacheRead"/> and a populated
    /// context-window snapshot for the 200k-token Opus window.
    /// </summary>
    [Fact]
    public async Task ResultFrame_EmitsTokenUsageMessageWithClaudeAgentAttribution()
    {
        var frame = JsonDocument.Parse(
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":55000,"num_turns":7,"result":"Done","session_id":"d1e2c3b4-aaaa-bbbb-cccc-0123456789ab","total_cost_usd":0.4231,"usage":{"input_tokens":1542,"cache_creation_input_tokens":2010,"cache_read_input_tokens":48230,"output_tokens":911}}""")
            .RootElement;

        var parser = new ClaudeUsageParser();
        Assert.True(parser.TryParse(frame, modelHint: "claude-opus-4-8", new CliModelRegistry(), out var usage));

        const string project = "agent-taskboard";
        const string jobId = "bug-agent-run-metriken";
        var startedAt = new DateTime(2026, 6, 3, 9, 0, 0, DateTimeKind.Utc);
        var completedAt = startedAt.AddSeconds(55);
        var runId = AgentMessageBusBridge.DeriveRunId(jobId, startedAt);

        await _bridge.EmitTokenUsageRichAsync(
            project,
            jobId,
            runId,
            AgentMessageBusBridge.ParticipantForCli("claude"),
            topic: "claude-turn",
            usage: usage,
            latency: new AgentMessageLatency(
                RequestedAt: startedAt,
                CompletedAt: completedAt,
                TotalMs: (long)(completedAt - startedAt).TotalMilliseconds));

        var msg = Assert.Single(_store.Recent(_workspace, project, 10));
        Assert.Equal("token-usage", msg.Kind);
        Assert.Equal("agent:claude", msg.ParticipantId);
        Assert.Equal("claude-turn", msg.Topic);
        Assert.Equal(jobId, msg.JobId);
        Assert.Equal(runId, msg.RunId);

        Assert.NotNull(msg.Tokens);
        Assert.Equal("claude-opus-4-8", msg.Tokens!.Model);
        Assert.Equal(1542, msg.Tokens.Input);
        Assert.Equal(911, msg.Tokens.Output);
        Assert.Equal(48230, msg.Tokens.CacheRead);
        Assert.Equal(2010, msg.Tokens.CacheWrite);
        Assert.False(msg.Tokens.InputIncludesCached);
        Assert.NotNull(msg.Tokens.ContextWindow);
        Assert.Equal(200_000, msg.Tokens.ContextWindow!.TotalSize);
        Assert.Equal(1542 + 48230, msg.Tokens.ContextWindow.Used);
    }

    /// <summary>
    /// BusAggregationCache.OnAppended feeds the per-job token summary the
    /// Overview reads. A Claude-attributed emit must flow through that hook so
    /// the rollup is non-empty for the agent run itself, not just the Haiku
    /// aspect turns the orchestrator emits.
    /// </summary>
    [Fact]
    public async Task EmittedClaudeUsage_IsVisibleToBusAggregationCache()
    {
        var usage = new ParsedTurnUsage(
            Model: "claude-opus-4-8",
            Input: 1542,
            Output: 911,
            CacheRead: 48230,
            CacheWrite: 2010,
            ReasoningOutput: null,
            ContextWindow: new AgentMessageContextWindow(TotalSize: 200_000, Used: 49772, Remaining: 150_228));

        const string project = "agent-taskboard";
        await _bridge.EmitTokenUsageRichAsync(
            project,
            jobId: "agg-job",
            runId: "agg-run",
            participantId: AgentMessageBusBridge.ParticipantForCli("claude"),
            topic: "claude-turn",
            usage: usage);

        var snapshot = _cache.Aggregate(_workspace, project, since: null, until: null);
        Assert.Equal(1, snapshot.TotalMessages);
        Assert.Equal(1542, snapshot.Totals.Input);
        Assert.Equal(911, snapshot.Totals.Output);
        Assert.Equal(48230, snapshot.Totals.CacheRead);
        Assert.Contains(snapshot.ByModel, b => b.Key == "claude-opus-4-8");
        Assert.Contains(snapshot.ByParticipant, b => b.Key == "agent:claude" && b.Input == 1542);
    }

    /// <summary>
    /// A multi-attempt task spawns the agent more than once; each run's
    /// <c>result</c> frame must land as its own bus event so the per-turn
    /// breakdown survives across attempts. We emit twice and verify both
    /// messages land under <c>agent:claude</c>.
    /// </summary>
    [Fact]
    public async Task MultipleRuns_ProduceOneBusEventPerResultFrame()
    {
        const string project = "agent-taskboard";
        var first = new ParsedTurnUsage("claude-opus-4-8", 1000, 100, 0, 0, null, null);
        var second = new ParsedTurnUsage("claude-opus-4-8", 2000, 200, 0, 0, null, null);

        await _bridge.EmitTokenUsageRichAsync(project, "job-x", "run-1",
            AgentMessageBusBridge.ParticipantForCli("claude"), "claude-turn", first);
        await _bridge.EmitTokenUsageRichAsync(project, "job-x", "run-2",
            AgentMessageBusBridge.ParticipantForCli("claude"), "claude-turn", second);

        var msgs = _store.Recent(_workspace, project, 10);
        Assert.Equal(2, msgs.Count);
        Assert.All(msgs, m => Assert.Equal("agent:claude", m.ParticipantId));
        Assert.All(msgs, m => Assert.Equal("claude-turn", m.Topic));
    }
}
