using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2793 requirement 3 ("Evidence"): every deletion is appended to a
/// per-project reclaim report (ref, sha, class, reason, task key, timestamp)
/// under the workspace <c>reports/</c> folder, independent of the ref name
/// still existing. These tests exercise <see cref="BranchRetentionEvidenceWriter"/>
/// directly, without needing a real git checkout.
/// </summary>
public sealed class BranchRetentionEvidenceWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "branch-reclaim-evidence-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private BranchRetentionEvidenceWriter Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _tempDir,
        }).Build();
        return new BranchRetentionEvidenceWriter(config, NullLogger<BranchRetentionEvidenceWriter>.Instance);
    }

    [Fact]
    public void AppendDeleted_WritesOneRowPerDeletedAction_SkipsKeptActions()
    {
        var writer = Build();
        var actions = new[]
        {
            new BranchRetentionAction(
                "remote", "task/AGT-1", "sha1", DateTimeOffset.UtcNow, BranchRetentionDecision.Delete,
                Deleted: true, Reason: "Deleted after age and develop/main ancestry recheck.",
                Namespace: BranchNamespace.Task, TaskKey: "AGT-1"),
            new BranchRetentionAction(
                "remote", "task/AGT-2", "sha2", DateTimeOffset.UtcNow, BranchRetentionDecision.TooYoung,
                Deleted: false, Reason: "Tip commit is within the retention window.",
                Namespace: BranchNamespace.Task, TaskKey: "AGT-2"),
        };

        writer.AppendDeleted("Demo", actions);

        var file = writer.ReportFile("Demo")!;
        Assert.True(File.Exists(file));
        var lines = File.ReadAllLines(file);
        var row = Assert.Single(lines);
        using var doc = JsonDocument.Parse(row);
        Assert.Equal("task/AGT-1", doc.RootElement.GetProperty("ref").GetString());
        Assert.Equal("sha1", doc.RootElement.GetProperty("sha").GetString());
        Assert.Equal("Task", doc.RootElement.GetProperty("class").GetString());
        Assert.Equal("AGT-1", doc.RootElement.GetProperty("taskKey").GetString());
        Assert.Equal(
            "Deleted after age and develop/main ancestry recheck.",
            doc.RootElement.GetProperty("reason").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestampUtc", out _));
    }

    [Fact]
    public void AppendDeleted_NoDeletedActions_WritesNoFile()
    {
        var writer = Build();
        var actions = new[]
        {
            new BranchRetentionAction(
                "remote", "task/AGT-3", "sha3", DateTimeOffset.UtcNow, BranchRetentionDecision.NotMergedIntoMain,
                Deleted: false, Reason: "Tip is not contained in main.", Namespace: BranchNamespace.Task),
        };

        writer.AppendDeleted("Demo", actions);

        Assert.False(File.Exists(writer.ReportFile("Demo")));
    }

    [Fact]
    public void AppendDeleted_AppendsAcrossCalls()
    {
        var writer = Build();
        var first = new[]
        {
            new BranchRetentionAction(
                "remote", "task/first", "sha-a", DateTimeOffset.UtcNow, BranchRetentionDecision.Delete,
                Deleted: true, Reason: "r1", Namespace: BranchNamespace.Task),
        };
        var second = new[]
        {
            new BranchRetentionAction(
                "remote", "task/second", "sha-b", DateTimeOffset.UtcNow, BranchRetentionDecision.Delete,
                Deleted: true, Reason: "r2", Namespace: BranchNamespace.Task),
        };

        writer.AppendDeleted("Demo", first);
        writer.AppendDeleted("Demo", second);

        var lines = File.ReadAllLines(writer.ReportFile("Demo")!);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void ReportFile_WithoutTaskRepository_ReturnsNull()
    {
        var config = new ConfigurationBuilder().Build();
        var writer = new BranchRetentionEvidenceWriter(config, NullLogger<BranchRetentionEvidenceWriter>.Instance);

        Assert.Null(writer.ReportFile("Demo"));
    }
}
