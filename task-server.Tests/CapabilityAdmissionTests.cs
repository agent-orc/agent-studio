using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class CapabilityAdmissionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Repeated_codex_auth_failure_drains_only_codex_while_claude_and_review_continue()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var project = await SeedTasksAsync(store, 3);
        await RegisterAndAdvertiseAsync(store, clock, "codex", "coding-codex", "host-a",
            CapabilityProtocol.CodingExecutor, CapabilityProtocol.ProviderAuthentication("codex"));
        await RegisterAndAdvertiseAsync(store, clock, "claude", "coding-claude", "host-a",
            CapabilityProtocol.CodingExecutor, CapabilityProtocol.ProviderAuthentication("claude"));
        await RegisterAndAdvertiseAsync(
            store,
            clock,
            "review",
            "review-instance",
            "host-a",
            CapabilityProtocol.ReviewExecutor,
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            ReviewCapabilities.GitMaterialization,
            ReviewCapabilities.SemanticReview);

        var source = await store.ClaimAsync(
            new ClaimRequest("claude", "coding-claude", RequiredCapabilities:
            [
                CapabilityProtocol.CodingExecutor,
                CapabilityProtocol.ProviderAuthentication("claude"),
            ]),
            "claude",
            default);
        var resultSha = new string('2', 40);
        var resultRef = FencedGitRefs.ImmutableResult(
            source.Run!.RunId,
            source.Lease!.Fence,
            resultSha);
        var envelope = new ImmutableResultEnvelope(
            "repo-project",
            source.Run.RunId,
            new string('1', 40),
            resultSha,
            resultRef,
            null,
            new string('3', 64),
            RepositoryUrl: "https://example.invalid/project.git");
        var envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
        await store.AcknowledgeResultHandoffAsync(
            source.Run.RunId,
            new ResultHandoffRequest(
                "claude",
                "coding-claude",
                source.Lease!.LeaseId,
                source.Lease.Fence,
                1,
                $"handoff:{source.Run.RunId}",
                envelopeDigest,
                envelope),
            "claude",
            default);
        await store.CompleteRunAsync(
            source.Run.RunId,
            new CompleteRunRequest(
                "claude",
                "coding-claude",
                source.Lease.LeaseId,
                source.Lease.Fence,
                "success",
                "done",
                envelopeDigest,
                $"completion:{source.Run.RunId}",
                2),
            "claude",
            default);
        await store.CreateReviewSubjectAsync(
            new CreateReviewSubjectRequest(
                source.Task!.TaskId,
                source.Run.RunId,
                "repo-project",
                "https://example.invalid/project.git",
                resultSha,
                resultRef,
                null,
                null,
                "host-a",
                "review-policy-v1",
                new ReviewPlanDto(
                    [new ReviewCommandDto("requirements", "requirements", "review-tool", ["requirements"])],
                    ["requirements"]),
                $"review-subject:{source.Run.RunId}"),
            "orchestrator",
            default);

        await FailAsync(store, clock, "codex", "coding-codex",
            CapabilityProtocol.ProviderAuthentication("codex"), "codex-401-1");
        var drained = await FailAsync(store, clock, "codex", "coding-codex",
            CapabilityProtocol.ProviderAuthentication("codex"), "codex-401-2");

        Assert.Equal(CapabilityHealthStates.Draining, drained.HealthState);
        Assert.False(drained.WholeHostDraining);
        var codexClaim = await store.ClaimAsync(
            new ClaimRequest("codex", "coding-codex", RequiredCapabilities:
            [
                CapabilityProtocol.CodingExecutor,
                CapabilityProtocol.ProviderAuthentication("codex"),
            ]),
            "codex",
            default);
        Assert.Equal("empty", codexClaim.Status);
        Assert.Contains("provider-auth:codex", codexClaim.Message);

        var claudeClaim = await store.ClaimAsync(
            new ClaimRequest("claude", "coding-claude", RequiredCapabilities:
            [
                CapabilityProtocol.CodingExecutor,
                CapabilityProtocol.ProviderAuthentication("claude"),
            ]),
            "claude",
            default);
        Assert.Equal("claimed", claudeClaim.Status);
        Assert.NotNull(await store.GetTaskAsync(project.ProjectId, claudeClaim.Task!.TaskKey, default));
        var reviewClaim = await store.ClaimReviewAsync(
            new ReviewClaimRequest(
                "review",
                "review-instance",
                RequiredCapabilities:
                [
                    CapabilityProtocol.ReviewExecutor,
                    ReviewCapabilities.SemanticReview,
                ]),
            "review",
            default);
        Assert.Equal("claimed", reviewClaim.Status);
        Assert.Equal(resultSha, reviewClaim.Subject!.ExpectedResultSha);
    }

    [Fact]
    public async Task Missing_workflow_push_scope_is_visible_but_does_not_block_coding_claims()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        await store.RegisterRunnerAsync(
            "coding",
            new RegisterRunnerRequest(
                "coding",
                "host-a",
                "coding-instance",
                "1.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "coding",
            default);
        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest(
                "coding",
                "coding-instance",
                CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime,
                300,
                1,
                [
                    new AdvertisedCapabilityDto(
                        CapabilityProtocol.CodingExecutor,
                        "executor"),
                    new AdvertisedCapabilityDto(
                        CapabilityProtocol.GitPush,
                        "source"),
                    new AdvertisedCapabilityDto(
                        CapabilityProtocol.GitWorkflowPush,
                        "source",
                        "ready-no-workflow-scope",
                        Detail: "workflow scope missing"),
                ]),
            "coding",
            default);

        var claim = await store.ClaimAsync(
            new ClaimRequest(
                "coding",
                "coding-instance",
                RequiredCapabilities:
                [
                    CapabilityProtocol.CodingExecutor,
                    CapabilityProtocol.GitPush,
                ]),
            "coding",
            default);

        Assert.Equal("claimed", claim.Status);
        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        var workflow = Assert.Single(
            snapshot.Capabilities,
            capability => capability.Key == CapabilityProtocol.GitWorkflowPush);
        Assert.Equal("ready-no-workflow-scope", workflow.AdvertisedStatus);
        Assert.Equal("workflow scope missing", workflow.Detail);
    }

    [Fact]
    public async Task Provider_auth_probe_transitions_are_retained_across_advertisements_and_restart()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        const string runner = "claude";
        const string instance = "claude-instance";
        var capability = CapabilityProtocol.ProviderAuthentication("claude");
        await RegisterAndAdvertiseAsync(
            store,
            clock,
            runner,
            instance,
            "host-a",
            CapabilityProtocol.CodingExecutor,
            capability);

        clock.Advance(TimeSpan.FromMinutes(1));
        await store.AdvertiseCapabilitiesAsync(
            Advertisement(
                clock,
                runner,
                instance,
                2,
                CapabilityProtocol.CodingExecutor,
                capability) with
            {
                Capabilities =
                [
                    new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
                    new AdvertisedCapabilityDto(
                        capability,
                        "provider-auth",
                        "unavailable",
                        Identity: "claude",
                        Detail: "Not logged in"),
                ],
            },
            runner,
            default);

        var unavailable = Assert.Single(
            Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Capabilities,
            item => item.Key == capability);
        var transition = Assert.Single(unavailable.RecoveryHistory);
        Assert.Equal("ready", transition.FromState);
        Assert.Equal("unavailable", transition.ToState);
        Assert.Contains("probe changed", transition.Reason, StringComparison.Ordinal);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var afterRestart = Assert.Single(
            Assert.Single(await restarted.ListRunnerCapabilitySnapshotsAsync(default)).Capabilities,
            item => item.Key == capability);
        Assert.Single(afterRestart.RecoveryHistory);

        clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.AdvertiseCapabilitiesAsync(
            Advertisement(
                clock,
                runner,
                instance,
                3,
                CapabilityProtocol.CodingExecutor,
                capability),
            runner,
            default);
        var recovered = Assert.Single(
            Assert.Single(await restarted.ListRunnerCapabilitySnapshotsAsync(default)).Capabilities,
            item => item.Key == capability);
        Assert.Collection(
            recovered.RecoveryHistory,
            item => Assert.Equal("unavailable", item.ToState),
            item =>
            {
                Assert.Equal("unavailable", item.FromState);
                Assert.Equal("ready", item.ToState);
            });
    }

    [Theory]
    [InlineData(CapabilityProtocol.Disk)]
    [InlineData(CapabilityProtocol.LeaseAuthority)]
    public async Task Shared_foundation_failure_drains_the_host_but_not_another_host(string capability)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        await RegisterAndAdvertiseAsync(store, clock, "host-a-runner", "instance-a", "host-a",
            CapabilityProtocol.CodingExecutor, capability);
        await RegisterAndAdvertiseAsync(store, clock, "host-b-runner", "instance-b", "host-b",
            CapabilityProtocol.CodingExecutor, capability);

        var failure = await FailAsync(
            store, clock, "host-a-runner", "instance-a", capability, "foundation-1");

        Assert.True(failure.WholeHostDraining);
        var blocked = await store.ClaimAsync(
            new ClaimRequest("host-a-runner", "instance-a", RequiredCapabilities: [CapabilityProtocol.CodingExecutor]),
            "host-a-runner",
            default);
        Assert.Equal("empty", blocked.Status);
        Assert.Contains("automatic whole-host drain", blocked.Message);
        var healthy = await store.ClaimAsync(
            new ClaimRequest("host-b-runner", "instance-b", RequiredCapabilities: [CapabilityProtocol.CodingExecutor]),
            "host-b-runner",
            default);
        Assert.Equal("claimed", healthy.Status);
        var operatorDrain = await store.RequestOperatorHostDrainAsync(
            "host-b",
            new OperatorHostDrainRequest("planned maintenance"),
            "operator",
            default);
        Assert.Equal("operator-draining", operatorDrain.AdmissionState);
        Assert.Equal("planned maintenance", operatorDrain.OperatorDrainReason);
        Assert.Null(operatorDrain.AutomaticDrainReason);
        var blockedByOperatorDrain = await store.ClaimAsync(
            new ClaimRequest("host-b-runner", "instance-b", RequiredCapabilities: [CapabilityProtocol.CodingExecutor]),
            "host-b-runner",
            default);
        Assert.Equal("empty", blockedByOperatorDrain.Status);
        Assert.Contains("operator-requested whole-host drain", blockedByOperatorDrain.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Half_open_reserves_exactly_one_canary_and_failed_canary_returns_to_longer_cooldown()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 3);
        const string runner = "codex";
        const string instance = "codex-instance";
        var capability = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, runner, instance, "host-a",
            CapabilityProtocol.CodingExecutor, capability);
        await FailAsync(store, clock, runner, instance, capability, "fail-1");
        var initialDrain = await FailAsync(store, clock, runner, instance, capability, "fail-2");
        clock.Advance(initialDrain.CooldownUntil!.Value - clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1));

        var canary = await store.ClaimAsync(
            new ClaimRequest(runner, instance, RequiredCapabilities: [CapabilityProtocol.CodingExecutor, capability]),
            runner,
            default);
        Assert.Equal("claimed", canary.Status);
        Assert.Contains(capability, canary.CanaryCapabilities!);
        var blocked = await store.ClaimAsync(
            new ClaimRequest(runner, instance, RequiredCapabilities: [CapabilityProtocol.CodingExecutor, capability]),
            runner,
            default);
        Assert.Equal("empty", blocked.Status);
        Assert.Contains("canary", blocked.Message);

        var failedCanary = await store.ReportCapabilityFailureAsync(
            new CapabilityFailureRequest(
                runner, instance, capability, "ProviderUnauthorized", "Codex returned 401",
                clock.GetUtcNow().UtcDateTime, "canary-failed", "run", canary.Run!.RunId, canary.Lease!.Fence),
            runner,
            default);
        Assert.Equal(CapabilityHealthStates.Draining, failedCanary.HealthState);
        Assert.True(failedCanary.CooldownUntil > initialDrain.CooldownUntil);
        clock.Advance(
            failedCanary.CooldownUntil!.Value
            - clock.GetUtcNow().UtcDateTime
            + TimeSpan.FromSeconds(1));
        await store.AdvertiseCapabilitiesAsync(
            Advertisement(
                clock,
                runner,
                instance,
                2,
                CapabilityProtocol.CodingExecutor,
                capability),
            runner,
            default);
        var recoveryCanary = await store.ClaimAsync(
            new ClaimRequest(runner, instance, RequiredCapabilities: [CapabilityProtocol.CodingExecutor, capability]),
            runner,
            default);
        Assert.Equal("claimed", recoveryCanary.Status);
        Assert.Single(recoveryCanary.CanaryCapabilities!);
        await CompleteSuccessfulRunAsync(store, recoveryCanary, runner, instance);

        var normal = await store.ClaimAsync(
            new ClaimRequest(runner, instance, RequiredCapabilities: [CapabilityProtocol.CodingExecutor, capability]),
            runner,
            default);
        Assert.Equal("claimed", normal.Status);
        Assert.Empty(normal.CanaryCapabilities!);
    }

    [Fact]
    public async Task Capability_drain_does_not_revoke_running_work_and_restart_preserves_the_drain()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        var capability = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "codex", "instance-a", "host-a",
            CapabilityProtocol.CodingExecutor, capability);
        var running = await store.ClaimAsync(
            new ClaimRequest("codex", "instance-a", RequiredCapabilities: [CapabilityProtocol.CodingExecutor, capability]),
            "codex",
            default);

        await FailAsync(
            store, clock, "codex", "instance-a", capability, "failure-1",
            running.Run!.RunId, running.Lease!.Fence);
        await FailAsync(
            store, clock, "codex", "instance-a", capability, "failure-2",
            running.Run.RunId, running.Lease.Fence);
        var renewed = await store.RenewLeaseAsync(
            running.Run.RunId,
            new LeaseRenewRequest("codex", "instance-a", running.Lease!.LeaseId, running.Lease.Fence),
            "codex",
            default);
        Assert.Equal("renewed", renewed.Status);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var snapshots = await restarted.ListRunnerCapabilitySnapshotsAsync(default);
        var codex = Assert.Single(snapshots, item => item.RunnerId == "codex");
        Assert.Equal(
            CapabilityHealthStates.Draining,
            Assert.Single(codex.Capabilities, item => item.Key == capability).HealthState);
    }

    [Fact]
    public async Task Successful_provider_probe_clears_auth_drain_without_runner_restart()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        const string runner = "codex";
        const string instance = "codex-instance";
        var capability = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(
            store,
            clock,
            runner,
            instance,
            "host-a",
            CapabilityProtocol.CodingExecutor,
            capability);
        await FailAsync(store, clock, runner, instance, capability, "auth-1");
        await FailAsync(store, clock, runner, instance, capability, "auth-2");

        await store.AdvertiseCapabilitiesAsync(
            new CapabilityAdvertisementRequest(
                runner,
                instance,
                CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime,
                300,
                2,
                [
                    new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
                    new AdvertisedCapabilityDto(
                        capability,
                        "provider-auth",
                        "ready",
                        Signal: "ok"),
                ]),
            runner,
            default);

        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        var auth = Assert.Single(snapshot.Capabilities, item => item.Key == capability);
        Assert.Equal(CapabilityHealthStates.Healthy, auth.HealthState);
        Assert.Equal(0, auth.ConsecutiveFailures);
        Assert.Null(auth.CooldownUntil);
        Assert.Contains(
            auth.RecoveryHistory,
            item => item.FromState == CapabilityHealthStates.Draining
                    && item.ToState == CapabilityHealthStates.Healthy);

        var claim = await store.ClaimAsync(
            new ClaimRequest(
                runner,
                instance,
                RequiredCapabilities: [CapabilityProtocol.CodingExecutor, capability]),
            runner,
            default);
        Assert.Equal("claimed", claim.Status);
    }

    [Fact]
    public async Task Advertisement_generation_and_failure_idempotency_reject_stale_writes()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        await store.RegisterRunnerAsync(
            "runner",
            new RegisterRunnerRequest(
                "runner", "host-a", "instance-a", "1.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "runner",
            default);
        var advertisement = Advertisement(
            clock,
            "runner",
            "instance-a",
            2,
            CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.RepositoryAccess);
        await store.AdvertiseCapabilitiesAsync(advertisement, "runner", default);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AdvertiseCapabilitiesAsync(advertisement with { Generation = 1 }, "runner", default));
        Assert.Equal("stale-capability-advertisement", stale.Code);

        var request = new CapabilityFailureRequest(
            "runner", "instance-a", CapabilityProtocol.CodingExecutor,
            "ExecutorUnavailable", "failed", clock.GetUtcNow().UtcDateTime,
            "failure-key");
        var first = await store.ReportCapabilityFailureAsync(request, "runner", default);
        var replay = await store.ReportCapabilityFailureAsync(request, "runner", default);
        Assert.Equal(first, replay);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportCapabilityFailureAsync(
                request with { Reason = "different payload" }, "runner", default));
        Assert.Equal("idempotency-conflict", conflict.Code);

        var claim = await store.ClaimAsync(
            new ClaimRequest("runner", "instance-a", RequiredCapabilities: [CapabilityProtocol.CodingExecutor]),
            "runner",
            default);
        var staleClaim = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportCapabilityFailureAsync(
                request with
                {
                    IdempotencyKey = "stale-claim",
                    ClaimKind = "run",
                    ClaimId = claim.Run!.RunId,
                    Fence = claim.Lease!.Fence + 1,
                },
                "runner",
                default));
        Assert.Equal("stale-capability-claim", staleClaim.Code);
        var unrelatedCapability = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportCapabilityFailureAsync(
                request with
                {
                    CapabilityKey = CapabilityProtocol.RepositoryAccess,
                    IdempotencyKey = "unrelated-claim-capability",
                    ClaimKind = "run",
                    ClaimId = claim.Run!.RunId,
                    Fence = claim.Lease!.Fence,
                },
                "runner",
                default));
        Assert.Equal("capability-not-required-by-claim", unrelatedCapability.Code);
        var future = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ReportCapabilityFailureAsync(
                request with
                {
                    IdempotencyKey = "future-failure",
                    OccurredAt = clock.GetUtcNow().UtcDateTime.AddMinutes(3),
                },
                "runner",
                default));
        Assert.Contains("future", future.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TaskServerStore Store(string path, TimeProvider clock)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = path }),
            clock);

    private static async Task<ProjectDto> SeedTasksAsync(TaskServerStore store, int count)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "CAP"), "test", default);
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
        var registrationCapabilities = capabilities.Contains(CapabilityProtocol.ReviewExecutor)
            ? new[]
            {
                ReviewCapabilities.ReviewExecutor,
                ReviewCapabilities.GitMaterialization,
                ReviewCapabilities.SemanticReview,
                ReviewCapabilities.VisionReview,
            }
            : new[] { ReviewCapabilities.CodingExecutor };
        await store.RegisterRunnerAsync(
            runner,
            new RegisterRunnerRequest(
                runner, host, instance, "1.0", TaskServerProtocol.Current, registrationCapabilities),
            runner,
            default);
        await store.AdvertiseCapabilitiesAsync(
            Advertisement(clock, runner, instance, 1, capabilities),
            runner,
            default);
    }

    private static CapabilityAdvertisementRequest Advertisement(
        ManualTimeProvider clock,
        string runner,
        string instance,
        long generation,
        params string[] capabilities)
        => new(
            runner,
            instance,
            CapabilityProtocol.CurrentSchemaVersion,
            clock.GetUtcNow().UtcDateTime,
            300,
            generation,
            capabilities.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray());

    private static async Task CompleteSuccessfulRunAsync(
        TaskServerStore store,
        ClaimResponse claim,
        string runner,
        string instance)
    {
        var resultSha = new string('5', 40);
        var resultRef = FencedGitRefs.ImmutableResult(
            claim.Run!.RunId,
            claim.Lease!.Fence,
            resultSha);
        var envelope = new ImmutableResultEnvelope(
            "repo-project",
            claim.Run.RunId,
            new string('4', 40),
            resultSha,
            resultRef,
            null,
            new string('6', 64));
        var digest = ResultEnvelopeDigest.Compute(envelope);
        await store.AcknowledgeResultHandoffAsync(
            claim.Run.RunId,
            new ResultHandoffRequest(
                runner,
                instance,
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                1,
                $"handoff:{claim.Run.RunId}",
                digest,
                envelope),
            runner,
            default);
        await store.CompleteRunAsync(
            claim.Run.RunId,
            new CompleteRunRequest(
                runner,
                instance,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "success",
                "done",
                digest,
                $"completion:{claim.Run.RunId}",
                2),
            runner,
            default);
    }

    private static Task<CapabilityFailureResponse> FailAsync(
        TaskServerStore store,
        ManualTimeProvider clock,
        string runner,
        string instance,
        string capability,
        string key,
        string? claimId = null,
        long? fence = null)
        => store.ReportCapabilityFailureAsync(
            new CapabilityFailureRequest(
                runner,
                instance,
                capability,
                "ProviderUnauthorized",
                "provider returned 401",
                clock.GetUtcNow().UtcDateTime,
                key,
                claimId is null ? null : "run",
                claimId,
                fence),
            runner,
            default);
}
