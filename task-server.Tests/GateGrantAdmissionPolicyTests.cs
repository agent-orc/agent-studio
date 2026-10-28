using AgentStudio.TaskServer;
using Xunit;

namespace TaskServer.Tests;

public sealed class GateGrantAdmissionPolicyTests
{
    [Theory]
    [InlineData(0, true, "gate-slot")]
    [InlineData(1, false, "gate-slot-occupied")]
    [InlineData(3, false, "gate-slot-occupied")]
    public void Slot_mode_keeps_one_fixed_gate_per_host(int liveGates, bool admitted, string reason)
    {
        var decision = GateGrantAdmissionPolicy.Decide(false, liveGates,
            new GateHostAllocation(8, 1, true), occupiedHostGrants: 0);

        Assert.Equal(admitted, decision.Admitted);
        Assert.False(decision.ReservesGrant);
        Assert.Equal(reason, decision.Reason);
    }

    public static TheoryData<int, bool?, long, int, bool, string> GrantCases => new()
    {
        // live gates, acknowledged (null = no allocation), version, occupied grants, admitted, reason
        { 0, null, 0, 0, false, "host-allocation-missing" },
        { 0, false, 4, 0, false, "allocation-version-mismatch" },
        { 0, true, 4, 2, false, "host-grant-full" },
        { 0, true, 4, 3, false, "host-grant-full" },
        { 0, true, 4, 1, true, "host-grant" },
        // A second gate on one host is admitted when the shared budget has room.
        { 1, true, 4, 1, true, "host-grant" },
    };

    [Theory]
    [MemberData(nameof(GrantCases))]
    public void Grant_mode_reserves_from_the_shared_versioned_allocation(int liveGates, bool? acknowledged,
        long version, int occupiedGrants, bool admitted, string reason)
    {
        var allocation = acknowledged is null ? null : new GateHostAllocation(2, version, acknowledged.Value);

        var decision = GateGrantAdmissionPolicy.Decide(true, liveGates, allocation, occupiedGrants);

        Assert.Equal(admitted, decision.Admitted);
        Assert.Equal(admitted, decision.ReservesGrant);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(null, "host-a", false)]
    [InlineData("slot", "host-a", false)]
    [InlineData("canary", "host-a", true)]
    [InlineData("canary", "host-b", false)]
    [InlineData("grant", "host-b", true)]
    [InlineData("GRANT", "host-b", true)]
    public void Switch_selects_grant_admission_per_host(string? mode, string host, bool usesGrant)
        => Assert.Equal(usesGrant, GateGrantAdmissionPolicy.UsesGrant(mode, ["host-a"], host));

    [Fact]
    public void Unknown_switch_value_fails_closed()
        => Assert.Throws<InvalidOperationException>(() => GateGrantAdmissionPolicy.Mode("fixed"));
}
