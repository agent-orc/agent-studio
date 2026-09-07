using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class FileTreeFullBackupServiceTests : IDisposable
{
    private readonly RetentionTestWorkspace _fixture = new(initializeGit: true);
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CreateVerifyRestoreIntoEmptyDirectoryRoundTripsHotUntrackedAndColdData()
    {
        var taskRoot = _fixture.SeedTask("P", "7-archive", "P-2", DateTimeOffset.UtcNow.AddDays(-31));
        Directory.CreateDirectory(Path.Combine(taskRoot, "logs"));
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "logs", "cli-output.log"), "cold original\n");
        _fixture.CommitAll("seed task");
        var store = new FileTreeRetentionStore(_fixture.Workspace, _fixture.Archive);
        var policy = RetentionPolicy.Default();
        var plan = new RetentionPlanner().Plan(await store.EnumerateTasksAndFilesAsync(), policy, DateTimeOffset.UtcNow);
        await new RetentionExecutor(store).ApplyAsync(plan, policy);
        _fixture.CommitAll("archive task evidence");
        Directory.CreateDirectory(Path.Combine(taskRoot, "results"));
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "results", "untracked.md"), "untracked evidence\n");

        var service = new FileTreeFullBackupService();
        var backup = await service.CreateAsync(_fixture.Workspace, _fixture.Backups);
        var inventory = await service.VerifyAsync(backup);
        var restored = Path.Combine(_fixture.Root, "restored-workspace");
        await service.RestoreAsync(backup, restored);

        Assert.True(inventory.Files.Count >= 4);
        Assert.True(File.Exists(Path.Combine(restored, "projects", "P", "tasks", "7-archive", "P-2", "results", "untracked.md")));
        var restoredStore = new FileTreeRetentionStore(restored);
        await restoredStore.RestoreAsync("P-2");
        Assert.Equal("cold original\n", await File.ReadAllTextAsync(Path.Combine(restored, "projects", "P", "tasks", "7-archive", "P-2", "logs", "cli-output.log")));
    }

    /// <summary>
    /// A real workspace always carries foreign files under results/ and attachments/ - this card's own
    /// results folder held a fixture workspace whose archive-manifest.json aborted the production backup
    /// after 20 minutes with an empty output directory.
    /// </summary>
    [Fact]
    public async Task ForeignArchiveManifestUnderResultsIsIgnoredAndDoesNotAbortTheBackup()
    {
        var taskRoot = _fixture.SeedTask("P", "7-archive", "P-3", DateTimeOffset.UtcNow.AddDays(-1), bucket: "002");
        var foreign = Path.Combine(taskRoot, "results", "fixture-workspace", "projects", "PF", "tasks", "7-archive", "AGT-1");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "archive-manifest.json"), "{\"schemaVersion\":1}");
        await File.WriteAllTextAsync(Path.Combine(foreign, "task.json"), "{\"id\":\"AGT-1\"}");
        _fixture.CommitAll("seed foreign fixture");

        var service = new FileTreeFullBackupService();
        var backup = await service.CreateAsync(_fixture.Workspace, _fixture.Backups);
        var inventory = await service.VerifyAsync(backup);

        Assert.True(File.Exists(Path.Combine(backup, "complete.json")), "backup must complete");
        Assert.Empty(inventory.Warnings);
        Assert.Equal(1, inventory.TaskCount);
        Assert.Contains(inventory.Steps, step => step.Name == "bundle");
        Assert.Contains(inventory.Steps, step => step.Name == "hashing");
    }

    [Fact]
    public async Task MalformedPointerOnARealTaskIsAWarningNotAnException()
    {
        var taskRoot = _fixture.SeedTask("P", "7-archive", "P-4", DateTimeOffset.UtcNow.AddDays(-1), bucket: "002");
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "archive-manifest.json"), "{ not json");
        _fixture.CommitAll("seed malformed pointer");

        var service = new FileTreeFullBackupService();
        var inventory = await service.VerifyAsync(await service.CreateAsync(_fixture.Workspace, _fixture.Backups));

        Assert.Contains(inventory.Warnings, warning => warning.Contains("unreadable", StringComparison.Ordinal));
    }
}
