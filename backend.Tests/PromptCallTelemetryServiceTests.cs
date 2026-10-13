using System.Text.Json;
using AgentStudio.Prompts;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PromptCallTelemetryServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"prompt-call-tests-{Guid.NewGuid():N}");

    public PromptCallTelemetryServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "prompts"));
    }

    [Fact]
    public void Render_AppendsEveryCallAndAggregatesByContentVersion()
    {
        const string name = "runner-fresh-start.md";
        var promptPath = Path.Combine(_root, "prompts", name);
        File.WriteAllText(promptPath, "Hello {{name}}");
        var config = Configuration();
        var telemetry = new PromptCallTelemetryService(TokenTestServices.Pricing(),
            config,
            NullLogger<PromptCallTelemetryService>.Instance);
        var prompts = new RuntimePromptService(
            config,
            NullLogger<RuntimePromptService>.Instance,
            telemetry);
        var context = new PromptCallContext(
            "Alpha",
            "core",
            ModelIds.ClaudeHaiku45);

        prompts.Render(name, new Dictionary<string, string?> { ["name"] = "one" }, context);
        prompts.Render(name, new Dictionary<string, string?> { ["name"] = "two" }, context);
        File.WriteAllText(promptPath, "Changed {{name}}");
        prompts.InvalidateCache(name);
        prompts.Render(name, new Dictionary<string, string?> { ["name"] = "three" }, context);

        Assert.Equal(3, File.ReadLines(telemetry.LogPath).Count());
        var currentVersion = prompts.TryGetEffectiveVersion(name);
        var aggregate = telemetry.Aggregate(
            [name],
            new Dictionary<string, string?> { [name] = currentVersion });
        var calls = aggregate[name];

        Assert.Equal(3, calls.TotalCalls);
        Assert.Equal(2, calls.Versions.Count);
        Assert.Equal(1, calls.CurrentVersionCalls);
        Assert.Equal(3, calls.Calls7d);
        Assert.False(calls.IsDead);
        Assert.True(calls.InputTokens > 0);
        Assert.Equal(0, calls.UnpricedCalls);
        Assert.True(calls.CostUsd > 0m);

        using var first = JsonDocument.Parse(File.ReadLines(telemetry.LogPath).First());
        var root = first.RootElement;
        Assert.Equal(name, root.GetProperty("promptId").GetString());
        Assert.Equal("Alpha", root.GetProperty("project").GetString());
        Assert.Equal("core", root.GetProperty("step").GetString());
        Assert.Equal(ModelIds.ClaudeHaiku45, root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("tokensEstimated").GetBoolean());
    }

    [Fact]
    public void Aggregate_SkipsMalformedJsonlTail()
    {
        const string name = "summary-protocol.md";
        var promptPath = Path.Combine(_root, "prompts", name);
        File.WriteAllText(promptPath, "Summary");
        var config = Configuration();
        var telemetry = new PromptCallTelemetryService(TokenTestServices.Pricing(),
            config,
            NullLogger<PromptCallTelemetryService>.Instance);
        var prompts = new RuntimePromptService(
            config,
            NullLogger<RuntimePromptService>.Instance,
            telemetry);

        prompts.Render(name, new Dictionary<string, string?>());
        File.AppendAllText(telemetry.LogPath, "{\"torn\":");

        var aggregate = telemetry.Aggregate(
            [name],
            new Dictionary<string, string?>
            {
                [name] = prompts.TryGetEffectiveVersion(name),
            });
        Assert.Equal(1, aggregate[name].TotalCalls);
        Assert.Equal(1, aggregate[name].UnpricedCalls7d);
    }

    [Fact]
    public void UseProjectOverride_RecordsOverrideVersionAndOrigin()
    {
        const string name = "review-aspect-code-quality.md";
        File.WriteAllText(Path.Combine(_root, "prompts", name), "Default prompt");
        var config = Configuration();
        var telemetry = new PromptCallTelemetryService(TokenTestServices.Pricing(),
            config,
            NullLogger<PromptCallTelemetryService>.Instance);
        var prompts = new RuntimePromptService(
            config,
            NullLogger<RuntimePromptService>.Instance,
            telemetry);

        var effective = prompts.UseProjectOverride(
            name,
            "Project-specific prompt",
            new PromptCallContext("Alpha", "aspect-code-quality", ModelIds.ClaudeHaiku45));

        Assert.Equal("Project-specific prompt", effective);
        var record = Assert.Single(telemetry.ReadAll());
        Assert.Equal(name, record.PromptId);
        Assert.Equal("project-override", record.Source);
        Assert.Equal("Alpha", record.Project);
        Assert.Equal("aspect-code-quality", record.Step);
        Assert.NotEqual(prompts.TryGetEffectiveVersion(name), record.Version);
    }

    private IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(_root, "prompts"),
                ["PromptTemplates:OverridePath"] = Path.Combine(_root, "overrides"),
                ["PromptTelemetry:Path"] = Path.Combine(_root, "logs", "prompt-calls.jsonl"),
            })
            .Build();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
