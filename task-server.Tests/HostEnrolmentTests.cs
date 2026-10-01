using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>Store-level acceptance for the AGT-W63 I03 runner-host enrolment contract.</summary>
public sealed class HostEnrolmentTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Coding = [CapabilityProtocol.CodingExecutor];

    [Fact]
    public async Task Second_host_joins_with_its_own_principals_and_copied_first_host_principal_is_refused()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        await EnrolAsync(store, "host-a", "coding-a", "review-a", total: 2);
        await EnrolAsync(store, "host-b", "coding-b", "review-b", total: 2);

        var copied = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            EnrolAsync(store, "host-c", "coding-a", "review-c", total: 1));
        Assert.Equal("principal-enrolled-elsewhere", copied.Code);

        // The first host's coding secret copied onto host-b registers from the
        // wrong host and is refused before any lease is minted.
        await RegisterAndAdvertiseAsync(store, clock, "coding-a", "copy", "host-b", Coding);
        var refused = await ClaimAsync(store, "coding-a", "copy");
        Assert.Equal("empty", refused.Status);
        Assert.Equal(HostAdmissionReasons.PrincipalNotEnrolled, refused.PlacementReason);

        await RegisterAndAdvertiseAsync(store, clock, "coding-b", "b-1", "host-b", Coding);
        var claimed = await ClaimAsync(store, "coding-b", "b-1");
        Assert.Equal("claimed", claimed.Status);
    }

    [Fact]
    public async Task Restart_preserves_enrolment_identity_and_generation()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var enrolled = await EnrolAsync(first, "host-a", "coding-a", "review-a", total: 2);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var listed = Assert.Single(await restarted.ListHostEnrolmentsAsync(default));
        Assert.Equal(enrolled.Generation, listed.Generation);
        Assert.Equal(["coding-a", "review-a"], listed.Roles.Select(role => role.PrincipalId));
        Assert.Equal(new HostEnvelopeDto(2, 2, 1), listed.Envelope);
    }

    [Fact]
    public async Task Stale_capability_missing_login_and_bad_repository_proof_are_typed_refusals()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        await EnrolAsync(store, "host-a", "coding-a", "review-a", total: 2);
        await store.RegisterRunnerAsync(
            "coding-a",
            new RegisterRunnerRequest("coding-a", "host-a", "i-1", "1.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "coding-a",
            default);
        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest(
                "coding-a", "i-1", CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300, 1,
                [
                    new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
                    new AdvertisedCapabilityDto(CapabilityProtocol.RepositoryAccess, "repository", "failed",
                        Detail: "ls-remote denied"),
                ]),
            "coding-a",
            default);

        var login = await ClaimAsync(store, "coding-a", "i-1",
            CapabilityProtocol.CodingExecutor, CapabilityProtocol.ProviderAuthentication("codex"));
        Assert.Equal(HostAdmissionReasons.ProviderLoginMissing, login.PlacementReason);

        var repository = await ClaimAsync(store, "coding-a", "i-1",
            CapabilityProtocol.CodingExecutor, CapabilityProtocol.RepositoryAccess);
        Assert.Equal(HostAdmissionReasons.RepositoryProofFailed, repository.PlacementReason);

        clock.Advance(TimeSpan.FromSeconds(301));
        var stale = await ClaimAsync(store, "coding-a", "i-1", CapabilityProtocol.CodingExecutor);
        Assert.Equal(HostAdmissionReasons.CapabilityStale, stale.PlacementReason);
        Assert.All([login, repository, stale], response => Assert.Null(response.Lease));
    }

    [Fact]
    public async Task Concurrent_polls_from_several_hosts_claim_each_task_once_and_conserve_role_budgets()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 6);
        var hosts = new[] { "host-a", "host-b", "host-c", "host-d" };
        foreach (var host in hosts)
        {
            await EnrolAsync(store, host, $"coding-{host}", $"review-{host}", total: 2, coding: 1, review: 1);
            await RegisterAndAdvertiseAsync(store, clock, $"coding-{host}", $"i-{host}", host, Coding);
        }

        var polls = hosts
            .SelectMany(host => Enumerable.Range(0, 3).Select(_ => host))
            .Select(host => Task.Run(() => ClaimAsync(store, $"coding-{host}", $"i-{host}")))
            .ToArray();
        var responses = await Task.WhenAll(polls);

        var claimed = responses.Where(response => response.Status == "claimed").ToArray();
        Assert.Equal(claimed.Length, claimed.Select(response => response.Task!.TaskId).Distinct().Count());
        Assert.Equal(claimed.Length, claimed.Select(response => response.Lease!.LeaseId).Distinct().Count());
        Assert.NotEmpty(claimed);
        // Each host's coding role cap is one slot, so four hosts hold at most four leases.
        Assert.True(claimed.Length <= hosts.Length);
        Assert.All(
            claimed.GroupBy(response => response.Run!.RunnerId),
            group => Assert.Single(group));
        Assert.All(
            responses.Where(response => response.Status != "claimed" && response.PlacementReason is not null),
            response => Assert.Contains(
                response.PlacementReason,
                new[] { HostAdmissionReasons.RoleSlotBudgetFull, "no-admissible-task", "project-parallelism-full" }));
    }

    [Fact]
    public async Task Shared_envelope_counts_coding_work_against_the_review_role()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        await EnrolAsync(store, "host-a", "coding-a", "review-a", total: 1, coding: 1, review: 1);
        await RegisterAndAdvertiseAsync(store, clock, "coding-a", "c-1", "host-a", Coding);
        await RegisterAndAdvertiseAsync(store, clock, "review-a", "r-1", "host-a",
            CapabilityProtocol.ReviewExecutor);

        Assert.Equal("claimed", (await ClaimAsync(store, "coding-a", "c-1")).Status);
        var review = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "r-1", RequiredCapabilities: [CapabilityProtocol.ReviewExecutor]),
            "review-a",
            default);

        Assert.Equal("empty", review.Status);
        Assert.Equal(HostAdmissionReasons.SlotBudgetFull, review.AdmissionReason);
    }

    [Fact]
    public async Task Offline_host_work_is_reassigned_by_lease_expiry_and_its_late_replay_is_fenced()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        await EnrolAsync(store, "host-a", "coding-a", "review-a", total: 1, coding: 1, review: 1);
        await EnrolAsync(store, "host-b", "coding-b", "review-b", total: 1, coding: 1, review: 1);
        await RegisterAndAdvertiseAsync(store, clock, "coding-a", "a-1", "host-a", Coding);
        var original = await ClaimAsync(store, "coding-a", "a-1");
        Assert.Equal("claimed", original.Status);

        // host-a goes offline: no renewals. An expired lease is not free
        // capacity; it stays held and host-b cannot take the task.
        clock.Advance(TimeSpan.FromSeconds(200));
        await RegisterAndAdvertiseAsync(store, clock, "coding-b", "b-1", "host-b", Coding);
        var blocked = await ClaimAsync(store, "coding-b", "b-1");
        Assert.NotEqual("claimed", blocked.Status);

        // The fail-closed path: the authority restarts with the attempt in
        // process-unknown, and the administrator records containment proof.
        store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await store.ResolveUnknownAttemptAsync(
            original.Run!.RunId,
            new ResolveUnknownAttemptRequest("host-a service stopped; no process remains"),
            "admin",
            default);
        await RegisterAndAdvertiseAsync(store, clock, "coding-b", "b-2", "host-b", Coding);
        var takeover = await ClaimAsync(store, "coding-b", "b-2");
        Assert.Equal("claimed", takeover.Status);
        Assert.Equal(original.Task!.TaskId, takeover.Task!.TaskId);
        Assert.True(takeover.Lease!.Fence > original.Lease!.Fence);

        // host-a reconnects with a new instance and replays its obsolete completion.
        await RegisterAndAdvertiseAsync(store, clock, "coding-a", "a-2", "host-a", Coding);
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteRunAsync(
            original.Run!.RunId,
            new CompleteRunRequest("coding-a", "a-1", original.Lease.LeaseId, original.Lease.Fence,
                "success", "late replay", null, $"completion:{original.Run.RunId}", 1),
            "coding-a",
            default));
        var reconnected = await ClaimAsync(store, "coding-a", "a-2");
        Assert.NotEqual("claimed", reconnected.Status);
        Assert.Null(reconnected.Lease);
    }

    [Fact]
    public async Task Drain_removal_and_reenrolment_do_not_create_duplicate_ownership()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        var enrolled = await EnrolAsync(store, "host-a", "coding-a", "review-a", total: 2);
        await RegisterAndAdvertiseAsync(store, clock, "coding-a", "a-1", "host-a", Coding);

        await store.RequestOperatorHostDrainAsync("host-a", new OperatorHostDrainRequest("maintenance"), "admin", default);
        Assert.Equal(HostAdmissionReasons.HostDraining, (await ClaimAsync(store, "coding-a", "a-1")).PlacementReason);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RemoveHostAsync(
            "host-a", new RemoveHostRequest(enrolled.Generation - 1, "retire"), "admin", default));
        Assert.Equal("host-enrolment-generation-mismatch", stale.Code);
        var removed = await store.RemoveHostAsync(
            "host-a", new RemoveHostRequest(enrolled.Generation, "retire"), "admin", default);
        Assert.Equal(HostEnrolmentStatuses.Removed, removed.Status);
        Assert.Empty(removed.Roles);

        var replayedEnrolment = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            EnrolAsync(store, "host-a", "coding-a", "review-a", total: 2, expectedGeneration: enrolled.Generation));
        Assert.Equal("host-enrolment-generation-mismatch", replayedEnrolment.Code);

        var reenrolled = await EnrolAsync(
            store, "host-a", "coding-a2", "review-a2", total: 2, expectedGeneration: removed.Generation);
        Assert.Equal(HostEnrolmentStatuses.Enrolled, reenrolled.Status);
        Assert.Equal(removed.Generation + 1, reenrolled.Generation);
        Assert.Single(await store.ListHostEnrolmentsAsync(default));
        // The retired principal no longer owns the host and is refused there.
        var retired = await ClaimAsync(store, "coding-a", "a-1");
        Assert.Equal("empty", retired.Status);
    }

    [Fact]
    public async Task Class_placement_selects_eligible_enrolled_hosts_and_drain_moves_claims_to_the_peer()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await SeedTasksAsync(store, 3);
        foreach (var (host, platform) in new[] { ("host-a", "linux"), ("host-b", "linux"), ("host-w", "windows") })
        {
            await EnrolAsync(store, host, $"coding-{host}", $"review-{host}", total: 2);
            await RegisterAndAdvertiseAsync(store, clock, $"coding-{host}", $"i-{host}", host,
                CapabilityProtocol.CodingExecutor, $"platform:{platform}");
        }
        await store.SetMaxParallelismAsync(project.ProjectId, new SetMaxParallelismRequest(2), "operator", default);
        await store.UpdateProjectPlacementAsync(project.ProjectId,
            new UpdateProjectPlacementRequest(["platform:linux"], null, 2, 0), "operator", default);

        ClaimRequest Claim(string host) => new($"coding-{host}", $"i-{host}",
            RequiredCapabilities: Coding, EffectiveMaxParallelism: 2, RuntimeCapacityAppliedVersion: 1);
        var windows = await store.ClaimAsync(Claim("host-w"), "coding-host-w", default);
        Assert.NotEqual("claimed", windows.Status);
        var first = await store.ClaimAsync(Claim("host-a"), "coding-host-a", default);
        Assert.Equal("claimed", first.Status);
        await store.RequestOperatorHostDrainAsync("host-a", new OperatorHostDrainRequest("maintenance"), "operator", default);
        Assert.Equal(HostAdmissionReasons.HostDraining,
            (await store.ClaimAsync(Claim("host-a"), "coding-host-a", default)).PlacementReason);
        var peer = await store.ClaimAsync(Claim("host-b"), "coding-host-b", default);
        Assert.Equal("claimed", peer.Status);
        Assert.NotEqual(first.Task!.TaskId, peer.Task!.TaskId);
    }

    [Fact]
    public async Task Enrolment_does_not_change_project_parallelism()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await SeedTasksAsync(store, 1);
        var before = await store.GetProjectPlacementAsync(project.ProjectId, default);
        await EnrolAsync(store, "host-a", "coding-a", "review-a", total: 8, coding: 8, review: 4);
        var after = await store.GetProjectPlacementAsync(project.ProjectId, default);
        Assert.Equal(before, after);
    }

    private static Task<HostEnrolmentDto> EnrolAsync(
        TaskServerStore store,
        string host,
        string codingPrincipal,
        string reviewPrincipal,
        int total,
        int coding = -1,
        int review = 1,
        long expectedGeneration = 0)
        => store.EnrolHostAsync(
            host,
            new EnrolHostRequest(
                "linux",
                [
                    new HostRolePrincipalDto(HostRoles.Coding, codingPrincipal),
                    new HostRolePrincipalDto(HostRoles.Review, reviewPrincipal),
                ],
                new HostEnvelopeDto(total, coding < 0 ? total : coding, review),
                expectedGeneration),
            "admin",
            default);

    private static Task<ClaimResponse> ClaimAsync(
        TaskServerStore store, string runner, string instance, params string[] required)
        => store.ClaimAsync(
            new ClaimRequest(runner, instance, RequiredCapabilities: required.Length == 0 ? Coding : required),
            runner,
            default);

    private static TaskServerStore Store(string path, TimeProvider clock)
        => new(Options.Create(new TaskServerOptions { DataDirectory = path }), clock);

    private static async Task<ProjectDto> SeedTasksAsync(TaskServerStore store, int count)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "HST"), "test", default);
        for (var index = 0; index < count; index++)
            await store.CreateTaskAsync(
                project.ProjectId,
                new CreateTaskRequest($"Task {index + 1}", "Do the work", "2-ready"),
                "test",
                default);
        return project;
    }

    private static async Task RegisterAndAdvertiseAsync(
        TaskServerStore store,
        ManualTimeProvider clock,
        string runner,
        string instance,
        string host,
        params string[] capabilities)
    {
        var review = capabilities.Contains(CapabilityProtocol.ReviewExecutor);
        await store.RegisterRunnerAsync(
            runner,
            new RegisterRunnerRequest(
                runner, host, instance, "1.0", TaskServerProtocol.Current,
                review
                    ? [ReviewCapabilities.ReviewExecutor, ReviewCapabilities.GitMaterialization, ReviewCapabilities.SemanticReview]
                    : [ReviewCapabilities.CodingExecutor]),
            runner,
            default);
        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest(
                runner, instance, CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300,
                clock.GetUtcNow().ToUnixTimeSeconds(),
                capabilities.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray()),
            runner,
            default);
    }
}
