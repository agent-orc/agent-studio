using System.Text.Json;
using AgentStudio.Areas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Dossier tags: the descriptor field, the catalogue projection, and the write
/// boundary that refuses unknown ids (AGT-2803).
/// </summary>
public sealed class WorkbenchTagServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-tags-" + Guid.NewGuid().ToString("N"));

    public WorkbenchTagServiceTests()
    {
        Directory.CreateDirectory(_root);
        WriteWorkbench();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Catalogue_ProjectsTheDescriptorTags()
    {
        SetDescriptorTags("""["delivery-chain","incident"]""");
        var (catalogue, _) = Services();

        var item = Assert.Single(catalogue.List("Project")!.Items);

        Assert.True(item.Valid, item.Error);
        Assert.Equal(["delivery-chain", "incident"], item.Tags);
    }

    [Fact]
    public void Catalogue_ProjectsAnEmptyTagListWhenTheFieldIsAbsent()
    {
        var (catalogue, _) = Services();

        Assert.Empty(Assert.Single(catalogue.List("Project")!.Items).Tags);
    }

    [Theory]
    [InlineData("\"delivery-chain\"", "tags must be an array")]
    [InlineData("""[1]""", "tags must be an array")]
    [InlineData("""["Delivery Chain"]""", "Invalid tag id")]
    public void Catalogue_RefusesAMalformedTagsField(string json, string expected)
    {
        SetDescriptorTags(json);
        var (catalogue, _) = Services();

        var item = Assert.Single(catalogue.List("Project")!.Items);

        Assert.False(item.Valid);
        Assert.Contains(expected, item.Error);
    }

    [Fact]
    public void Set_WritesTheNormalizedTagsAndTheCatalogueSeesThem()
    {
        var (catalogue, service) = Services();

        var result = service.Set("Project", "tagged", new SetWorkbenchTagsRequest
        {
            Tags = ["Delivery-Chain", "incident", "delivery-chain"],
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(["delivery-chain", "incident"], result.Tags);
        using var json = JsonDocument.Parse(File.ReadAllText(Descriptor()));
        Assert.Equal(["delivery-chain", "incident"],
            json.RootElement.GetProperty("tags").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["delivery-chain", "incident"],
            Assert.Single(catalogue.List("Project")!.Items).Tags);
    }

    [Fact]
    public void Set_RefusesAnUnknownTagIdAndLeavesTheDescriptorUntouched()
    {
        var (_, service) = Services();
        var before = File.ReadAllText(Descriptor());

        var result = service.Set("Project", "tagged", new SetWorkbenchTagsRequest
        {
            Tags = ["delivery-chain", "not-a-registered-tag"],
        });

        Assert.False(result.Success);
        Assert.Equal("validation", result.ErrorCode);
        Assert.Contains("not-a-registered-tag", result.Error);
        Assert.Equal(before, File.ReadAllText(Descriptor()));
    }

    [Fact]
    public void Set_WithAnEmptyListClearsTheTags()
    {
        var (_, service) = Services();
        Assert.True(service.Set("Project", "tagged", new SetWorkbenchTagsRequest { Tags = ["observation"] }).Success);

        Assert.True(service.Set("Project", "tagged", new SetWorkbenchTagsRequest { Tags = [] }).Success);

        using var json = JsonDocument.Parse(File.ReadAllText(Descriptor()));
        Assert.Empty(json.RootElement.GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public void Set_OnAnUnknownDossierReportsNotFound()
    {
        var (_, service) = Services();

        var result = service.Set("Project", "missing", new SetWorkbenchTagsRequest { Tags = [] });

        Assert.False(result.Success);
        Assert.Equal("not-found", result.ErrorCode);
    }

    private string Descriptor() => Path.Combine(_root, "docs", "tagged", "workbench.json");

    private void WriteWorkbench()
    {
        var dir = Path.GetDirectoryName(Descriptor())!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Tagged</h1>");
        File.WriteAllText(Descriptor(), """
          {"schemaVersion":1,"id":"tagged","title":"Tagged","summary":"Question","entrypoint":"index.html","status":"active","phase":"testing","updatedAt":"2026-09-13T10:00:00Z"}
          """);
    }

    private void SetDescriptorTags(string json)
    {
        var descriptor = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Descriptor()))!.AsObject();
        descriptor["tags"] = System.Text.Json.Nodes.JsonNode.Parse(json);
        File.WriteAllText(Descriptor(), descriptor.ToJsonString());
    }

    private (WorkbenchCatalogueService, WorkbenchTagService) Services()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var catalogue = new WorkbenchCatalogueService(scanner, registry, git, configuration: config);
        var areas = new AreaRegistryService(
            new TagRegistryService(NullLogger<TagRegistryService>.Instance, config),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config));
        return (catalogue, new WorkbenchTagService(catalogue, areas, git));
    }
}
