using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2038: the workspace token timeline must count a project's <b>full</b>
/// inference spend - coding-agent runs and supporting loops on top of the
/// orchestrator meta-turns - and split each project's total into
/// Agent / Supporting / Orchestrator so the orchestrator share can be shown
/// separately. Before the fix the bus reader loaded orchestrator-participant
/// messages only, so agent runs (the bulk of the spend) were invisible and
/// the numbers read far too small.
///
/// <para>
/// These tests emit token-usage messages across all three participant kinds
/// and assert both the corrected totals and the invariant that
/// Agent + Supporting + Orchestrator == Total on every per-project row.
/// </para>
/// </summary>
public sealed class WorkspaceTokensTimelineCategoryTests : IDisposable
{
    private readonly string _workspace;

    public WorkspaceTokensTimelineCategoryTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "workspace-timeline-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Build_CountsAgentAndSupporting_NotJustOrchestrator()
    {
        var (bridge, store) = BuildStack();
        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        // Realistic night: a big agent run, a supporting audit, and a small
        // orchestrator meta-turn - all inside the 24h window.
        await EmitAsync(bridge, store, "studio", Agent("studio"),
            Usage("claude-opus-4-7", 3_000_000, 40_000), now.AddHours(-4));
        await EmitAsync(bridge, store, "studio", Support("studio"),
            Usage("claude-haiku-4-5", 120_000, 8_000), now.AddHours(-3));
        await EmitAsync(bridge, store, "studio", Orchestrator("studio"),
            Usage("claude-haiku-4-5", 20_000, 2_000), now.AddHours(-1));

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(
            store, _workspace, new[] { ("studio", Path.Combine(_workspace, "studio")) },
            windowHours: 24, bucketMinutes: 60, nowUtc: now);

        var project = Assert.Single(bus.Projects);

        // The agent run dominates - orchestrator alone would have been ~44K.
        Assert.Equal(3_040_000, project.AgentTokens);
        Assert.Equal(128_000, project.SupportingTokens);
        Assert.Equal(22_000, project.OrchestratorTokens);
        Assert.Equal(3, project.Calls);

        // Summen-invariante: the three categories reconstruct the total.
        Assert.Equal(project.Total, project.AgentTokens + project.SupportingTokens + project.OrchestratorTokens);
        Assert.Equal(3_190_000, project.Total);
    }

    [Fact]
    public async Task Build_CellCategorySplit_AddsUpToCellTotal()
    {
        var (bridge, store) = BuildStack();
        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        // Two participants land in the same hour bucket.
        await EmitAsync(bridge, store, "studio", Agent("studio"),
            Usage("claude-opus-4-7", 500_000, 10_000), now.AddMinutes(-40));
        await EmitAsync(bridge, store, "studio", Orchestrator("studio"),
            Usage("claude-haiku-4-5", 10_000, 1_000), now.AddMinutes(-30));

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(
            store, _workspace, new[] { ("studio", Path.Combine(_workspace, "studio")) },
            windowHours: 24, bucketMinutes: 60, nowUtc: now);

        var cell = Assert.Single(bus.Cells);
        Assert.Equal(510_000, cell.AgentTokens);
        Assert.Equal(0, cell.SupportingTokens);
        Assert.Equal(11_000, cell.OrchestratorTokens);
        Assert.Equal(cell.Total, cell.AgentTokens + cell.SupportingTokens + cell.OrchestratorTokens);
    }

    [Fact]
    public async Task Build_ChatClassDoesNotPresentPartialCostAsComplete()
    {
        var (bridge, store) = BuildStack();
        var now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        await EmitAsync(bridge, store, "studio", "chat:codex",
            Usage("gpt-5.6-sol", 100, 20), now.AddMinutes(-20));
        await EmitAsync(bridge, store, "studio", "chat:codex",
            Usage("unknown-model", 50, 10), now.AddMinutes(-15));

        var timeline = BusBackedWorkspaceTimelineReader.BuildFromStore(
            store, _workspace, [("studio", Path.Combine(_workspace, "studio"))],
            windowHours: 24, bucketMinutes: 60, nowUtc: now);

        var project = Assert.Single(timeline.Projects);
        var cell = Assert.Single(timeline.Cells);
        Assert.Equal(180, project.ChatTokens);
        Assert.Equal(180, cell.ChatTokens);
        Assert.Null(project.ChatCostUsd);
        Assert.Null(cell.ChatCostUsd);
    }

    [Fact]
    public async Task Build_LastActivity_ReflectsNewestAgentRun_NotOnlyOrchestrator()
    {
        var (bridge, store) = BuildStack();
        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        // Orchestrator went quiet 6h ago; an agent run is still going 5m ago.
        await EmitAsync(bridge, store, "studio", Orchestrator("studio"),
            Usage("claude-haiku-4-5", 20_000, 2_000), now.AddHours(-6));
        var newest = now.AddMinutes(-5);
        await EmitAsync(bridge, store, "studio", Agent("studio"),
            Usage("claude-opus-4-7", 800_000, 12_000), newest);

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(
            store, _workspace, new[] { ("studio", Path.Combine(_workspace, "studio")) },
            windowHours: 24, bucketMinutes: 60, nowUtc: now);

        var project = Assert.Single(bus.Projects);
        Assert.Equal(newest.ToString("o"), project.LastActivity);
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

    private async Task EmitAsync(
        AgentMessageBusBridge bridge,
        AgentMessageBusStore store,
        string project,
        string participantId,
        OrchestratorTokenUsage usage,
        DateTime ts)
    {
        var before = store.Query(_workspace, project, new AgentMessageQuery(Kind: "token-usage")).Count;
        await bridge.EmitTokenUsageAsync(
            project: project,
            jobId: null,
            participantId: participantId,
            topic: "token",
            usage: usage,
            createdAt: ts);
        await WaitForBusCountAsync(store, project, before + 1);
    }

    private async Task WaitForBusCountAsync(AgentMessageBusStore store, string project, int expected, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var got = store.Query(_workspace, project, new AgentMessageQuery(Kind: "token-usage")).Count;
            if (got >= expected) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Bus did not reach {expected} {project} token-usage messages within {timeoutMs}ms.");
    }

    private static string Agent(string project) => $"agent:{project}";
    private static string Support(string project) => $"support:{project}";
    private static string Orchestrator(string project) => AgentMessageBusBridge.ParticipantOrchestratorFor(project);

    private static OrchestratorTokenUsage Usage(string model, int input, int output) => new()
    {
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = 0,
        CacheCreationTokens = 0,
    };
}
