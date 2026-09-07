using AgentStudio.TaskServer;
using System.Text.Json;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionCliTests : IDisposable
{
    private readonly RetentionTestWorkspace _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task DryRunAndAgt2739ScenarioReportZeroThenOneArchiveAction()
    {
        var root = _fixture.SeedTask("P", "7-archive", "P-9", DateTimeOffset.UtcNow.AddDays(-1));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        await File.WriteAllTextAsync(Path.Combine(root, "logs", "cli-output.log"), "seeded fixture\n");
        var args = new[] { "retention", "plan", "--workspace", _fixture.Workspace, "--archive", _fixture.Archive,
            "--policy", "default", "--json" };

        Assert.Equal(0, await RetentionCommand.RunAsync(TaskServerCommandLine.Parse(args).Retention!, default));
        Assert.Equal(0, LatestReport().ActionCount);

        await File.WriteAllTextAsync(Path.Combine(root, "task.json"), JsonSerializer.Serialize(new
        {
            id = "P-9", key = "P-9", state = "archive", enteredLaneAt = DateTimeOffset.UtcNow.AddDays(-31),
        }));
        Assert.Equal(0, await RetentionCommand.RunAsync(TaskServerCommandLine.Parse(args).Retention!, default));
        var aged = LatestReport();
        Assert.Equal(1, aged.ActionCount);
        Assert.Equal("P-9", Assert.Single(aged.TopTasks).TaskKey);
    }

    [Fact]
    public void ParserPrintsHelpForMissingOperationAndExplicitHelp()
    {
        var missing = TaskServerCommandLine.Parse(["retention"]);
        var explicitHelp = TaskServerCommandLine.Parse(["retention", "--help"]);

        Assert.Equal("help", missing.Retention!.Operation);
        Assert.Equal("help", explicitHelp.Retention!.Operation);
        Assert.Contains("task-server retention plan", TaskServerCommandLine.RetentionUsage, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRequiresWorkspaceTaskAndBackupOutput()
    {
        Assert.Throws<ArgumentException>(() => TaskServerCommandLine.Parse(["retention", "plan"]));
        Assert.Throws<ArgumentException>(() => TaskServerCommandLine.Parse(["retention", "restore", "--workspace", "x"]));
        Assert.Throws<ArgumentException>(() => TaskServerCommandLine.Parse(["retention", "backup-full", "--workspace", "x"]));
    }

    /// <summary>The pseudo task carries workspace-wide runtime data and belongs to no project.</summary>
    [Fact]
    public async Task ProjectScopedPlanExcludesTheWorkspaceRuntimePseudoTask()
    {
        _fixture.SeedTask("P", "7-archive", "P-9", DateTimeOffset.UtcNow.AddDays(-1));
        SeedOldBusLog();

        Assert.Equal(0, await RunAsync("plan"));
        Assert.Contains(LatestReport().ByProject, group => group.Name == RetentionCommand.RuntimePseudoProject);

        Assert.Equal(0, await RunAsync("plan", "--project", "P"));
        var scoped = LatestReport();
        Assert.DoesNotContain(scoped.ByProject, group => group.Name == RetentionCommand.RuntimePseudoProject);
        Assert.DoesNotContain(scoped.TopTasks, item => item.TaskKey == "_runtime");
    }

    /// <summary>
    /// The first apply report showed hotTaskBytes rising from 7.7 GB to 9.0 GB because the excerpts it had
    /// just written were counted as hot task data. The excerpt cost is now its own figure.
    /// </summary>
    [Fact]
    public async Task ApplyReportsExcerptBytesSeparatelyFromHotTaskBytes()
    {
        var root = _fixture.SeedTask("P", "7-archive", "P-9", DateTimeOffset.UtcNow.AddDays(-60));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        await File.WriteAllLinesAsync(Path.Combine(root, "logs", "cli-output.log"),
            Enumerable.Range(1, 4000).Select(index => $"[12:00:00] line {index}"));

        Assert.Equal(0, await RunAsync("apply"));

        var report = LatestReport();
        var excerpt = Path.Combine(root, "retention-excerpt-stage-1.md");
        Assert.True(File.Exists(excerpt));
        Assert.Equal(new FileInfo(excerpt).Length, report.After.ExcerptBytes);
        Assert.True(report.After.HotTaskBytes < report.Before.HotTaskBytes,
            $"hot task bytes must shrink: {report.Before.HotTaskBytes} -> {report.After.HotTaskBytes}");
        Assert.True(report.After.ColdBytes > 0);
    }

    [Fact]
    public async Task ReExcerptRebuildsBoundedExcerptsFromTheColdPayload()
    {
        var root = _fixture.SeedTask("P", "7-archive", "P-9", DateTimeOffset.UtcNow.AddDays(-60));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        await File.WriteAllLinesAsync(Path.Combine(root, "logs", "cli-output.log"),
            Enumerable.Range(1, 4000).Select(index => $"[12:00:00] ERROR failed step {index}"));
        Assert.Equal(0, await RunAsync("apply"));

        // Stand in for the bloated excerpts the operator withheld; the originals are only in the archive now.
        var excerpt = Path.Combine(root, "retention-excerpt-stage-1.md");
        await File.WriteAllTextAsync(excerpt, new string('x', 1024 * 1024));

        Assert.Equal(0, await RunAsync("re-excerpt"));

        var rebuilt = await File.ReadAllTextAsync(excerpt);
        Assert.StartsWith("# Retention excerpt", rebuilt, StringComparison.Ordinal);
        Assert.True(new FileInfo(excerpt).Length <= RetentionExcerptWriter.MaxExcerptBytes);
        Assert.Contains("ERROR failed step", rebuilt, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "logs", "cli-output.log")), "originals stay cold");
    }

    private void SeedOldBusLog()
    {
        var path = Path.Combine(_fixture.Workspace, "logs", "bus", "old.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-200));
    }

    private async Task<int> RunAsync(string operation, params string[] extra)
    {
        string[] args = ["retention", operation, "--workspace", _fixture.Workspace, "--archive", _fixture.Archive,
            "--policy", "default", "--json", .. extra];
        return await RetentionCommand.RunAsync(TaskServerCommandLine.Parse(args).Retention!, default);
    }

    private RetentionCliReport LatestReport()
    {
        var path = Directory.EnumerateFiles(Path.Combine(_fixture.Workspace, ".metadata", "retention-runs"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal).Last();
        return JsonSerializer.Deserialize<RetentionCliReport>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
