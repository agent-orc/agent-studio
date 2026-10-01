using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProjectPlacementTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Matching_peer_claims_after_drain_without_task_rewrite()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store, 3);
        await HostAsync(store, clock, "runner-a", "host-a", "platform:linux");
        await HostAsync(store, clock, "runner-b", "host-b", "platform:linux");
        await store.SetMaxParallelismAsync(project.ProjectId,
            new SetMaxParallelismRequest(2), "operator", default);
        var placement = await store.UpdateProjectPlacementAsync(project.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], null, 2, 0),
            "operator", default);
        Assert.Equal(1, placement.Version);
        var stored = await store.GetProjectPlacementAsync(project.ProjectId, default);
        Assert.Equal(placement.Version, stored!.Version);
        Assert.Equal(placement.RequiredCapabilities, stored.RequiredCapabilities);

        var first = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.Equal("claimed", first.Status);
        Assert.Equal("matched", first.PlacementReason);
        var drain = await store.RequestOperatorHostDrainAsync("host-a",
            new OperatorHostDrainRequest("maintenance"), "operator", default);
        Assert.NotNull(drain.OperatorDrainAt);
        var blocked = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.Equal("empty", blocked.Status);
        var second = await store.ClaimAsync(AcknowledgedClaim("runner-b"), "runner-b", default);
        Assert.Equal("claimed", second.Status);
        Assert.NotEqual(first.Task!.TaskId, second.Task!.TaskId);
        Assert.Equal("active", first.Lease!.Status);
        Assert.Contains(await store.ListProjectPlacementAdmissionsAsync(project.ProjectId, default),
            admission => admission.RunnerId == "runner-b" && admission.Reason == "matched");
    }

    [Fact]
    public async Task Placement_refuses_unacknowledged_capacity_stale_capability_pin_and_project_parallelism()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store, 2);
        await HostAsync(store, clock, "runner-a", "host-a", "platform:linux");
        await HostAsync(store, clock, "runner-b", "host-b", "platform:windows");
        await store.UpdateProjectPlacementAsync(project.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], null, 1, 0),
            "operator", default);

        var unacknowledged = await store.ClaimAsync(
            new ClaimRequest("runner-a", "runner-a:1", RequiredCapabilities: [CapabilityProtocol.CodingExecutor]),
            "runner-a", default);
        Assert.Equal("capacity-version-unacknowledged", unacknowledged.PlacementReason);
        var incompatible = await store.ClaimAsync(AcknowledgedClaim("runner-b"), "runner-b", default);
        Assert.StartsWith("placement-capability-unavailable", incompatible.PlacementReason);
        Assert.Contains(await store.ListProjectPlacementAdmissionsAsync(project.ProjectId, default),
            admission => admission.RunnerId == "runner-b"
                         && admission.Reason.StartsWith("placement-capability-unavailable", StringComparison.Ordinal));
        var first = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.Equal("claimed", first.Status);
        var full = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.Equal("project-concurrency-full", full.PlacementReason);

        var secondProject = await ReadyProjectAsync(store, 1, "Second", "SEC");
        await store.UpdateProjectPlacementAsync(secondProject.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], "runner-a", 1, 0),
            "operator", default);
        var pinned = await store.ClaimAsync(AcknowledgedClaim("runner-b"), "runner-b", default);
        Assert.Equal("pinned-runner-mismatch", pinned.PlacementReason);
        clock.Advance(TimeSpan.FromSeconds(301));
        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest("runner-a", "runner-a:1",
                CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300, 2,
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")]),
            "runner-a", default);
        var stale = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.StartsWith("placement-capability-unavailable", stale.PlacementReason);
    }

    [Fact]
    public async Task Fresh_host_load_routes_claim_to_peer_with_capacity()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store, 1);
        await HostAsync(store, clock, "runner-a", "host-a", "platform:linux", cpuPercent: 96);
        await HostAsync(store, clock, "runner-b", "host-b", "platform:linux", cpuPercent: 12);
        await store.UpdateProjectPlacementAsync(project.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], null, 1, 0),
            "operator", default);

        var loaded = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.Equal("host-load-above-target", loaded.PlacementReason);
        var peer = await store.ClaimAsync(AcknowledgedClaim("runner-b"), "runner-b", default);
        Assert.Equal("claimed", peer.Status);
        Assert.Equal("matched", peer.PlacementReason);
    }

    [Fact]
    public async Task Removal_waits_for_lease_release_and_closes_old_registration()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store, 1);
        await HostAsync(store, clock, "runner-a", "host-a", "platform:linux");
        await store.UpdateProjectPlacementAsync(project.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], null, 1, 0),
            "operator", default);
        var claim = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        await store.RetireStudioHostAsync("host-a", "decommission", "operator", default);
        Assert.Equal("empty",
            (await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default)).Status);
        var active = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.PermanentlyDeleteStudioHostAsync("host-a", "operator", default));
        Assert.Equal("host-authority-active", active.Code);

        await store.ReleaseLeaseAsync(claim.Run!.RunId,
            new LeaseReleaseRequest("runner-a", "runner-a:1",
                claim.Lease!.LeaseId, claim.Lease.Fence, "stopped"),
            "runner-a", default);
        var removed = await store.PermanentlyDeleteStudioHostAsync("host-a", "operator", default);
        Assert.NotNull(removed.PermanentlyDeletedAt);
        var rejoin = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RegisterRunnerAsync("runner-a",
                new RegisterRunnerRequest("runner-a", "host-a", "runner-a:2", "1.0",
                    TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]),
                "runner-a", default));
        Assert.Equal("host-removed", rejoin.Code);
    }

    [Fact]
    public async Task Capacity_update_requires_the_new_applied_version_before_admission()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store, 1);
        await HostAsync(store, clock, "runner-a", "host-a", "platform:linux");
        await store.UpdateProjectPlacementAsync(project.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], null, 1, 0),
            "operator", default);
        var desired = await store.UpdateRuntimeCapacitySettingsAsync("host-a",
            new UpdateRuntimeCapacitySettingsRequest(1, 80, "balanced", 1),
            "operator", default);
        Assert.Equal(2, desired.Version);

        var oldVersion = await store.ClaimAsync(AcknowledgedClaim("runner-a"), "runner-a", default);
        Assert.Equal("capacity-version-unacknowledged", oldVersion.PlacementReason);
        var applied = await store.ClaimAsync(
            AcknowledgedClaim("runner-a") with
            {
                EffectiveMaxParallelism = 1,
                RuntimeCapacityAppliedVersion = desired.Version,
            }, "runner-a", default);
        Assert.Equal("claimed", applied.Status);
        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(desired.Version, snapshot.RuntimeCapacityAppliedVersion);
    }

    [Fact]
    public async Task Legacy_project_claim_reports_legacy_routing_after_an_earlier_refusal()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var pinnedElsewhere = await ReadyProjectAsync(store, 1, "Pinned", "PIN");
        clock.Advance(TimeSpan.FromSeconds(1));
        var legacy = await ReadyProjectAsync(store, 1, "Legacy", "LEG");
        await HostAsync(store, clock, "runner-a", "host-a", "platform:linux");
        await HostAsync(store, clock, "runner-b", "host-b", "platform:linux");
        await store.UpdateProjectPlacementAsync(pinnedElsewhere.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], "runner-a", 1, 0),
            "operator", default);

        var claim = await store.ClaimAsync(AcknowledgedClaim("runner-b"), "runner-b", default);

        Assert.Equal("claimed", claim.Status);
        Assert.Equal(legacy.ProjectId, claim.Task!.ProjectId);
        Assert.Equal(ProjectPlacementReasons.LegacyRouting, claim.PlacementReason);
        Assert.Contains(await store.ListProjectPlacementAdmissionsAsync(pinnedElsewhere.ProjectId, default),
            admission => admission.RunnerId == "runner-b" && admission.Reason == "pinned-runner-mismatch");
    }

    [Fact]
    public async Task Selection_evaluates_each_project_once_and_stops_at_the_first_admissible_task()
    {
        var yielded = 0;
        async IAsyncEnumerable<TaskDto> Ready()
        {
            foreach (var (task, project) in new[]
                     {
                         ("A-1", "A"), ("A-2", "A"), ("A-3", "A"), ("B-1", "B"), ("C-1", "C"),
                     })
            {
                yielded++;
                await Task.Yield();
                yield return new TaskDto(task, project, task, task, "2-ready", 1,
                    Start.UtcDateTime, Start.UtcDateTime);
            }
        }
        var evaluated = new List<string>();

        var selected = await ProjectPlacementSelection.SelectAsync(
            Ready(),
            project =>
            {
                evaluated.Add(project);
                return Task.FromResult(project == "B" ? "matched" : "refused");
            },
            verdict => verdict == "matched");

        Assert.Equal("B-1", selected.Task?.TaskId);
        Assert.Equal("matched", selected.Verdict);
        Assert.Equal(["A", "B"], evaluated);
        Assert.Equal(4, yielded);
    }

    private static ClaimRequest AcknowledgedClaim(string runner) =>
        new(runner, runner + ":1", RequiredCapabilities: [CapabilityProtocol.CodingExecutor],
            EffectiveMaxParallelism: 2, RuntimeCapacityAppliedVersion: 1);

    private static async Task<ProjectDto> ReadyProjectAsync(
        TaskServerStore store, int count, string name = "Project", string prefix = "PRJ")
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest(name), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, name, prefix), "test", default);
        for (var i = 0; i < count; i++)
            await store.CreateTaskAsync(project.ProjectId,
                new CreateTaskRequest($"Task {i}", "work", "2-ready"), "test", default);
        return project;
    }

    private static async Task HostAsync(
        TaskServerStore store, ManualTimeProvider clock, string runner, string host, string platform,
        double? cpuPercent = null)
    {
        await store.RegisterRunnerAsync(runner,
            new RegisterRunnerRequest(runner, host, runner + ":1", "1.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]),
            runner, default);
        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest(runner, runner + ":1",
                CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300, 1,
                [
                    new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
                    new AdvertisedCapabilityDto(platform, "platform"),
                ],
                cpuPercent is null ? null : new HostTelemetrySnapshotDto(
                    clock.GetUtcNow().UtcDateTime, cpuPercent, 0, 0, 0,
                    1, 2, 0, 0, 0, 0, 4, 0)),
            runner, default);
    }

    private static TaskServerStore Store(string path, TimeProvider clock) =>
        new(Options.Create(new TaskServerOptions { DataDirectory = path }), clock);
}
