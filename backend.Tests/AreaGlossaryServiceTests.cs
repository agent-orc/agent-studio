using AgentStudio.Areas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The glossary read/write path against a real checkout: the page lands under
/// the area, the round trip preserves the terms, and a refused write leaves no
/// file behind.
/// </summary>
public sealed class AreaGlossaryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "area-glossary-" + Guid.NewGuid().ToString("N"));

    public AreaGlossaryServiceTests() => Directory.CreateDirectory(Path.Combine(_root, "docs"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Read_OfAnAreaWithoutAPageReportsAnEmptyGlossary()
    {
        var result = Service().Read("Project", "delivery-chain");

        Assert.True(result.Success, result.Error);
        Assert.False(result.Glossary!.Exists);
        Assert.Empty(result.Glossary.Terms);
        Assert.Equal("docs/areas/delivery-chain/glossary.md", result.Glossary.Path);
        Assert.Equal("Delivery chain", result.Glossary.Label);
    }

    [Fact]
    public void Write_CreatesTheWikiPageUnderTheAreaAndReadsBackTheTerms()
    {
        var service = Service();

        var written = service.Write("Project", "observation", new SetAreaGlossaryRequest
        {
            Terms =
            [
                new() { Term = "Probe", Definition = "A bounded check the watcher runs.", Synonyms = ["watcher probe"] },
            ],
        });

        Assert.True(written.Success, written.Error);
        var page = Path.Combine(_root, "docs", "areas", "observation", "glossary.md");
        Assert.True(File.Exists(page));
        var markdown = File.ReadAllText(page);
        Assert.Contains("area: observation", markdown);
        Assert.Contains("tags: [observation]", markdown);
        Assert.Contains("## Probe", markdown);

        var read = service.Read("Project", "observation");
        Assert.True(read.Glossary!.Exists);
        var term = Assert.Single(read.Glossary.Terms);
        Assert.Equal("Probe", term.Term);
        Assert.Equal(["watcher probe"], term.Synonyms);
    }

    [Fact]
    public void Write_RefusesAnUnknownArea()
    {
        var result = Service().Write("Project", "not-an-area", new SetAreaGlossaryRequest());

        Assert.False(result.Success);
        Assert.Equal("not-found", result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "docs", "areas", "not-an-area")));
    }

    [Fact]
    public void Write_RefusesInvalidTermsWithoutTouchingTheRepository()
    {
        var result = Service().Write("Project", "observation", new SetAreaGlossaryRequest
        {
            Terms = [new() { Term = "Probe", Definition = "   " }],
        });

        Assert.False(result.Success);
        Assert.Equal("validation", result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "docs", "areas", "observation")));
    }

    [Fact]
    public void Write_ServesAProjectAreaOnceItIsDeclared()
    {
        var config = Configuration();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var areas = new AreaRegistryService(
            new TagRegistryService(NullLogger<TagRegistryService>.Instance, config), settings);
        var service = Service(config, areas);

        Assert.Equal("not-found", service.Write("Project", "billing", new SetAreaGlossaryRequest()).ErrorCode);
        areas.SetProjectAreas("Project", [new AreaDefinition { Id = "billing", Label = "Billing" }]);

        var result = service.Write("Project", "billing", new SetAreaGlossaryRequest
        {
            Terms = [new() { Term = "Invoice", Definition = "A bill sent to a customer." }],
        });

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "docs", "areas", "billing", "glossary.md")));
    }

    private AreaGlossaryService Service() => Service(Configuration(), null);

    private AreaGlossaryService Service(IConfiguration config, AreaRegistryService? areas)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        areas ??= new AreaRegistryService(
            new TagRegistryService(NullLogger<TagRegistryService>.Instance, config),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config));
        return new AreaGlossaryService(
            areas, scanner, registry,
            new ManagedRepositoryMutationService(git, logger: NullLogger<ManagedRepositoryMutationService>.Instance),
            NullLogger<AreaGlossaryService>.Instance);
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        })
        .Build();
}
