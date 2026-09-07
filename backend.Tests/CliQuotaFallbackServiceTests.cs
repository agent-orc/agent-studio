using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CliQuotaFallbackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-quota-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;

    public CliQuotaFallbackServiceTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
    }

    [Fact]
    public void Resolve_QuotaFull_SelectsConfiguredFallbackWithThinkingLevel()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "claude",
            PrimaryModel = "claude-opus",
            FallbackCliType = "codex",
            FallbackModel = "gpt-5.3-codex",
            FallbackThinkingLevel = "high",
        });

        var decision = service.Resolve("claude", null, null, cli =>
            cli == "claude"
                ? new CapEvaluation(true, "claude", "Weekly", 95, 100)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal("codex", decision.CliType);
        Assert.Equal("gpt-5.3-codex", decision.Model);
        Assert.Equal("high", decision.ThinkingLevel);
        Assert.Contains("Weekly", decision.Reason);
    }

    [Fact]
    public void Resolve_AfterQuotaReset_ReturnsPrimaryAgain()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "claude",
            PrimaryModel = "claude-opus",
            FallbackModel = "claude-sonnet",
        });

        var decision = service.Resolve("claude", null, null, _ => CapEvaluation.NotBlocked);

        Assert.False(decision.IsFallback);
        Assert.Equal("claude", decision.CliType);
        Assert.Equal("claude-opus", decision.Model);
    }

    [Fact]
    public void Resolve_QuotaFull_DoesNotBypassProviderCapWithinSameCli()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = "gpt-5.6-sol",
            FallbackModel = "gpt-5.3-codex",
            FallbackThinkingLevel = "medium",
        });

        var decision = service.Resolve("codex", null, null, _ =>
            new CapEvaluation(true, "codex", "Model window", 95, 100));

        Assert.False(decision.IsFallback);
        Assert.Equal("codex", decision.CliType);
        Assert.Equal("gpt-5.6-sol", decision.Model);
        Assert.Contains("same-family", decision.Reason);
    }

    [Fact]
    public void Resolve_DoesNotUseFallbackWhenItsCliIsAlsoBlocked()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "claude",
            FallbackCliType = "codex",
            FallbackModel = "gpt-5.3-codex",
        });

        var decision = service.Resolve("claude", null, null, cli =>
            new CapEvaluation(true, cli, "Weekly", 95, 100));

        Assert.False(decision.IsFallback);
        Assert.Contains("fallback codex", decision.Reason);
    }

    [Fact]
    public void Set_PersistsWorkspaceRoute()
    {
        NewService().Set(new CliModelRouteProfile { CliType = "codex", PrimaryModel = "gpt-5.3", FallbackModel = "gpt-5.2" });

        var profile = NewService().GetAll()["codex"];
        Assert.Equal("gpt-5.3", profile.PrimaryModel);
        Assert.Equal("gpt-5.2", profile.FallbackModel);
    }

    [Fact]
    public void Resolve_WithoutOverride_UsesTokenEconomyQualifiedEquivalentTier()
    {
        var service = NewService();

        var decision = service.Resolve("codex", "gpt-5.6-terra", "medium", cli =>
            cli == "codex"
                ? new CapEvaluation(true, "codex", "Weekly", 98, 98)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal("claude", decision.CliType);
        Assert.Equal("claude-sonnet-5", decision.Model);
        Assert.Equal("high", decision.ThinkingLevel);
        Assert.Equal(CliModelRouteSources.Catalogue, decision.RouteSource);
        Assert.Equal("terra-medium", decision.EquivalentTier);
    }

    [Fact]
    public void Resolve_ExplicitEmptyOverride_DisablesCatalogueFallback()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = "gpt-5.6-terra",
            PrimaryThinkingLevel = "medium",
        });

        var decision = service.Resolve("codex", "gpt-5.6-terra", "medium", _ =>
            new CapEvaluation(true, "codex", "Weekly", 98, 98));

        Assert.False(decision.IsFallback);
        Assert.Equal(CliModelRouteSources.OperatorOverride, decision.RouteSource);
    }

    [Fact]
    public void Resolve_LegacyEmptyProfile_MigratesToCatalogueFallback()
    {
        File.WriteAllText(
            Path.Combine(_root, "cli-model-routing.json"),
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    cliType = "codex",
                    primaryModel = "gpt-5.6-terra",
                    primaryThinkingLevel = "medium",
                    fallbackCliType = (string?)null,
                    fallbackModel = (string?)null,
                    fallbackThinkingLevel = (string?)null,
                },
            }));

        var service = NewService();
        var decision = service.Resolve("codex", "gpt-5.6-terra", "medium", cli =>
            cli == "codex"
                ? new CapEvaluation(true, "codex", "Weekly", 98, 98)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal(CliModelRouteSources.Catalogue, decision.RouteSource);
        Assert.Equal("claude-sonnet-5", decision.Model);
    }

    [Fact]
    public void Resolve_PrimaryOnlyCatalogueEdit_PreservesDerivedFallback()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = "gpt-5.6-terra",
            PrimaryThinkingLevel = "medium",
            RouteSource = CliModelRouteSources.Catalogue,
        });

        var reloaded = NewService();
        var decision = reloaded.Resolve("codex", null, null, cli =>
            cli == "codex"
                ? new CapEvaluation(true, "codex", "Weekly", 98, 98)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal(CliModelRouteSources.Catalogue, reloaded.GetAll()["codex"].RouteSource);
        Assert.Equal("claude-sonnet-5", decision.Model);
    }

    [Fact]
    public void Resolve_UnqualifiedTier_DoesNotCrossCorrectnessFloor()
    {
        var service = NewService();

        var decision = service.Resolve("codex", "gpt-5.6-sol", "xhigh", _ =>
            new CapEvaluation(true, "codex", "Weekly", 98, 98));

        Assert.False(decision.IsFallback);
        Assert.Equal("codex", decision.CliType);
    }

    private CliQuotaFallbackService NewService() =>
        new(_config, NullLogger<CliQuotaFallbackService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
