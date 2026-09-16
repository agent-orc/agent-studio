using AgentStudio.Areas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The per-project areas registry and the closed tag list it projects. Project
/// additions live in the workspace project settings, so a real settings store
/// on a temporary workspace is the smallest honest harness.
/// </summary>
public sealed class AreaRegistryServiceTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "area-registry-" + Guid.NewGuid().ToString("N"));

    public AreaRegistryServiceTests() => Directory.CreateDirectory(_workspace);
    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void List_WithoutProjectAdditionsIsTheProductDefault()
    {
        var areas = Build().List("Project");

        Assert.Equal(AreaTaxonomy.ProductDefaults.Select(area => area.Id), areas.Select(area => area.Id));
        Assert.All(areas, area => Assert.Equal(AreaSources.Product, area.Source));
    }

    [Fact]
    public void SetProjectAreas_PersistsTheAdditionAndKeepsItOutOfOtherProjects()
    {
        var service = Build();

        service.SetProjectAreas("Project",
            [new AreaDefinition { Id = "billing", Label = "Billing", Description = "Invoices." }]);

        var reloaded = Build();
        Assert.Contains(reloaded.List("Project"), area => area.Id == "billing" && area.Source == AreaSources.Project);
        Assert.DoesNotContain(reloaded.List("Other"), area => area.Id == "billing");
        Assert.Equal("billing", Assert.Single(reloaded.ProjectAreas("Project")).Id);
    }

    [Fact]
    public void EffectiveTags_TypeTheProductAreasAsAreasAndTheRestAsFacets()
    {
        var entries = Build().EffectiveTags("Project").ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        Assert.Equal(TagKinds.Area, entries["delivery-chain"].Kind);
        // security is both a product area and a quality domain id; the area wins
        // so the flat namespace keeps exactly one entry for it.
        Assert.Equal(TagKinds.Area, entries["security"].Kind);
        Assert.Equal(TagKinds.Facet, entries["incident"].Kind);
        Assert.Equal(TagKinds.Facet, entries["review-prioritization"].Kind);
        Assert.Equal(TagKinds.Facet, entries["architecture"].Kind);
    }

    [Fact]
    public void EffectiveTags_AddTheProjectAreasTheWorkspaceRegistryDoesNotCarry()
    {
        var service = Build();
        service.SetProjectAreas("Project", [new AreaDefinition { Id = "billing", Label = "Billing" }]);

        var billing = Assert.Single(service.EffectiveTags("Project").Where(entry => entry.Id == "billing"));

        Assert.Equal(TagKinds.Area, billing.Kind);
        Assert.Equal("Billing", billing.Label);
        Assert.DoesNotContain(service.EffectiveTags("Other"), entry => entry.Id == "billing");
    }

    [Fact]
    public void ValidateTags_NormalizesKeepsOrderAndDropsDuplicates()
    {
        var result = Build().ValidateTags("Project", ["Delivery-Chain", "incident", "delivery-chain"]);

        Assert.True(result.Ok);
        Assert.Equal(["delivery-chain", "incident"], result.TagIds);
    }

    [Fact]
    public void ValidateTags_RefusesUnknownIds()
    {
        var result = Build().ValidateTags("Project", ["delivery-chain", "not-a-registered-tag"]);

        Assert.False(result.Ok);
        Assert.Equal(["not-a-registered-tag"], result.Unknown);
        Assert.Contains("Unknown tag ids", result.Error);
    }

    [Fact]
    public void ValidateTags_AcceptsAProjectAreaOnceItIsDeclared()
    {
        var service = Build();
        Assert.False(service.ValidateTags("Project", ["billing"]).Ok);

        service.SetProjectAreas("Project", [new AreaDefinition { Id = "billing", Label = "Billing" }]);

        Assert.True(service.ValidateTags("Project", ["billing"]).Ok);
    }

    [Fact]
    public void TagRegistry_SeedsTheAreaVocabularyAndRefusesToDeleteAnAreaId()
    {
        var tags = Tags();

        Assert.Contains(tags.GetAll(), entry => entry.Id == "token-economy" && entry.Kind == TagKinds.Area);
        Assert.Contains(tags.GetAll(), entry => entry.Id == "migration" && entry.Kind == TagKinds.Facet);
        Assert.Throws<InvalidOperationException>(() => tags.Delete("delivery-chain"));
        Assert.Throws<ArgumentException>(() => tags.Create(null, "Delivery chain", null, null, TagKinds.Area));
        Assert.Throws<InvalidOperationException>(() => tags.Create("delivery-chain", "Delivery chain", null, null));
    }

    private AreaRegistryService Build() => new(Tags(), Settings());

    private TagRegistryService Tags() =>
        new(NullLogger<TagRegistryService>.Instance, Configuration());

    private ProjectSettingsService Settings() =>
        new(NullLogger<ProjectSettingsService>.Instance, Configuration());

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
        .Build();
}
