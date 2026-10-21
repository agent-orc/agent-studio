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

        Advertise(registry, clock, 1, firstProbeAt, firstProbeAt);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol");
        clock.Advance(TimeSpan.FromSeconds(1));
        var stale = Advertise(registry, clock, 2, firstProbeAt, firstProbeAt);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("unavailable", Assert.Single(stale.Capabilities).AdvertisedStatus);

        // A fresh host observation without a later server watermark is a race,
        // not proof.
        var racing = Advertise(registry, clock, 3, clock.GetUtcNow().UtcDateTime, firstProbeAt);
        Assert.Equal("unavailable", Assert.Single(racing.Capabilities).AdvertisedStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = Advertise(registry, clock, 4, clock.GetUtcNow().UtcDateTime, racing.LastSeenAt);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("ready", Assert.Single(recovered.Capabilities).AdvertisedStatus);
    }

    [Fact]
    public void Delayed_advertisement_from_a_host_clock_ahead_cannot_clear_a_model_rejection()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var registry = Registry(clock);
        // The host clock runs 90 seconds ahead of Studio.
        DateTime HostNow() => clock.GetUtcNow().UtcDateTime.AddSeconds(90);
        var beforeRefusal = Advertise(registry, clock, 1, HostNow(), null).LastSeenAt;

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol");
        clock.Advance(TimeSpan.FromSeconds(1));
        // The probe started before the refusal and arrives late. Its host
        // observation is later than the refusal on Studio's clock.
        var delayed = Advertise(registry, clock, 2, HostNow(), beforeRefusal);
        Assert.Equal("unavailable", Assert.Single(delayed.Capabilities).AdvertisedStatus);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        // A watermark Studio has not issued yet is not evidence either.
        var forged = Advertise(registry, clock, 3, HostNow(), HostNow());
        Assert.Equal("unavailable", Assert.Single(forged.Capabilities).AdvertisedStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        Advertise(registry, clock, 4, HostNow(), delayed.LastSeenAt);
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
        DateTime hostTime,
        DateTime? watermark)
        => registry.AdvertiseCapabilities("runner-a", new Contract.CapabilityAdvertisementRequest(
            "runner-a", "instance-a", Contract.CapabilityProtocol.CurrentSchemaVersion,
            hostTime > clock.GetUtcNow().UtcDateTime ? hostTime : clock.GetUtcNow().UtcDateTime,
            180, generation,
            [new Contract.AdvertisedCapabilityDto(
                Contract.CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol"), "model-acceptance", "ready",
                CredentialGeneration: "login-a", CredentialObservedAt: hostTime,
                ProbeServerWatermark: watermark)]));
}
