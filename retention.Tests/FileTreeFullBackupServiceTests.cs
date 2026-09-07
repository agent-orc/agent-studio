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
}
