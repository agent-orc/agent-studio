using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

// clock-independent: the store runs on a ManualTimeProvider; no test compares against "now".

/// <summary>
/// Task-folder artefacts in the Task Server (pipeline delivery target,
/// section 05, AGT-3320): task files and run artifacts carry content hashes,
/// content above the inline limit lives in the content object store, the
/// workspace mirror writes revisions only for changed hashes, and the
/// integrated-card query answers release attribution from task file rows.
/// </summary>
public sealed class TaskArtifactsContentTests
{
    private const long InlineLimit = 1024;
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Object_store_writes_once_by_hash_and_refuses_mismatched_or_damaged_content()
    {
        using var temp = new TempDirectory();
        var objects = new TaskContentObjectStore(Path.Combine(temp.Path, "objects"), InlineLimit);
        var content = Encoding.UTF8.GetBytes(new string('x', 4096));
        var sha = TaskContentObjectStore.Sha256(content);

        Assert.True(objects.Spills(content.LongLength));
        Assert.False(objects.Spills(InlineLimit));
        objects.Put(sha, content);
        objects.Put(sha, content);
        Assert.Equal(content, objects.Read(sha));
        Assert.Equal(Path.Combine(objects.Root, "sha256", sha[..2], sha[2..4], sha), objects.PathFor(sha));
        Assert.Throws<ArgumentException>(() => objects.Put(sha, [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => objects.Read("../escape"));

        File.WriteAllBytes(objects.PathFor(sha), Encoding.UTF8.GetBytes(new string('y', 4096)));
        Assert.Throws<InvalidDataException>(() => objects.Read(sha));
        File.Delete(objects.PathFor(sha));
        Assert.Throws<InvalidDataException>(() => objects.Read(sha));
    }

    [Fact]
    public async Task Task_files_carry_hashes_and_content_above_the_inline_limit_lives_in_the_object_store()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (project, task) = await SeedTaskAsync(store);
        var small = Encoding.UTF8.GetBytes("small prompt");
        var large = Encoding.UTF8.GetBytes(new string('L', 3000));

        var first = await store.PutTaskFileAsync(project.ProjectId, task.TaskId, "prompt.md",
            new UpdateTaskFileRequest(Convert.ToBase64String(small), 0, "text/markdown"), "test", default);
        var second = await store.PutTaskFileAsync(project.ProjectId, task.TaskId, "prompt.md",
            new UpdateTaskFileRequest(Convert.ToBase64String(large), 1, "text/markdown"), "test", default);

        Assert.Equal(Sha(small), first.Sha256);
        Assert.Equal(Sha(large), second.Sha256);
        Assert.True(File.Exists(store.ContentObjects.PathFor(Sha(large))));
        Assert.False(File.Exists(store.ContentObjects.PathFor(Sha(small))));
        var read = await store.GetTaskFileContentAsync(project.ProjectId, task.TaskId, "prompt.md", default);
        Assert.Equal(large, read!.Value.Content);
        Assert.Equal(Sha(large), read.Value.Meta.Sha256);
        var history = await store.GetTaskFileHistoryAsync(project.ProjectId, task.TaskId, "prompt.md", default);
        Assert.Equal(
            [(1L, Sha(small), (long?)small.Length), (2L, Sha(large), (long?)large.Length)],
            history.Revisions.Select(revision => (revision.Version, revision.Sha256, revision.SizeBytes)));
    }

    [Fact]
    public async Task Run_artifact_above_the_inline_limit_is_object_stored_and_read_back_by_hash()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store);
        var content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("report line\n", 400)));

        var artifact = await store.IngestArtifactAsync(seed.RunId,
            new ArtifactIngestRequest("art-large", "results/report.html", "text/html",
                Convert.ToBase64String(content), Sha(content), "ingest-large", seed.Fence),
            "runner-content", default);

        Assert.True(File.Exists(store.ContentObjects.PathFor(artifact.Sha256)));
        var read = await store.GetArtifactContentAsync(seed.RunId, artifact.ArtifactId, default);
        Assert.Equal(content, Convert.FromBase64String(read!.ContentBase64));
        var result = await store.GetTaskResultContentAsync(seed.ProjectId, seed.TaskId, "results/report.html", default);
        Assert.Equal(content, Convert.FromBase64String(result!.ContentBase64));
    }

    [Fact]
    public async Task Workspace_mirror_asks_for_changed_content_writes_one_revision_per_change_and_rejects_unknown_cards()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (project, task) = await SeedTaskAsync(store);
        var prompt = Encoding.UTF8.GetBytes("Do the work");
        var result = Encoding.UTF8.GetBytes(new string('R', 2048));

        var probe = await store.MirrorWorkspaceTaskFilesAsync(project.Name, new TaskFileMirrorRequest(
        [
            new TaskFileMirrorTask(task.TaskKey, [Entry("prompt.md", prompt), Entry("results/report.html", result)]),
            new TaskFileMirrorTask("NOPE-404", [Entry("prompt.md", prompt)]),
        ]), "mirror", default);
        Assert.Equal(
            [(task.TaskKey, "prompt.md"), (task.TaskKey, "results/report.html")],
            probe.Needed.Select(item => (item.TaskKey, item.Path)));
        Assert.Equal("task-missing", Assert.Single(probe.Rejected).Code);
        Assert.Equal(0, probe.Written);

        var write = await store.MirrorWorkspaceTaskFilesAsync(project.Name, new TaskFileMirrorRequest(
        [
            new TaskFileMirrorTask(task.TaskKey, [Entry("prompt.md", prompt, withContent: true), Entry("results/report.html", result, withContent: true)]),
        ]), "mirror", default);
        Assert.Equal((2, 0), (write.Written, write.Unchanged));
        Assert.True(File.Exists(store.ContentObjects.PathFor(Sha(result))));

        var again = await store.MirrorWorkspaceTaskFilesAsync(project.Name, new TaskFileMirrorRequest(
        [
            new TaskFileMirrorTask(task.TaskKey, [Entry("prompt.md", prompt, withContent: true)], ["results/report.html"]),
        ]), "mirror", default);
        Assert.Equal((0, 1, 1), (again.Written, again.Unchanged, again.Removed));
        Assert.Null(await store.GetTaskFileContentAsync(project.ProjectId, task.TaskId, "results/report.html", default));
        var history = await store.GetTaskFileHistoryAsync(project.ProjectId, task.TaskId, "prompt.md", default);
        Assert.Single(history.Revisions);

        await Assert.ThrowsAsync<ArgumentException>(() => store.MirrorWorkspaceTaskFilesAsync(project.Name,
            new TaskFileMirrorRequest([new TaskFileMirrorTask(task.TaskKey, [Entry("../escape.md", prompt)])]), "mirror", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.MirrorWorkspaceTaskFilesAsync(project.Name,
            new TaskFileMirrorRequest([new TaskFileMirrorTask(task.TaskKey,
                [new TaskFileMirrorEntry("status.md", Sha(prompt), prompt.Length, "text/markdown", Convert.ToBase64String(result))])]),
            "mirror", default));
    }

    [Fact]
    public async Task Integrated_cards_come_from_verified_integration_records_only()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Attribution"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Attribution", "ATT"), "test", default);
        var verified = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Verified", State: "7-archive"), "test", default);
        var unverified = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Unverified"), "test", default);
        var malformed = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Malformed"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("No record"), "test", default);

        await MirrorAsync(store, project.Name, verified.TaskKey, TaskServerStore.IntegrationRecordPath, Json(new
        {
            version = 1, state = "integrated-verified", sha = "aaa111", integrationBranch = "develop",
            deliveryShas = new[] { "bbb222", "ccc333" },
        }));
        await MirrorAsync(store, project.Name, unverified.TaskKey, TaskServerStore.IntegrationRecordPath, Json(new
        {
            version = 1, state = "integrated-unverified", sha = "ddd444", integrationBranch = "develop",
        }));
        await MirrorAsync(store, project.Name, malformed.TaskKey, TaskServerStore.IntegrationRecordPath, "{ not json"u8.ToArray());

        var response = await store.ListIntegratedCardsAsync(project.Name, default);

        var card = Assert.Single(response.Cards);
        Assert.Equal((verified.TaskKey, "Verified", "7-archive", "develop"), (card.TaskKey, card.Title, card.State, card.IntegrationBranch));
        Assert.Equal(["aaa111", "bbb222", "ccc333"], card.Shas);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ListIntegratedCardsAsync("missing-project", default));
    }

    [Fact]
    public async Task Full_backup_carries_object_stored_content_and_restore_brings_it_back()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (project, task) = await SeedTaskAsync(store);
        var large = Encoding.UTF8.GetBytes(new string('B', 5000));
        await store.PutTaskFileAsync(project.ProjectId, task.TaskId, "results/large.txt",
            new UpdateTaskFileRequest(Convert.ToBase64String(large), 0, "text/plain"), "test", default);

        var management = new FullBackupManagementService(store);
        var summary = await management.CreateAsync("test", default);
        var copied = Path.Combine(store.BackupDirectory, "full", summary.Id, "objects", "sha256", Sha(large)[..2], Sha(large)[2..4], Sha(large));
        Assert.Equal(large, File.ReadAllBytes(copied));
        Assert.True((await management.VerifyAsync(summary.Id, default)).Verified);

        File.Delete(store.ContentObjects.PathFor(Sha(large)));
        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
        Assert.True((await management.RestoreAsync(summary.Id, "operator", default)).Restored);
        var read = await store.GetTaskFileContentAsync(project.ProjectId, task.TaskId, "results/large.txt", default);
        Assert.Equal(large, read!.Value.Content);
    }

    [Theory]
    [InlineData("""{"state":"integrated-verified","sha":"a1","deliveryShas":["b2"],"integrationBranch":"develop"}""", "a1,b2", "develop")]
    [InlineData("""{"state":"integrated-verified","deliveryShas":["b2", 7, ""],"integrationBranch":"develop"}""", "b2", "develop")]
    [InlineData("""{"state":"integrated-verified"}""", "", "")]
    [InlineData("""{"state":"integrated-unverified","sha":"a1"}""", null, null)]
    [InlineData("""["integrated-verified"]""", null, null)]
    [InlineData("""not json""", null, null)]
    public void Integration_record_reader_attributes_only_verified_records(string json, string? shas, string? branch)
    {
        var record = IntegrationRecordAttribution.TryRead(Encoding.UTF8.GetBytes(json));

        Assert.Equal(shas, record is null ? null : string.Join(',', record.Shas));
        Assert.Equal(branch, record?.IntegrationBranch);
    }

    internal static TaskServerStore Store(string dataDirectory, long inlineLimit = InlineLimit)
        => new(Options.Create(new TaskServerOptions
        {
            DataDirectory = dataDirectory,
            ContentInlineLimitBytes = inlineLimit,
        }), new ManualTimeProvider(Start));

    internal static async Task<(ProjectDto Project, TaskDto Task)> SeedTaskAsync(TaskServerStore store, string name = "Artefacts")
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest(name), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, name, "ART"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Artefact task", "Do the work"), "test", default);
        return (project, task);
    }

    internal static async Task MirrorAsync(TaskServerStore store, string project, string taskKey, string path, byte[] content)
    {
        var response = await store.MirrorWorkspaceTaskFilesAsync(project, new TaskFileMirrorRequest(
            [new TaskFileMirrorTask(taskKey, [Entry(path, content, withContent: true)])]), "mirror", default);
        Assert.Empty(response.Rejected);
    }

    internal static TaskFileMirrorEntry Entry(string path, byte[] content, bool withContent = false)
        => new(path, Sha(content), content.LongLength, "application/octet-stream",
            withContent ? Convert.ToBase64String(content) : null);

    internal static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static async Task<(string TaskId, string ProjectId, string RunId, long Fence)> SeedClaimedTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Content"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Content", "CON"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Content task", State: "2-ready"), "test", default);
        const string instance = "runner-content:1";
        await store.RegisterRunnerAsync("runner-content",
            new RegisterRunnerRequest("runner-content", "host-content", instance, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-content", instance), "test", default);
        return (claim.Task!.TaskId, project.ProjectId, claim.Run!.RunId, claim.Lease!.Fence);
    }
}
