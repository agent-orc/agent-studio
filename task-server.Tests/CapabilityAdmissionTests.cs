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
    public async Task Confirmed_incident_signal_blocks_a_ready_provider_capability()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        await RegisterAndAdvertiseAsync(store, clock, "codex", "instance", "host-a",
            CapabilityProtocol.CodingExecutor, CapabilityProtocol.ProviderAuthentication("codex"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "codex", "instance", 2, clock.GetUtcNow().UtcDateTime, 180, 2,
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
             new AdvertisedCapabilityDto(CapabilityProtocol.ProviderAuthentication("codex"),
                 "provider-auth", "ready", Signal: "provider_incident",
                 CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                 EffectiveSource: "native-cli-store", HealthOutcome: "provider_incident",
                 ServiceAvailability: "unavailable", CredentialHealth: "unknown")], CredentialHealthVersion: 2), "codex", default);
        var claim = await store.ClaimAsync(new ClaimRequest("codex", "instance",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor,
                CapabilityProtocol.ProviderAuthentication("codex")]), "codex", default);
        Assert.NotEqual("claimed", claim.Status);
    }

    [Fact]
    public async Task Review_claim_stops_on_confirmed_provider_incident()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var key = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "review", "instance", "host-a",
            CapabilityProtocol.ReviewExecutor, key);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "review", "instance", 2, clock.GetUtcNow().UtcDateTime, 180, 2,
            [new AdvertisedCapabilityDto(CapabilityProtocol.ReviewExecutor, "executor"),
             new AdvertisedCapabilityDto(key, "provider-auth", "ready",
                 CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                 EffectiveSource: "native-cli-store", HealthOutcome: "provider_incident",
                 CredentialHealth: "unknown", ServiceAvailability: "unavailable")],
            CredentialHealthVersion: 2), "review", default);
        var claim = await store.ClaimReviewAsync(new ReviewClaimRequest("review", "instance",
            RequiredCapabilities: [CapabilityProtocol.ReviewExecutor, key]), "review", default);
        Assert.Equal(ReviewClaimEmptyReasons.CapabilityAdmission, claim.Reason);
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
    }

    [Fact]
    public async Task Incident_uses_one_durable_canary_and_requires_new_real_success()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        var key = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "runner-a", "instance-a", "host-a",
            CapabilityProtocol.CodingExecutor, key);
        await RegisterAndAdvertiseAsync(store, clock, "runner-b", "instance-b", "host-b",
            CapabilityProtocol.CodingExecutor, key);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseHealth("runner-a", "instance-a", 2, "provider_incident");
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
        var otherClaim = await store.ClaimAsync(new ClaimRequest("runner-b", "instance-b",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, key]), "runner-b", default);
        Assert.NotEqual("claimed", otherClaim.Status);
        Assert.False((await store.ReserveProviderCanaryAsync("runner-a", "instance-a", "codex", default)).Allowed);

        clock.Advance(TimeSpan.FromSeconds(70));
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var permits = await Task.WhenAll(
            store.ReserveProviderCanaryAsync("runner-a", "instance-a", "codex", default),
            restarted.ReserveProviderCanaryAsync("runner-b", "instance-b", "codex", default));
        Assert.Single(permits, item => item.Allowed);
        var owner = permits[0].Allowed ? "runner-a" : "runner-b";
        var ownerInstance = permits[0].Allowed ? "instance-a" : "instance-b";
        Assert.Equal("canary", (await store.ReserveProviderCanaryAsync(
            owner, ownerInstance, "codex", default)).Reason);
        var other = permits[0].Allowed ? "runner-b" : "runner-a";
        var otherInstance = permits[0].Allowed ? "instance-b" : "instance-a";

        // Cached healthy evidence from another host and an old success from
        // the owner cannot release the service-wide incident hold.
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseHealth(other, otherInstance, other == "runner-a" ? 3 : 2,
            "healthy", Start.UtcDateTime);
        await AdvertiseHealth(owner, ownerInstance, owner == "runner-a" ? 3 : 2,
            "healthy", Start.UtcDateTime);
        Assert.NotEqual("claimed", (await store.ClaimAsync(new ClaimRequest("runner-b", "instance-b",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, key]), "runner-b", default)).Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        var olderRealSuccess = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseHealth(other, otherInstance, other == "runner-a" ? 4 : 3,
            "provider_incident");
        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseHealth(owner, ownerInstance, owner == "runner-a" ? 4 : 3,
            "healthy", olderRealSuccess);
        Assert.NotEqual("claimed", (await store.ClaimAsync(new ClaimRequest("runner-b", "instance-b",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, key]), "runner-b", default)).Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseHealth(owner, ownerInstance, owner == "runner-a" ? 5 : 4,
            "healthy", clock.GetUtcNow().UtcDateTime);
        if (owner == "runner-a")
        {
            Assert.NotEqual("claimed", (await store.ClaimAsync(new ClaimRequest("runner-b", "instance-b",
                RequiredCapabilities: [CapabilityProtocol.CodingExecutor, key]), "runner-b", default)).Status);
            clock.Advance(TimeSpan.FromSeconds(1));
            await AdvertiseHealth("runner-b", "instance-b", 4, "healthy", clock.GetUtcNow().UtcDateTime);
        }
        Assert.Equal("claimed", (await store.ClaimAsync(new ClaimRequest("runner-b", "instance-b",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, key]), "runner-b", default)).Status);

        async Task AdvertiseHealth(string runner, string instance, long generation,
            string outcome, DateTime? successAt = null)
            => await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                runner, instance, 2, clock.GetUtcNow().UtcDateTime, 180, generation,
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
                 new AdvertisedCapabilityDto(key, "provider-auth", "ready",
                     CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                     LastRealSuccessAt: successAt, EffectiveSource: "native-cli-store",
                     HealthOutcome: outcome,
                     ServiceAvailability: outcome == "healthy" ? "available" : "unavailable",
                     CredentialHealth: outcome == "healthy" ? "valid" : "unknown")],
                CredentialHealthVersion: 2), runner, default);
    }

    [Fact]
    public async Task Invalid_shared_binding_creates_one_runbook_item_and_other_provider_can_claim()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 2);
        var claude = CapabilityProtocol.ProviderAuthentication("claude");
        var codex = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "coding", "coding-instance", "host-a",
            CapabilityProtocol.CodingExecutor, claude);
        await RegisterAndAdvertiseAsync(store, clock, "review", "review-instance", "host-a",
            CapabilityProtocol.ReviewExecutor, claude);
        await RegisterAndAdvertiseAsync(store, clock, "other", "other-instance", "host-a",
            CapabilityProtocol.CodingExecutor, codex);
        clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var (runner, instance) in new[] { ("coding", "coding-instance"), ("review", "review-instance") })
            await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                runner, instance, 2, clock.GetUtcNow().UtcDateTime, 180, 2,
                [new AdvertisedCapabilityDto(claude, "provider-auth", "ready",
                    CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                    EffectiveSource: "environment-file", HealthOutcome: "credential_invalid",
                    ServiceAvailability: "unknown", CredentialHealth: "invalid")], CredentialHealthVersion: 2), runner, default);
        var renewal = Assert.Single(await store.ListProviderHealthItemsAsync(default));
        Assert.Equal("renewal", renewal.Kind);
        Assert.Equal("docs/operations/setup/cli-relogin-runbook.md", renewal.RunbookId);
        Assert.Equal("generation-a", renewal.CredentialGeneration);
        Assert.NotEqual("claimed", (await store.ClaimAsync(new ClaimRequest("coding", "coding-instance",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, claude]), "coding", default)).Status);
        Assert.Equal("claimed", (await store.ClaimAsync(new ClaimRequest("other", "other-instance",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, codex]), "other", default)).Status);
    }

    [Fact]
    public async Task Indeterminate_requires_fifteen_minutes_before_one_diagnosis_item()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var key = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "coding", "instance", "host-a",
            CapabilityProtocol.CodingExecutor, key);
        async Task Advertise(long generation)
            => await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                "coding", "instance", 2, clock.GetUtcNow().UtcDateTime, 180, generation,
                [new AdvertisedCapabilityDto(key, "provider-auth", "ready",
                    CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                    EffectiveSource: "native-cli-store", HealthOutcome: "indeterminate",
                    ServiceAvailability: "unknown", CredentialHealth: "unknown")], CredentialHealthVersion: 2), "coding", default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Advertise(2);
        clock.Advance(TimeSpan.FromMinutes(14));
        await Advertise(3);
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
        clock.Advance(TimeSpan.FromMinutes(2));
        await Advertise(4);
        await Advertise(5);
        Assert.Equal("diagnosis", Assert.Single(await store.ListProviderHealthItemsAsync(default)).Kind);
    }

    [Fact]
    public async Task Incident_failed_canaries_back_off_at_sixty_one_twenty_and_three_hundred_seconds()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var key = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "coding", "instance", "host-a",
            CapabilityProtocol.CodingExecutor, key);
        async Task Incident(long generation)
            => await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                "coding", "instance", 2, clock.GetUtcNow().UtcDateTime, 180, generation,
                [new AdvertisedCapabilityDto(key, "provider-auth", "ready",
                    CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                    EffectiveSource: "native-cli-store", HealthOutcome: "provider_incident",
                    ServiceAvailability: "unavailable", CredentialHealth: "unknown")], CredentialHealthVersion: 2), "coding", default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Incident(2);
        var first = await store.ReserveProviderCanaryAsync("coding", "instance", "codex", default);
        Assert.InRange((first.NextRetryAt!.Value - clock.GetUtcNow().UtcDateTime).TotalSeconds, 55, 65);
        clock.Advance(TimeSpan.FromSeconds(70));
        Assert.True((await store.ReserveProviderCanaryAsync("coding", "instance", "codex", default)).Allowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Incident(3);
        var second = await store.ReserveProviderCanaryAsync("coding", "instance", "codex", default);
        Assert.InRange((second.NextRetryAt!.Value - clock.GetUtcNow().UtcDateTime).TotalSeconds, 115, 125);
        clock.Advance(TimeSpan.FromSeconds(130));
        Assert.True((await store.ReserveProviderCanaryAsync("coding", "instance", "codex", default)).Allowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Incident(4);
        var third = await store.ReserveProviderCanaryAsync("coding", "instance", "codex", default);
        Assert.InRange((third.NextRetryAt!.Value - clock.GetUtcNow().UtcDateTime).TotalSeconds, 295, 305);
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
    }

    [Theory]
    [InlineData("quota_exhausted", "limited")]
    [InlineData("network_failure", "ready")]
    public async Task Quota_and_network_keep_their_policy_owners_without_login_items(
        string outcome, string status)
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedTasksAsync(store, 1);
        var key = CapabilityProtocol.ProviderAuthentication("codex");
        await RegisterAndAdvertiseAsync(store, clock, "coding", "instance", "host-a",
            CapabilityProtocol.CodingExecutor, key);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "coding", "instance", 2, clock.GetUtcNow().UtcDateTime, 180, 2,
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
             new AdvertisedCapabilityDto(key, "provider-auth", status,
                 CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                 EffectiveSource: "native-cli-store", HealthOutcome: outcome,
                 ServiceAvailability: outcome == "quota_exhausted" ? "limited" : "unavailable",
                 CredentialHealth: "unknown")],
            CredentialHealthVersion: 2), "coding", default);
        Assert.Empty(await store.ListProviderHealthItemsAsync(default));
        Assert.Equal("no-hold", (await store.ReserveProviderCanaryAsync(
            "coding", "instance", "codex", default)).Reason);
        Assert.NotEqual("claimed", (await store.ClaimAsync(new ClaimRequest("coding", "instance",
            RequiredCapabilities: [CapabilityProtocol.CodingExecutor, key]), "coding", default)).Status);
    }

    [Fact]
    public void Rich_capability_fields_survive_json_deserialization_with_legacy_constructor_available()
    {
        var observed = Start.UtcDateTime;
        var request = new CapabilityAdvertisementRequest("runner", "instance", 2,
            observed, 180, 1, [new AdvertisedCapabilityDto("provider-auth:claude", "provider-auth",
                CredentialGeneration: "generation-b", EffectiveSource: "environment-file")],
            CredentialHealthVersion: 1);
        var json = System.Text.Json.JsonSerializer.Serialize(request);
        var read = System.Text.Json.JsonSerializer.Deserialize<CapabilityAdvertisementRequest>(json)!;
        Assert.Equal(1, read.CredentialHealthVersion);
        Assert.Equal("generation-b", Assert.Single(read.Capabilities).CredentialGeneration);
    }

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

    [Fact]
    public async Task Credential_observation_v2_round_trips_and_old_observation_cannot_replace_it()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await RegisterAndAdvertiseAsync(store, clock, "runner-v2", "instance-v2", "host-v2",
            CapabilityProtocol.CodingExecutor, CapabilityProtocol.ProviderAuthentication("claude"));
        var observed = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
        var newer = new CapabilityAdvertisementRequest("runner-v2", "instance-v2", 2,
            observed, 180, 2, [new AdvertisedCapabilityDto(
                CapabilityProtocol.ProviderAuthentication("claude"), "provider-auth",
                CredentialGeneration: "generation-b", CredentialObservedAt: observed,
                LastRealSuccessAt: observed, ExpiryProvenance: "unknown",
                EffectiveSource: "environment-file", NativeFileShadowed: true,
                EvidenceRefs: ["evidence:probe-1"])]);
        await store.AdvertiseCapabilitiesAsync(newer, "runner-v2", default);
        var capability = Assert.Single((await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-v2").Capabilities,
            item => item.Key == CapabilityProtocol.ProviderAuthentication("claude"));
        Assert.Equal("generation-b", capability.CredentialGeneration);
        Assert.Equal("environment-file", capability.EffectiveSource);
        Assert.True(capability.NativeFileShadowed);
        Assert.Equal(["evidence:probe-1"], capability.EvidenceRefs);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AdvertiseCapabilitiesAsync(
            newer with { AdvertisedAt = observed.AddSeconds(-1) }, "runner-v2", default));
    }

    [Fact]
    public async Task Delayed_observation_cannot_advance_generation_over_newer_credential_metadata()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var key = CapabilityProtocol.ProviderAuthentication("claude");
        await RegisterAndAdvertiseAsync(store, clock, "runner-v2", "instance-v2", "host-v2",
            CapabilityProtocol.CodingExecutor, key);
        var newerAt = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
        var newer = new CapabilityAdvertisementRequest("runner-v2", "instance-v2", 2,
            newerAt, 180, 2, [new AdvertisedCapabilityDto(key, "provider-auth",
                CredentialGeneration: "generation-b", CredentialObservedAt: newerAt,
                ExpiryProvenance: "unknown", EffectiveSource: "environment-file")]);
        await store.AdvertiseCapabilitiesAsync(newer, "runner-v2", default);

        var delayedAt = newerAt.AddSeconds(-30);
        var delayedAdvertisement = newer with
        {
            AdvertisedAt = delayedAt,
            Generation = 3,
            Capabilities = [new AdvertisedCapabilityDto(key, "provider-auth",
                CredentialGeneration: "generation-a", CredentialObservedAt: delayedAt,
                ExpiryProvenance: "unknown", EffectiveSource: "native-cli-store")],
        };
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AdvertiseCapabilitiesAsync(delayedAdvertisement, "runner-v2", default));

        var delayedCredential = delayedAdvertisement with { AdvertisedAt = newerAt.AddSeconds(10) };
        await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AdvertiseCapabilitiesAsync(delayedCredential, "runner-v2", default));

        var capability = Assert.Single((await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-v2").Capabilities, item => item.Key == key);
        Assert.Equal(newerAt, capability.AdvertisedAt);
        Assert.Equal("generation-b", capability.CredentialGeneration);
        Assert.Equal("environment-file", capability.EffectiveSource);
    }

    [Fact]
    public async Task Restarted_instance_is_not_fenced_by_the_previous_instances_clock()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var key = CapabilityProtocol.ProviderAuthentication("claude");
        await RegisterAndAdvertiseAsync(store, clock, "runner-v2", "instance-a", "host-v2",
            CapabilityProtocol.CodingExecutor, key);
        var beforeRestart = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
        var previous = new CapabilityAdvertisementRequest("runner-v2", "instance-a", 2,
            beforeRestart, 180, 50, [new AdvertisedCapabilityDto(key, "provider-auth",
                CredentialGeneration: "generation-a", CredentialObservedAt: beforeRestart,
                ExpiryProvenance: "unknown", EffectiveSource: "native-cli-store")]);
        await store.AdvertiseCapabilitiesAsync(previous, "runner-v2", default);

        // The host clock stepped back across the restart, so the new instance
        // starts with a lower generation and earlier observation times.
        await store.RegisterRunnerAsync("runner-v2", new RegisterRunnerRequest(
            "runner-v2", "host-v2", "instance-b", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "runner-v2", default);
        var afterRestart = beforeRestart.AddMinutes(-10);
        var restarted = previous with
        {
            InstanceId = "instance-b",
            AdvertisedAt = afterRestart,
            Generation = 1,
            Capabilities = [new AdvertisedCapabilityDto(key, "provider-auth",
                CredentialGeneration: "generation-b", CredentialObservedAt: afterRestart,
                ExpiryProvenance: "unknown", EffectiveSource: "environment-file")],
        };
        await store.AdvertiseCapabilitiesAsync(restarted, "runner-v2", default);

        var capability = Assert.Single((await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-v2").Capabilities, item => item.Key == key);
        Assert.Equal("generation-b", capability.CredentialGeneration);
        Assert.Equal(afterRestart, capability.AdvertisedAt);

        // Instance ownership still fences the replaced instance, and the new
        // instance still cannot replay its own older observation.
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AdvertiseCapabilitiesAsync(previous with
            {
                AdvertisedAt = beforeRestart.AddMinutes(1), Generation = 99,
            }, "runner-v2", default));
        Assert.Equal("runner-instance-mismatch", stale.Code);
        var delayed = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AdvertiseCapabilitiesAsync(restarted with
            {
                AdvertisedAt = afterRestart.AddSeconds(-1), Generation = 2,
            }, "runner-v2", default));
        Assert.Equal("stale-capability-advertisement", delayed.Code);
    }

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
