using AgentStudio.Areas;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the pure area policy: no filesystem, no configuration,
/// no clock. Every branch the endpoints rely on is decided here.
/// </summary>
public class AreaTaxonomyTests
{
    [Fact]
    public void ProductDefaults_AreTheTenAreasDecidedInD3()
    {
        Assert.Equal(
            new[]
            {
                "execution-and-runner", "delivery-chain", "gates-and-review", "observation",
                "task-and-board-ui", "dossiers-and-documentation", "websites", "security",
                "retention", "token-economy",
            },
            AreaTaxonomy.ProductDefaults.Select(area => area.Id));
        Assert.All(AreaTaxonomy.ProductDefaults, area =>
        {
            Assert.True(AreaTaxonomy.IsValidId(area.Id), area.Id);
            Assert.False(string.IsNullOrWhiteSpace(area.Label));
            Assert.False(string.IsNullOrWhiteSpace(area.Description));
            Assert.Equal(AreaSources.Product, area.Source);
            Assert.Equal($"docs/areas/{area.Id}/glossary.md", area.GlossaryPath);
        });
    }

    [Fact]
    public void QualityFacets_ReuseTheQualityStudioDomainIdsWithoutClaimingAnAreaId()
    {
        var facetIds = AreaTaxonomy.QualityFacets.Select(facet => facet.Id).ToList();
        // The plan's domain list, minus the one id an area already owns.
        Assert.Equal(
            new[]
            {
                "architecture", "correctness", "testing", "privacy", "payments", "reliability",
                "performance", "accessibility", "localization", "operations",
                "review-prioritization", "seo",
            },
            facetIds);
        Assert.DoesNotContain("security", facetIds);
    }

    [Theory]
    [InlineData("delivery-chain", true)]
    [InlineData("seo", true)]
    [InlineData("a", true)]
    [InlineData("Delivery-Chain", false)]
    [InlineData("delivery--chain", false)]
    [InlineData("-delivery", false)]
    [InlineData("delivery-", false)]
    [InlineData("delivery chain", false)]
    [InlineData("delivery_chain", false)]
    [InlineData("", false)]
    [InlineData("this-id-is-far-too-long-to-be-accepted", false)]
    public void IsValidId_AcceptsOnlyTheStableGrammar(string id, bool expected) =>
        Assert.Equal(expected, AreaTaxonomy.IsValidId(id));

    [Theory]
    [InlineData("security", null, TagKinds.Area)]          // area wins over the quality domain of the same id
    [InlineData("security", TagKinds.Facet, TagKinds.Area)]
    [InlineData("delivery-chain", null, TagKinds.Area)]
    [InlineData("incident", null, TagKinds.Facet)]
    [InlineData("incident", "AREA", TagKinds.Area)]        // an explicit stored kind still counts
    [InlineData("unknown-id", "nonsense", TagKinds.Facet)]
    public void ResolveKind_LetsAreaIdsWin(string id, string? stored, string expected) =>
        Assert.Equal(expected, AreaTaxonomy.ResolveKind(id, stored));

    [Fact]
    public void ResolveKind_AlsoHonoursProjectAreaIds()
    {
        Assert.Equal(TagKinds.Area,
            AreaTaxonomy.ResolveKind("billing", null, new HashSet<string> { "billing" }));
        Assert.Equal(TagKinds.Facet, AreaTaxonomy.ResolveKind("billing", null, new HashSet<string>()));
    }

    [Fact]
    public void Merge_KeepsProductOrderAppendsProjectAreasAndRelabelsInPlace()
    {
        var merged = AreaTaxonomy.Merge(
        [
            new AreaDefinition { Id = "billing", Label = "Billing", Description = "Invoices." },
            new AreaDefinition { Id = "security", Label = "Security and access" },
        ]);

        Assert.Equal(AreaTaxonomy.ProductDefaults.Count + 1, merged.Count);
        Assert.Equal(AreaTaxonomy.ProductDefaults.Select(area => area.Id).Append("billing"),
            merged.Select(area => area.Id));
        var security = merged.Single(area => area.Id == "security");
        Assert.Equal("Security and access", security.Label);
        Assert.Equal(AreaSources.Product, security.Source);
        // A blank field falls back to the product default instead of erasing it.
        Assert.Equal(
            AreaTaxonomy.ProductDefaults.Single(area => area.Id == "security").Description,
            security.Description);
        var billing = merged.Single(area => area.Id == "billing");
        Assert.Equal(AreaSources.Project, billing.Source);
        Assert.Equal("docs/areas/billing/glossary.md", billing.GlossaryPath);
    }

