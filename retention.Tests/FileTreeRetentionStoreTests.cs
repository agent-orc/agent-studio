using AgentStudio.Retention;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Retention.Tests;

public sealed class FileTreeRetentionStoreTests : IDisposable
{
    private readonly RetentionTestWorkspace _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ArchiveManifestRestoreAndIdempotentRerunRoundTrip()
    {
        var taskRoot = _fixture.SeedTask("P", "7-archive", "P-1", DateTimeOffset.UtcNow.AddDays(-31));
        var original = "first\nERROR failed exit code 2\nlast\n";
        Directory.CreateDirectory(Path.Combine(taskRoot, "logs"));
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "logs", "cli-output.log"), original);
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "status.md"), "status stays hot\n");
        var store = new FileTreeRetentionStore(_fixture.Workspace, _fixture.Archive);
        var policy = RetentionPolicy.Default();
        var plan = new RetentionPlanner().Plan(await store.EnumerateTasksAndFilesAsync(), policy, DateTimeOffset.UtcNow);

        var result = await new RetentionExecutor(store).ApplyAsync(plan, policy);

        Assert.Empty(result.Errors);
        Assert.False(File.Exists(Path.Combine(taskRoot, "logs", "cli-output.log")));
        Assert.True(File.Exists(Path.Combine(taskRoot, "retention-excerpt-stage-1.md")));
        Assert.True(File.Exists(Path.Combine(taskRoot, "archive-manifest.json")));
        var manifestPath = Assert.Single(Directory.EnumerateFiles(_fixture.Archive, "manifest.json", SearchOption.AllDirectories));
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var archivedFile = Assert.Single(manifest.Files);
        Assert.Equal("logs/cli-output.log", archivedFile.RelativePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original))), archivedFile.Sha256);
        using (var zip = ZipFile.OpenRead(Path.Combine(Path.GetDirectoryName(manifestPath)!, "payload.zip")))
            Assert.NotNull(zip.GetEntry("logs/cli-output.log"));

        var second = new RetentionPlanner().Plan(await store.EnumerateTasksAndFilesAsync(), policy, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(second.Actions, action => action.Kind == RetentionActionKind.ArchiveHeavy);

        await store.RestoreAsync("P-1");
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(taskRoot, "logs", "cli-output.log")));
        var pointer = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(taskRoot, "archive-manifest.json")));
        Assert.Equal(JsonValueKind.String, pointer.RootElement.GetProperty("restoredAt").ValueKind);
    }

    [Fact]
    public async Task InventoryReadsLaneAndTerminalTimeFromTaskJsonUnderNumericBucket()
    {
        var terminalAt = DateTimeOffset.UtcNow.AddDays(-31);
        var taskRoot = _fixture.SeedTask("P", "7-archive", "P-2", terminalAt, bucket: "002");
        Directory.CreateDirectory(Path.Combine(taskRoot, "logs"));
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "logs", "cli-output.log"), "production shape\n");
        var store = new FileTreeRetentionStore(_fixture.Workspace, _fixture.Archive);

        var task = Assert.Single(await store.EnumerateTasksAndFilesAsync());

        Assert.Equal("7-archive", task.Lane);
        Assert.Equal(terminalAt, task.TerminalAt);
        var plan = new RetentionPlanner().Plan([task], RetentionPolicy.Default(), DateTimeOffset.UtcNow);
        Assert.Contains(plan.Actions, action => action.Kind == RetentionActionKind.ArchiveHeavy);
    }
}
