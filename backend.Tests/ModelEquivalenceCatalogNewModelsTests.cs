using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelEquivalenceCatalogNewModelsTests
{
    [Theory]
    [InlineData(ModelIds.ClaudeOpus55)]
    [InlineData(ModelIds.ClaudeHaiku55)]
    public void A_new_model_cannot_inherit_unpublished_predecessor_equivalence(string model)
    {
        var catalogue = new ModelEquivalenceCatalog(TokenTestServices.Pricing());
        Assert.Null(catalogue.TryGetRoute(CliTypes.Claude, model, "low", CliTypes.Codex));
        Assert.DoesNotContain(catalogue.Routes, route => route.FromModel == model || route.ToModel == model);
    }
}
