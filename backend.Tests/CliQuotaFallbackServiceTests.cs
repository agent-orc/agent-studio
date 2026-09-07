using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void Catalogue_equivalence_requires_an_explicit_safe_entry()
    {
        var exact = ModelMetadataRegistry.EquivalentFor(
            CliTypes.Codex,
            ModelIds.Gpt56Sol,
            "high");
        var unknown = ModelMetadataRegistry.EquivalentFor(
            CliTypes.Codex,
            "gpt-5.7-unqualified",
            "high");

        Assert.NotNull(exact);
        Assert.Equal(ModelIds.ClaudeOpus5, exact!.TargetModel);
        Assert.Null(unknown);
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
    public void Resolve_QuotaFull_SupportsModelFallbackWithinSameCli()
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

        Assert.True(decision.IsFallback);
        Assert.Equal("codex", decision.CliType);
        Assert.Equal("gpt-5.3-codex", decision.Model);
        Assert.Equal("medium", decision.ThinkingLevel);
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

    [Theory]
    [InlineData("gpt-5.6-sol", "high", "claude", "claude-opus-5", "high")]
    [InlineData("gpt-5.4-mini", "high", "claude", "claude-sonnet-5", "medium")]
    [InlineData("gpt-5.6-terra", "medium", "claude", "claude-sonnet-5", "medium")]
    public void Resolve_DerivesCrossFamilyEquivalentFromCatalogue(
        string model,
        string thinking,
        string expectedCli,
        string expectedModel,
        string expectedThinking)
    {
        var service = NewService();

        var decision = service.Resolve("codex", model, thinking, cli =>
            cli == "codex"
                ? new CapEvaluation(true, "codex", "Weekly", 98, 98)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal(expectedCli, decision.CliType);
        Assert.Equal(expectedModel, decision.Model);
        Assert.Equal(expectedThinking, decision.ThinkingLevel);
        Assert.Equal(CliFallbackSources.Catalogue, decision.FallbackSource);
    }

    [Fact]
    public void Resolve_ExplicitDisableOverridesCatalogue()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = ModelIds.Gpt56Sol,
            FallbackDisabled = true,
        });

        var decision = service.Resolve("codex", ModelIds.Gpt56Sol, "high", _ =>
            new CapEvaluation(true, "codex", "Weekly", 98, 98));

        Assert.False(decision.IsFallback);
        Assert.Equal(CliTypes.Codex, decision.CliType);
    }

    [Fact]
    public void GetAll_ExposesCatalogueRouteProvenance()
    {
        var profile = NewService().GetAll()[CliTypes.Codex];

        Assert.Equal(CliFallbackSources.Catalogue, profile.FallbackSource);
        Assert.Equal(CliTypes.Claude, profile.FallbackCliType);
        Assert.NotNull(profile.FallbackModel);
    }

    [Fact]
    public void ObserveAdmission_ClearsStaleActiveFallbackWhenNewLaunchMustWait()
    {
        var service = NewService();
        var activated = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchFallback,
            CliTypes.Claude,
            ModelIds.ClaudeOpus5,
            "high",
            true,
            "codex capped",
            DateTime.UtcNow.AddHours(2),
            null);
        service.ObserveAdmission(CliTypes.Codex, activated, DateTime.UtcNow);
        Assert.Single(service.GetActiveFallbacks());

        service.ObserveAdmission(
            CliTypes.Codex,
            activated with { Outcome = QuotaAdmissionOutcome.Wait, IsFallback = false },
            DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(service.GetActiveFallbacks());
    }

    private CliQuotaFallbackService NewService() =>
        new(_config, NullLogger<CliQuotaFallbackService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
