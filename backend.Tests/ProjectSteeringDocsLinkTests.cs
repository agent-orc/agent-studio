using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectSteeringDocsLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steering-links-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "steering-outside-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inventory_and_read_skip_linked_files_and_directories()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "AGENTS.md"), "outside-secret");
        File.CreateSymbolicLink(Path.Combine(_root, "AGENTS.md"), Path.Combine(_outside, "AGENTS.md"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), _outside);
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "inside-project");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "alpha",
            ["WatchPaths:0:RepositoryPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var docs = new ProjectSteeringDocsService(scanner, NullLogger<ProjectSteeringDocsService>.Instance);

        Assert.DoesNotContain(docs.GetOverview("alpha")!.Sources, source => source.RelPath == "AGENTS.md");
        Assert.Null(docs.ReadFile("alpha", "AGENTS.md"));
        Assert.Null(docs.ReadFile("alpha", "linked/AGENTS.md"));
        Assert.Equal("inside-project", docs.ReadFile("alpha", "CLAUDE.md")!.Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_outside)) Directory.Delete(_outside, recursive: true);
    }
}
