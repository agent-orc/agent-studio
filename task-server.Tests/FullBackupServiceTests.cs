using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class FullBackupServiceTests
{
    [Fact]
    public async Task Full_backup_round_trips_through_verify_and_restore()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "FB"), "test", default);

        var management = new FullBackupManagementService(store);
        var summary = await management.CreateAsync("test", default);
        Assert.True(summary.TotalBytes > 0);
        Assert.Equal(0, summary.ColdPayloadCount);

        var listed = await management.ListAsync(default);
        Assert.Contains(listed, item => item.Id == summary.Id);

        var verified = await management.VerifyAsync(summary.Id, default);
        Assert.True(verified.Verified);
        Assert.Equal(summary.SetSha256, verified.Summary.SetSha256);
        await Assert.ThrowsAsync<ArgumentException>(() => management.VerifyAsync("..", default));

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
        var restored = await management.RestoreAsync(summary.Id, "operator", default);
        Assert.True(restored.Restored);

        var projects = await store.ListProjectsAsync(workspace.WorkspaceId, default);
        Assert.Single(projects);
    }

    [Fact]
    public async Task Full_backup_carries_cold_payload_and_rebinds_manifest_on_a_new_archive_root()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var sourceArchive = Path.Combine(temp.Path, "archive-source");
        var destinationArchive = Path.Combine(temp.Path, "archive-restored");
        var source = Store(temp.Path, clock, sourceArchive);
        await source.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(source);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("full backup log\n", 40)));
        await source.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest(
                "art-full-cold", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "full-backup-ingest", seed.Fence),
            "runner-full", default);
        var terminal = await source.UpdateTaskAsync(
            seed.ProjectId, seed.Task.TaskId,
            new UpdateTaskRequest(null, null, "7-archive", seed.Task.Version), "test", default);
        await source.ReleaseLeaseAsync(
            seed.RunId,
            new LeaseReleaseRequest("runner-full", seed.InstanceId, seed.LeaseId, seed.Fence, "completed"),
            "test", default);
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await source.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default)).AppliedActions);

        var management = new FullBackupManagementService(source);
        var summary = await management.CreateAsync("test", default);
        Assert.Equal(1, summary.ColdPayloadCount);
        var setRoot = Path.Combine(source.BackupDirectory, "full", summary.Id);
        Assert.True(File.Exists(Path.Combine(setRoot, "complete.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(setRoot, "manifests"), "*.json"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(setRoot, "cold"), "payload.zip", SearchOption.AllDirectories));
        Assert.All(
            new[] { "tasks.jsonl", "runs.jsonl", "reviews.jsonl", "integrations.jsonl", "project-costs.jsonl" },
            name => Assert.True(File.Exists(Path.Combine(setRoot, "export", name)), name));

        await source.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "cross-host restore"), "operator", default);
        var destination = Store(temp.Path, clock, destinationArchive);
        await destination.InitializeForBackupAsync();
        var restored = await new FullBackupManagementService(destination).RestoreAsync(summary.Id, "operator", default);
        Assert.True(restored.Restored);

        var manifest = await destination.GetRetentionManifestAsync(terminal!.TaskKey, default);
        var payloadPath = Assert.Single(manifest!.Stages).PayloadPath;
        Assert.StartsWith(Path.GetFullPath(destinationArchive), Path.GetFullPath(payloadPath), StringComparison.Ordinal);
        Assert.True(File.Exists(payloadPath));
        await destination.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Normal, "restore artifact"), "operator", default);
        await destination.RestoreArchivedTaskAsync(terminal.TaskKey, "operator", default);
        var artifact = await destination.GetArtifactContentAsync(seed.RunId, "art-full-cold", default);
        Assert.Equal(bytes, Convert.FromBase64String(artifact!.ContentBase64));
    }

    [Fact]
    public async Task Full_backup_verify_rejects_a_set_with_case_only_path_collisions()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var management = new FullBackupManagementService(store);
        var summary = await management.CreateAsync("test", default);

        var setRoot = Path.Combine(store.BackupDirectory, "full", summary.Id);
        var collisionDirectory = Path.Combine(setRoot, "cold");
        Directory.CreateDirectory(collisionDirectory);
        await File.WriteAllTextAsync(Path.Combine(collisionDirectory, "note.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(collisionDirectory, "NOTE.txt"), "two");

        // The set can never restore correctly onto a case-insensitive filesystem (Windows/NTFS
        // default) once two members differ only by case, so verification must fail loudly here
        // rather than let a cross-platform restore silently drop one of the files.
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => management.VerifyAsync(summary.Id, default));
        Assert.Contains("differ only by case", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_backup_thinning_keeps_daily_weekly_and_monthly_union()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T03:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var management = new FullBackupManagementService(store);
        var created = new List<FullBackupSummaryDto>();
        for (var index = 0; index < 4; index++)
        {
            created.Add(await management.CreateAsync("scheduler", default));
            clock.Advance(TimeSpan.FromDays(1));
        }

        var removed = await management.ThinAsync(
            new AgentStudio.Retention.FullBackupRetentionPolicy { Daily = 1, Weekly = 1, Monthly = 1 },
            "scheduler",
            default);

        Assert.Equal(3, removed);
        Assert.Equal(created[^1].Id, Assert.Single(await management.ListAsync(default)).Id);
        Assert.Empty(Directory.EnumerateFiles(store.BackupDirectory, "*-full-set-*.db", SearchOption.TopDirectoryOnly));
    }

    private static async Task<(TaskDto Task, string ProjectId, string RunId, string LeaseId, string InstanceId, long Fence)>
        SeedClaimedTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Full Backup"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Full Backup", "FUL"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Cold task", State: "2-ready"), "test", default);
        const string instance = "runner-full:1";
        await store.RegisterRunnerAsync(
            "runner-full",
            new RegisterRunnerRequest("runner-full", "host-full", instance, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-full", instance), "test", default);
        return (claim.Task!, project.ProjectId, claim.Run!.RunId, claim.Lease!.LeaseId, instance, claim.Lease.Fence);
    }

    private static TaskServerStore Store(
        string dataDirectory,
        TimeProvider? clock = null,
        string? archivePath = null)
        => new(Options.Create(new TaskServerOptions
        {
            DataDirectory = dataDirectory,
            RetentionArchivePath = archivePath,
        }), clock ?? TimeProvider.System);
}
