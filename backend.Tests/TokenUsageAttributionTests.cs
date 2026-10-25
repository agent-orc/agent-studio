using AgentStudio.Bus;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: rows carry fixed timestamps only as bus payload; nothing compares them with now.
public sealed class TokenUsageAttributionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "token-attribution-" + Guid.NewGuid().ToString("N"));

    private static readonly Dictionary<string, string> Projects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PROJ-1"] = "PROJ-1",
        ["Studio"] = "PROJ-1",
        ["PROJ-2"] = "PROJ-2",
    };

    private static readonly Dictionary<string, string> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["job-a"] = "PROJ-2",
    };

    private static TokenUsageAttribution.Result Resolve(string? project, string[]? tags, string? jobId)
        => TokenUsageAttribution.Resolve(project, tags, jobId,
            name => Projects.GetValueOrDefault(name),
            job => Jobs.GetValueOrDefault(job));

    [Theory]
    [InlineData("Studio", null, null, "PROJ-1", TokenUsageAttribution.ByProject)]
    [InlineData(null, "project:Studio", null, "PROJ-1", TokenUsageAttribution.ByTag)]
    [InlineData(null, "project:PROJ-2", "job-a", "PROJ-2", TokenUsageAttribution.ByTag)]
    [InlineData(null, null, "job-a", "PROJ-2", TokenUsageAttribution.ByJob)]
    [InlineData("Unknown", null, "job-a", "PROJ-2", TokenUsageAttribution.ByJob)]
    [InlineData(null, "project:Unknown", "job-a", "PROJ-2", TokenUsageAttribution.ByJob)]
    [InlineData(null, "token-usage", null, null, TokenUsageAttribution.None)]
    [InlineData(null, null, "job-unknown", null, TokenUsageAttribution.None)]
    [InlineData(null, null, null, null, TokenUsageAttribution.None)]
    public void Explicit_project_then_tag_then_owning_job(
        string? project, string? tag, string? jobId, string? expectedProject, string expectedRule)
    {
        var result = Resolve(project, tag is null ? null : ["token-usage", tag], jobId);
        Assert.Equal(expectedProject, result.ProjectId);
        Assert.Equal(expectedRule, result.Rule);
    }

    [Fact]
    public async Task Ad_hoc_rows_stay_in_the_workspace_scope_and_carry_their_project_tag()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        var at = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        var usage = new OrchestratorTokenUsage { Model = "claude-sonnet-5", InputTokens = 100, OutputTokens = 20 };

        Assert.True(await bridge.EmitTokenUsageAsync(null, "job-a", "support:adhoc", "title-generation",
            usage, at, attributedProject: "Studio"));
        Assert.True(await bridge.EmitTokenUsageAsync(null, null, "support:adhoc", "summary-generation",
            usage, at.AddMinutes(1)));

        var rows = store.Query(_root, AgentMessageBusPaths.WorkspaceScope, new AgentMessageQuery(Kind: "token-usage"));
        Assert.Equal(2, rows.Count);
        var tagged = rows.Single(row => row.Topic == "title-generation");
        Assert.Contains("project:Studio", tagged.Tags!);
        Assert.Equal("PROJ-1", Resolve(tagged.Project, tagged.Tags?.ToArray(), tagged.JobId).ProjectId);
        var untagged = rows.Single(row => row.Topic == "summary-generation");
        Assert.DoesNotContain(untagged.Tags!, tag => tag.StartsWith("project:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reasoning_tokens_survive_the_bus_and_the_receipt_reader()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        var at = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        await bridge.EmitTokenUsageAsync("Studio", "job-a", "agent:codex", "task-token-receipt",
            new OrchestratorTokenUsage
            {
                Model = "gpt-5.6-sol", InputTokens = 100, OutputTokens = 50, ReasoningTokens = 30, CliType = "codex",
            }, at);
        await bridge.EmitTokenUsageAsync("Studio", "job-a", "agent:claude", "task-token-receipt",
            new OrchestratorTokenUsage { Model = "claude-sonnet-5", InputTokens = 100, OutputTokens = 50 }, at);

        var entries = BusTokenEntryConverter.LoadTokenUsageEntries(store, _root, "Studio");
        Assert.Equal(30, entries.Single(entry => entry.ParticipantId == "agent:codex").TokenUsage!.ReasoningTokens);
        Assert.Null(entries.Single(entry => entry.ParticipantId == "agent:claude").TokenUsage!.ReasoningTokens);

        var fold = TaskTokenSummaryFold.PerJob(TokenTestServices.Pricing().Price(entries));
        var receipt = fold["job-a"];
        Assert.Contains(receipt.Entries, call => call.ReasoningTokens == 30);
        Assert.Contains(receipt.Entries, call => call.ReasoningTokens is null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
