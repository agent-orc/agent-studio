using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2740 drift guard: <c>ModelMetadataRegistry.NormalizeId</c> resolves a
/// durable receipt's display label back to the catalog id it was formed
/// from, so a receipt that persisted <see cref="ModelMetadata.Label"/>
/// instead of <see cref="ModelMetadata.Id"/> still prices correctly. This
/// only protects models TokenEconomy actually prices; several registry
/// entries (e.g. GPT-4o, Gemini) are deliberately unpriced placeholders
/// (see <c>ModelMetadataRegistry</c> comments) and are out of scope here.
/// </summary>
public class ModelMetadataRegistryTests
{
    public static IEnumerable<object[]> PricedEntries() => ModelMetadataRegistry.All
        .Where(entry => TokenEconomy.ModelPriceCatalog.Default.Find(entry.Id) != null)
        .Select(entry => new object[] { entry.Id, entry.Label });

    [Theory]
    [MemberData(nameof(PricedEntries))]
    public void FindByLabel_ResolvesToTheIdTokenEconomyPrices(string id, string label)
    {
        var byLabel = ModelMetadataRegistry.FindByLabel(label);
        Assert.NotNull(byLabel);
        Assert.Equal(id, byLabel!.Id);
        Assert.Equal(id, ModelMetadataRegistry.NormalizeId(label));
    }

    [Fact]
    public void PricedEntries_IsNonEmpty()
    {
        // Guards against the MemberData filter silently matching nothing
        // (e.g. a TokenEconomy package bump that renames every listing).
        Assert.NotEmpty(PricedEntries());
    }

    [Fact]
    public void FindByLabel_IsCaseInsensitive()
    {
        var metadata = ModelMetadataRegistry.FindByLabel("claude sonnet 5");
        Assert.NotNull(metadata);
        Assert.Equal(ModelIds.ClaudeSonnet5, metadata!.Id);
    }

    [Fact]
    public void NormalizeId_ResolvesLabelAliasAndRawIdToTheSameCanonicalId()
    {
        Assert.Equal(ModelIds.ClaudeSonnet46, ModelMetadataRegistry.NormalizeId("Claude Sonnet 4.6"));
        Assert.Equal(ModelIds.ClaudeSonnet46, ModelMetadataRegistry.NormalizeId("claude-sonnet-4.6"));
        Assert.Equal(ModelIds.ClaudeSonnet46, ModelMetadataRegistry.NormalizeId(ModelIds.ClaudeSonnet46));
    }

    [Fact]
    public void NormalizeId_UnknownModel_ReturnsTrimmedInputUnchanged()
    {
        Assert.Equal("some-unlisted-model", ModelMetadataRegistry.NormalizeId(" some-unlisted-model "));
    }
}
