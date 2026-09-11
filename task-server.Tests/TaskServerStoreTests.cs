using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class TaskServerStoreTests
{
    [Fact]
    public async Task Releasing_a_dead_runner_attempt_returns_its_progress_task_to_ready()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        await store.ReleaseLeaseAsync(
            claim.Run!.RunId,
            new LeaseReleaseRequest(
                "runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence,
                "runner-process-missing"),
            "runner-a",
            default);

        var released = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
        Assert.Equal("2-ready", released!.State);
        var replacement = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        Assert.Equal("claimed", replacement.Status);
        Assert.Equal(task.TaskId, replacement.Task!.TaskId);
        Assert.True(replacement.Lease!.Fence > claim.Lease.Fence);
    }

    [Fact]
    public async Task Schema_migration_is_recorded_and_a_newer_store_fails_closed()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        Assert.Equal(TaskServerStore.CurrentSchemaVersion, store.Status().SchemaVersion);

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM schema_migrations WHERE version = {TaskServerStore.CurrentSchemaVersion};";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = $"UPDATE meta SET value = '{TaskServerStore.CurrentSchemaVersion + 1}' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        var olderBinary = Store(temp.Path);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => olderBinary.InitializeAsync());
        Assert.Contains("newer than this service supports", error.Message);
        Assert.False(olderBinary.AuthorityReady);
    }

    [Fact]
    public async Task Repeated_startup_applies_each_schema_migration_once()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var second = Store(temp.Path);
        await second.InitializeAsync();

        await using var connection = new SqliteConnection(
            $"Data Source={first.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*) FROM schema_migrations WHERE version = {TaskServerStore.CurrentSchemaVersion};";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Task_row_mapping_accepts_the_legacy_nine_column_projection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'task-1' AS id, 'project-1' AS project_id, 'TS-1' AS task_key,
                   'Legacy projection' AS title, '6-completed' AS state, 3 AS version,
                   '2026-09-01T01:02:03.0000000Z' AS created_at,
                   '2026-09-02T01:02:03.0000000Z' AS updated_at,
                   'body' AS body;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var task = TaskServerStore.ReadTask(reader);

        Assert.Equal("TS-1", task.TaskKey);
        Assert.Equal("body", task.Body);
        Assert.Null(task.ArchiveState);
        Assert.Null(task.ArchivedAt);
    }

    [Fact]
    public async Task Task_row_mapping_retains_archive_metadata_from_the_extended_projection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'task-2' AS id, 'project-1' AS project_id, 'TS-2' AS task_key,
                   'Extended projection' AS title, '7-archive' AS state, 4 AS version,
                   '2026-09-01T01:02:03.0000000Z' AS created_at,
                   '2026-09-02T01:02:03.0000000Z' AS updated_at,
                   NULL AS body, 'archived' AS archive_state,
                   '2026-09-03T04:05:06.0000000Z' AS archived_at;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var task = TaskServerStore.ReadTask(reader);

        Assert.Equal("TS-2", task.TaskKey);
        Assert.Null(task.Body);
        Assert.Equal("archived", task.ArchiveState);
        Assert.Equal(
            DateTime.Parse("2026-09-03T04:05:06.0000000Z").ToUniversalTime(),
            task.ArchivedAt);
    }

    [Fact]
    public async Task Version_fourteen_store_upgrades_to_legacy_migration_ledger_and_artifact_pointers()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();

        await using (var connection = new SqliteConnection(
                         $"Data Source={first.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE legacy_migration_reports;
                DROP TABLE legacy_migration_orphans;
                DROP TABLE legacy_migration_entities;
                ALTER TABLE artifacts DROP COLUMN source_path;
                ALTER TABLE artifacts DROP COLUMN pointer_only;
                DELETE FROM schema_migrations WHERE version = 15;
                UPDATE meta SET value = '14' WHERE key = 'schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path);
        await upgraded.InitializeForBackupAsync();

        await using var upgradedConnection = new SqliteConnection(
            $"Data Source={upgraded.DatabasePath};Pooling=False");
        await upgradedConnection.OpenAsync();
        await using var query = upgradedConnection.CreateCommand();
        query.CommandText = """
            SELECT count(*) FROM sqlite_master
             WHERE type = 'table'
               AND name IN ('legacy_migration_entities',
                            'legacy_migration_orphans',
                            'legacy_migration_reports');
            """;
        Assert.Equal(3L, (long)(await query.ExecuteScalarAsync())!);
        query.CommandText = """
            SELECT count(*) FROM pragma_table_info('artifacts')
             WHERE name IN ('source_path', 'pointer_only');
            """;
        Assert.Equal(2L, (long)(await query.ExecuteScalarAsync())!);
        query.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        Assert.Equal("15", (string)(await query.ExecuteScalarAsync())!);
        query.CommandText = "SELECT count(*) FROM schema_migrations WHERE version = 15;";
        Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_ten_adds_durable_result_finalization_state_and_reaches_current_schema()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();

        await using (var connection = new SqliteConnection(
                         $"Data Source={first.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE result_finalizations;
                DELETE FROM schema_migrations WHERE version = 11;
                UPDATE meta SET value = '10' WHERE key = 'schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path);
        await upgraded.InitializeAsync();

        await using var upgradedConnection = new SqliteConnection(
            $"Data Source={upgraded.DatabasePath};Pooling=False");
        await upgradedConnection.OpenAsync();
        await using var table = upgradedConnection.CreateCommand();
        table.CommandText = """
            SELECT count(*) FROM sqlite_master
             WHERE type = 'table' AND name = 'result_finalizations';
            """;
        Assert.Equal(1L, (long)(await table.ExecuteScalarAsync())!);
        table.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        Assert.Equal(
            TaskServerStore.CurrentSchemaVersion.ToString(),
            (string)(await table.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_nine_seeds_default_flow_and_adds_task_version_fence()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var workspace = await first.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Migration"), "test", default);
        var project = await first.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Migration", "MIG"),
            "test",
            default);

        await using (var connection = new SqliteConnection(
                         $"Data Source={first.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM flow_definitions;
                ALTER TABLE orchestration_runs DROP COLUMN task_version;
                DELETE FROM schema_migrations WHERE version = 9;
                UPDATE meta SET value = '8' WHERE key = 'schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path);
        await upgraded.InitializeAsync();

        var definition = await upgraded.GetFlowDefinitionAsync(project.ProjectId, default);
        Assert.NotNull(definition);
        Assert.Equal(0, definition.Version);
        Assert.Equal(OrchestrationDefaults.CreateStages(), definition.Stages);
        await using var upgradedConnection = new SqliteConnection(
            $"Data Source={upgraded.DatabasePath};Pooling=False");
        await upgradedConnection.OpenAsync();
        await using var column = upgradedConnection.CreateCommand();
        column.CommandText = """
            SELECT count(*) FROM pragma_table_info('orchestration_runs')
             WHERE name = 'task_version';
            """;
        Assert.Equal(1L, (long)(await column.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Backup_initialization_does_not_quarantine_live_authority()
    {
        using var temp = new TempDirectory();
        var servingStore = Store(temp.Path);
        await servingStore.InitializeAsync();
        await SeedReadyTaskAsync(servingStore);
        await servingStore.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);
        var claim = await servingStore.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);

        var backupProcessStore = Store(temp.Path);
        await backupProcessStore.InitializeForBackupAsync();
        await backupProcessStore.CreateBackupAsync(
            new BackupRequest("timer"),
            "timer",
            default);

        var renewed = await servingStore.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest(
                "runner-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence),
            "runner-a",
            default);
        Assert.Equal("active", renewed.Lease!.Status);
    }

    [Fact]
    public async Task Restart_restores_fence_authority_and_quarantines_the_attempt()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var (_, _, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        Assert.Equal("claimed", claim.Status);
        Assert.NotNull(claim.Lease);
        var run = claim.Run!;
        var lease = claim.Lease!;

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        Assert.True(restarted.AuthorityReady);
        Assert.Equal(first.ServerId, restarted.ServerId);

        await restarted.RegisterRunnerAsync("runner-b", Runner("instance-b"), "test", default);
        var contender = await restarted.ClaimAsync(new ClaimRequest("runner-b", "instance-b"), "test", default);
        Assert.Equal("empty", contender.Status);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.IngestEventAsync(
            run.RunId,
            new EventIngestRequest("evt-stale", "test", "{}", "stale-1", lease.Fence),
            "runner-a",
            default));
        Assert.Equal("lease-not-active", stale.Code);

        var staleDecision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            run.RunId,
            ExecutionAttemptKind.Coding,
            ExitCode: -1,
            StdErr: "provider process terminated during Task Server restart"));
        var staleCompletion = await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-stale",
                lease.LeaseId,
                lease.Fence,
                staleDecision.Outcome.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:stale",
                Sequence: 1,
                OutcomeDecision: staleDecision),
            "runner-a",
            default));
        Assert.Equal("stale-fence", staleCompletion.Code);

        await restarted.ResolveUnknownAttemptAsync(
            run.RunId,
            new ResolveUnknownAttemptRequest("systemd unit is inactive and the cgroup is empty"),
            "operator",
            default);
        var replacement = await restarted.ClaimAsync(new ClaimRequest("runner-b", "instance-b"), "test", default);
        Assert.Equal("claimed", replacement.Status);
        Assert.True(replacement.Lease!.Fence > lease.Fence);
        Assert.Equal(task.TaskId, replacement.Task!.TaskId);
    }

    [Fact]
    public async Task Restart_registration_re_adopts_coding_attempt_and_accepts_completion()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "runner-a", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var registered = await restarted.RegisterRunnerAsync(
            "runner-a",
            Runner("replacement-instance") with
            {
                ActiveAttempts =
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Coding,
                        claim.Run!.RunId,
                        task.TaskKey,
                        claim.Lease!.LeaseId,
                        claim.Lease.Fence,
                        LeaseInstanceId: claim.Lease.InstanceId),
                ],
            },
            "runner-a",
            default);

        Assert.Equal("adopted", Assert.Single(registered.AttemptAdoptions!).Status);
        Assert.Equal(1, Assert.Single(
            await restarted.ListRunnerCapabilitySnapshotsAsync(default),
            snapshot => snapshot.RunnerId == "runner-a").Telemetry!.ActiveSlots);
        var completed = await restarted.CompleteRunAsync(
            claim.Run.RunId,
            new CompleteRunRequest(
                "runner-a",
                claim.Lease.InstanceId,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "blocked",
                IdempotencyKey: $"completion:{claim.Run.RunId}:after-restart",
                Sequence: 1),
            "runner-a",
            default);

        Assert.Equal("blocked", completed.Status);
        Assert.Equal("4-auto-review", (await restarted.GetTaskAsync(
            project.ProjectId, task.TaskKey, default))!.State);
    }

    [Fact]
    public async Task Backup_restore_preserves_resources_events_artifacts_audit_identity_and_fences()
    {
        await TempDirectory.RunAsync(async temp =>
        {
            var store = Store(temp.Path);
            await store.InitializeAsync();
            var (_, project, task) = await SeedReadyTaskAsync(store);
            await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
            var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
            var lease = claim.Lease!;
            var runId = claim.Run!.RunId;

            await store.IngestEventAsync(runId, new EventIngestRequest("evt-1", "runner.output", "{\"text\":\"hello\"}", "event-1", lease.Fence), "runner-a", default);
            var bytes = Encoding.UTF8.GetBytes("evidence");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await store.IngestArtifactAsync(runId, new ArtifactIngestRequest("art-1", "result.txt", "text/plain", Convert.ToBase64String(bytes), sha, "artifact-1", lease.Fence), "runner-a", default);
            var handoff = await store.AcknowledgeResultHandoffAsync(
                runId, Handoff(runId, lease, sequence: 3), "runner-a", default);
            await store.CompleteRunAsync(runId, new CompleteRunRequest(
                "runner-a", "instance-a", lease.LeaseId, lease.Fence, "success",
                ResultEnvelopeDigest: handoff.EnvelopeDigest,
                IdempotencyKey: $"completion:{runId}",
                Sequence: 4), "runner-a", default);

            var serverId = store.ServerId;
            var backup = await store.CreateBackupAsync(new BackupRequest("acceptance"), "operator", default);
            await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("must disappear after restore"), "test", default);
            await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
            var restored = await store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default);

            Assert.True(restored.Verified);
            Assert.True(restored.Restored);
            Assert.Equal(TaskServerMode.Maintenance, store.Mode);
            Assert.Equal(serverId, store.ServerId);
            Assert.Single(await store.ListTasksAsync(project.ProjectId, default));
            var restoredEvents = await store.ListEventsAsync(runId, 0, default);
            Assert.Equal(3, restoredEvents.Count);
            Assert.Contains(restoredEvents, item => item.Kind == LifecycleEventKinds.RunCompleted);
            Assert.Contains(restoredEvents, item => item.Kind == LifecycleEventKinds.PostProcessingCompleted);
            Assert.Single(await store.ListArtifactsAsync(runId, default));
            Assert.Contains(await store.ListAuditAsync(0, default), record => record.Action == "run.claimed");
            Assert.Equal(task.TaskId, (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.TaskId);
            var history = await store.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
            Assert.NotNull(history);
            Assert.Single(history.Runs);
            Assert.Equal(restoredEvents, history.Events);
            Assert.Single(history.Artifacts);
            Assert.Contains(history.Audit, record => record.Action == "run.completed");
            Assert.Equal(restoredEvents[^1].Cursor, history.LastCursor);

            var incremental = await store.GetTaskHistoryAsync(
                project.ProjectId,
                task.TaskKey,
                restoredEvents[0].Cursor,
                default);
            Assert.NotNull(incremental);
            Assert.All(incremental.Events, item => Assert.True(item.Cursor > restoredEvents[0].Cursor));
        });
    }

    [Fact]
    public async Task Backup_and_restore_release_database_file_handles_before_cleanup()
    {
        await TempDirectory.RunAsync(async temp =>
        {
            var store = Store(temp.Path);
            await store.InitializeAsync();
            await SeedReadyTaskAsync(store);

            var backup = await store.CreateBackupAsync(new BackupRequest("handle-check"), "operator", default);
            AssertCanOpenExclusively(backup.Path);

            await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore handle check"), "operator", default);
            var restored = await store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default);

            Assert.True(restored.Restored);
            AssertCanOpenExclusively(backup.Path);
            AssertCanOpenExclusively(store.DatabasePath);
        });
    }

    [Fact]
    public async Task Legacy_migration_rehearses_inventory_freeze_import_integrity_and_evidence_git_preservation()
    {
        await TempDirectory.RunAsync(async data =>
        {
            await TempDirectory.RunAsync(async legacy =>
            {
                var taskDirectory = Path.Combine(legacy.Path, "projects", "agent-studio", "2-ready", "AGT-1");
                Directory.CreateDirectory(Path.Combine(taskDirectory, "results"));
                Directory.CreateDirectory(Path.Combine(legacy.Path, ".git"));
                Directory.CreateDirectory(Path.Combine(legacy.Path, ".metadata"));
                Directory.CreateDirectory(Path.Combine(legacy.Path, "identities"));
                await File.WriteAllTextAsync(Path.Combine(legacy.Path, ".git", "HEAD"), "ref: refs/heads/main\n");
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "job.json"), """
                    {"id":"AGT-1","title":"Migrated task","state":"2-ready","projectName":"Agent Studio"}
                    """);
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "prompt.md"), "Migrated prompt");
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "timeline.jsonl"), "{\"kind\":\"created\",\"timestamp\":\"2026-07-17T10:00:00Z\"}\n");
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "results", "evidence.txt"), "proof");
                await File.WriteAllTextAsync(Path.Combine(legacy.Path, "identities", "idle-runner.json"), """
                    {
                      "id": "idle-runner",
                      "displayName": "Idle Runner",
                      "kind": "service",
                      "registeredAt": "2026-07-16T09:00:00Z",
                      "lastSeenAt": "2026-07-17T09:59:00Z"
                    }
                    """);
                await File.WriteAllTextAsync(Path.Combine(legacy.Path, ".metadata", "attempt-authority.json"), """
                    {
                      "schemaVersion": 4,
                      "authorityEpoch": 7,
                      "lastFenceByTask": { "AGT-1": 9 },
                      "runAttempts": [
                        {
                          "attemptId": "run_legacy_1",
                          "taskKey": "AGT-1",
                          "repositoryId": "repo_legacy",
                          "state": 2,
                          "lastFence": 8,
                          "authorityEpoch": 7,
                          "createdAt": "2026-07-17T10:01:00Z",
                          "terminalAt": "2026-07-17T10:03:00Z",
                          "resultSha": "1111111111111111111111111111111111111111",
                          "lease": {
                            "leaseId": "lease_coding_1",
                            "fence": 8,
                            "authorityEpoch": 7,
                            "executorId": "coding-runner",
                            "hostId": "windows-host",
                            "leaseInstanceId": "coding-instance",
                            "acquiredAt": "2026-07-17T10:01:00Z",
                            "expiresAt": "2026-07-17T10:02:00Z"
                          }
                        }
                      ],
                      "reviewAttempts": [
                        {
                          "attemptId": "review_legacy_1",
                          "taskKey": "AGT-1",
                          "repositoryId": "repo_legacy",
                          "sourceRunAttemptId": "run_legacy_1",
                          "state": 1,
                          "lastFence": 3,
                          "authorityEpoch": 7,
                          "createdAt": "2026-07-17T10:04:00Z",
                          "lease": {
                            "leaseId": "lease_review_1",
                            "fence": 3,
                            "authorityEpoch": 7,
                            "executorId": "review-runner",
                            "hostId": "windows-host",
                            "leaseInstanceId": "review-instance",
                            "acquiredAt": "2026-07-17T10:04:00Z",
                            "expiresAt": "2026-07-17T10:05:00Z"
                          },
                          "subject": {
                            "subjectId": "subject_legacy_1",
                            "repositoryId": "repo_legacy",
                            "expectedResultSha": "1111111111111111111111111111111111111111",
                            "sourceRunAttemptId": "run_legacy_1",
                            "reviewPolicyHash": "policy-legacy",
                            "createdAt": "2026-07-17T10:03:30Z",
                            "plan": { "commands": [], "requiredAspects": [] }
                          }
                        }
                      ]
                    }
                    """);
                await File.WriteAllTextAsync(Path.Combine(legacy.Path, ".metadata", "attempt-authority.archive-2026-07-16.json"), """
                    {
                      "schemaVersion": 1,
                      "archivedAt": "2026-07-16T12:00:00Z",
                      "runAttempts": [
                        {
                          "attemptId": "run_legacy_archived",
                          "taskKey": "AGT-1",
                          "repositoryId": "repo_legacy",
                          "state": 2,
                          "lastFence": 4,
                          "authorityEpoch": 6,
                          "createdAt": "2026-07-16T10:01:00Z",
                          "terminalAt": "2026-07-16T10:03:00Z",
                          "resultSha": "2222222222222222222222222222222222222222"
                        }
                      ],
                      "reviewAttempts": []
                    }
                    """);

                var store = Store(data.Path);
                await store.InitializeAsync();
                var migration = new LegacyMigrationService(store);
                var request = new LegacyMigrationRequest(
                    legacy.Path,
                    "Agent Studio for Software",
                    true,
                    RequireAttemptAuthority: true);
                var inventory = await migration.InventoryAsync(request, default);
                Assert.Equal(1, inventory.Projects);
                Assert.Equal(1, inventory.Tasks);
                Assert.Equal(1, inventory.Events);
                Assert.Equal(1, inventory.Artifacts);
                Assert.Equal(2, inventory.CodingAttempts);
                Assert.Equal(1, inventory.RunnerIdentities);
                Assert.Equal(1, inventory.ReviewAttempts);
                Assert.Equal(2, inventory.Leases);
                Assert.Equal(7, inventory.AuthorityEpoch);

                request = request with { ExpectedMigrationId = inventory.MigrationId };
                await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "single-writer cutover"), "operator", default);
                var result = await migration.ImportAsync(request, "operator", default);
                Assert.True(result.Imported);
                Assert.Equal(2, result.CodingAttempts);
                Assert.Equal(1, result.RunnerIdentities);
                Assert.Equal(1, result.ReviewAttempts);
                Assert.Equal(2, result.Leases);
                Assert.False(string.IsNullOrWhiteSpace(result.IntegritySha256));
                Assert.Contains("Restore backup", result.RollbackBoundary);
                Assert.True(Directory.Exists(Path.Combine(data.Path, "migration-evidence", result.MigrationId)));

                var project = Assert.Single(await store.ListProjectsAsync(null, default));
                var migrated = Assert.Single(await store.ListTasksAsync(project.ProjectId, default));
                Assert.Equal("AGT-1", migrated.TaskKey);
                Assert.Equal("Migrated prompt", migrated.Body);
                Assert.Single(await store.ListEventsAsync(string.Empty, 0, default));
                Assert.Single(await store.ListArtifactsAsync(string.Empty, default));

                var migrationBackup = Assert.Single(Directory.EnumerateFiles(
                    store.BackupDirectory,
                    "*-before-legacy-import-*.db",
                    SearchOption.TopDirectoryOnly));
                AssertCanOpenExclusively(migrationBackup);
                AssertCanOpenExclusively(store.DatabasePath);

                await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM runs WHERE id = 'run_legacy_1' AND status = 'completed';"));
                Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM runs WHERE id = 'run_legacy_archived' AND status = 'completed';"));
                Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM leases WHERE lease_id = 'lease_coding_1' AND fence = 8;"));
                Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM review_attempts WHERE id = 'review_legacy_1' AND status = 'process-unknown' AND fence = 3;"));
                Assert.Equal(1L, Scalar(connection, """
                    SELECT count(*) FROM review_attempts
                     WHERE id = 'review_legacy_1'
                       AND resource_namespace = 'review-review_legacy_1-f3'
                       AND port_base BETWEEN 24000 AND 55992;
                    """));
                Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM runners WHERE id = 'idle-runner' AND name = 'Idle Runner';"));
                Assert.Equal(9L, Scalar(connection, "SELECT last_fence FROM fence_counters WHERE task_id = (SELECT id FROM tasks WHERE task_key = 'AGT-1');"));
            });
        });
    }

    [Fact]
    public async Task Legacy_migration_imports_the_current_task_layout_and_uses_the_stable_task_key()
    {
        await TempDirectory.RunAsync(async data =>
        {
            await TempDirectory.RunAsync(async legacy =>
            {
                var taskDirectory = Path.Combine(legacy.Path, "projects", "PROJ-002", "tasks", "002", "AGT-2663");
                Directory.CreateDirectory(taskDirectory);
                Directory.CreateDirectory(Path.Combine(legacy.Path, ".metadata"));
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "task.json"), """
                    {
                      "id": "task-server-cutover",
                      "key": "AGT-2663",
                      "title": "Cut over the local task server",
                      "state": "3-progress",
                      "createdAt": "2026-08-16T18:01:28Z"
                    }
                    """);
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "job.json"), """
                    {"id":"WRONG-LEGACY-DUPLICATE","title":"Must not win","state":"2-ready"}
                    """);
                await File.WriteAllTextAsync(Path.Combine(legacy.Path, ".metadata", "attempt-authority.json"), """
                    {"schemaVersion":4,"authorityEpoch":11,"lastFenceByTask":{"AGT-2663":14},"runAttempts":[],"reviewAttempts":[]}
                    """);

                var store = Store(data.Path);
                await store.InitializeAsync();
                var migration = new LegacyMigrationService(store);
                var request = new LegacyMigrationRequest(
                    legacy.Path,
                    "Agent Studio",
                    true,
                    RequireAttemptAuthority: true);
                var inventory = await migration.InventoryAsync(request, default);
                Assert.Equal(1, inventory.Projects);
                Assert.Equal(1, inventory.Tasks);
                Assert.Equal(11, inventory.AuthorityEpoch);

                await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "current-layout rehearsal"), "operator", default);
                var result = await migration.ImportAsync(
                    request with { ExpectedMigrationId = inventory.MigrationId },
                    "operator",
                    default);

                Assert.True(result.Imported);
                var project = Assert.Single(await store.ListProjectsAsync(null, default));
                var task = Assert.Single(await store.ListTasksAsync(project.ProjectId, default));
                Assert.Equal("AGT-2663", task.TaskKey);
                Assert.Equal("Cut over the local task server", task.Title);

                await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                Assert.Equal(14L, Scalar(connection, "SELECT last_fence FROM fence_counters WHERE task_id = (SELECT id FROM tasks WHERE task_key = 'AGT-2663');"));
            });
        });
    }

    [Fact]
    public async Task Legacy_import_rejects_a_source_that_changed_after_inventory()
    {
        using var data = new TempDirectory();
        using var legacy = new TempDirectory();
        var taskDirectory = Path.Combine(legacy.Path, "projects", "agent-studio", "2-ready", "AGT-1");
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "job.json"), """
            {"id":"AGT-1","title":"Migrated task","state":"2-ready","projectName":"Agent Studio"}
            """);
        var prompt = Path.Combine(taskDirectory, "prompt.md");
        await File.WriteAllTextAsync(prompt, "Original prompt");

        var store = Store(data.Path);
        await store.InitializeAsync();
        var migration = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(legacy.Path, "Agent Studio for Software", true);
        var inventory = await migration.InventoryAsync(request, default);
        var invalidInventory = Assert.Throws<TaskServerConflictException>(
            () => LegacyMigrationService.ValidateInventoryHash(inventory with { Tasks = inventory.Tasks + 1 }));
        Assert.Equal("legacy-inventory-invalid", invalidInventory.Code);
        await File.WriteAllTextAsync(prompt, "Changed prompt with a different length");

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "single-writer cutover"), "operator", default);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => migration.ImportAsync(
            request with { ExpectedMigrationId = inventory.MigrationId },
            "operator",
            default));

        Assert.Equal("legacy-inventory-mismatch", conflict.Code);
        Assert.Empty(await store.ListProjectsAsync(null, default));
    }

    [Fact]
    public async Task Legacy_cutover_can_require_attempt_authority_to_prevent_fence_reset()
    {
        using var data = new TempDirectory();
        using var legacy = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(legacy.Path, "projects"));
        var store = Store(data.Path);
        await store.InitializeAsync();
        var migration = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(
            legacy.Path,
            "Workspace",
            true,
            RequireAttemptAuthority: true);

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(
            () => migration.InventoryAsync(request, default));

        Assert.Equal("legacy-attempt-authority-required", conflict.Code);
    }

    [Fact]
    public async Task Legacy_cutover_inventories_every_entity_is_idempotent_and_restores_the_same_inventory_hash()
    {
        using var source = new TempDirectory();
        using var data = new TempDirectory();
        var ready = Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "2-ready", "AGT-1");
        var archived = Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "7-archive", "AGT-2");
        Directory.CreateDirectory(Path.Combine(ready, "results"));
        Directory.CreateDirectory(Path.Combine(ready, "logs"));
        Directory.CreateDirectory(archived);
        Directory.CreateDirectory(Path.Combine(source.Path, ".metadata", "orchestrator-sessions", "global"));
        Directory.CreateDirectory(Path.Combine(source.Path, "projects", "PROJ-001", ".orchestrator", "context-chats"));
        Directory.CreateDirectory(Path.Combine(source.Path, "logs", "bus", "Agent Studio"));
        Directory.CreateDirectory(Path.Combine(source.Path, "docs", "cutover"));
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "projects.json"), $$"""
            {"projects":[{"id":"PROJ-001","displayName":"Agent Studio","shortCode":"AGT","nextTaskKeySeq":3,"storageLocation":"{{Path.Combine(source.Path, "projects", "PROJ-001", "tasks").Replace("\\", "\\\\")}}"}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(ready, "task.json"), """
            {
              "id":"epic-cutover","key":"AGT-1","title":"Cutover epic","state":"2-ready","kind":"epic",
              "tags":["integrationpending"],
              "commits":[{"sha":"1111111"},{"sha":"2222222"}],
              "integrationRecords":[{"id":"integration-1","classification":"integrated-verified"}],
              "delivery":{"resultRef":"refs/agent-studio/result/1","deliveryRef":"refs/heads/task/agt-1"}
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(archived, "task.json"), """
            {"id":"archived-task","key":"AGT-2","title":"Archived","state":"7-archive"}
            """);
        await File.WriteAllTextAsync(Path.Combine(ready, "prompt.md"), "Migrate every entity.");
        await File.WriteAllTextAsync(Path.Combine(ready, "logs", "timeline.jsonl"),
            "{\"kind\":\"created\",\"timestamp\":\"2026-09-01T10:00:00Z\"}\n");
        await File.WriteAllTextAsync(Path.Combine(archived, "timeline.jsonl"),
            "{\"kind\":\"archived\",\"timestamp\":\"2026-09-02T10:00:00Z\"}\n");
        await File.WriteAllTextAsync(Path.Combine(ready, "results", "report.txt"), "review evidence");
        await File.WriteAllTextAsync(
            Path.Combine(ready, "logs", "cli-output.log"),
            new string('x', 10 * 1024 * 1024 + 1024));
        await File.WriteAllTextAsync(Path.Combine(source.Path, "docs", "cutover", "workbench.json"),
            "{\"schemaVersion\":2,\"pageKind\":\"workbench\",\"id\":\"cutover\"}");
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "orchestrator-sessions", "global", "session.json"),
            "{\"contextKey\":\"global\",\"kind\":\"global\"}");
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "orchestrator-sessions", "global", "history.jsonl"),
            "{\"kind\":\"boot\"}\n");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "projects", "PROJ-001", "orchestrator-chat.jsonl"),
            "{\"ts\":\"2026-09-01T12:00:00Z\",\"role\":\"user\",\"text\":\"project\"}\n");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "projects", "PROJ-001", ".orchestrator", "context-chats", "task.jsonl"),
            "{\"ts\":\"2026-09-01T12:01:00Z\",\"role\":\"user\",\"text\":\"task\"}\n");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "logs", "bus", "Agent Studio", "2026-09-01.jsonl"),
            "{\"kind\":\"task-event\"}\n");
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "pending-integration-records.json"), """
            {"pendingIntegrations":[
              {"id":"integration-orphan","taskKey":"GONE-9","classification":"integrated-historical"},
              {"id":"integration-known","taskKey":"AGT-1","classification":"integrated-verified"}
            ]}
            """);
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "attempt-authority.json"), """
            {
              "authorityEpoch":4,"lastFenceByTask":{"AGT-1":5,"GONE-9":12},
              "runAttempts":[
                {"attemptId":"run-open","taskKey":"AGT-1","repositoryId":"repo","state":1,"lastFence":5,"createdAt":"2026-09-01T10:00:00Z","lease":{"leaseId":"lease-open","fence":5,"executorId":"runner-1","hostId":"host-1","acquiredAt":"2026-09-01T10:00:00Z","expiresAt":"2026-09-01T10:05:00Z"}},
                {"attemptId":"run-closed","taskKey":"AGT-2","repositoryId":"repo","state":2,"lastFence":2,"createdAt":"2026-08-01T10:00:00Z","terminalAt":"2026-08-01T10:05:00Z"},
                {"attemptId":"run-orphan","taskKey":"GONE-9","repositoryId":"repo","state":1,"lastFence":12,"createdAt":"2026-07-01T10:00:00Z","lease":{"leaseId":"lease-orphan","fence":12,"executorId":"runner-orphan","hostId":"host-old","acquiredAt":"2026-07-01T10:00:00Z","expiresAt":"2026-07-01T10:05:00Z"}}
              ],
              "reviewAttempts":[
                {"attemptId":"review-open","taskKey":"AGT-1","repositoryId":"repo","sourceRunAttemptId":"run-open","state":1,"lastFence":3,"createdAt":"2026-09-01T10:06:00Z","lease":{"leaseId":"review-lease","fence":3,"executorId":"reviewer-1","hostId":"host-1","acquiredAt":"2026-09-01T10:06:00Z","expiresAt":"2026-09-01T10:10:00Z"},"subject":{"subjectId":"subject-1","repositoryId":"repo","expectedResultSha":"1111111111111111111111111111111111111111","sourceRunAttemptId":"run-open","resultRef":"refs/agent-studio/result/1"}},
                {"attemptId":"review-orphan","taskKey":"GONE-9","repositoryId":"repo","sourceRunAttemptId":"run-orphan","state":2,"lastFence":2,"createdAt":"2026-07-01T10:06:00Z","terminalAt":"2026-07-01T10:10:00Z","subject":{"subjectId":"subject-orphan","repositoryId":"repo","expectedResultSha":"2222222222222222222222222222222222222222","sourceRunAttemptId":"run-orphan"}}
              ]
            }
            """);

        var store = Store(data.Path);
        await store.InitializeAsync();
        var service = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(source.Path, "Workspace", true, RequireAttemptAuthority: true);
        var inventory = await service.InventoryAsync(request, default);
        Assert.Equal(2, inventory.Tasks);
        Assert.Equal(1, inventory.Epics);
        Assert.Equal(1, inventory.Dossiers);
        Assert.Equal(1, inventory.OrchestratorSessions);
        Assert.Equal(2, inventory.ContextChats);
        Assert.Equal(2, inventory.ContextChatTurns);
        Assert.Equal(1, inventory.PendingIntegrationRecords);
        Assert.Equal(2, inventory.GitCommits);
        Assert.Equal(1, inventory.IntegrationRecords);
        Assert.Equal(1, inventory.ResultRefs);
        Assert.Equal(1, inventory.DeliveryRefs);
        Assert.Equal(1, inventory.BusLogFiles);
        Assert.Equal(3, inventory.CodingAttempts);
        Assert.Equal(2, inventory.ReviewAttempts);
        Assert.Equal(3, inventory.Leases);
        var orphaned = Assert.IsType<LegacyMigrationOrphanCounts>(inventory.OrphanedReferences);
        Assert.Equal(1, orphaned.CodingAttempts);
        Assert.Equal(1, orphaned.ReviewAttempts);
        Assert.Equal(1, orphaned.Leases);
        Assert.Equal(1, orphaned.FenceCounters);
        Assert.Equal(1, orphaned.IntegrationRecords);
        Assert.Equal(5, orphaned.Total);
        Assert.Contains(inventory.Warnings, warning => warning.Contains("removed tasks", StringComparison.Ordinal));
        var projectCounts = Assert.Single(inventory.ProjectCounts!);
        Assert.Equal(1, projectCounts.States["2-ready"]);
        Assert.Equal(1, projectCounts.States["7-archive"]);

        using var frozen = new TempDirectory();
        CopyDirectory(source.Path, frozen.Path);
        var frozenRequest = request with { LegacyRoot = frozen.Path };
        var frozenInventory = await service.InventoryAsync(frozenRequest, default);
        Assert.Equal(inventory.MigrationId, frozenInventory.MigrationId);
        Assert.Equal(inventory.InventorySha256, frozenInventory.InventorySha256);

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "cutover"), "operator", default);
        var imported = await service.ImportAsync(
            frozenRequest with { ExpectedMigrationId = inventory.MigrationId }, inventory, "operator", default);
        Assert.False(imported.Idempotent);
        Assert.Equal(inventory.InventorySha256, imported.AfterInventorySha256);
        Assert.NotEmpty(imported.ReportSignature!);
        Assert.True(File.Exists(Path.Combine(store.DataDirectory, imported.ReportPath!)));
        Assert.Single(await store.ListLegacyMigrationReportsAsync(default));
        var artifactReferences = await store.ListArtifactsAsync(string.Empty, default);
        Assert.Equal(2, artifactReferences.Count);
        Assert.All(artifactReferences, artifact =>
        {
            Assert.True(artifact.PointerOnly);
            Assert.StartsWith(frozen.Path, artifact.SourcePath!, StringComparison.Ordinal);
        });
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM runs WHERE id = 'run-open' AND status = 'process-unknown';"));
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM runs WHERE id = 'run-closed' AND status = 'completed';"));
            Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM artifacts WHERE pointer_only = 1 AND length(content) = 0;"));
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM legacy_migration_entities WHERE entity_kind = 'bus-log-reference';"));
            Assert.Equal(5L, Scalar(connection, "SELECT count(*) FROM legacy_migration_orphans;"));
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM legacy_migration_orphans WHERE entity_kind = 'coding-attempt' AND orphaned_task_key = 'GONE-9' AND status = 'process-unknown';"));
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM legacy_migration_orphans WHERE entity_kind = 'lease' AND entity_key = 'lease-orphan' AND status = 'process-unknown';"));
            Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM runs WHERE id = 'run-orphan';"));
        }
        var preImportBackupCount = Directory.EnumerateFiles(store.BackupDirectory, "*.db").Count();
        var repeated = await service.ImportAsync(
            frozenRequest with { ExpectedMigrationId = inventory.MigrationId }, inventory, "operator", default);
        Assert.True(repeated.Idempotent);
        Assert.Equal(preImportBackupCount, Directory.EnumerateFiles(store.BackupDirectory, "*.db").Count());

        var backup = await store.CreateBackupAsync(new BackupRequest("post-import"), "operator", default);
        Assert.Equal(inventory.InventorySha256, backup.InventorySha256);
        using var restoredData = new TempDirectory();
        var restoredStore = Store(restoredData.Path);
        await restoredStore.InitializeAsync();
        await restoredStore.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
        var copiedBackup = Path.Combine(restoredStore.BackupDirectory, Path.GetFileName(backup.Path));
        File.Copy(backup.Path, copiedBackup);
        var restored = await restoredStore.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default);
        Assert.True(restored.Restored);
        Assert.Equal(inventory.InventorySha256, restored.InventorySha256);
        var restoredReport = await restoredStore.GetLegacyMigrationReportAsync(inventory.MigrationId, default);
        Assert.NotNull(restoredReport);
        Assert.Equal(inventory.InventorySha256, restoredReport!.AfterInventorySha256);
        Assert.True(File.Exists(Path.Combine(restoredStore.DataDirectory, restoredReport.ReportPath)));
        await using var restoredConnection = new SqliteConnection($"Data Source={restoredStore.DatabasePath};Pooling=False");
        await restoredConnection.OpenAsync();
        Assert.Equal(5L, Scalar(restoredConnection, "SELECT count(*) FROM legacy_migration_orphans;"));
    }

    [Fact]
    public async Task Legacy_inventory_counts_a_dossier_once_when_a_registered_repository_is_nested_in_the_root()
    {
        using var source = new TempDirectory();
        using var data = new TempDirectory();
        var repository = Path.Combine(source.Path, "repos", "product");
        Directory.CreateDirectory(Path.Combine(repository, "docs"));
        Directory.CreateDirectory(Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "2-ready", "AGT-1"));
        Directory.CreateDirectory(Path.Combine(source.Path, ".metadata"));
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "projects.json"), $$"""
            {"projects":[{"id":"PROJ-001","displayName":"Agent Studio","shortCode":"AGT","nextTaskKeySeq":2,
              "storageLocation":"{{Path.Combine(source.Path, "projects", "PROJ-001", "tasks").Replace("\\", "\\\\")}}",
              "repositoryPath":"{{repository.Replace("\\", "\\\\")}}"}]}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "2-ready", "AGT-1", "task.json"),
            """{"key":"AGT-1","title":"Nested repository","state":"2-ready"}""");
        await File.WriteAllTextAsync(Path.Combine(repository, "docs", "workbench.json"),
            """{"schemaVersion":2,"pageKind":"workbench","id":"nested"}""");

        var store = Store(data.Path);
        await store.InitializeAsync();
        var service = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(source.Path, "Workspace", true);
        var inventory = await service.InventoryAsync(request, default);

        // The descriptor is reachable from both the legacy root and the registered repository
        // root. Counting it twice would exceed the rows the ledger can hold and abort the import.
        Assert.Equal(1, inventory.Dossiers);

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "cutover"), "operator", default);
        var imported = await service.ImportAsync(
            request with { ExpectedMigrationId = inventory.MigrationId }, inventory, "operator", default);
        Assert.True(imported.Imported);
    }

    [Fact]
    public async Task Legacy_import_disambiguates_duplicate_task_key_prefixes_and_reports_them()
    {
        using var source = new TempDirectory();
        using var data = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, ".metadata"));
        foreach (var (project, key) in new[] { ("P1", "A-1"), ("P2", "B-1") })
        {
            Directory.CreateDirectory(Path.Combine(source.Path, "projects", project, "tasks", "2-ready", key));
            await File.WriteAllTextAsync(
                Path.Combine(source.Path, "projects", project, "tasks", "2-ready", key, "task.json"),
                $$"""{"key":"{{key}}","title":"{{project}}","state":"2-ready"}""");
        }
        // Neither registered project declares a shortCode, so both fall back to the same literal.
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "projects.json"), $$"""
            {"projects":[
              {"id":"P1","displayName":"One","nextTaskKeySeq":2,"storageLocation":"{{Path.Combine(source.Path, "projects", "P1", "tasks").Replace("\\", "\\\\")}}"},
              {"id":"P2","displayName":"Two","nextTaskKeySeq":2,"storageLocation":"{{Path.Combine(source.Path, "projects", "P2", "tasks").Replace("\\", "\\\\")}}"}]}
            """);

        var store = Store(data.Path);
        await store.InitializeAsync();
        var service = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(source.Path, "Workspace", true);
        var inventory = await service.InventoryAsync(request, default);
        Assert.Contains(inventory.Warnings, warning => warning.Contains("shares task key prefix", StringComparison.Ordinal));

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "cutover"), "operator", default);
        var imported = await service.ImportAsync(
            request with { ExpectedMigrationId = inventory.MigrationId }, inventory, "operator", default);
        Assert.True(imported.Imported);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        Assert.Equal(2L, Scalar(connection, "SELECT count(DISTINCT task_key_prefix) FROM projects;"));
    }

    [Fact]
    public async Task Legacy_inventory_degrades_an_unreadable_integration_file_to_a_warning()
    {
        using var source = new TempDirectory();
        using var data = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "2-ready", "AGT-1"));
        Directory.CreateDirectory(Path.Combine(source.Path, ".metadata"));
        await File.WriteAllTextAsync(Path.Combine(source.Path, ".metadata", "projects.json"), $$"""
            {"projects":[{"id":"PROJ-001","displayName":"Agent Studio","shortCode":"AGT","nextTaskKeySeq":2,
              "storageLocation":"{{Path.Combine(source.Path, "projects", "PROJ-001", "tasks").Replace("\\", "\\\\")}}"}]}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(source.Path, "projects", "PROJ-001", "tasks", "2-ready", "AGT-1", "task.json"),
            """{"key":"AGT-1","title":"Live","state":"2-ready"}""");
        await File.WriteAllTextAsync(
            Path.Combine(source.Path, ".metadata", "pending-integration-records.json"),
            """{"pendingIntegrations":[{"id":"int-orphan","taskKey":"GONE-9","classification":"integrated-historical"}]}""");
        await File.WriteAllTextAsync(
            Path.Combine(source.Path, ".metadata", "integration-records.json"), "not json at all");

        var store = Store(data.Path);
        await store.InitializeAsync();
        var service = new LegacyMigrationService(store);
        var inventory = await service.InventoryAsync(
            new LegacyMigrationRequest(source.Path, "Workspace", true), default);

        // One corrupt file must not cost the operator the whole inventory run.
        Assert.Contains(inventory.Warnings, warning =>
            warning.Contains("Skipped unreadable integration records", StringComparison.Ordinal));
        Assert.Equal(1, inventory.OrphanedReferences!.IntegrationRecords);
    }

    [Fact]
    public async Task Failed_restore_rolls_back_to_the_live_store_and_remains_ready_in_maintenance()
    {
        await TempDirectory.RunAsync(async temp =>
        {
            var store = Store(temp.Path);
            await store.InitializeAsync();
            var (_, project, _) = await SeedReadyTaskAsync(store);
            var backup = await store.CreateBackupAsync(new BackupRequest("future-schema"), "operator", default);
            await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("must survive failed restore"), "test", default);

            await using (var connection = new SqliteConnection($"Data Source={backup.Path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE meta SET value = '999' WHERE key = 'schema_version';";
                await command.ExecuteNonQueryAsync();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await command.ExecuteNonQueryAsync();
            }

            var serverId = store.ServerId;
            await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default));

            Assert.Contains("newer than this service supports", error.Message);
            Assert.True(store.AuthorityReady);
            Assert.Equal(TaskServerMode.Maintenance, store.Mode);
            Assert.Equal(serverId, store.ServerId);
            Assert.Equal(2, (await store.ListTasksAsync(project.ProjectId, default)).Count);
            AssertCanOpenExclusively(backup.Path);
            AssertCanOpenExclusively(store.DatabasePath);
        });
    }

    [Fact]
    public async Task Idempotency_keys_replay_the_same_ingest_and_reject_cross_run_aliases()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, _) = await SeedReadyTaskAsync(store);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Second task", "Do more work", "2-ready"), "test", default);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var first = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var second = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var firstRun = first.Run!;
        var firstLease = first.Lease!;
        var secondRun = second.Run!;
        var secondLease = second.Lease!;

        var eventRequest = new EventIngestRequest("evt-1", "runner.output", "{\"text\":\"hello\"}", "shared-event-key", firstLease.Fence);
        var eventCreated = await store.IngestEventAsync(firstRun.RunId, eventRequest, "runner-a", default);
        var eventReplay = await store.IngestEventAsync(firstRun.RunId, eventRequest with { EventId = "evt-retry" }, "runner-a", default);
        Assert.Equal(eventCreated.EventId, eventReplay.EventId);
        var eventConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.IngestEventAsync(
            secondRun.RunId,
            eventRequest with { EventId = "evt-other", Fence = secondLease.Fence },
            "runner-a",
            default));
        Assert.Equal("idempotency-conflict", eventConflict.Code);

        var bytes = Encoding.UTF8.GetBytes("evidence");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifactRequest = new ArtifactIngestRequest(
            "art-1", "result.txt", "text/plain", Convert.ToBase64String(bytes), sha, "shared-artifact-key", firstLease.Fence);
        var artifactCreated = await store.IngestArtifactAsync(firstRun.RunId, artifactRequest, "runner-a", default);
        var artifactReplay = await store.IngestArtifactAsync(firstRun.RunId, artifactRequest with { ArtifactId = "art-retry" }, "runner-a", default);
        Assert.Equal(artifactCreated.ArtifactId, artifactReplay.ArtifactId);
        var artifactConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.IngestArtifactAsync(
            secondRun.RunId,
            artifactRequest with { ArtifactId = "art-other", Fence = secondLease.Fence },
            "runner-a",
            default));
        Assert.Equal("idempotency-conflict", artifactConflict.Code);

        var audit = await store.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "event.ingested");
        Assert.Single(audit, record => record.Action == "artifact.ingested");
    }

    [Fact]
    public async Task Typed_outcome_completion_is_fenced_idempotent_and_survives_restart_with_raw_facts()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;

        var wrongIdentity = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "run-other",
            ExecutionAttemptKind.Coding,
            StdErr: "HTTP 401 Missing bearer authentication",
            ExitCode: 1,
            DurableOutputState: DurableOutputState.Published,
            DurableOutputReference: "refs/heads/runner/test"));
        var identityConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence,
                ExecutionOutcomeKind.AuthenticationFailure.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:wrong-attempt",
                Sequence: 1,
                OutcomeDecision: wrongIdentity),
            "runner-a",
            default));
        Assert.Equal("attempt-identity-mismatch", identityConflict.Code);

        var decision = ExecutionOutcomeAdapter.Classify(wrongIdentity.RawFacts with { AttemptId = run.RunId });
        var outcomeConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence,
                ExecutionOutcomeKind.SuccessfulCompletion.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:outcome-mismatch",
                Sequence: 1,
                OutcomeDecision: decision),
            "runner-a",
            default));
        Assert.Equal("outcome-decision-mismatch", outcomeConflict.Code);

        var request = new CompleteRunRequest(
            "runner-a",
            "instance-a",
            lease.LeaseId,
            lease.Fence,
            decision.Outcome.ToString(),
            "provider capability is unavailable",
            IdempotencyKey: $"completion:{run.RunId}:typed-outcome",
            Sequence: 1,
            OutcomeDecision: decision);

        var completed = await store.CompleteRunAsync(run.RunId, request, "runner-a", default);
        var replay = await store.CompleteRunAsync(run.RunId, request, "runner-a", default);
        var conflictingReplay = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            run.RunId,
            request with
            {
                OutcomeDecision = ExecutionOutcomeAdapter.Classify(
                    decision.RawFacts with { StdErr = "HTTP 401 different replay facts" }),
            },
            "runner-a",
            default));

        Assert.Equal(ExecutionOutcomeKind.AuthenticationFailure.ToString(), completed.Status);
        Assert.Equal(completed, replay);
        Assert.Equal("completion-conflict", conflictingReplay.Code);
        var firstEvents = await store.ListEventsAsync(run.RunId, 0, default);
        var classified = Assert.Single(firstEvents, item => item.Kind == "execution.outcome.classified");
        using (var payload = JsonDocument.Parse(classified.PayloadJson))
        {
            Assert.Equal(ExecutionOutcomeAdapter.Version, payload.RootElement.GetProperty("classifierVersion").GetString());
            Assert.Equal("authenticationFailure", payload.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("waitForCapabilityRecovery", payload.RootElement.GetProperty("recoveryAction").GetString());
            Assert.Equal("high", payload.RootElement.GetProperty("confidence").GetString());
            Assert.Equal(
                "HTTP 401 Missing bearer authentication",
                payload.RootElement.GetProperty("rawFacts").GetProperty("stdErr").GetString());
        }

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var replayedEvents = await restarted.ListEventsAsync(run.RunId, 0, default);
        Assert.Equal(classified, Assert.Single(replayedEvents, item => item.Kind == "execution.outcome.classified"));
        var timeline = Assert.Single(await restarted.ListAttemptsAsync(project.ProjectId, task.TaskKey, default));
        Assert.Equal(run.RunId, timeline.Run.RunId);
        Assert.Equal(ExecutionOutcomeKind.AuthenticationFailure, timeline.OutcomeDecision!.Outcome);
        Assert.Equal(ExecutionRecoveryAction.WaitForCapabilityRecovery, timeline.OutcomeDecision.RecoveryAction);
        Assert.Equal("HTTP 401 Missing bearer authentication", timeline.OutcomeDecision.RawFacts.StdErr);
    }

    [Fact]
    public async Task Needs_input_completion_persists_question_and_salvage_branch_and_routes_to_human_review()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        const string question = "Which deployment strategy should I implement?\n\n"
            + "- Option A: managed connector. Recommended for simpler operations.\n"
            + "- Option B: direct LAN deployment. Requires customer network access.\n\n"
            + "Reply with A or B to continue.";
        const string salvageBranch = "runner/agent-runner-01/AGT-2736";
        var request = new CompleteRunRequest(
            "runner-a",
            "instance-a",
            lease.LeaseId,
            lease.Fence,
            ExecutionOutcomeKind.ExplicitAgentBlocker.ToString(),
            "choose-connector-vs-lan-deployment-strategy",
            IdempotencyKey: $"completion:{run.RunId}:needs-input",
            Sequence: 1,
            NeedsInputMessage: question,
            SalvageBranch: salvageBranch);

        await store.CompleteRunAsync(run.RunId, request, "runner-a", default);
        await store.CompleteRunAsync(run.RunId, request, "runner-a", default);

        Assert.Equal("5-human-review", (await store.GetTaskAsync(
            project.ProjectId, task.TaskKey, default))!.State);
        using var completion = JsonDocument.Parse(Assert.Single(
            await store.ListEventsAsync(run.RunId, 0, default),
            item => item.Kind == LifecycleEventKinds.RunCompleted).PayloadJson);
        Assert.Equal(
            "Which deployment strategy should I implement?",
            completion.RootElement.GetProperty("needsInputFirstLine").GetString());
        Assert.Equal(
            "results/needs-input.md",
            completion.RootElement.GetProperty("needsInputArtifact").GetString());
        Assert.Equal(salvageBranch, completion.RootElement.GetProperty("salvageBranch").GetString());

        var audit = Assert.Single(
            await store.ListAuditAsync(0, default),
            entry => entry.Action == "run.completed");
        using var auditPayload = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal(question, auditPayload.RootElement.GetProperty("NeedsInputMessage").GetString());
        Assert.Equal(salvageBranch, auditPayload.RootElement.GetProperty("SalvageBranch").GetString());
    }

    [Fact]
    public async Task Typed_event_payloads_fail_before_persistence_when_the_bound_is_exceeded()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = temp.Path,
                MaximumEventPayloadBytes = 128,
            }),
            TimeProvider.System);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.IngestEventAsync(
            claim.Run!.RunId,
            new EventIngestRequest(
                "evt-oversized",
                LifecycleEventKinds.ToolTrace,
                new string('x', 129),
                "oversized-event",
                claim.Lease!.Fence),
            "runner-a",
            default));

        Assert.Contains("128-byte limit", error.Message);
        Assert.Empty(await store.ListEventsAsync(claim.Run!.RunId, 0, default));
    }

    [Fact]
    public async Task Protocol_novelty_event_remains_visible_in_run_and_task_history()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        var novelty = new ProtocolNoveltyTelemetry(
            "codex",
            "0.7.0",
            "item.completed/future_widget",
            1,
            1,
            new string('a', 64));

        var ingested = await store.IngestEventAsync(
            claim.Run!.RunId,
            new EventIngestRequest(
                "evt-protocol-novelty",
                LifecycleEventKinds.ProtocolUnknownFrame,
                JsonSerializer.Serialize(new { stream = "system", text = novelty.ToMarker() }),
                "protocol-novelty-1",
                claim.Lease!.Fence),
            "runner-a",
            default);

        Assert.Equal(LifecycleEventKinds.ProtocolUnknownFrame, ingested.Kind);
        var runJournal = await store.ListEventsAsync(claim.Run.RunId, 0, default);
        Assert.Equal(ingested, Assert.Single(runJournal));
        var taskHistory = await store.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
        Assert.NotNull(taskHistory);
        Assert.Equal(ingested, Assert.Single(taskHistory!.Events));
    }

    [Fact]
    public async Task Drain_stops_new_admission_allows_completion_and_prepares_safe_shutdown()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Draining, "upgrade"), "operator", default);

        var admission = await Assert.ThrowsAsync<TaskServerConflictException>(
            () => store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default));
        Assert.Equal("admission-closed", admission.Code);

        var lease = claim.Lease!;
        var inventoryPulse = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                AvailableSlots: 0,
                Inventory: new RunnerProcessInventory(
                    DateTime.UtcNow,
                    [new RunnerProcessInfo(
                        claim.Run!.RunId,
                        claim.Task!.TaskKey,
                        4242,
                        "/worktrees/active",
                        DateTime.UtcNow)])),
            "runner-a",
            default);
        Assert.Equal("empty", inventoryPulse.Status);
        var renewed = await store.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence),
            "runner-a",
            default);
        Assert.Equal("renewed", renewed.Status);

        var handoff = await store.AcknowledgeResultHandoffAsync(
            claim.Run!.RunId,
            Handoff(claim.Run.RunId, lease, sequence: 1),
            "runner-a",
            default);
        await store.CompleteRunAsync(
            claim.Run.RunId,
            new CompleteRunRequest(
                "runner-a", "instance-a", lease.LeaseId, lease.Fence, "success",
                ResultEnvelopeDigest: handoff.EnvelopeDigest,
                IdempotencyKey: $"completion:{claim.Run.RunId}",
                Sequence: 2),
            "runner-a",
            default);
        var prepared = await store.PrepareShutdownAsync(new PrepareShutdownRequest("upgrade"), "operator", default);
        Assert.True(prepared.SafeToStop);
        Assert.Equal(TaskServerMode.Maintenance, prepared.Mode);
    }

    [Fact]
    public async Task Lost_handoff_ack_replays_one_envelope_and_one_lane_transition()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        var request = Handoff(run.RunId, lease, sequence: 4);

        var first = await store.AcknowledgeResultHandoffAsync(run.RunId, request, "runner-a", default);
        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var replay = await restarted.AcknowledgeResultHandoffAsync(
            run.RunId, request, "runner-a", default);

        Assert.False(first.Replay);
        Assert.True(replay.Replay);
        Assert.Equal(first.EnvelopeDigest, replay.EnvelopeDigest);
        Assert.Equal(first.AcknowledgedAt, replay.AcknowledgedAt);
        var reconstructable = await restarted.GetResultHandoffAsync(run.RunId, default);
        Assert.NotNull(reconstructable);
        Assert.Equal(request.Envelope.RepositoryId, reconstructable.Envelope.RepositoryId);
        Assert.Equal(request.Envelope.ResultSha, reconstructable.Envelope.ResultSha);
        Assert.Equal(request.Envelope.ImmutableRemoteRef, reconstructable.Envelope.ImmutableRemoteRef);

        var completion = new CompleteRunRequest(
            "runner-a", "instance-a", lease.LeaseId, lease.Fence, "success",
            ResultEnvelopeDigest: first.EnvelopeDigest,
            IdempotencyKey: $"completion:{run.RunId}",
            Sequence: 5);
        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);
        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);

        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "result-handoff.acknowledged");
        Assert.Single(audit, record => record.Action == "run.completed");
    }

    [Fact]
    public async Task Restart_completes_from_a_locally_durable_handoff_ack_without_replaying_handoff()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        var acknowledgement = await first.AcknowledgeResultHandoffAsync(
            run.RunId,
            Handoff(run.RunId, lease, sequence: 4),
            "runner-a",
            default);

        // The runner persisted the acknowledgement before both processes
        // restarted. Recovery must continue directly with the journaled
        // completion instead of requiring a duplicate handoff request merely
        // to reactivate the process-unknown lease.
        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var completion = new CompleteRunRequest(
            lease.RunnerId,
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            "success",
            ResultEnvelopeDigest: acknowledgement.EnvelopeDigest,
            IdempotencyKey: $"completion:{run.RunId}",
            Sequence: 5);

        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);
        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);

        var recovered = await restarted.GetTaskAsync(project.ProjectId, task.TaskKey, default);
        Assert.Equal("4-auto-review", recovered!.State);
        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "result-handoff.acknowledged");
        Assert.Single(audit, record => record.Action == "run.completed");
    }

    [Fact]
    public async Task Restart_accepts_unacknowledged_handoff_from_the_exact_process_unknown_authority()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.AcknowledgeResultHandoffAsync(
                claim.Run!.RunId,
                Handoff(claim.Run.RunId, claim.Lease! with { Fence = claim.Lease.Fence + 1 }, sequence: 8),
                "runner-a",
                default));
        Assert.Equal("stale-fence", stale.Code);
        var ack = await restarted.AcknowledgeResultHandoffAsync(
            claim.Run!.RunId,
            Handoff(claim.Run.RunId, claim.Lease!, sequence: 7),
            "runner-a",
            default);

        Assert.Equal("acknowledged", ack.State);
    }

    [Fact]
    public async Task Successful_completion_requires_the_matching_durable_envelope()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            claim.Run!.RunId,
            new CompleteRunRequest(
                "runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence, "success",
                ResultEnvelopeDigest: new string('a', 64),
                IdempotencyKey: $"completion:{claim.Run.RunId}",
                Sequence: 2),
            "runner-a",
            default));

        Assert.Equal("result-handoff-required", conflict.Code);
    }

    [Fact]
    public async Task Outbox_observability_reports_backlog_oldest_sequence_and_final_state()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        await store.ReportRunnerOutboxAsync(
            "runner-a",
            new RunnerOutboxStatusRequest(
                "instance-a", 12, 8, 4, 9, "transfer-recovery", "run-1", null, DateTime.UtcNow),
            "runner-a",
            default);

        var rows = await store.ListRunnerOutboxesAsync(default);
        var row = Assert.Single(rows);
        Assert.Equal(4, row.BacklogCount);
        Assert.Equal(9, row.OldestUnacknowledgedSequence);
        Assert.Equal("transfer-recovery", row.FinalHandoffState);
        Assert.Equal(4, store.Status().OutboxBacklog);
        Assert.Equal(9, store.Status().OldestUnacknowledgedSequence);
    }

    [Fact]
    public async Task Outbox_sequence_is_persisted_and_rejects_a_late_new_fact()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        await store.IngestEventAsync(
            run.RunId,
            new EventIngestRequest(
                "event-2",
                "runner.status",
                "{}",
                "event-key-2",
                lease.Fence,
                RunnerId: lease.RunnerId,
                InstanceId: lease.InstanceId,
                LeaseId: lease.LeaseId,
                Sequence: 2),
            "runner-a",
            default);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.IngestEventAsync(
                run.RunId,
                new EventIngestRequest(
                    "event-1",
                    "runner.status",
                    "{}",
                    "event-key-1",
                    lease.Fence,
                    RunnerId: lease.RunnerId,
                    InstanceId: lease.InstanceId,
                    LeaseId: lease.LeaseId,
                    Sequence: 1),
                "runner-a",
                default));

        Assert.Equal("stale-outbox-sequence", stale.Code);
        var stored = Assert.Single(await store.ListEventsAsync(run.RunId, 0, default));
        Assert.Equal(2, stored.Sequence);
    }

    [Fact]
    public async Task Completed_task_extends_result_retention_from_the_terminal_transition()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock, resultRetentionDays: 30);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        var request = Handoff(
            claim.Run!.RunId,
            claim.Lease!,
            sequence: 1);
        var acknowledgement = await store.AcknowledgeResultHandoffAsync(
            claim.Run.RunId,
            request,
            "runner-a",
            default);
        Assert.Equal(
            clock.GetUtcNow().AddDays(30).UtcDateTime,
            acknowledgement.RetainUntil);

        clock.Advance(TimeSpan.FromDays(10));
        var current = await store.GetTaskAsync(
            project.ProjectId,
            task.TaskKey,
            default);
        await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskKey,
            new UpdateTaskRequest(
                null,
                null,
                "6-completed",
                current!.Version),
            "test",
            default);

        var retained = await store.GetResultHandoffAsync(
            claim.Run.RunId,
            default);
        Assert.Equal(
            clock.GetUtcNow().AddDays(30).UtcDateTime,
            retained!.RetainUntil);
    }

    [Fact]
    public async Task Invariant_reconciliation_schedules_orphan_process_termination()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 40, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a", Runner("instance-a"), "test", default);

        var started = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(121));
        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                AvailableSlots: 0,
                Inventory: new RunnerProcessInventory(
                    clock.GetUtcNow().UtcDateTime,
                    [new RunnerProcessInfo(
                        "run-without-lease", "TS-404", 4242,
                        "/worktrees/TS-404", started)])),
            "runner-a",
            default);

        Assert.Equal(1, await store.ReconcileInvariantsAsync(default));
        var response = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                AvailableSlots: 0,
                Inventory: new RunnerProcessInventory(
                    clock.GetUtcNow().UtcDateTime,
                    [new RunnerProcessInfo(
                        "run-without-lease", "TS-404", 4242,
                        "/worktrees/TS-404", started)])),
            "runner-a",
            default);

        var action = Assert.Single(response.ReconciliationActions!);
        Assert.Equal("run-inventory", action.Category);
        Assert.Equal("terminate-process", action.Action);
        Assert.Equal(4242, action.Pid);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "invariant.orphan-process");
        var registry = await store.GetInvariantRegistryAsync(default);
        Assert.Equal(4, registry.Definitions.Count);
        Assert.Equal(1, registry.PendingRunnerActions);
        Assert.Contains(
            registry.RecentViolations,
            record => record.Action == "invariant.orphan-process");
    }

    [Fact]
    public async Task Invariant_reconciliation_records_lease_without_process_for_backend_authority()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 1, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                RequestedTtlSeconds: 180),
            "runner-a",
            default);

        clock.Advance(TimeSpan.FromSeconds(121));
        await store.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest(
                "runner-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                Inventory: new RunnerProcessInventory(
                    clock.GetUtcNow().UtcDateTime,
                    [])),
            "runner-a",
            default);

        Assert.Equal(1, await store.ReconcileInvariantsAsync(default));
        var retained = await store.GetTaskAsync(
            project.ProjectId, task.TaskKey, default);
        Assert.Equal("3-progress", retained!.State);
        var events = await store.ListEventsAsync(claim.Run.RunId, 0, default);
        var mismatch = Assert.Single(
            events,
            item => item.Kind == "invariant.lease-without-process");
        Assert.Contains("backend-authority", mismatch.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invariant_reconciliation_records_progress_without_run_without_requeueing()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 1, 20, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(
                workspace.WorkspaceId, "Project", "TS"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest(
                "Stranded", "No run owns this card", "3-progress"),
            "test",
            default);

        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(1, await store.ReconcileInvariantsAsync(default));

        var retained = await store.GetTaskAsync(
            project.ProjectId, task.TaskKey, default);
        Assert.Equal("3-progress", retained!.State);
        var audit = await store.ListAuditAsync(0, default);
        Assert.Contains(
            audit,
            record => record.Action == "invariant.lane-process-consistency");
    }

    [Fact]
    public async Task Lane_reconciliation_retains_process_unknown_authority()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 1, 40, 0, TimeSpan.Zero));
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync(
            "runner-a", Runner("instance-a"), "test", default);
        await first.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "runner-a",
            default);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        clock.Advance(TimeSpan.FromSeconds(121));

        Assert.Equal(1, await restarted.ReconcileInvariantsAsync(default));
        var contained = await restarted.GetTaskAsync(
            project.ProjectId, task.TaskKey, default);
        Assert.Equal("3-progress", contained!.State);
        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Contains(
            audit,
            record => record.Action == "invariant.lane-process-consistency"
                      && record.DetailJson.Contains(
                          "containment-required",
                          StringComparison.Ordinal));
        Assert.Equal(0, await restarted.ReconcileInvariantsAsync(default));
    }

    [Fact]
    public async Task Result_handoff_rejects_a_ref_from_another_fence_generation()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        var lease = claim.Lease!;
        var request = Handoff(claim.Run!.RunId, lease, sequence: 1);
        var staleEnvelope = request.Envelope with
        {
            ImmutableRemoteRef = FencedGitRefs.ImmutableResult(
                claim.Run.RunId,
                lease.Fence + 1,
                request.Envelope.ResultSha),
        };
        var staleRequest = request with
        {
            Envelope = staleEnvelope,
            EnvelopeDigest = ResultEnvelopeDigest.Compute(staleEnvelope),
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AcknowledgeResultHandoffAsync(
                claim.Run.RunId,
                staleRequest,
                "runner-a",
                default));

        Assert.Contains("Immutable result ref must be", error.Message);
        Assert.Contains($"fence-{lease.Fence}/", error.Message);
    }

    [Fact]
    public async Task Result_handoff_replays_an_already_acknowledged_legacy_ref_during_upgrade()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        var request = Handoff(claim.Run!.RunId, claim.Lease!, sequence: 1);
        await store.AcknowledgeResultHandoffAsync(
            claim.Run.RunId,
            request,
            "runner-a",
            default);

        var legacyEnvelope = request.Envelope with
        {
            ImmutableRemoteRef =
                $"refs/heads/agent-studio/results/{claim.Run.RunId}/" +
                request.Envelope.ResultSha,
        };
        var legacyDigest = ResultEnvelopeDigest.Compute(legacyEnvelope);
        var legacyKey = $"handoff:{claim.Run.RunId}:{legacyDigest}";
        await using (var connection = new SqliteConnection(
                         $"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE result_handoffs
                   SET immutable_remote_ref = $ref,
                       envelope_digest = $digest,
                       idempotency_key = $key
                 WHERE run_id = $run;
                """;
            command.Parameters.AddWithValue("$ref", legacyEnvelope.ImmutableRemoteRef);
            command.Parameters.AddWithValue("$digest", legacyDigest);
            command.Parameters.AddWithValue("$key", legacyKey);
            command.Parameters.AddWithValue("$run", claim.Run.RunId);
            await command.ExecuteNonQueryAsync();
        }

        var replay = await store.AcknowledgeResultHandoffAsync(
            claim.Run.RunId,
            request with
            {
                Envelope = legacyEnvelope,
                EnvelopeDigest = legacyDigest,
                IdempotencyKey = legacyKey,
            },
            "runner-a",
            default);

        Assert.True(replay.Replay);
        Assert.Equal(legacyDigest, replay.EnvelopeDigest);
    }

    private static ResultHandoffRequest Handoff(string runId, LeaseDto lease, long sequence)
    {
        var envelope = new ImmutableResultEnvelope(
            "repo-project",
            runId,
            new string('1', 40),
            new string('2', 40),
            FencedGitRefs.ImmutableResult(runId, lease.Fence, new string('2', 40)),
            null,
            new string('3', 64));
        var digest = ResultEnvelopeDigest.Compute(envelope);
        return new ResultHandoffRequest(
            lease.RunnerId,
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            sequence,
            $"handoff:{runId}:{digest}",
            digest,
            envelope);
    }

    private static TaskServerStore Store(
        string dataDirectory,
        TimeProvider? clock = null,
        int resultRetentionDays = 30)
        => new(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = dataDirectory,
                ResultRetentionDays = resultRetentionDays,
            }),
            clock ?? TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instance)
        => new(
            "runner",
            "host-a",
            instance,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]);

    private static void AssertCanOpenExclusively(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)> SeedReadyTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Task", "Do the work", "2-ready"), "test", default);
        return (workspace, project, task);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "task-server-tests", Guid.NewGuid().ToString("N"));
    public string Path { get; }

    public static async Task RunAsync(Func<TempDirectory, Task> action)
    {
        var temp = new TempDirectory();
        Exception? failure = null;
        try
        {
            await action(temp);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            temp.Dispose();
        }
        catch (Exception cleanupException)
        {
            failure = failure is null
                ? cleanupException
                : new AggregateException(
                    "The test body and temporary-directory cleanup both failed.",
                    failure,
                    cleanupException);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Failed to clean temporary test directory '{Path}'.", exception);
        }
    }
}
