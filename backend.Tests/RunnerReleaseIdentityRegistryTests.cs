using AgentStudio.Runner;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2826: the release identity reported in registration and in every
/// capability heartbeat has to survive into the snapshot Execution Hosts reads,
/// and an in-place host upgrade has to be visible from the heartbeat alone.
/// </summary>
public sealed class RunnerReleaseIdentityRegistryTests
{
    private static readonly Contract.RunnerReleaseIdentityDto Old = new(
        "agt-host-20260823T060000Z-bbbbbbb",
        "0.2.7",
        "bbbbbbb2222",
        new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc));

    private static readonly Contract.RunnerReleaseIdentityDto New = new(
        "agt-host-20260911T080000Z-aaaaaaa",
        "0.3.0",
        "aaaaaaa1111",
        new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void The_registered_release_identity_reaches_the_capability_snapshot()
    {
        var registry = new V1ReviewExecutorRegistry();
        Register(registry, Old);

        Advertise(registry, generation: 1, release: null);

        Assert.Equal(Old, Assert.Single(registry.ListCapabilitySnapshots()).Release);
    }

    [Fact]
    public void A_heartbeat_reporting_a_new_release_updates_the_snapshot()
    {
        var registry = new V1ReviewExecutorRegistry();
        Register(registry, Old);

        var advertised = Advertise(registry, generation: 1, release: New);

        Assert.Equal(New, advertised.Release);
        Assert.Equal(New, Assert.Single(registry.ListCapabilitySnapshots()).Release);
    }

    /// <summary>
    /// A daemon predating the field must not blank the host's release, which
    /// would read as "not reported" and hide a real drift.
    /// </summary>
    [Fact]
    public void A_heartbeat_without_the_field_keeps_the_last_known_release()
    {
        var registry = new V1ReviewExecutorRegistry();
        Register(registry, Old);
        Advertise(registry, generation: 1, release: New);

        Advertise(registry, generation: 2, release: null);

        Assert.Equal(New, Assert.Single(registry.ListCapabilitySnapshots()).Release);
    }

    [Fact]
    public void A_host_that_never_reported_a_release_stays_null()
    {
        var registry = new V1ReviewExecutorRegistry();
        Register(registry, release: null);

        Advertise(registry, generation: 1, release: null);

        Assert.Null(Assert.Single(registry.ListCapabilitySnapshots()).Release);
    }

    private static void Register(
        V1ReviewExecutorRegistry registry,
        Contract.RunnerReleaseIdentityDto? release)
        => registry.Register("runner-1", new Contract.RegisterRunnerRequest(
            "runner-1",
            "host-1",
            "instance-1",
            release?.ReleaseId ?? "unknown",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor],
            Release: release));

    private static Contract.RunnerCapabilitySnapshotDto Advertise(
        V1ReviewExecutorRegistry registry,
        long generation,
        Contract.RunnerReleaseIdentityDto? release)
        => registry.AdvertiseCapabilities("runner-1", new Contract.CapabilityAdvertisementRequest(
            "runner-1",
            "instance-1",
            Contract.CapabilityProtocol.CurrentSchemaVersion,
            DateTime.UtcNow,
            180,
            generation,
            [new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.CodingExecutor, "executor")],
            Release: release));
}
