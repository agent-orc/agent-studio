using AgentStudio.Docs;
using AgentStudio.Registry;
using AgentStudio.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Global search's fourth domain: Dossiers, fed by the Workbench catalogue
/// across every registered project rather than a per-repository git scan.
/// </summary>
public sealed class GlobalSearchDossierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"global-search-dossiers-{Guid.NewGuid():N}");

    public GlobalSearchDossierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Search_RanksExactDossierKeyBeforeATitleOrSummaryMatch()
    {
        var (service, registry) = Build();
        var watcherRoot = RegisterProject(registry, "Watcher Project");
        WriteWorkbench(watcherRoot, "orchestrator-waechter", "Global Orchestrator Watcher",
            "Watcher rollout notes.", "active", "shaping", key: "AGT-W15");
        WriteWorkbench(watcherRoot, "runner-link", "Runner link ownership",
            "Follow-up referencing AGT-W15 in its own summary.", "active", "shaping", key: "AGT-W20");

        var response = service.Search("AGT-W15", Domains(), 20);

        Assert.Equal(2, response.Dossiers.Count);
        Assert.Equal("AGT-W15", response.Dossiers[0].DossierKey);
        Assert.Equal("orchestrator-waechter", response.Dossiers[0].WorkbenchId);
    }

    [Fact]
    public void Search_MatchesOnTitleWords()
    {
        var (service, registry) = Build();
        var root = RegisterProject(registry, "Runner Project");
        WriteWorkbench(root, "runner-link", "Runner link ownership",
            "Tunnel retirement decision.", "active", "shaping", key: "AGT-W20");

        var response = service.Search("runner link", Domains(), 20);

        var item = Assert.Single(response.Dossiers);
        Assert.Equal("Runner Project", item.ProjectName);
        Assert.Equal("AGT-W20", item.DossierKey);
        Assert.Equal("runner-link", item.WorkbenchId);
        Assert.Null(item.Path);
    }

    [Fact]
    public void Search_MatchesOnSummaryText()
    {
        var (service, registry) = Build();
        var root = RegisterProject(registry, "Batch Project");
        WriteWorkbench(root, "batch-gate-concept", "Batch Gate decision",
            "One single-flight full suite for a closed delivery wave.", "decided", "decision-ready", key: "AGT-W30");

        var response = service.Search("single-flight", Domains(), 20);

        Assert.Equal("AGT-W30", Assert.Single(response.Dossiers).DossierKey);
    }

    [Fact]
    public void Search_IncludesHistoryStatusesForAnArchivedDossier()
    {
        var (service, registry) = Build();
        var root = RegisterProject(registry, "History Project");
        WriteWorkbench(root, "old-idea", "Old idea", "Discarded exploration.",
            "archived", null, key: "AGT-W40");

        var response = service.Search("AGT-W40", Domains(), 20);

        var item = Assert.Single(response.Dossiers);
        Assert.Equal("archived", item.Lane);
    }

    [Fact]
    public void Search_ExcludesDossiersFromAnArchivedProject()
    {
        var (service, registry) = Build();
        var root = RegisterProject(registry, "Retired Project");
        WriteWorkbench(root, "retired-dossier", "Retired dossier", "Should not surface.",
            "active", "shaping", key: "AGT-W50");
        var project = registry.FindByIdOrDisplayName("Retired Project")!;
        registry.SetArchived(project.Id, true);

        var response = service.Search("AGT-W50", Domains(), 20);

        Assert.Empty(response.Dossiers);
    }

    private static HashSet<string> Domains() => new(StringComparer.OrdinalIgnoreCase) { "dossiers" };

    private (GlobalSearchService Service, ProjectRegistry Registry) Build()
    {
        var config = new ConfigurationBuilder().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var indexes = new GlobalSearchIndexes(config, git, NullLogger<GlobalSearchIndexes>.Instance);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git);
        var docs = new ProjectDocsService(
            scanner, registry, NullLogger<ProjectDocsService>.Instance, git, workbenches);
        var service = new GlobalSearchService(scanner, indexes, registry, docs, NullLogger<GlobalSearchService>.Instance);
        return (service, registry);
    }

    private string RegisterProject(ProjectRegistry registry, string displayName)
    {
        var root = Path.Combine(_root, displayName.Replace(' ', '-'));
        Directory.CreateDirectory(root);
        registry.EnsureProjectForStorage(Path.Combine(root, ".orchestrator", "jobs"), displayName, DefaultWorkspace.Id);
        return root;
    }

    private static void WriteWorkbench(
        string root, string id, string title, string summary, string status, string? phase, string key)
    {
        var dir = Path.Combine(root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        var phaseProperty = phase == null ? "" : $",\"phase\":\"{phase}\"";
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","key":"{{key}}","title":"{{title}}","summary":"{{summary}}","entrypoint":"index.html","status":"{{status}}","updatedAt":"2026-08-01T10:00:00Z"{{phaseProperty}}}
          """);
    }
}
