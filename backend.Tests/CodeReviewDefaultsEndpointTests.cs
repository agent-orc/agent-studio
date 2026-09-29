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
        var listing = TokenPricing.Catalog.Values
            .Select(value => new { value.ModelId, History = value.History.OrderBy(price => price.ValidFrom).ToArray() })
            .First(value => value.History.Length > 1
                && value.History[0].InputPerMTok != value.History[1].InputPerMTok);
        var transition = listing.History[1].ValidFrom;
        var fields = new Dictionary<string, string> { ["model"] = listing.ModelId };
        var before = TaskCodeReviewEndpoints.ResolveReviewUsage(new FileGenerationMeta
        {
            Model = listing.ModelId,
            TokensIn = 1_000_000,
            TokensTotal = 1_000_000,
            StartedAt = transition.AddTicks(-1),
        }, fields);
        var after = TaskCodeReviewEndpoints.ResolveReviewUsage(new FileGenerationMeta
        {
            Model = listing.ModelId,
            TokensIn = 1_000_000,
            TokensTotal = 1_000_000,
            StartedAt = transition,
        }, fields);

        Assert.True(before.Cost.ModelKnown);
        Assert.True(after.Cost.ModelKnown);
        Assert.NotEqual(before.Cost.Total, after.Cost.Total);
        Assert.NotEqual(before.Cost.PriceBasis!.ValidFrom, after.Cost.PriceBasis!.ValidFrom);
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
