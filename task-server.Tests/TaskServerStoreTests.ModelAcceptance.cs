using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed partial class TaskServerStoreTests
{
    private const string UnsupportedModelFrame = """
        {"type":"turn.failed","error":{"type":"invalid_request_error","message":"The gpt-6-sol model is not supported when using Codex with a ChatGPT account","status":400}}
        """;

    [Fact]
    public async Task Delayed_advertisement_from_a_runner_clock_ahead_cannot_clear_a_model_rejection()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedPinnedTaskAsync(store, "codex", "gpt-6-sol");
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 1, probeSequence: 10);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await CompleteWithModelRejectionAsync(store, claim, rejectionSequence: 12);
        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.State);

        // The runner clock runs 90 seconds ahead. Its probe 11 ran before the
        // rejection but reaches the server afterwards, stamped later than the
        // server's rejection event.
        clock.Advance(TimeSpan.FromSeconds(5));
        var runnerAhead = clock.GetUtcNow().UtcDateTime.AddSeconds(90);
        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 2, probeSequence: 11,
            observedAt: runnerAhead, advertisedAt: runnerAhead);

        Assert.Equal("unavailable", Assert.Single(
            (await store.ListRunnerCapabilitySnapshotsAsync(default)).Single().Capabilities).AdvertisedStatus);
        Assert.Equal("empty", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);

        clock.Advance(TimeSpan.FromSeconds(5));
        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 3, probeSequence: 13,
            observedAt: runnerAhead.AddSeconds(5), advertisedAt: runnerAhead.AddSeconds(5));
        Assert.Equal("claimed", (await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"), "test", default)).Status);
    }

    [Fact]
    public async Task Host_permit_is_not_reoffered_after_a_model_rejection_until_a_newer_probe()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedPinnedTaskAsync(store, "codex", "gpt-6-sol");
        await store.RegisterRunnerAsync("runner-a", PermitRunner(), "runner-a", default);
        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 1, probeSequence: 4);
        Assert.Single((await HostReportAsync(store, clock, 1)).AvailableWork);

        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        Assert.Equal("claimed", claim.Status);
        await CompleteWithModelRejectionAsync(store, claim, rejectionSequence: 7);

        Assert.Empty((await HostReportAsync(store, clock, 2)).AvailableWork);
        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 2, probeSequence: 6);
        Assert.Empty((await HostReportAsync(store, clock, 3)).AvailableWork);

        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 3, probeSequence: 8);
        var recovered = await HostReportAsync(store, clock, 4);
        var permit = Assert.Single(recovered.AvailableWork);

        // Acceptance rechecks in its own transaction: the probe expired after
        // the offer, so no lease is created.
        clock.Advance(TimeSpan.FromSeconds(181));
        var expired = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AcceptWorkPermitAsync(permit.PermitId, Accept(recovered, "expired-probe"), "runner-a", default));
        Assert.Equal("work-permit-model-unaccepted", expired.Code);

        await AdvertiseModelAsync(store, clock, "codex", "gpt-6-sol", "ready", 4, probeSequence: 9);
        var accepted = await store.AcceptWorkPermitAsync(
            permit.PermitId, Accept(recovered, "fresh-probe"), "runner-a", default);
        Assert.Equal("3-progress", accepted.Task.State);
    }

    [Fact]
    public async Task Host_permit_requires_fresh_acceptance_of_an_unverified_primary_model()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedPinnedTaskAsync(store, "claude", "claude-opus-5");
        await store.RegisterRunnerAsync("runner-a", PermitRunner(), "runner-a", default);
        var offered = await HostReportAsync(store, clock, 1);
        Assert.Empty(offered.AvailableWork);

        // A permit listed while the primary was proven is refused once that
        // proof is no longer fresh.
        await AdvertiseModelAsync(store, clock, "claude", "claude-opus-5", "ready", 1, probeSequence: 1);
        var listed = await HostReportAsync(store, clock, 2);
        var permit = Assert.Single(listed.AvailableWork);
        await AdvertiseModelAsync(store, clock, "claude", "claude-opus-5", "unknown", 2, probeSequence: 2);
        var refused = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AcceptWorkPermitAsync(permit.PermitId, Accept(listed, "unverified"), "runner-a", default));
        Assert.Equal("work-permit-model-unaccepted", refused.Code);
    }

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)> SeedPinnedTaskAsync(
        TaskServerStore store, string cliType, string model)
    {
        var seeded = await SeedReadyTaskAsync(store);
        await store.SetTaskCliTypeAsync(seeded.Project.ProjectId, seeded.Task.TaskId,
            new SetTaskCliTypeRequest(cliType, 0), "operator", default);
        await store.SetTaskModelAsync(seeded.Project.ProjectId, seeded.Task.TaskId,
            new SetTaskModelRequest(model, 1), "operator", default);
        return seeded;
    }

    private static async Task CompleteWithModelRejectionAsync(
        TaskServerStore store, ClaimResponse claim, long rejectionSequence)
    {
        var decision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            claim.Run!.RunId, ExecutionAttemptKind.Coding, ProviderTerminalEvent: UnsupportedModelFrame,
            ExitCode: 1, DurableOutputState: DurableOutputState.Missing,
            EffectiveCliType: "codex", EffectiveModel: "gpt-6-sol", EffectiveThinkingLevel: "medium",
            ModelRejectionProbeSequence: rejectionSequence));
        await store.CompleteRunAsync(claim.Run.RunId,
            new CompleteRunRequest("runner-a", "instance-a", claim.Lease!.LeaseId,
                claim.Lease.Fence, decision.Outcome.ToString(),
                IdempotencyKey: $"completion:{claim.Run.RunId}:model-refusal",
                Sequence: 1, OutcomeDecision: decision), "runner-a", default);
    }

    private static Task AdvertiseModelAsync(
        TaskServerStore store,
        ManualTimeProvider clock,
        string provider,
        string model,
        string status,
        long generation,
        long probeSequence,
        DateTime? observedAt = null,
        DateTime? advertisedAt = null,
        int freshForSeconds = 180)
        => store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-a", "instance-a", 2, advertisedAt ?? clock.GetUtcNow().UtcDateTime, freshForSeconds, generation,
            [new AdvertisedCapabilityDto(
                CapabilityProtocol.ModelAcceptance(provider, model), "model-acceptance", status,
                EvidenceId: CapabilityProtocol.ModelProbeEvidence(probeSequence),
                CredentialGeneration: "login-1",
                CredentialObservedAt: observedAt ?? clock.GetUtcNow().UtcDateTime)]), "runner-a", default);

    private static RegisterRunnerRequest PermitRunner()
        => new(
            "runner",
            "host-a",
            "instance-a",
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor, "permits", "local-queue", "host-post-processing"],
            HostOrchestratorContract.MinimumSupported,
            HostOrchestratorContract.MaximumSupported);

    private static Task<HostReportResponse> HostReportAsync(
        TaskServerStore store, ManualTimeProvider clock, long sequence)
        => store.AcceptHostReportAsync(
            "runner-a",
            new HostReportRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-a",
                sequence,
                clock.GetUtcNow().UtcDateTime,
                new HostCapacityDto(1, 1, 0, 0, 1),
                [new HostCapabilityDto("git-push", "ready", ObservedAt: clock.GetUtcNow().UtcDateTime)],
                [],
                [],
                []),
            "runner-a",
            default);

    private static WorkPermitAcceptRequest Accept(HostReportResponse report, string idempotencyKey)
        => new(
            HostOrchestratorContract.Current,
            "host-a",
            "instance-a",
            "runner-a",
            report.AcceptedSequence,
            report.PolicyVersion,
            idempotencyKey);
}
