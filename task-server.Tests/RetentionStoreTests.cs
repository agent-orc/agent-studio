using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RetentionStoreTests
{
    [Fact]
    public async Task Plan_on_a_fresh_store_reports_no_actions()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var plan = await store.PlanRetentionRunAsync(new RunRetentionRequest(), "test", default);

        Assert.Equal(0, plan.Plan.ActionCount);
        Assert.Equal(0, plan.Plan.TotalBytes);
        Assert.NotEmpty(plan.RunId);
    }

    [Fact]
    public async Task Heavy_artifact_archives_after_the_terminal_threshold_and_restores_byte_identical()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();

        var seed = await SeedClaimedTaskAsync(store, "runner-a");
        var originalBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line of cli output\n", 50)));
        var originalSha = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-heavy", "logs/cli-output.log", "text/plain", Convert.ToBase64String(originalBytes), originalSha, "ingest-heavy", seed.Fence),
            "runner-a", default);
        var task = await MakeTerminalAndReleaseAsync(store, seed, "runner-a");
        var runId = seed.RunId;

        // Terminal for 40 days clears the class C 30-day threshold but not the 180-day whole-task threshold.
        clock.Advance(TimeSpan.FromDays(40));

        var applied = await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default);
        Assert.Equal(1, applied.AppliedActions);
        Assert.Empty(applied.Errors);
        Assert.Contains(applied.Plan.Actions, action => action.Kind == "ArchiveHeavy" && action.TaskKey == task.TaskKey);

        await AssertArtifactArchivedAsync(store, runId, "art-heavy");

        // Class A (the task row itself) must never be touched by an archive run.
        var afterArchive = await store.GetTaskAsync(task.ProjectId, task.TaskId, default);
        Assert.Equal(task.TaskKey, afterArchive!.TaskKey);
        Assert.Equal("7-archive", afterArchive.State);

        // A second plan settles: the archived file is gone from hot inventory, so nothing more is proposed
        // until the 180-day whole-task threshold.
        var secondPlan = await store.PlanRetentionRunAsync(new RunRetentionRequest(), "test", default);
        Assert.Equal(0, secondPlan.Plan.ActionCount);

        await store.RestoreArchivedTaskAsync(task.TaskKey, "test", default);
        var restored = await store.GetArtifactContentAsync(runId, "art-heavy", default);
        Assert.NotNull(restored);
        Assert.Equal(originalSha, restored!.Sha256);
        Assert.Equal(originalBytes, Convert.FromBase64String(restored.ContentBase64));
    }

    [Fact]
    public async Task Archiving_a_whole_task_sets_the_stub_columns_and_restore_clears_them()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store, "runner-b");
        var bytes = "status content"u8.ToArray();
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-status", "status.md", "text/markdown", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ingest-status", seed.Fence),
            "runner-b", default);
        var task = await MakeTerminalAndReleaseAsync(store, seed, "runner-b");

        var result = await store.ArchiveTaskNowAsync(task.TaskKey, requestedStage: 2, "operator", default);
        Assert.Equal(1, result.AppliedActions);

        var manifest = await store.GetRetentionManifestAsync(task.TaskKey, default);
        Assert.NotNull(manifest);
        Assert.Equal("cold", manifest!.State);
        Assert.Contains(manifest.Stages, stage => stage.Stage == 2);

        var archivedTask = await store.GetTaskAsync(task.ProjectId, task.TaskId, default);
        Assert.Equal("archived", GetArchiveState(store, archivedTask!.TaskId));

        await store.RestoreArchivedTaskAsync(task.TaskKey, "operator", default);
        var restoredTask = await store.GetTaskAsync(task.ProjectId, task.TaskId, default);
        Assert.Null(GetArchiveState(store, restoredTask!.TaskId));
    }

    [Fact]
    public async Task Reading_archived_artifact_content_is_refused_with_a_manifest_pointer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store, "runner-c");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\n", 10)));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-log", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ingest-log", seed.Fence),
            "runner-c", default);
        var task = await MakeTerminalAndReleaseAsync(store, seed, "runner-c");
        var runId = seed.RunId;
        clock.Advance(TimeSpan.FromDays(31));
        await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default);

        var exception = await Assert.ThrowsAsync<ArtifactArchivedException>(
            () => store.GetArtifactContentAsync(runId, "art-log", default));
        Assert.Equal(task.TaskId, exception.TaskId);
        Assert.Equal(task.TaskKey, exception.TaskKey);
    }

    [Fact]
    public async Task Workspace_policy_update_is_versioned()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var initial = await store.GetWorkspaceRetentionPolicyDtoAsync(default);
        Assert.Equal(0, initial.Version);
        Assert.Equal(4, initial.Rules.Count);

        var updatedRules = initial.Rules.Select(rule => rule.ArtifactClass == "HeavyWorkingData"
            ? rule with { ArchiveAfterDaysTerminal = 14 }
            : rule).ToList();
        var updated = await store.UpdateWorkspaceRetentionPolicyAsync(
            new UpdateRetentionPolicyRequest(updatedRules, initial.Version), "operator", default);
        Assert.Equal(1, updated.Version);
        Assert.Equal(14, updated.Rules.Single(rule => rule.ArtifactClass == "HeavyWorkingData").ArchiveAfterDaysTerminal);

        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpdateWorkspaceRetentionPolicyAsync(
            new UpdateRetentionPolicyRequest(updatedRules, initial.Version), "operator", default));
    }

    [Fact]
    public async Task Apply_is_refused_in_read_only_and_maintenance_but_allowed_while_draining()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.ReadOnly, "rehearsal"), "operator", default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default));

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "rehearsal"), "operator", default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default));

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Draining, "rehearsal"), "operator", default);
        var draining = await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default);
        Assert.Empty(draining.Errors);
    }

    [Fact]
    public async Task Scheduler_skips_while_draining_and_runs_while_normal()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var retentionManagement = new RetentionManagementService(store);
        var admission = new TaskServerStartupExecutionAdmission(new ConfigurationBuilder().Build());
        var scheduler = new RetentionSchedulerHostedService(
            store, retentionManagement, Options.Create(new TaskServerOptions()), admission, TimeProvider.System,
            NullLogger<RetentionSchedulerHostedService>.Instance);

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Draining, "rehearsal"), "operator", default);
        Assert.Null(await scheduler.RunOnceAsync());

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Normal, "rehearsal"), "operator", default);
        var result = await scheduler.RunOnceAsync();
        Assert.NotNull(result);
        Assert.Empty(result!.Errors);
    }

    [Fact]
    public async Task Backup_and_restore_still_pass_with_an_archived_artifact_present()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store, "runner-d");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\n", 10)));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-log", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ingest-log", seed.Fence),
            "runner-d", default);
        await MakeTerminalAndReleaseAsync(store, seed, "runner-d");
        clock.Advance(TimeSpan.FromDays(31));
        var applied = await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default);
        Assert.Equal(1, applied.AppliedActions);

        var backup = await store.CreateBackupAsync(new BackupRequest("with-archived-artifact"), "operator", default);
        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
        var restored = await store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default);

        Assert.True(restored.Restored);
        await Assert.ThrowsAsync<ArtifactArchivedException>(() => store.GetArtifactContentAsync(seed.RunId, "art-log", default));
    }

    private static async Task AssertArtifactArchivedAsync(TaskServerStore store, string runId, string artifactId)
    {
        var exception = await Assert.ThrowsAsync<ArtifactArchivedException>(
            () => store.GetArtifactContentAsync(runId, artifactId, default));
        Assert.Equal(artifactId, exception.ArtifactId);
    }

    private static string? GetArchiveState(TaskServerStore store, string taskId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_state FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", taskId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    private sealed record ClaimedTaskSeed(TaskDto Task, ProjectDto Project, string RunId, string LeaseId, string InstanceId, long Fence);

    /// <summary>
    /// Claims a task so its lease is active and artifacts can be ingested. Ingestion requires an active
    /// lease at the claimed fence, so the caller must ingest before calling
    /// <see cref="MakeTerminalAndReleaseAsync"/>.
    /// </summary>
    private static async Task<ClaimedTaskSeed> SeedClaimedTaskAsync(TaskServerStore store, string runnerId)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "RET"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Task", "Do the work", "2-ready"), "test", default);
        await store.RegisterRunnerAsync(runnerId, Runner($"{runnerId}:1"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest(runnerId, $"{runnerId}:1"), "test", default);
        Assert.Equal("claimed", claim.Status);
        return new ClaimedTaskSeed(claim.Task!, project, claim.Run!.RunId, claim.Lease!.LeaseId, $"{runnerId}:1", claim.Lease.Fence);
    }

    /// <summary>
    /// Forces the task straight to a terminal lane (as if it had gone through auto-review and human review)
    /// and releases the lease. Releasing after the forced transition is a no-op on the task row, because the
    /// release only resets a task that is still '3-progress'; it still frees the lease for retention checks.
    /// </summary>
    private static async Task<TaskDto> MakeTerminalAndReleaseAsync(TaskServerStore store, ClaimedTaskSeed seed, string runnerId)
    {
        var terminal = await store.UpdateTaskAsync(
            seed.Project.ProjectId, seed.Task.TaskId, new UpdateTaskRequest(null, null, "7-archive", seed.Task.Version), "test", default);
        await store.ReleaseLeaseAsync(
            seed.RunId, new LeaseReleaseRequest(runnerId, seed.InstanceId, seed.LeaseId, seed.Fence, "completed"), "test", default);
        return terminal!;
    }

    private static TaskServerStore Store(string dataDirectory, TimeProvider? clock = null)
        => new(Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }), clock ?? TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instance)
        => new("runner", "host-a", instance, "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]);
}
