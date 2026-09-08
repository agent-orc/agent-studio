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
        var coldRow = ArtifactStorage(store, "art-heavy");
        Assert.Equal(originalSha, coldRow.Sha256);
        Assert.Equal(originalBytes.LongLength, coldRow.SizeBytes);
        Assert.True(coldRow.ContentIsNull);
        Assert.True(coldRow.Archived);

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
        var statusBytes = "status content"u8.ToArray();
        var promptBytes = "original task prompt"u8.ToArray();
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-status", "status.md", "text/markdown", Convert.ToBase64String(statusBytes),
                Convert.ToHexStringLower(SHA256.HashData(statusBytes)), "ingest-status", seed.Fence),
            "runner-b", default);
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-prompt", "prompt.md", "text/markdown", Convert.ToBase64String(promptBytes),
                Convert.ToHexStringLower(SHA256.HashData(promptBytes)), "ingest-prompt", seed.Fence),
            "runner-b", default);
        var task = await MakeTerminalAndReleaseAsync(store, seed, "runner-b");
        var classAHash = ClassAInventoryHash(store);

        var result = await store.ArchiveTaskNowAsync(task.TaskKey, new RetentionArchiveTaskRequest(2), "operator", default);
        Assert.Equal(1, result.AppliedActions);

        var manifest = await store.GetRetentionManifestAsync(task.TaskKey, default);
        Assert.NotNull(manifest);
        Assert.Equal("cold", manifest!.State);
        Assert.Contains(manifest.Stages, stage => stage.Stage == 2);

        var archivedTask = await store.GetTaskAsync(task.ProjectId, task.TaskId, default);
        Assert.Equal("archived", archivedTask!.ArchiveState);
        Assert.NotNull(archivedTask.ArchivedAt);
        Assert.Equal("archived", Assert.Single(await store.ListTasksAsync(task.ProjectId, default)).ArchiveState);
        Assert.Equal(statusBytes, Convert.FromBase64String(
            (await store.GetArtifactContentAsync(seed.RunId, "art-status", default))!.ContentBase64));
        await AssertArtifactArchivedAsync(store, seed.RunId, "art-prompt");

        await store.RestoreArchivedTaskAsync(task.TaskKey, "operator", default);
        var restoredTask = await store.GetTaskAsync(task.ProjectId, task.TaskId, default);
        Assert.Null(restoredTask!.ArchiveState);
        Assert.Null(restoredTask.ArchivedAt);
        Assert.Equal(promptBytes, Convert.FromBase64String(
            (await store.GetArtifactContentAsync(seed.RunId, "art-prompt", default))!.ContentBase64));
        Assert.Equal(classAHash, ClassAInventoryHash(store));
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
        Assert.Equal(new FullBackupRetentionDto(), initial.FullBackups);

        var updatedRules = initial.Rules.Select(rule => rule.ArtifactClass == "HeavyWorkingData"
            ? rule with { ArchiveAfterDaysTerminal = 14 }
            : rule).ToList();
        var updated = await store.UpdateWorkspaceRetentionPolicyAsync(
            new UpdateRetentionPolicyRequest(updatedRules, initial.Version, new FullBackupRetentionDto(5, 3, 10)), "operator", default);
        Assert.Equal(1, updated.Version);
        Assert.Equal(14, updated.Rules.Single(rule => rule.ArtifactClass == "HeavyWorkingData").ArchiveAfterDaysTerminal);
        Assert.Equal(new FullBackupRetentionDto(5, 3, 10), updated.FullBackups);
        Assert.Equal(new FullBackupRetentionDto(5, 3, 10), (await store.GetWorkspaceRetentionPolicyDtoAsync(default)).FullBackups);

        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpdateWorkspaceRetentionPolicyAsync(
            new UpdateRetentionPolicyRequest(updatedRules, initial.Version), "operator", default));
    }

    [Fact]
    public async Task Project_id_override_changes_the_sqlite_plan_for_that_project()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store, "runner-override");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("override log\n", 20)));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-override", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ingest-override", seed.Fence),
            "runner-override", default);
        await MakeTerminalAndReleaseAsync(store, seed, "runner-override");
        clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(0, (await store.PlanRetentionRunAsync(new RunRetentionRequest(), "test", default)).Plan.ActionCount);

        var workspace = await store.GetWorkspaceRetentionPolicyDtoAsync(default);
        var overrideRule = workspace.Rules.Single(rule => rule.ArtifactClass == "HeavyWorkingData") with
        {
            ArchiveAfterDaysTerminal = 10,
        };
        await store.UpdateProjectRetentionPolicyAsync(
            seed.Project.ProjectId,
            new UpdateRetentionPolicyRequest([overrideRule], 0),
            "operator",
            default);

        var plan = await store.PlanRetentionRunAsync(
            new RunRetentionRequest(Project: seed.Project.ProjectId), "test", default);
        Assert.Contains(plan.Plan.Actions, action => action.TaskId == seed.Task.TaskId && action.Stage == 1);
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
        var fullBackupManagement = new FullBackupManagementService(store);
        var admission = new TaskServerStartupExecutionAdmission(new ConfigurationBuilder().Build());
        var events = new RecordingTaskServerEventPublisher();
        var scheduler = new RetentionSchedulerHostedService(
            store, retentionManagement, fullBackupManagement, new FixedRetentionLoadGate(true), events,
            Options.Create(new TaskServerOptions()), admission, TimeProvider.System,
            NullLogger<RetentionSchedulerHostedService>.Instance);

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Draining, "rehearsal"), "operator", default);
        Assert.Null(await scheduler.RunOnceAsync());

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Normal, "rehearsal"), "operator", default);
        var result = await scheduler.RunOnceAsync();
        Assert.NotNull(result);
        Assert.Empty(result!.Errors);
        Assert.Contains(await store.ListRetentionRunsAsync(default), run => run.Id == result.RunId && run.Trigger == "scheduled");
        Assert.Contains(events.Messages, message => message.Kind == "retention.run.completed");
        Assert.Single(await fullBackupManagement.ListAsync(default));

        var loaded = new RetentionSchedulerHostedService(
            store, retentionManagement, fullBackupManagement, new FixedRetentionLoadGate(false), events,
            Options.Create(new TaskServerOptions()), admission, TimeProvider.System,
            NullLogger<RetentionSchedulerHostedService>.Instance);
        Assert.Null(await loaded.RunOnceAsync());
    }

    [Fact]
    public async Task Cold_payload_deletion_requires_policy_enablement_and_explicit_confirmation()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store, "runner-delete");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("old log\n", 20)));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-delete", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ingest-delete", seed.Fence),
            "runner-delete", default);
        var task = await MakeTerminalAndReleaseAsync(store, seed, "runner-delete");
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default)).AppliedActions);
        var coldManifest = await store.GetRetentionManifestAsync(task.TaskKey, default);
        var payloadPath = Assert.Single(coldManifest!.Stages).PayloadPath;
        Assert.True(File.Exists(payloadPath));

        var current = await store.GetWorkspaceRetentionPolicyDtoAsync(default);
        var enabledRules = current.Rules.Select(rule => rule.ArtifactClass == "HeavyWorkingData"
            ? rule with { DeleteArchiveEnabled = true, DeleteArchiveAfterDaysTerminal = 30 }
            : rule).ToList();
        await store.UpdateWorkspaceRetentionPolicyAsync(
            new UpdateRetentionPolicyRequest(enabledRules, current.Version, current.FullBackups), "operator", default);

        var unconfirmed = await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "operator", default);
        Assert.Equal(0, unconfirmed.AppliedActions);
        Assert.Single(unconfirmed.Warnings);
        Assert.True(File.Exists(payloadPath));

        var confirmed = await store.ApplyRetentionRunAsync(new RunRetentionRequest(ConfirmColdDelete: true), "operator", default);
        Assert.Equal(1, confirmed.AppliedActions);
        Assert.False(File.Exists(payloadPath));
        var tombstone = await store.GetRetentionManifestAsync(task.TaskKey, default);
        Assert.Equal("tombstone", tombstone!.State);
        Assert.NotNull(tombstone.TombstonedAt);
        Assert.All(tombstone.Stages, stage => Assert.Empty(stage.PayloadPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreArchivedTaskAsync(task.TaskKey, "operator", default));
        Assert.Equal(0, (await store.PlanRetentionRunAsync(new RunRetentionRequest(), "operator", default)).Plan.ActionCount);
    }

    [Fact]
    public async Task Stage_two_promotes_an_existing_cold_task_when_only_hot_stubs_remain()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var seed = await SeedClaimedTaskAsync(store, "runner-promote");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("promote log\n", 20)));
        await store.IngestArtifactAsync(
            seed.RunId,
            new ArtifactIngestRequest("art-promote", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "ingest-promote", seed.Fence),
            "runner-promote", default);
        var task = await MakeTerminalAndReleaseAsync(store, seed, "runner-promote");

        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await store.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default)).AppliedActions);
        var promoted = await store.ArchiveTaskNowAsync(
            task.TaskKey,
            new RetentionArchiveTaskRequest(2),
            "operator",
            default);

        Assert.Equal(1, promoted.AppliedActions);
        Assert.Contains(promoted.Plan.Actions, action => action.Stage == 2 && action.FileCount == 0);
        var manifest = await store.GetRetentionManifestAsync(task.TaskKey, default);
        Assert.Equal("archived", GetArchiveState(store, task.TaskId));
        Assert.Contains(manifest!.Stages, stage => stage.Stage == 2 && stage.TotalBytes == 0);
        await store.RestoreArchivedTaskAsync(task.TaskKey, "operator", default);
        Assert.Equal(bytes, Convert.FromBase64String(
            (await store.GetArtifactContentAsync(seed.RunId, "art-promote", default))!.ContentBase64));
    }

    [Fact]
    public async Task Artifact_content_migration_preserves_dependent_foreign_key_target()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE artifacts_old_shape(
                    id TEXT PRIMARY KEY, run_id TEXT NOT NULL, name TEXT NOT NULL, media_type TEXT NOT NULL,
                    sha256 TEXT NOT NULL, content BLOB NOT NULL, size_bytes INTEGER NOT NULL,
                    idempotency_key TEXT NOT NULL UNIQUE, fence INTEGER NOT NULL, created_at TEXT NOT NULL,
                    sequence INTEGER);
                INSERT INTO artifacts_old_shape SELECT id, run_id, name, media_type, sha256, content,
                    size_bytes, idempotency_key, fence, created_at, sequence FROM artifacts;
                DROP TABLE artifacts;
                ALTER TABLE artifacts_old_shape RENAME TO artifacts;
                UPDATE meta SET value='12' WHERE key='schema_version';
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = Store(temp.Path);
        await upgraded.InitializeForBackupAsync();
        using var verify = new SqliteConnection($"Data Source={upgraded.DatabasePath};Pooling=False");
        verify.Open();
        Assert.Equal(0L, ScalarLong(verify, "SELECT [notnull] FROM pragma_table_info('artifacts') WHERE name='content';"));
        Assert.Equal("artifacts", ScalarString(verify,
            "SELECT [table] FROM pragma_foreign_key_list('result_finalizations') WHERE [from]='artifact_id';"));
        Assert.Equal(0L, ScalarLong(verify, "SELECT count(*) FROM pragma_foreign_key_check;"));
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

    [Fact]
    public async Task Legacy_import_applies_active_policy_and_reports_archived_bytes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        using var data = new TempDirectory();
        using var legacy = new TempDirectory();
        var taskDirectory = Path.Combine(legacy.Path, "projects", "legacy", "7-archive", "IMP-1");
        var resultsDirectory = Path.Combine(taskDirectory, "results", "logs");
        Directory.CreateDirectory(resultsDirectory);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "task.json"), """
            {
              "key": "IMP-1",
              "title": "Old imported task",
              "state": "7-archive",
              "projectName": "Legacy",
              "createdAt": "2026-01-01T00:00:00Z",
              "updatedAt": "2026-01-02T00:00:00Z"
            }
            """);
        var logBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("legacy log\n", 30)));
        await File.WriteAllBytesAsync(Path.Combine(resultsDirectory, "cli-output.log"), logBytes);

        var store = Store(data.Path, clock);
        await store.InitializeAsync();
        var migration = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(legacy.Path, "Imported", true, PreserveEvidenceGit: false);
        var inventory = await migration.InventoryAsync(request, default);
        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "import rehearsal"), "operator", default);

        var result = await migration.ImportAsync(
            request with { ExpectedMigrationId = inventory.MigrationId }, "operator", default);

        Assert.Equal(1, result.ArchivedTasks);
        Assert.Equal(logBytes.LongLength, result.ArchivedBytes);
        Assert.Contains(await store.ListRetentionRunsAsync(default), run => run.Trigger == "import" && run.AppliedBytes == logBytes.LongLength);
        var manifest = await store.GetRetentionManifestAsync("IMP-1", default);
        Assert.NotNull(manifest);
        Assert.True(File.Exists(Assert.Single(manifest!.Stages).PayloadPath));
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal(1L, ScalarLong(connection, "SELECT count(*) FROM artifacts WHERE archived=1 AND content IS NULL;"));
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

    private static (string Sha256, long SizeBytes, bool ContentIsNull, bool Archived) ArtifactStorage(
        TaskServerStore store,
        string artifactId)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, size_bytes, content IS NULL, archived FROM artifacts WHERE id=$id;";
        command.Parameters.AddWithValue("$id", artifactId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0);
    }

    private static string ClassAInventoryHash(TaskServerStore store)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var text = new StringBuilder();
        foreach (var table in new[] { "tasks", "runs", "events", "leases", "review_attempts" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var name = reader.GetName(index);
                    if (table == "tasks" && name is "archive_state" or "archived_at") continue;
                    text.Append(table).Append(':').Append(name).Append('=').Append(reader.GetValue(index)).Append('\n');
                }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
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

    private sealed class FixedRetentionLoadGate(bool allowed) : IRetentionRuntimeLoadGate
    {
        public RetentionRuntimeLoadDecision Observe() => new(allowed, allowed ? 0.1 : 9, allowed ? "test-low" : "test-high");
    }

    private sealed class RecordingTaskServerEventPublisher : ITaskServerEventPublisher
    {
        public List<TaskServerOperationalEvent> Messages { get; } = [];

        public Task PublishAsync(TaskServerOperationalEvent message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
