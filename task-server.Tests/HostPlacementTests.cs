using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class HostPlacementTests
{
    private static readonly DateTime Queued = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("coding", "linux", "x64", "container", "sha256:a", "codex", "project", true)]
    [InlineData("review", "linux", "x64", "container", "sha256:a", "codex", "project", false)]
    [InlineData("coding", "windows", "x64", "container", "sha256:a", "codex", "project", false)]
    [InlineData("coding", "linux", "arm64", "container", "sha256:a", "codex", "project", false)]
    [InlineData("coding", "linux", "x64", "process", "sha256:a", "codex", "project", false)]
    [InlineData("coding", "linux", "x64", "container", "sha256:b", "codex", "project", false)]
    [InlineData("coding", "linux", "x64", "container", "sha256:a", "claude", "project", false)]
    [InlineData("coding", "linux", "x64", "container", "sha256:a", "codex", "elsewhere", false)]
    public void Hard_requirements_are_exact(
        string role, string os, string architecture, string executor, string toolchain,
        string provider, string repository, bool eligible)
    {
        var host = Advert("host", "runner", role, os, architecture, executor,
            toolchain, provider, repository);
        var decision = HostPlacementPolicy.Decide(host, "coding", "project",
            ["os:linux", "arch:x64", "executor:container", "toolchain:sha256:a",
             "provider-auth:codex", "repository:project"], Queued, Queued,
            TimeSpan.FromSeconds(30), false, true, true);
        Assert.Equal(eligible, decision.Eligible);
        if (!eligible) Assert.NotEmpty(decision.Rejected);
    }

    [Fact]
    public void Warm_mirror_is_a_bounded_preference_and_queue_age_releases_cold_host()
    {
        var cold = Advert("cold", "runner", "coding", "linux", "x64", "container",
            "sha256:a", "codex", "project");
        var early = HostPlacementPolicy.Decide(cold, "coding", "project", [],
            Queued, Queued.AddSeconds(29), TimeSpan.FromSeconds(30), true, true, true);
        var aged = HostPlacementPolicy.Decide(cold, "coding", "project", [],
            Queued, Queued.AddSeconds(30), TimeSpan.FromSeconds(30), true, true, true);
        Assert.True(early.Eligible);
        Assert.True(early.WarmPreferenceActive);
        Assert.True(aged.Eligible);
        Assert.False(aged.WarmPreferenceActive);
    }

    [Fact]
    public async Task Shadow_receipt_preserves_existing_claim_and_records_difference()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, "shadow");
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store);
        await RegisterAsync(store);
        var claim = await store.ClaimAsync(Acknowledged(), "runner", default);
        Assert.Equal("claimed", claim.Status);
        var receipt = Assert.Single(await store.ListPlacementShadowReceiptsAsync(null, 10, default));
        Assert.Equal("claimed", receipt.ActualStatus);
        Assert.Equal("rejected", receipt.ShadowStatus);
        Assert.Contains("host-advertisement-missing", receipt.RejectedReasons);
        Assert.Equal(project.ProjectId, claim.Task!.ProjectId);
        Assert.Equal(claim.Lease!.LeaseId, receipt.LeaseId);
        Assert.Equal(claim.Lease.Fence, receipt.Fence);
        Assert.Equal(claim.Lease.Fence, receipt.AuthorityEpoch);
    }

    [Fact]
    public async Task Canary_requires_current_allocation_ack_and_preserves_running_lease()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, "canary");
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store, 2);
        await RegisterAsync(store);
        await store.UpsertHostPlacementAsync("host", new UpsertHostPlacementRequest(
            "runner", "instance", ["coding"], "linux", "x64", ["container"],
            ["sha256:a"], ["codex"], [project.ProjectId], [], 0), "runner", default);
        var first = await store.ClaimAsync(Acknowledged(), "runner", default);
        Assert.Equal("claimed", first.Status);
        var capacity = (await store.GetRuntimeCapacitySettingsAsync("host", default))!;
        await store.UpdateRuntimeCapacitySettingsAsync("host",
            new UpdateRuntimeCapacitySettingsRequest(3, 80, "balanced", capacity.Version),
            "operator", default);
        var stale = await store.ClaimAsync(Acknowledged(), "runner", default);
        Assert.Equal("empty", stale.Status);
        Assert.Contains("allocation-version-mismatch", stale.PlacementReason);
        var renewed = await store.RenewLeaseAsync(first.Run!.RunId,
            new LeaseRenewRequest("runner", "instance", first.Lease!.LeaseId,
                first.Lease.Fence), "runner", default);
        Assert.Equal("active", renewed.Lease!.Status);
    }

    [Fact]
    public async Task Hundred_concurrent_claims_create_one_authority_holder()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, "canary");
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store);
        await RegisterAsync(store);
        await AdvertiseAsync(store, "host", "runner", project.ProjectId);
        var claims = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            store.ClaimAsync(Acknowledged(), "runner", default)));
        Assert.Single(claims, claim => claim.Status == "claimed");
        Assert.Single((await store.ListPlacementShadowReceiptsAsync(null, 100, default))
            .Where(receipt => receipt.ActualStatus == "claimed"));
    }

    [Fact]
    public async Task Eligible_cold_host_claims_after_warm_window()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(Queued));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = temp.Path,
            DynamicPlacementMode = "canary",
            DynamicPlacementCanaryHostIds = ["cold"],
            DynamicPlacementWarmPreferenceSeconds = 30,
        }), clock);
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store);
        await RegisterAsync(store, "cold", "cold-runner");
        await RegisterAsync(store, "warm", "warm-runner");
        await AdvertiseAsync(store, "cold", "cold-runner", project.ProjectId);
        await AdvertiseAsync(store, "warm", "warm-runner", project.ProjectId, warm: true);
        await store.ClaimAsync(new ClaimRequest("warm-runner", "instance", AvailableSlots: 0,
            EffectiveMaxParallelism: 2, RuntimeCapacityAppliedVersion: 1), "warm-runner", default);

        var early = await store.ClaimAsync(new ClaimRequest("cold-runner", "instance",
            EffectiveMaxParallelism: 2, RuntimeCapacityAppliedVersion: 1), "cold-runner", default);
        Assert.Equal("warm-repository-preference", early.PlacementReason);
        clock.Advance(TimeSpan.FromSeconds(30));
        var aged = await store.ClaimAsync(new ClaimRequest("cold-runner", "instance",
            EffectiveMaxParallelism: 2, RuntimeCapacityAppliedVersion: 1), "cold-runner", default);
        Assert.Equal("claimed", aged.Status);
        Assert.Equal("cold", aged.Task is null ? null :
            (await store.ListPlacementShadowReceiptsAsync(aged.Task.TaskId, 10, default))
                .First(receipt => receipt.ActualStatus == "claimed").HostId);
    }

    [Fact]
    public async Task Registered_repository_requires_runner_probe_before_host_advertisement()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path, "shadow");
        await store.InitializeAsync();
        var project = await ReadyProjectAsync(store);
        await RegisterAsync(store);
        const string origin = "https://github.com/example/repository.git";
        await store.RegisterProjectRepositoryAsync(
            new RegisterProjectRepositoryRequest(project.ProjectId, project.WorkspaceId,
                project.Name, project.TaskKeyPrefix, origin, "develop"), "operator", default);
        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            AdvertiseAsync(store, "host", "runner", project.ProjectId));
        Assert.Equal("repository-proof-required", refused.Code);
        await store.RecordProjectRepositoryProbeAsync(project.ProjectId, "runner",
            new ProjectRepositoryProbeRequest(origin, origin, true, true, false), default);
        var admitted = await store.UpsertHostPlacementAsync("host",
            new UpsertHostPlacementRequest("runner", "instance", ["coding"], "linux",
                "x64", ["container"], [], [], [project.ProjectId], [], 0),
            "runner", default);
        Assert.Equal(1, admitted.Version);
        var persisted = (await store.GetHostPlacementAsync("host", default))!;
        Assert.Equal(admitted.Version, persisted.Version);
        Assert.Equal(admitted.RepositoryIds, persisted.RepositoryIds);
    }

    private static HostPlacementAdvertisement Advert(string host, string runner, string role,
        string os, string arch, string executor, string toolchain, string provider, string repo)
        => new(host, runner, "instance", [role], os, arch, [executor], [toolchain],
            [provider], [repo], [], 1, Queued);

    private static TaskServerStore Store(string path, string mode)
        => new(Options.Create(new TaskServerOptions
            { DataDirectory = path, DynamicPlacementMode = mode,
                DynamicPlacementCanaryHostIds = ["host"] }),
            new ManualTimeProvider(new DateTimeOffset(Queued)));

    private static async Task<ProjectDto> ReadyProjectAsync(TaskServerStore store, int count = 1)
    {
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PROJ"), "test", default);
        for (var i = 0; i < count; i++)
            await store.CreateTaskAsync(project.ProjectId,
                new CreateTaskRequest($"Task {i}", "work", "2-ready"), "test", default);
        return project;
    }

    private static Task RegisterAsync(TaskServerStore store, string host = "host", string runner = "runner")
        => store.RegisterRunnerAsync(runner,
            new RegisterRunnerRequest(runner, host, "instance", "1.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor],
                BootstrapMaxParallelism: 2), runner, default);

    private static Task AdvertiseAsync(TaskServerStore store, string host, string runner,
        string projectId, bool warm = false)
        => store.UpsertHostPlacementAsync(host, new UpsertHostPlacementRequest(
            runner, "instance", ["coding"], "linux", "x64", ["container"],
            ["sha256:a"], ["codex"], [projectId], warm ? [projectId] : [], 0),
            runner, default);

    private static ClaimRequest Acknowledged()
        => new("runner", "instance", EffectiveMaxParallelism: 2,
            RuntimeCapacityAppliedVersion: 1);
}
