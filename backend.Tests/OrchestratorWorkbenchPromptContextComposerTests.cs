using AgentStudio.Docs;
using AgentStudio.Orchestrator;
using AgentStudio.Registry;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorWorkbenchPromptContextComposerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "orchestrator-workbench-composer-" + Guid.NewGuid().ToString("N"));

    public OrchestratorWorkbenchPromptContextComposerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Compose_RendersDossierMetadataAndEntrypointExcerptWithinBoundedBlocks()
    {
        WriteWorkbench("kontext-orchestrator-chats", "Context-Aware Orchestrator Chats", "decided", "testing");
        SetWorkbenchKey("kontext-orchestrator-chats", "AGT-W9");

        var composed = Composer().Compose("Project", "AGT-W9");

        Assert.NotNull(composed);
        Assert.Equal("AGT-W9", composed!.WorkbenchKey);
        Assert.Contains("dossier metadata", composed.IncludedBlocks);
        Assert.Contains("index.html excerpt", composed.IncludedBlocks);
        Assert.Contains("Key: AGT-W9", composed.PromptBlock);
        Assert.Contains("Title: Context-Aware Orchestrator Chats", composed.PromptBlock);
        Assert.Contains("Status: decided", composed.PromptBlock);
        Assert.Contains("=== ACTIVE DOSSIER CONTEXT ===", composed.PromptBlock);
        Assert.Contains("<h1>Context-Aware Orchestrator Chats</h1>", composed.PromptBlock);
    }

    [Fact]
    public void Compose_ReturnsNull_ForUnknownDossierKey()
    {
        WriteWorkbench("kontext-orchestrator-chats", "Context-Aware Orchestrator Chats", "decided", "testing");
        SetWorkbenchKey("kontext-orchestrator-chats", "AGT-W9");

        Assert.Null(Composer().Compose("Project", "AGT-W404"));
    }

    [Fact]
    public void Compose_ReturnsNull_WhenWorkbenchKeyIsMissing()
        => Assert.Null(Composer().Compose("Project", null));

    private OrchestratorWorkbenchPromptContextComposer Composer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(
            Path.Combine(_root, ".orchestrator", "jobs"),
            "Project",
            DefaultWorkspace.Id);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var mutations = new ManagedRepositoryMutationService(
            git,
            pushQueue: null,
            logger: NullLogger<ManagedRepositoryMutationService>.Instance);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git, repositoryMutations: mutations);
        return new OrchestratorWorkbenchPromptContextComposer(workbenches);
    }

    private void WriteWorkbench(string id, string title, string status, string phase)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","title":"{{title}}","summary":"Question","entrypoint":"index.html","status":"{{status}}","phase":"{{phase}}","updatedAt":"2026-08-17T00:00:00Z"}
          """);
    }

    private void SetWorkbenchKey(string id, string key)
    {
        var path = Path.Combine(_root, "docs", "workbenches", id, "workbench.json");
        var descriptor = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        descriptor["key"] = key;
        File.WriteAllText(path, descriptor.ToJsonString());
    }
}
