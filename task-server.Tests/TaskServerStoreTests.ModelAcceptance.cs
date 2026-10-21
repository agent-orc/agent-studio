using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed partial class TaskServerStoreTests
{
    private const string ModelRefusalFrame = """
        {"type":"turn.failed","error":{"type":"invalid_request_error","message":"The gpt-6-sol model is not supported when using Codex with a ChatGPT account","status":400}}
        """;

    [Fact]
    public async Task Claim_requires_fresh_host_acceptance_of_a_named_primary_model()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await PinRouteAsync(store, project, task, "claude", "claude-opus-5");
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var host = new ModelProbeHost(store, clock);

        // Another provider's accepted model does not verify this card's route.
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 1);
        var unverified = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        Assert.Equal("empty", unverified.Status);

        await host.AdvertiseAsync("claude", "claude-opus-5", "ready", 2);
        clock.Advance(TimeSpan.FromSeconds(181));
        Assert.Equal("empty", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);

        await host.AdvertiseAsync("claude", "claude-opus-5", "ready", 3);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(task.TaskId, claim.Task?.TaskId);
    }

    [Fact]
    public async Task Delayed_advertisement_with_runner_clock_ahead_cannot_clear_a_model_rejection()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await PinRouteAsync(store, project, task, "codex", "gpt-6-sol");
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        // The runner clock runs a minute ahead of the server.
        var host = new ModelProbeHost(store, clock, TimeSpan.FromMinutes(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 1);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await RefuseModelAsync(store, claim, "primary-refusal");

        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);
        Assert.Contains(await store.ListEventsAsync(claim.Run!.RunId, 0, default),
            item => item.Kind == "execution.model-capability-held");

        // Built before the runner recorded the refusal, delivered after it.
        // Its observation time is later than the server's rejection time.
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 1);
        Assert.Equal("empty", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);
        Assert.Equal("unavailable", (await host.StatusAsync("codex", "gpt-6-sol")));

        // A larger sequence is not yet ordered after the refusal either.
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 2);
        Assert.Equal("empty", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "unavailable", 3);
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 3);
        Assert.Equal("empty", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 4);
        Assert.Equal("ready", await host.StatusAsync("codex", "gpt-6-sol"));
        Assert.Equal("claimed", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);
    }

    [Fact]
    public async Task Rejected_fallback_does_not_hold_a_runnable_primary_route()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await PinRouteAsync(store, project, task, "claude", "claude-opus-5");
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        await new ModelProbeHost(store, clock).AdvertiseAsync("claude", "claude-opus-5", "ready", 1);

        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await RefuseModelAsync(store, claim, "fallback-refusal");

        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);
        Assert.Contains(await store.ListEventsAsync(claim.Run!.RunId, 0, default),
            item => item.Kind == "execution.model-capability-held");
        Assert.Equal("claimed", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);
    }

    [Fact]
    public async Task Rejected_same_provider_fallback_does_not_hold_a_blank_primary_route()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.SetTaskCliTypeAsync(project.ProjectId, task.TaskId,
            new SetTaskCliTypeRequest("codex", 0), "operator", default);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);

        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await RefuseModelAsync(store, claim, "same-provider-refusal");

        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);
        Assert.Equal("claimed", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);
    }

    [Fact]
    public async Task Host_permit_is_not_reoffered_or_accepted_after_a_model_rejection()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await PinRouteAsync(store, project, task, "codex", "gpt-6-sol");
        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest(
            "runner", "host-a", "instance-a", "1.0.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor, "permits", "local-queue", "host-post-processing"],
            HostOrchestratorContract.MinimumSupported, HostOrchestratorContract.MaximumSupported),
            "test", default);
        var host = new ModelProbeHost(store, clock);
        long reportSequence = 0;

        // Unverified named model: no offer.
        Assert.Empty((await ReportAsync()).AvailableWork);
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 1);
        var offered = await ReportAsync();
        var permit = Assert.Single(offered.AvailableWork);

        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await RefuseModelAsync(store, claim, "permit-refusal");
        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);

        var afterRejection = await ReportAsync();
        Assert.Empty(afterRejection.AvailableWork);
        // A permit read before the rejection is refused in the lease transaction.
        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AcceptWorkPermitAsync(
            permit.PermitId,
            new WorkPermitAcceptRequest(HostOrchestratorContract.Current, "host-a", "instance-a", "runner-a",
                afterRejection.AcceptedSequence, afterRejection.PolicyVersion, "accept-after-rejection"),
            "runner-a", default));
        Assert.Equal("work-permit-model-unaccepted", refused.Code);

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "unavailable", 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.AdvertiseAsync("codex", "gpt-6-sol", "ready", 3);
        var recovered = await ReportAsync();
        var reoffered = Assert.Single(recovered.AvailableWork);
        var acceptance = await store.AcceptWorkPermitAsync(
            reoffered.PermitId,
            new WorkPermitAcceptRequest(HostOrchestratorContract.Current, "host-a", "instance-a", "runner-a",
                recovered.AcceptedSequence, recovered.PolicyVersion, "accept-after-recovery"),
            "runner-a", default);
        Assert.Equal("accepted", acceptance.Status);

        Task<HostReportResponse> ReportAsync()
            => store.AcceptHostReportAsync("runner-a", new HostReportRequest(
                HostOrchestratorContract.Current, "host-a", "instance-a", ++reportSequence,
                clock.GetUtcNow().UtcDateTime, new HostCapacityDto(2, 2, 0, 0, 2),
                [new HostCapabilityDto("git-push", "ready", ObservedAt: clock.GetUtcNow().UtcDateTime)],
                [], [], []), "runner-a", default);
    }

    /// <summary>Advertises fresh accepted verdicts from runner-a's instance-a.</summary>
    private static Task AcceptModelsAsync(
        TaskServerStore store, ManualTimeProvider clock, params (string Provider, string Model)[] models)
        => store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-a", "instance-a", 2, clock.GetUtcNow().UtcDateTime, 900, clock.GetUtcNow().UtcTicks,
            models.Select((route, index) => new AdvertisedCapabilityDto(
                CapabilityProtocol.ModelAcceptance(route.Provider, route.Model), "model-acceptance", "ready",
                CredentialGeneration: "login-1", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                EvidenceId: CapabilityProtocol.ModelProbeEvidence(index + 1))).ToArray()), "runner-a", default);

    private static async Task PinRouteAsync(
        TaskServerStore store, ProjectDto project, TaskDto task, string cliType, string model)
    {
        await store.SetTaskCliTypeAsync(project.ProjectId, task.TaskId,
            new SetTaskCliTypeRequest(cliType, 0), "operator", default);
        await store.SetTaskModelAsync(project.ProjectId, task.TaskId,
            new SetTaskModelRequest(model, 1), "operator", default);
    }

    private static async Task RefuseModelAsync(TaskServerStore store, ClaimResponse claim, string key)
    {
        var decision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            claim.Run!.RunId, ExecutionAttemptKind.Coding, ProviderTerminalEvent: ModelRefusalFrame,
            ExitCode: 1, DurableOutputState: DurableOutputState.Missing,
            EffectiveCliType: "codex", EffectiveModel: "gpt-6-sol", EffectiveThinkingLevel: "medium"));
        Assert.Equal(ExecutionOutcomeKind.ProviderRejectedRequest, decision.Outcome);
        await store.CompleteRunAsync(claim.Run.RunId,
            new CompleteRunRequest(claim.Lease!.RunnerId, claim.Lease.InstanceId, claim.Lease.LeaseId,
                claim.Lease.Fence, decision.Outcome.ToString(),
                IdempotencyKey: $"completion:{claim.Run.RunId}:{key}",
                Sequence: 1, OutcomeDecision: decision), claim.Lease.RunnerId, default);
    }

    /// <summary>One runner instance whose clock may run ahead of the server.</summary>
    private sealed class ModelProbeHost(TaskServerStore store, ManualTimeProvider clock, TimeSpan? skew = null)
    {
        private long _generation;

        public Task AdvertiseAsync(string provider, string model, string status, long probeSequence)
        {
            var runnerNow = clock.GetUtcNow().UtcDateTime + (skew ?? TimeSpan.Zero);
            return store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
                "runner-a", "instance-a", 2, runnerNow, 180, ++_generation,
                [new AdvertisedCapabilityDto(
                    CapabilityProtocol.ModelAcceptance(provider, model), "model-acceptance", status,
                    CredentialGeneration: "login-1", CredentialObservedAt: runnerNow,
                    EvidenceId: CapabilityProtocol.ModelProbeEvidence(probeSequence))]), "runner-a", default);
        }

        public async Task<string> StatusAsync(string provider, string model)
            => (await store.ListRunnerCapabilitySnapshotsAsync(default)).Single().Capabilities
                .Single(item => item.Key == CapabilityProtocol.ModelAcceptance(provider, model)).AdvertisedStatus;
    }
}
