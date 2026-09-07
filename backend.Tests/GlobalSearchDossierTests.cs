using AgentStudio.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The Dossier search domain. Ranking runs against a fixture catalogue; the
/// project selection runs against a real workspace so an archived project
/// provably contributes nothing.
/// </summary>
public sealed class GlobalSearchDossierTests : IDisposable
{
    private static readonly Dictionary<string, string> Colors =
        new(StringComparer.OrdinalIgnoreCase) { ["Agent Studio"] = "#569cd6" };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "global-search-dossiers-" + Guid.NewGuid().ToString("N"));
    private readonly string _archivedRoot = Path.Combine(
        Path.GetTempPath(), "global-search-dossiers-archived-" + Guid.NewGuid().ToString("N"));

    public GlobalSearchDossierTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_archivedRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
        try { Directory.Delete(_archivedRoot, true); } catch (IOException) { }
    }

    [Fact]
    public void SearchDossiers_RanksExactKeyBeforeTitleBeforeSummary()
    {
        var catalogue = new[]
        {
            Dossier("summary-hit", "Unrelated title", "Notes on AGT-W15 as prior art", key: "AGT-W40"),
            Dossier("title-hit", "AGT-W15 follow-up", "Something else", key: "AGT-W41"),
            Dossier("key-hit", "Orchestrator watcher", "Watch the runner", key: "AGT-W15"),
        };

        var results = GlobalSearchService.SearchDossiers(catalogue, "AGT-W15", 20, Colors);

        Assert.Equal(new[] { "key-hit", "title-hit", "summary-hit" }, results.Select(x => x.DossierId));
    }

    [Fact]
    public void SearchDossiers_MatchesTitleWordsAndCarriesTheViewerRoute()
    {
        var catalogue = new[]
        {
            Dossier("remote-runner-link-health", "Remote runner link health",
                "Keep the remote runner link observable.", key: "AGT-W22"),
        };

        var item = Assert.Single(GlobalSearchService.SearchDossiers(catalogue, "runner link", 20, Colors));

        Assert.Equal("dossiers", item.Domain);
        Assert.Equal("Agent Studio", item.ProjectName);
        Assert.Equal("#569cd6", item.ProjectColor);
        Assert.Equal("Remote runner link health", item.Title);
        Assert.Equal("AGT-W22", item.DossierKey);
        Assert.Equal("remote-runner-link-health", item.DossierId);
        Assert.Equal("Keep the remote runner link observable.", item.Summary);
        Assert.Equal("active · shaping", item.Subtitle);
        Assert.Null(item.Path);
    }

    [Fact]
    public void SearchDossiers_MatchesSummaryStatusAndPhaseWords()
    {
        var catalogue = new[]
        {
            Dossier("telemetry-layer", "Telemetry layer", "Collect bounded run telemetry.", key: "AGT-W7"),
        };

        Assert.Single(GlobalSearchService.SearchDossiers(catalogue, "bounded run", 20, Colors));
        Assert.Single(GlobalSearchService.SearchDossiers(catalogue, "active", 20, Colors));
        Assert.Single(GlobalSearchService.SearchDossiers(catalogue, "shaping", 20, Colors));
        Assert.Empty(GlobalSearchService.SearchDossiers(catalogue, "decision-ready", 20, Colors));
    }

    [Fact]
    public void SearchDossiers_KeepsHistoryAndDropsInvalidDescriptors()
    {
        var catalogue = new[]
        {
            Dossier("discarded", "Discarded watcher", "Watcher idea", key: "AGT-W15", status: "archived"),
            Dossier("written-up", "Documented watcher", "Watcher idea", key: "AGT-W16", status: "documented"),
            Dossier("broken", "Broken watcher", "Watcher idea", key: "AGT-W17", valid: false),
        };

        var results = GlobalSearchService.SearchDossiers(catalogue, "watcher", 20, Colors);

        Assert.Equal(new[] { "discarded", "written-up" }, results.Select(x => x.DossierId).Order());
        Assert.Equal(new[] { "archived · shaping", "documented · shaping" }, results.Select(x => x.Subtitle).Order());
    }

    [Fact]
    public void SearchDossiers_BoundsResultsToTheRequestedLimit()
    {
        var catalogue = Enumerable.Range(1, 12)
            .Select(index => Dossier($"watcher-{index}", $"Watcher {index}", "Watch it", key: $"AGT-W{index}"))
            .ToArray();

        Assert.Equal(5, GlobalSearchService.SearchDossiers(catalogue, "watcher", 5, Colors).Count);
    }

    [Fact]
    public void Search_FindsDossiersByKeyAndSkipsArchivedProjects()
    {
        WriteDossier(_root, "orchestrator-waechter", "AGT-W15", "Orchestrator watcher", "Watch the runner loop.");
        WriteDossier(_archivedRoot, "retired-idea", "OLD-W15", "Retired watcher", "Watch the runner loop.");
        var (search, registry) = Search();

        var found = search.Search("AGT-W15", Domains("dossiers"), 20);
        var item = Assert.Single(found.Dossiers);
        Assert.Equal("orchestrator-waechter", item.DossierId);
        Assert.Equal("Agent Studio", item.ProjectName);
        Assert.Empty(found.Errors);

        var beforeArchive = search.Search("Retired watcher", Domains("dossiers"), 20);
        Assert.Equal("retired-idea", Assert.Single(beforeArchive.Dossiers).DossierId);

        registry.SetArchived(
            registry.List().Single(project => project.DisplayName == "Retired").Id, archived: true);

        Assert.Empty(search.Search("Retired watcher", Domains("dossiers"), 20).Dossiers);
    }

    [Fact]
    public void Search_LeavesDossiersEmptyWhenTheDomainIsNotRequested()
    {
        WriteDossier(_root, "orchestrator-waechter", "AGT-W15", "Orchestrator watcher", "Watch the runner loop.");

        Assert.Empty(Search().Search.Search("AGT-W15", Domains("tasks"), 20).Dossiers);
    }

    private static HashSet<string> Domains(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    private (GlobalSearchService Search, ProjectRegistry Registry) Search()
    {
        var jobs = Path.Combine(_root, ".orchestrator", "jobs");
        var archivedJobs = Path.Combine(_archivedRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(archivedJobs);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Agent Studio",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = jobs,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(jobs, "Agent Studio", DefaultWorkspace.Id);
        // "Retired" has no watch path, so archiving it in the registry removes
        // it from the catalogue project set instead of falling back to config.
        registry.EnsureProjectForStorage(archivedJobs, "Retired", DefaultWorkspace.Id);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git);
        var docs = new ProjectDocsService(
            scanner, registry, NullLogger<ProjectDocsService>.Instance, git, workbenches);
        return (
            new GlobalSearchService(
                scanner, git, registry, docs, workbenches,
                NullLogger<GlobalSearchService>.Instance),
            registry);
    }

    private static void WriteDossier(string root, string id, string key, string title, string summary)
    {
        var dir = Path.Combine(root, "docs", "operations", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","key":"{{key}}","title":"{{title}}",
           "summary":"{{summary}}","entrypoint":"index.html","status":"active",
           "phase":"shaping","updatedAt":"2026-09-01T10:00:00Z"}
          """);
    }

    private static WorkbenchOverviewItem Dossier(
        string id, string title, string summary, string key,
        string status = "active", bool valid = true) =>
        new("Agent Studio", new WorkbenchListItem(
            id, title, summary, status, "shaping",
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            $"docs/operations/{id}/index.html", valid,
            valid ? null : "Descriptor needs repair.", [])
        {
            Key = key,
        });
}
