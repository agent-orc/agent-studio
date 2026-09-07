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

    private RetentionCliReport LatestReport()
    {
        var path = Directory.EnumerateFiles(Path.Combine(_fixture.Workspace, ".metadata", "retention-runs"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal).Last();
        return JsonSerializer.Deserialize<RetentionCliReport>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
