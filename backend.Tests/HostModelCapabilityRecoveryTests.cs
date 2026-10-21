using AgentStudio.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class HostModelCapabilityRecoveryTests
{
    [Fact]
    public void Cached_or_racing_advertisement_cannot_clear_a_model_rejection()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var registry = Registry(clock);
        var firstProbeAt = clock.GetUtcNow().UtcDateTime;

        Advertise(registry, clock, 1, firstProbeAt, probeSequence: 4);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol", rejectionSequence: 7);
        clock.Advance(TimeSpan.FromSeconds(1));
        var stale = Advertise(registry, clock, 2, firstProbeAt, probeSequence: 4);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("unavailable", Assert.Single(stale.Capabilities).AdvertisedStatus);

        // A probe that started before the rejection was recorded on the runner.
        var racing = Advertise(registry, clock, 3, clock.GetUtcNow().UtcDateTime, probeSequence: 6);
        Assert.Equal("unavailable", Assert.Single(racing.Capabilities).AdvertisedStatus);
        var unsequenced = Advertise(registry, clock, 4, clock.GetUtcNow().UtcDateTime, probeSequence: null);
        Assert.Equal("unavailable", Assert.Single(unsequenced.Capabilities).AdvertisedStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = Advertise(registry, clock, 5, clock.GetUtcNow().UtcDateTime, probeSequence: 8);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("ready", Assert.Single(recovered.Capabilities).AdvertisedStatus);
    }

    [Fact]
    public void Delayed_advertisement_from_a_runner_clock_ahead_cannot_clear_a_model_rejection()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var registry = Registry(clock);
        var runnerClockAhead = clock.GetUtcNow().UtcDateTime.AddMinutes(30);

        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol", rejectionSequence: 12);
        clock.Advance(TimeSpan.FromSeconds(5));
        // Probed before the rejection, delivered after it, stamped by a runner
        // clock half an hour ahead of the server.
        var delayed = Advertise(registry, clock, 1, runnerClockAhead, probeSequence: 11);

        Assert.Equal("unavailable", Assert.Single(delayed.Capabilities).AdvertisedStatus);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        var newer = Advertise(registry, clock, 2, runnerClockAhead.AddMinutes(-40), probeSequence: 13);
        Assert.Equal("ready", Assert.Single(newer.Capabilities).AdvertisedStatus);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
    }

    private static V1ReviewExecutorRegistry Registry(FakeTimeProvider clock)
    {
        var registry = new V1ReviewExecutorRegistry(clock);
        registry.Register("runner-a", new Contract.RegisterRunnerRequest(
            "runner-a", "host-a", "instance-a", "test", Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor]));
        return registry;
    }

    private static Contract.RunnerCapabilitySnapshotDto Advertise(
        V1ReviewExecutorRegistry registry,
        FakeTimeProvider clock,
        long generation,
        DateTime observedAt,
        long? probeSequence)
        => registry.AdvertiseCapabilities("runner-a", new Contract.CapabilityAdvertisementRequest(
            "runner-a", "instance-a", Contract.CapabilityProtocol.CurrentSchemaVersion,
            clock.GetUtcNow().UtcDateTime, 180, generation,
            [new Contract.AdvertisedCapabilityDto(
                Contract.CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol"), "model-acceptance", "ready",
                EvidenceId: probeSequence is { } sequence
                    ? Contract.CapabilityProtocol.ModelProbeEvidence(sequence)
                    : null,
                CredentialGeneration: "login-a", CredentialObservedAt: observedAt)]));
}
