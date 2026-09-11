using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Coverage for the embedded model-migration catalog (AGT-2716): the version
/// stamp, referential validation, and proposal-detection lookups the card
/// model badge / project pipeline settings / Workspace CLI Management all
/// drive off of via <c>GET /api/cli/model-migrations</c>.
/// </summary>
public sealed class ModelMigrationCatalogRegistryTests
{
    private static ModelMigrationCatalogRegistry BuildEmbedded()
        => new(new EmbeddedModelMigrationCatalogSource());

    [Fact]
    public void EmbeddedCatalog_LoadsWithAVersionAndWikiPath()
    {
        var registry = BuildEmbedded();
        Assert.False(string.IsNullOrWhiteSpace(registry.Catalog.Version));
        Assert.Equal("docs/system/domains/model-routing-policy.md", registry.Catalog.WikiPath);
        Assert.NotEmpty(registry.Catalog.Migrations);
    }

    [Fact]
    public void FindMigration_ClaudeHaiku45_HasNoEntry()
    {
        // 2026-09-06 fact check: no newer Haiku generation exists, so a card
        // pinned to claude-haiku-4-5 must show no "update available" - this is
        // the whole reason the card was scoped down from the operator's
        // original "haiku is outdated" assumption.
        var registry = BuildEmbedded();
        Assert.Null(registry.FindMigration(ModelIds.ClaudeHaiku45));
    }

    [Theory]
    [InlineData(ModelIds.ClaudeOpus48)]
    [InlineData(ModelIds.ClaudeOpus47)]
    [InlineData(ModelIds.ClaudeOpus46)]
    [InlineData(ModelIds.ClaudeOpus45)]
    public void FindMigration_SupersededOpusGenerations_ProposeOpus5_SafeAuto(string supersededOpus)
    {
        var registry = BuildEmbedded();
        var migration = registry.FindMigration(supersededOpus);

        Assert.NotNull(migration);
        Assert.Equal(ModelIds.ClaudeOpus5, migration!.To);
        Assert.Equal(ModelFamilies.ClaudeOpus, migration.Family);
        Assert.True(migration.SafeAuto);
    }

    [Theory]
    [InlineData(ModelIds.ClaudeSonnet46)]
    [InlineData(ModelIds.ClaudeSonnet45)]
    public void FindMigration_SupersededSonnetGenerations_ProposeSonnet5_SafeAuto(string supersededSonnet)
    {
        var registry = BuildEmbedded();
        var migration = registry.FindMigration(supersededSonnet);

        Assert.NotNull(migration);
        Assert.Equal(ModelIds.ClaudeSonnet5, migration!.To);
        Assert.Equal(ModelFamilies.ClaudeSonnet, migration.Family);
        Assert.True(migration.SafeAuto);
    }

    [Fact]
    public void FindMigration_Gpt54Mini_HasNoEntry()
    {
        // Whether the cheap tier should leave gpt-mini for Sonnet is a Token
        // Economy decision, not a family-generation rule (model-routing-policy.md).
        var registry = BuildEmbedded();
        Assert.Null(registry.FindMigration(ModelIds.Gpt54Mini));
    }

    [Fact]
    public void FindMigration_UnknownModel_ReturnsNull()
    {
        var registry = BuildEmbedded();
        Assert.Null(registry.FindMigration("not-a-real-model"));
        Assert.Null(registry.FindMigration(null));
        Assert.Null(registry.FindMigration("  "));
    }

    [Fact]
    public void Validate_RejectsMissingVersion()
    {
        var doc = new ModelMigrationCatalogDocument
        {
            Version = "",
            WikiPath = "docs/system/domains/model-routing-policy.md",
        };
        Assert.Throws<InvalidOperationException>(() => new ModelMigrationCatalogRegistry(new FixedSource(doc)));
    }

    [Fact]
    public void Validate_RejectsDuplicateFromIds()
    {
        var doc = new ModelMigrationCatalogDocument
        {
            Version = "test",
            WikiPath = "docs/system/domains/model-routing-policy.md",
            Migrations =
            [
                new ModelMigrationEntry { From = "a", To = "b", Family = "f", SafeAuto = true, Reason = "r" },
                new ModelMigrationEntry { From = "a", To = "c", Family = "f", SafeAuto = true, Reason = "r" },
            ],
        };
        Assert.Throws<InvalidOperationException>(() => new ModelMigrationCatalogRegistry(new FixedSource(doc)));
    }

    [Fact]
    public void Validate_RejectsSelfMigration()
    {
        var doc = new ModelMigrationCatalogDocument
        {
            Version = "test",
            WikiPath = "docs/system/domains/model-routing-policy.md",
            Migrations = [new ModelMigrationEntry { From = "a", To = "a", Family = "f", SafeAuto = true, Reason = "r" }],
        };
        Assert.Throws<InvalidOperationException>(() => new ModelMigrationCatalogRegistry(new FixedSource(doc)));
    }

    private sealed class FixedSource(ModelMigrationCatalogDocument document) : IModelMigrationCatalogSource
    {
        public ModelMigrationCatalogDocument Load() => document;
    }
}
