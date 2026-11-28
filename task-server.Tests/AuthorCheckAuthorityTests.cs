using System.Security.Cryptography;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class AuthorCheckAuthorityTests
{
    private static readonly byte[] Bundle = "bounded-source"u8.ToArray();

    [Fact]
    public async Task Rejects_stale_parent_and_wrong_digest_then_claims_concurrent_replay_once()
    {
        using var directory = new TempDirectory();
        var (store, settings, parent, claimRequest, task) = await StartAsync(directory.Path);
        var submission = Submit(parent, settings.Base);
        using var staleStream = new MemoryStream(Bundle);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.SubmitAuthorCheckAsync(
            submission with { Parent = parent with { Fence = parent.Fence - 1 } }, "coding-principal", staleStream, default));
        Assert.Equal(0, staleStream.Position);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SubmitAuthorCheckAsync(
            submission with { Snapshot = submission.Snapshot with { Sha256 = new string('0', 64) } },
            "coding-principal", new MemoryStream(Bundle), default));
        var duplicate = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.SubmitAuthorCheckAsync(
            submission, "coding-principal", new MemoryStream(Bundle), default)));
        Assert.Equal(duplicate[0], duplicate[1]);
        Assert.Equal("queued", duplicate[0].Status);
        Assert.Equal("empty", (await ClaimAsync(store, claimRequest)).Status);
        await store.ObserveAuthorCheckProtectedWorkAsync(new("kori", 0, 1), "coordinator", default);
        Assert.Equal("empty", (await ClaimAsync(store, claimRequest)).Status);
        await store.ObserveAuthorCheckProtectedWorkAsync(new("kori", 0, 0), "coordinator", default);
        var claims = await Task.WhenAll(ClaimAsync(store, claimRequest), ClaimAsync(store, claimRequest));
        var claim = Assert.Single(claims.Where(item => item.Status == "claimed"));
        Assert.Equal(submission.Snapshot, claim.AuthorCheck!.Snapshot);
        var lease = claim.Lease!;
        var authority = new PostWorkAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReadAuthorCheckSourceAsync(lease.AttemptId, new(authority, new string('0', 64)), default));
        Assert.Equal(Bundle, await store.ReadAuthorCheckSourceAsync(lease.AttemptId, new(authority, claim.SourceGrant!), default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.SettleAuthorCheckAsync(lease.AttemptId, new(authority with { Fence = 0 }, "passed", 0, 20, [], 0, false), default));
        Assert.Empty(await store.ListArtifactsAsync(parent.RunId, default));
        var report = new AuthorCheckSettlement(authority, "failed", 1, 20, "error CS1002\n"u8.ToArray(), 0, false);
        var receipt = await store.SettleAuthorCheckAsync(lease.AttemptId, report, default);
        Assert.Equal(1, receipt.ExitCode);
        Assert.Contains("error CS1002", receipt.Diagnostics!);
        Assert.Equal(receipt.LogPath, Assert.Single(await store.ListArtifactsAsync(parent.RunId, default)).Name);
        Assert.Equal(receipt.CheckId, (await store.ReadAuthorCheckAsync(receipt.CheckId, parent, "coding-principal", default)).CheckId);
        var reused = await store.SubmitAuthorCheckAsync(submission with { RequestId = Guid.NewGuid() },
            "coding-principal", new MemoryStream(Bundle), default);
        Assert.Equal(receipt.CheckId, reused.CheckId);

        await store.SweepAuthorChecksAsync(default);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "author-check-source")));
        await using var db = new SqliteConnection($"Data Source={store.DatabasePath}");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM author_checks WHERE source_grant IS NOT NULL)
                 + (SELECT count(*) FROM review_attempts) + (SELECT count(*) FROM gate_attempts)
                 + (SELECT count(*) FROM runs WHERE result_sha IS NOT NULL)
                 + (SELECT count(*) FROM tasks WHERE id = $task AND state <> '3-progress');
            """;
        command.Parameters.AddWithValue("$task", task);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Disabling_the_pilot_fences_a_live_child_and_reclaims_source()
    {
        using var directory = new TempDirectory();
        var (store, settings, parent, request, _) = await StartAsync(directory.Path);
        var queued = await store.SubmitAuthorCheckAsync(Submit(parent, settings.Base), "coding-principal",
            new MemoryStream(Bundle), default);
        await store.ObserveAuthorCheckProtectedWorkAsync(new("kori", 0, 0), "coordinator", default);
        var lease = (await ClaimAsync(store, request)).Lease!;
        settings.Enabled = false;
        var authority = new PostWorkAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RenewPostWorkAsync(lease.AttemptId, new(authority, 30), default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.SettleAuthorCheckAsync(lease.AttemptId, new(authority, "passed", 0, 1, [], 0, false), default));
        Assert.Equal("canceled", (await store.ReadAuthorCheckAsync(queued.CheckId, parent, "coding-principal", default)).Status);
        Assert.True(await store.ReleaseContainedAuthorCheckAsync(lease.AttemptId, authority, default));
        Assert.True(await store.ReleaseContainedAuthorCheckAsync(lease.AttemptId, authority, default));
        await store.SweepAuthorChecksAsync(default);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "author-check-source")));
        Assert.Empty(await store.ListArtifactsAsync(parent.RunId, default));
        Assert.Equal("empty", (await ClaimAsync(store, request)).Status);
    }

    private static Task<PostWorkClaimResponse> ClaimAsync(TaskServerStore store, PostWorkClaimRequest request)
        => store.ClaimPostWorkAsync(request, "post", default, boundExecutor: true);

    private static AuthorCheckSubmission Submit(AuthorCheckParent parent, string prerequisite)
        => new(parent, Guid.NewGuid(), "build", new(prerequisite, new string('b', 40), new string('c', 40),
            Convert.ToHexStringLower(SHA256.HashData(Bundle)), Bundle.Length));

    private static async Task<(TaskServerStore Store, AuthorCheckPilotOptions Settings,
        AuthorCheckParent Parent, PostWorkClaimRequest ClaimRequest, string Task)> StartAsync(string path)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 28, 14, 0, 0, TimeSpan.Zero));
        var settings = new AuthorCheckPilotOptions
        {
            Enabled = true, ExecutorId = "post", ExecutorHostId = "kori", ProtectedWorkPrincipalId = "coordinator",
            Base = new string('a', 40), Image = "prewarmed@sha256:" + new string('e', 64), Solution = "test.sln",
        };
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = path, AuthorCheck = settings,
        }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PROJ"), "test", default);
        const string repo = "https://github.com/example/author-check.git";
        var registration = await store.RegisterProjectRepositoryAsync(new(project.ProjectId, workspace.WorkspaceId,
            project.Name, project.TaskKeyPrefix, repo, "develop"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Task", "work", "2-ready"), "test", default);
        await store.RegisterRunnerAsync("coding", new RegisterRunnerRequest("coding", settings.SourceHostId,
            "coding-instance", "1.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        await store.RegisterRunnerAsync("post", new RegisterRunnerRequest("post", "kori", "post-instance", "1.0",
            TaskServerProtocol.Current, [PostWorkCapabilities.Executor, AuthorCheckProtocol.Kind], BootstrapMaxParallelism: 4),
            "test", default);
        foreach (var runner in new[] { "coding", "post" })
            await store.RecordProjectRepositoryProbeAsync(project.ProjectId, runner,
                new ProjectRepositoryProbeRequest(repo, repo, true, true, false), default);
        await store.UpdateHostProjectPolicyAsync("kori", new(false, [project.ProjectId], 0), "test", default);
        await store.UpsertHostPlacementAsync("kori", new("post", "post-instance", ["post"], "linux", "x64",
            ["container"], [settings.Image], [], [project.ProjectId], [], 0), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("coding", "coding-instance"), "coding", default, "coding-principal");
        Assert.Equal("claimed", claim.Status);
        settings.RunId = claim.Run!.RunId;
        settings.ProjectId = project.ProjectId;
        settings.RepositoryId = registration.Registration.RepositoryId;
        var parent = new AuthorCheckParent(settings.RunId, "coding", "coding-instance", claim.Lease!.LeaseId, claim.Lease.Fence);
        var capacity = await store.UpdateRuntimeCapacitySettingsAsync("kori", new(4, 80, "balanced", 0), "operator", default);
        return (store, settings, parent, new("post", "post-instance", 30, capacity.AllocationVersion, true,
            settings.Image, 10L * 1024 * 1024 * 1024), claim.Task!.TaskId);
    }
}
