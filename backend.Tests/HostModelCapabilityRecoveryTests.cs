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
        var registry = new V1ReviewExecutorRegistry(clock);
        registry.Register("runner-a", new Contract.RegisterRunnerRequest(
            "runner-a", "host-a", "instance-a", "test", Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor]));
        var key = Contract.CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol");
        var firstProbeAt = clock.GetUtcNow().UtcDateTime;

        Advertise(1, firstProbeAt);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol");
        clock.Advance(TimeSpan.FromSeconds(1));
        var stale = Advertise(2, firstProbeAt);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("unavailable", Assert.Single(stale.Capabilities).AdvertisedStatus);

        var sameInstant = Advertise(3, clock.GetUtcNow().UtcDateTime.AddSeconds(-1));
        Assert.Equal("unavailable", Assert.Single(sameInstant.Capabilities).AdvertisedStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = Advertise(4, clock.GetUtcNow().UtcDateTime);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("ready", Assert.Single(recovered.Capabilities).AdvertisedStatus);

        Contract.RunnerCapabilitySnapshotDto Advertise(long generation, DateTime observedAt)
            => registry.AdvertiseCapabilities("runner-a", new Contract.CapabilityAdvertisementRequest(
                "runner-a", "instance-a", Contract.CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime, 180, generation,
                [new Contract.AdvertisedCapabilityDto(key, "model-acceptance", "ready",
                    CredentialGeneration: "login-a", CredentialObservedAt: observedAt)]));
    }
}