    [Fact]
    public void Merge_DropsAdditionsWithAnInvalidId()
    {
        var merged = AreaTaxonomy.Merge([new AreaDefinition { Id = "Not Valid", Label = "Nope" }]);

        Assert.Equal(AreaTaxonomy.ProductDefaults.Count, merged.Count);
    }

    [Fact]
    public void ValidateProjectAreas_AcceptsAWellFormedList() =>
        Assert.Null(AreaTaxonomy.ValidateProjectAreas(
            [new AreaDefinition { Id = "billing", Label = "Billing", Description = "Invoices." }],
            [], []));

    [Fact]
    public void ValidateProjectAreas_RefusesAnInvalidId()
    {
        var error = AreaTaxonomy.ValidateProjectAreas(
            [new AreaDefinition { Id = "Billing", Label = "Billing" }], [], []);

        Assert.Contains("Invalid area id 'Billing'", error);
    }

    [Fact]
    public void ValidateProjectAreas_RefusesAMissingLabelAndADuplicateId()
    {
        Assert.Contains("needs a label", AreaTaxonomy.ValidateProjectAreas(
            [new AreaDefinition { Id = "billing", Label = "  " }], [], []));
        Assert.Contains("declared twice", AreaTaxonomy.ValidateProjectAreas(
            [
                new AreaDefinition { Id = "billing", Label = "Billing" },
                new AreaDefinition { Id = "billing", Label = "Billing again" },
            ], [], []));
    }

    [Fact]
    public void ValidateProjectAreas_RefusesDroppingAnAreaThatIsStillTagged()
    {
        var error = AreaTaxonomy.ValidateProjectAreas(
            proposed: [],
            currentProjectAreaIds: ["billing"],
            referencedAreaIds: ["billing"]);

        Assert.Contains("Area ids are stable", error);
        Assert.Contains("billing", error);
    }

    [Fact]
    public void ValidateProjectAreas_AllowsDroppingAnUnusedProjectArea() =>
        Assert.Null(AreaTaxonomy.ValidateProjectAreas(
            proposed: [],
            currentProjectAreaIds: ["billing"],
            referencedAreaIds: ["delivery-chain"]));

    [Fact]
    public void ValidateProjectAreas_RefusesAnAreaIdThatIsAlreadyAFacet()
    {
        var error = AreaTaxonomy.ValidateProjectAreas(
            [new AreaDefinition { Id = "testing", Label = "Testing" }],
            currentProjectAreaIds: [],
            referencedAreaIds: [],
            reservedFacetIds: ["testing", "incident"]);

        Assert.Contains("already a facet tag", error);
    }

    [Fact]
    public void ValidateProjectAreas_KeepsAcceptingAnAreaItAlreadyDeclared() =>
        // Re-saving the list must not trip the facet guard on the project's own
        // area, which the effective vocabulary reports back as a tag.
        Assert.Null(AreaTaxonomy.ValidateProjectAreas(
            [new AreaDefinition { Id = "billing", Label = "Billing" }],
            currentProjectAreaIds: ["billing"],
            referencedAreaIds: ["billing"],
            reservedFacetIds: ["billing"]));

    [Fact]
    public void ValidateProjectAreas_RefusesMoreThanTheAllowedNumberOfAreas()
    {
        var proposed = Enumerable.Range(0, AreaTaxonomy.MaxAreasPerProject + 1)
            .Select(index => new AreaDefinition { Id = $"area-{index}", Label = $"Area {index}" })
            .ToList();

        Assert.Contains("at most", AreaTaxonomy.ValidateProjectAreas(proposed, [], []));
    }
}
