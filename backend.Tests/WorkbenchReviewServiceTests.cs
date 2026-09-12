using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchReviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-review-" + Guid.NewGuid().ToString("N"));
    public WorkbenchReviewServiceTests() { Directory.CreateDirectory(_root); WriteWorkbench(); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Record_WritesCurrentReviewAndAppendOnlyTimelineWithoutChangingLifecycle()
    {
        var (catalogue, service) = Services();
        var result = service.Record("Project", "reviewed", new RecordWorkbenchReviewRequest
        {
            Verdict = "partially-superseded",
            SupersededBy = ["AGT-W51"],
            ReviewedBy = "AGT-2784",
            Note = "The storage contract remains current.",
        });

        Assert.True(result.Success, result.Error);
        using var json = JsonDocument.Parse(File.ReadAllText(Descriptor()));
        Assert.Equal("active", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("partially-superseded", json.RootElement.GetProperty("review").GetProperty("verdict").GetString());
        Assert.Single(json.RootElement.GetProperty("reviewHistory").EnumerateArray());
        var item = Assert.Single(catalogue.List("Project")!.Items);
        Assert.Equal("AGT-2784", item.Review!.ReviewedBy);
    }

    [Theory]
    [InlineData("superseded", "", "Operator")]
    [InlineData("unknown", "AGT-W51", "Operator")]
    [InlineData("current", "", "Robert")]
    public void Record_RejectsInvalidMetadata(string verdict, string supersededBy, string reviewedBy)
    {
        var (_, service) = Services();
        var before = File.ReadAllText(Descriptor());
        var result = service.Record("Project", "reviewed", new RecordWorkbenchReviewRequest
        {
            Verdict = verdict,
            SupersededBy = string.IsNullOrEmpty(supersededBy) ? [] : [supersededBy],
            ReviewedBy = reviewedBy,
            Note = "Bounded note.",
        });
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(Descriptor()));
    }

    private string Descriptor() => Path.Combine(_root, "docs", "reviewed", "workbench.json");
    private void WriteWorkbench()
    {
        var dir = Path.GetDirectoryName(Descriptor())!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Reviewed</h1>");
        File.WriteAllText(Descriptor(), """
          {"schemaVersion":1,"id":"reviewed","title":"Reviewed","summary":"Question","entrypoint":"index.html","status":"active","phase":"testing","updatedAt":"2026-09-12T10:00:00Z"}
          """);
    }

    private (WorkbenchCatalogueService, WorkbenchReviewService) Services()
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
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var catalogue = new WorkbenchCatalogueService(scanner, registry, git, configuration: config);
        return (catalogue, new WorkbenchReviewService(catalogue, git));
    }
}
