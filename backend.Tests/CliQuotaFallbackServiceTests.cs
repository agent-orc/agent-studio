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

    // ── AGT-2751: derive a fallback from the equivalence catalogue when the
    // operator configured none, without requiring a hand-maintained pair ──
    [Fact]
    public void Resolve_QuotaFull_NoConfiguredProfile_DerivesFallbackFromCatalogue()
    {
        var service = NewServiceWithCatalog();

        var decision = service.Resolve(CliTypes.Codex, ModelIds.Gpt56Sol, "high", cli =>
            cli == CliTypes.Codex
                ? new CapEvaluation(true, CliTypes.Codex, "Weekly", 95, 100)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal(CliTypes.Claude, decision.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, decision.Model);
        Assert.Equal("high", decision.ThinkingLevel);
    }

    [Fact]
    public void Resolve_OperatorOverride_TakesPrecedenceOverCatalogue()
    {
        var service = NewServiceWithCatalog();
        service.Set(new CliModelRouteProfile
        {
            CliType = CliTypes.Codex,
            PrimaryModel = ModelIds.Gpt56Sol,
            FallbackCliType = CliTypes.Claude,
            FallbackModel = "operator-chosen-model",
        });

        var decision = service.Resolve(CliTypes.Codex, null, null, cli =>
            cli == CliTypes.Codex
                ? new CapEvaluation(true, CliTypes.Codex, "Weekly", 95, 100)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal("operator-chosen-model", decision.Model);
    }

    [Fact]
    public void GetEffectiveProfile_NoConfiguredFallback_MarksDerivedTrue()
    {
        var service = NewServiceWithCatalog();

        var effective = service.GetEffectiveProfile(CliTypes.Codex);

        Assert.True(effective.IsFallbackDerived);
        Assert.Equal(CliTypes.Claude, effective.FallbackCliType);
        Assert.Equal(ModelIds.ClaudeOpus5, effective.FallbackModel);
    }

    [Fact]
    public void GetEffectiveProfile_ConfiguredFallback_IsNotMarkedDerived()
    {
        var service = NewServiceWithCatalog();
        service.Set(new CliModelRouteProfile
        {
            CliType = CliTypes.Codex,
            FallbackCliType = CliTypes.Claude,
            FallbackModel = "operator-chosen-model",
        });

        var effective = service.GetEffectiveProfile(CliTypes.Codex);

        Assert.False(effective.IsFallbackDerived);
        Assert.Equal("operator-chosen-model", effective.FallbackModel);
    }

    private CliQuotaFallbackService NewService() =>
        new(_config, NullLogger<CliQuotaFallbackService>.Instance);

    private CliQuotaFallbackService NewServiceWithCatalog() =>
        new(_config, NullLogger<CliQuotaFallbackService>.Instance, new ModelEquivalenceCatalog());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
