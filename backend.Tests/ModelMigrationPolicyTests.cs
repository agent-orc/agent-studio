using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix test for the pure auto-migration decision (AGT-2716),
/// following the .NET style guide's "express branching lifecycle decisions as
/// pure policy with direct matrix tests" convention. Covers acceptance: a
/// safe migration applies automatically for a non-explicit model; an explicit
/// pin is never touched; the workspace switch turns automatic application off.
/// </summary>
public sealed class ModelMigrationPolicyTests
{
    private static readonly ModelMigrationCatalogRegistry Catalog = new(new EmbeddedModelMigrationCatalogSource());

    [Fact]
    public void NonExplicit_SupersededModel_AutoApplyOn_Migrates()
    {
        var migration = ModelMigrationPolicy.DecideAutoMigration(
            modelExplicit: false, model: ModelIds.ClaudeOpus48, autoApplyEnabled: true, catalog: Catalog);

        Assert.NotNull(migration);
        Assert.Equal(ModelIds.ClaudeOpus5, migration!.To);
    }

    [Fact]
    public void ExplicitPin_SupersededModel_NeverMigrates_EvenWithAutoApplyOn()
    {
        var migration = ModelMigrationPolicy.DecideAutoMigration(
            modelExplicit: true, model: ModelIds.ClaudeOpus48, autoApplyEnabled: true, catalog: Catalog);

        Assert.Null(migration);
    }

    [Fact]
    public void NonExplicit_SupersededModel_AutoApplyOff_DoesNotMigrate()
    {
        var migration = ModelMigrationPolicy.DecideAutoMigration(
            modelExplicit: false, model: ModelIds.ClaudeOpus48, autoApplyEnabled: false, catalog: Catalog);

        Assert.Null(migration);
    }

    [Fact]
    public void NonExplicit_ModelWithNoCatalogEntry_DoesNotMigrate()
    {
        var migration = ModelMigrationPolicy.DecideAutoMigration(
            modelExplicit: false, model: ModelIds.ClaudeHaiku45, autoApplyEnabled: true, catalog: Catalog);

        Assert.Null(migration);
    }

    [Fact]
    public void NonExplicit_BlankModel_DoesNotMigrate()
    {
        Assert.Null(ModelMigrationPolicy.DecideAutoMigration(false, null, true, Catalog));
        Assert.Null(ModelMigrationPolicy.DecideAutoMigration(false, "  ", true, Catalog));
    }

    [Fact]
    public void NonExplicit_CatalogEntryNotSafeAuto_DoesNotMigrate()
    {
        var notSafeAuto = new ModelMigrationCatalogRegistry(new FixedSource(new ModelMigrationCatalogDocument
        {
            Version = "test",
            WikiPath = "docs/system/domains/model-routing-policy.md",
            Migrations = [new ModelMigrationEntry
            {
                From = ModelIds.ClaudeOpus48, To = ModelIds.ClaudeOpus5,
                Family = ModelFamilies.ClaudeOpus, SafeAuto = false, Reason = "needs operator review",
            }],
        }));

        var migration = ModelMigrationPolicy.DecideAutoMigration(
            modelExplicit: false, model: ModelIds.ClaudeOpus48, autoApplyEnabled: true, catalog: notSafeAuto);

        Assert.Null(migration);
    }

    private sealed class FixedSource(ModelMigrationCatalogDocument document) : IModelMigrationCatalogSource
    {
        public ModelMigrationCatalogDocument Load() => document;
    }
}
