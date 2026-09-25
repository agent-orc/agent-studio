using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the resolution behind <c>GET /api/tasks/code-review/defaults</c>.
/// The panel seeds its CLI + model picker from this when the operator has
/// no remembered last-used pair, so the precedence (configured value wins,
/// hard fallback otherwise) is the load-bearing contract: a deployment can
/// set <c>CodeReviewStep:DefaultModel</c> and have it show up in the UI.
/// </summary>
public class CodeReviewDefaultsEndpointTests
{
    [Fact]
    public void ReviewUsage_UsesTheCatalogPriceAtTheRecordedRunDate()
    {
        var transition = TokenPricing.Catalog["claude-sonnet-5"].History.Max(price => price.ValidFrom);
        var fields = new Dictionary<string, string> { ["model"] = "claude-sonnet-5" };
        var before = TaskCodeReviewEndpoints.ResolveReviewUsage(new FileGenerationMeta
        {
            Model = "claude-sonnet-5",
            TokensIn = 1_000_000,
            TokensTotal = 1_000_000,
            StartedAt = transition.AddTicks(-1),
        }, fields);
        var after = TaskCodeReviewEndpoints.ResolveReviewUsage(new FileGenerationMeta
        {
            Model = "claude-sonnet-5",
            TokensIn = 1_000_000,
            TokensTotal = 1_000_000,
            StartedAt = transition,
        }, fields);

        Assert.True(after.Cost.ModelKnown);
        Assert.NotEqual(before.Cost.Total, after.Cost.Total);
        if (before.Cost.ModelKnown)
            Assert.NotEqual(before.Cost.PriceBasis!.ValidFrom, after.Cost.PriceBasis!.ValidFrom);
        else
            Assert.Equal(TokenEconomy.PriceStatus.NoPriceForDate, before.Cost.Status);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void ResolveDefaults_EmptyConfig_UsesHardFallbacks()
    {
        var (cli, model) = TaskCodeReviewEndpoints.ResolveDefaults(Config());

        Assert.Equal(TaskCodeReviewEndpoints.DefaultCliFallback, cli);
        Assert.Equal(TaskCodeReviewEndpoints.DefaultModelFallback, model);
        Assert.Equal(CliTypes.Codex, cli);
        Assert.Equal(ModelMetadataRegistry.DefaultForCli(CliTypes.Codex), model);
    }

    [Fact]
    public void ResolveDefaults_ConfiguredValues_WinOverFallbacks()
    {
        var config = Config(
            (TaskCodeReviewEndpoints.DefaultCliConfigKey, "codex"),
            (TaskCodeReviewEndpoints.DefaultModelConfigKey, "gpt-5-codex"));

        var (cli, model) = TaskCodeReviewEndpoints.ResolveDefaults(config);

        Assert.Equal("codex", cli);
        Assert.Equal("gpt-5-codex", model);
    }

    [Fact]
    public void ResolveDefaults_WhitespaceConfig_FallsBack()
    {
        var config = Config(
            (TaskCodeReviewEndpoints.DefaultCliConfigKey, "   "),
            (TaskCodeReviewEndpoints.DefaultModelConfigKey, ""));

        var (cli, model) = TaskCodeReviewEndpoints.ResolveDefaults(config);

        Assert.Equal(TaskCodeReviewEndpoints.DefaultCliFallback, cli);
        Assert.Equal(TaskCodeReviewEndpoints.DefaultModelFallback, model);
    }
}
