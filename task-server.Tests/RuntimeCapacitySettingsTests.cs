using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RuntimeCapacitySettingsTests
{
    [Fact]
    public async Task First_registration_bootstraps_capacity_and_later_runners_receive_it_unconfirmed()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var first = await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 7),
            "test",
            default);
        var second = await store.RegisterRunnerAsync(
            "runner-b",
            Runner("runner-b:1", bootstrap: 2),
            "test",
            default);

        Assert.Equal(7, first.RuntimeCapacity!.MaxParallelism);
        Assert.Equal(7, second.RuntimeCapacity!.MaxParallelism);
        Assert.Equal("balanced", second.RuntimeCapacity.RampStrategy);
        Assert.Equal(80, second.RuntimeCapacity.TargetLoadPercent);
        var secondSnapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-b");
        Assert.Null(secondSnapshot.EffectiveMaxParallelism);
        Assert.Null(secondSnapshot.RuntimeCapacityAppliedAt);
        Assert.Null(secondSnapshot.RuntimeCapacityAppliedVersion);
    }

    [Fact]
    public async Task Review_registration_preserves_its_role_capacity_without_seeding_coding_capacity()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var review = await store.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review",
                "host-a",
                "review-a:1",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                BootstrapMaxParallelism: 9),
            "test",
            default);
        var coding = await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 3),
            "test",
            default);

        Assert.Null(review.RuntimeCapacity);
        Assert.Equal(3, coding.RuntimeCapacity!.MaxParallelism);
        var reviewSnapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "review-a");
        Assert.Null(reviewSnapshot.EffectiveMaxParallelism);
        Assert.Null(reviewSnapshot.RuntimeCapacityAppliedAt);
        Assert.Equal(9, reviewSnapshot.RoleMaxParallelism);
    }

    [Fact]
    public async Task Update_is_versioned_and_exposed_through_the_runtime_service()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "test",
            default);
        var service = new RuntimeCapacitySettingsService(store);
        var current = await service.GetAsync("host-a");

        var updated = await service.UpdateAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(
                6,
                85,
                "aggressive",
                current!.Version),
            "operator",
            default);

        Assert.Equal(6, updated.MaxParallelism);
        Assert.Equal(85, updated.TargetLoadPercent);
        Assert.Equal("aggressive", updated.RampStrategy);
        Assert.Equal(current.Version + 1, updated.Version);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => service.UpdateAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(8, 80, "balanced", current.Version),
            "operator",
            default));
    }

    [Fact]
    public async Task Upgraded_host_preserves_its_capacity_version_for_allocation_acknowledgment()
    {
        using var temp = new TempDirectory();
        var original = Store(temp.Path);
        await original.InitializeAsync();
        var service = new RuntimeCapacitySettingsService(original);
        var first = await service.UpdateAsync("host-a",
            new UpdateRuntimeCapacitySettingsRequest(4, 80, "balanced", 0), "operator");
        var second = await service.UpdateAsync("host-a",
            new UpdateRuntimeCapacitySettingsRequest(5, 80, "balanced", first.Version), "operator");
        var latest = await service.UpdateAsync("host-a",
            new UpdateRuntimeCapacitySettingsRequest(6, 80, "balanced", second.Version), "operator");

        await using (var connection = new SqliteConnection(
                         $"Data Source={original.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE runtime_capacity_settings DROP COLUMN allocation_version;
                CREATE TABLE epics(id TEXT PRIMARY KEY);
                CREATE TABLE epic_tasks(epic_id TEXT, task_id TEXT);
                ALTER TABLE task_studio_fields ADD COLUMN epic_id TEXT;
                UPDATE meta SET value = '36' WHERE key = 'schema_version';
                DELETE FROM schema_migrations WHERE version = 37;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path);
        await upgraded.InitializeAsync();
        var capacity = (await upgraded.GetRuntimeCapacitySettingsAsync("host-a", default))!;
        Assert.Equal(latest.Version, capacity.Version);
        Assert.Equal(latest.Version, capacity.AllocationVersion);
        await using (var connection = new SqliteConnection(
                         $"Data Source={upgraded.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('epics','epic_tasks'))
                     + (SELECT COUNT(*) FROM pragma_table_info('task_studio_fields') WHERE name='epic_id');
                """;
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Assert.Equal("37", (string)(await command.ExecuteScalarAsync())!);
        }

        await upgraded.RegisterRunnerAsync("runner-a", Runner("runner-a:1", bootstrap: 2),
            "test", default);
        await upgraded.ClaimAsync(new ClaimRequest("runner-a", "runner-a:1",
            AvailableSlots: 0, EffectiveMaxParallelism: 6,
            RuntimeCapacityAppliedVersion: latest.Version), "runner-a", default);
        var snapshot = Assert.Single(await upgraded.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(latest.Version, snapshot.RuntimeCapacityAppliedVersion);
        Assert.NotNull(snapshot.RuntimeCapacityAppliedAt);

        await upgraded.InitializeAsync();
        Assert.Equal(latest.Version,
            (await upgraded.GetRuntimeCapacitySettingsAsync("host-a", default))!.AllocationVersion);
    }

    [Fact]
    public async Task A_host_can_be_configured_before_its_first_runner_connects()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var service = new RuntimeCapacitySettingsService(store);

        var created = await service.UpdateAsync(
            "fresh-host",
            new UpdateRuntimeCapacitySettingsRequest(6, 85, "conservative", 0),
            "operator",
            default);
        var registered = await store.RegisterRunnerAsync(
            "fresh-runner",
            new RegisterRunnerRequest(
                "fresh-runner",
                "fresh-host",
                "fresh-host:1",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor],
                BootstrapMaxParallelism: 2),
            "fresh-runner",
            default);

        Assert.Equal(1, created.Version);
        Assert.Equal(6, registered.RuntimeCapacity!.MaxParallelism);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "runtime-capacity.created"
                      && record.TargetId == "fresh-host");
    }

    [Fact]
    public async Task Matching_runner_version_is_audited_once_as_configuration_adoption()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "test",
            default);

        for (var poll = 0; poll < 2; poll++)
        {
            await store.ClaimAsync(
                new ClaimRequest(
                    "runner-a",
                    "runner-a:1",
                    AvailableSlots: 0,
                    EffectiveMaxParallelism: 4,
                    RuntimeCapacityAppliedVersion: 1),
                "runner-a",
                default);
        }

        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(4, snapshot.EffectiveMaxParallelism);
        Assert.Equal(1, snapshot.RuntimeCapacityAppliedVersion);
        Assert.NotNull(snapshot.RuntimeCapacityAppliedAt);
        var adoption = Assert.Single(
            await store.ListAuditAsync(0, default),
            record => record.Action == "runtime-capacity.applied");
        Assert.Equal("runner-a", adoption.TargetId);
        Assert.Contains("\"Version\":1", adoption.DetailJson);
    }

    [Fact]
    public async Task Replacement_runner_must_confirm_the_policy_for_its_own_instance()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "test",
            default);
        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                AvailableSlots: 0,
                EffectiveMaxParallelism: 4,
                RuntimeCapacityAppliedVersion: 1),
            "runner-a",
            default);

        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:2", bootstrap: 2),
            "test",
            default);

        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Null(snapshot.EffectiveMaxParallelism);
        Assert.Null(snapshot.RuntimeCapacityAppliedAt);
        Assert.Null(snapshot.RuntimeCapacityAppliedVersion);
    }

    [Theory]
    [InlineData(4, 2, 1, false, false)]
    [InlineData(3, 3, 1, false, false)]
    [InlineData(4, 3, null, true, true)]
    [InlineData(4, 3, 3, true, false)]
    public void Adoption_policy_requires_the_exact_value_and_version(
        int reportedMax,
        long reportedVersion,
        int? previousVersion,
        bool confirms,
        bool audit)
    {
        var desired = new RuntimeCapacitySettingsDto(
            "host-a", 4, 80, "balanced", 3, DateTime.UtcNow);

        var decision = RuntimeCapacityAdoptionPolicy.Decide(
            desired,
            reportedMax,
            reportedVersion,
            previousVersion);

        Assert.Equal(confirms, decision.ConfirmsDesired);
        Assert.Equal(audit, decision.EmitAudit);
    }

    [Fact]
    public async Task Central_capacity_limits_claims_across_runners_and_projects_on_one_host()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        foreach (var (name, prefix) in new[] { ("One", "ONE"), ("Two", "TWO") })
        {
            var project = await store.CreateProjectAsync(
                new CreateProjectRequest(workspace.WorkspaceId, name, prefix),
                "test",
                default);
            await store.CreateTaskAsync(
                project.ProjectId,
                new CreateTaskRequest($"{name} task", "work", "2-ready"),
                "test",
                default);
        }
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 1),
            "test",
            default);
        await store.RegisterRunnerAsync(
            "runner-b",
            Runner("runner-b:1", bootstrap: 9),
            "test",
            default);

        var first = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                EffectiveMaxParallelism: 1,
                RuntimeCapacityAppliedVersion: 1),
            "runner-a",
            default);
        var blocked = await store.ClaimAsync(
            new ClaimRequest(
                "runner-b",
                "runner-b:1",
                EffectiveMaxParallelism: 1,
                RuntimeCapacityAppliedVersion: 1),
            "runner-b",
            default);

        Assert.Equal("claimed", first.Status);
        Assert.Equal("empty", blocked.Status);
        Assert.Contains("capacity is full (1/1)", blocked.Message);
        Assert.Equal(1, blocked.RuntimeCapacity!.MaxParallelism);

        var capacity = await store.GetRuntimeCapacitySettingsAsync("host-a", default);
        await store.UpdateRuntimeCapacitySettingsAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(
                2,
                capacity!.TargetLoadPercent,
                capacity.RampStrategy,
                capacity.Version),
            "operator",
            default);
        var second = await store.ClaimAsync(
            new ClaimRequest(
                "runner-b",
                "runner-b:1",
                EffectiveMaxParallelism: 2,
                RuntimeCapacityAppliedVersion: 2),
            "runner-b",
            default);

        Assert.Equal("claimed", second.Status);
        Assert.NotEqual(first.Task!.ProjectId, second.Task!.ProjectId);
        Assert.Equal(2, second.RuntimeCapacity!.MaxParallelism);
        var snapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-b");
        Assert.Equal(2, snapshot.EffectiveMaxParallelism);
        Assert.NotNull(snapshot.RuntimeCapacityAppliedAt);
        Assert.Equal(2, snapshot.RuntimeCapacityAppliedVersion);
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instance, int bootstrap)
        => new(
            "runner",
            "host-a",
            instance,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor],
            BootstrapMaxParallelism: bootstrap);
}
