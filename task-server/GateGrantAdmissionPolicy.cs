namespace AgentStudio.TaskServer;

/// <summary>Host allocation facts a gate grant reservation reads (AGT-3019 D5 A).</summary>
internal sealed record GateHostAllocation(int MaxParallelism, long AllocationVersion, bool Acknowledged);

internal sealed record GateGrantAdmission(bool Admitted, bool ReservesGrant, string Reason, string? Message)
{
    public static GateGrantAdmission Slot() => new(true, false, "gate-slot", null);
    public static GateGrantAdmission Grant() => new(true, true, "host-grant", null);
    public static GateGrantAdmission Refused(string reason, string message) => new(false, false, reason, message);
}

/// <summary>
/// Pure gate admission (DP-S4). The fixed mode keeps the historical single
/// gate slot per host. The grant mode reserves one slot of the host's
/// versioned allocation, shared with coding leases, so a gate host spends the
/// same budget as every other role instead of a separate gate count.
/// </summary>
internal static class GateGrantAdmissionPolicy
{
    public const string SlotMode = "slot";
    public const string CanaryMode = "canary";
    public const string GrantMode = "grant";

    public static string Mode(string? configured) => configured?.Trim().ToLowerInvariant() switch
    {
        null or "" or SlotMode => SlotMode,
        CanaryMode => CanaryMode,
        GrantMode => GrantMode,
        _ => throw new InvalidOperationException(
            $"TaskServer:GateAdmissionMode must be '{SlotMode}', '{CanaryMode}' or '{GrantMode}'."),
    };

    public static bool UsesGrant(string? configured, IReadOnlyCollection<string> canaryHosts, string hostId)
        => Mode(configured) switch
        {
            GrantMode => true,
            CanaryMode => canaryHosts.Contains(hostId, StringComparer.Ordinal),
            _ => false,
        };

    public static GateGrantAdmission Decide(
        bool usesGrant,
        int liveGateAttemptsOnHost,
        GateHostAllocation? allocation,
        int occupiedHostGrants)
    {
        if (!usesGrant)
            return liveGateAttemptsOnHost >= 1
                ? GateGrantAdmission.Refused("gate-slot-occupied", "Gate host slot is occupied.")
                : GateGrantAdmission.Slot();
        if (allocation is null)
            return GateGrantAdmission.Refused("host-allocation-missing",
                "Gate host has no versioned host allocation to reserve a grant from.");
        if (!allocation.Acknowledged)
            return GateGrantAdmission.Refused("allocation-version-mismatch",
                $"Gate host has not acknowledged allocation version {allocation.AllocationVersion}.");
        if (occupiedHostGrants >= allocation.MaxParallelism)
            return GateGrantAdmission.Refused("host-grant-full",
                $"Host grants are fully reserved ({occupiedHostGrants}/{allocation.MaxParallelism}).");
        return GateGrantAdmission.Grant();
    }
}
