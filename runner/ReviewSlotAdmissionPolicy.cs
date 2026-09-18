namespace AgentRunner;

public sealed record ReviewSlotAdmissionDecision(
    bool Admitted,
    string Reason,
    double? LoadPerCore,
    bool ActiveSlotsContinue = true);

/// <summary>
/// Immediate load-aware admission for new Review Executor slots. This policy
/// never owns or cancels an active review; it decides only whether the daemon
/// may ask the Task Server for one more attempt. ActiveSlotsContinue makes that
/// boundary explicit for restart-recovery and load-gate matrix tests.
/// </summary>
public static class ReviewSlotAdmissionPolicy
{
    public static ReviewSlotAdmissionDecision Decide(
        HostTelemetrySample? sample,
        int activeSlots,
        int slotCeiling,
        double maxLoadPerCore,
        WorkerResourceEnvelope? envelope = null,
        double? roleQuotaCores = null)
    {
        // AGT-2866: admission and enforcement read one budget. A centrally
        // recommended ceiling that no longer leaves a full envelope per slot is
        // clamped here rather than admitted and then throttled by the kernel,
        // and a host whose declared slot count is already oversubscribed says so
        // instead of blaming the current load.
        var envelopeCeiling = envelope is null
            ? slotCeiling
            : WorkerResourceEnvelope.SlotCeilingForCores(envelope.HostCores);
        var roleQuotaCeiling = roleQuotaCores is > 0
            ? ReviewPlaneCapacity.SupportedWorkers(roleQuotaCores.Value)
            : slotCeiling;
        var effectiveCeiling = Math.Min(slotCeiling, Math.Min(envelopeCeiling, roleQuotaCeiling));
        if (activeSlots >= effectiveCeiling)
            return new(
                false,
                roleQuotaCeiling < slotCeiling && roleQuotaCeiling <= envelopeCeiling
                    ? $"slot ceiling reached ({activeSlots}/{effectiveCeiling}); role quota "
                      + $"{roleQuotaCores:0.##} cores supports {roleQuotaCeiling} review workers "
                      + $"at {ReviewPlaneCapacity.MinimumCoresPerReview:0.##} cores each"
                    : effectiveCeiling == slotCeiling
                    ? $"slot ceiling reached ({activeSlots}/{slotCeiling})"
                    : $"slot ceiling reached ({activeSlots}/{effectiveCeiling}); "
                      + $"envelope budget clamped the configured ceiling {slotCeiling} "
                      + $"to what {envelope!.HostCores} cores can still cover",
                null);
        if (envelope is not null && envelope.CoresPerSlot < WorkerResourceEnvelope.MinimumCoresPerSlot)
            return new(
                false,
                $"envelope budget exhausted: {envelope.HostCores} cores across {envelope.TotalSlots} "
                + $"declared slots is {envelope.CoresPerSlot:0.00} cores per slot, below the "
                + $"{WorkerResourceEnvelope.MinimumCoresPerSlot:0.00} core floor; lower "
                + "RUNNER_HOST_CODING_SLOTS / RUNNER_HOST_REVIEW_SLOTS",
                null);
        if (sample?.Load1 is not { } load || sample.CpuCores <= 0)
            return new(false, "current load telemetry is unavailable", null);

        var normalized = load / sample.CpuCores;
        if (normalized >= maxLoadPerCore)
        {
            return new(
                false,
                $"load/core {normalized:0.00} is at or above {maxLoadPerCore:0.00}; "
                + $"cpu={Percent(sample.CpuPercent)} steal={Percent(sample.CpuStealPercent)}",
                normalized);
        }
        return new(
            true,
            $"load/core {normalized:0.00} is below {maxLoadPerCore:0.00}"
            + (envelope is null ? string.Empty : $"; envelope {envelope.CoresPerSlot:0.00} cores/slot"),
            normalized);
    }

    private static string Percent(double? value)
        => value is null ? "unknown" : $"{value:0.0}%";
}

public static class ReviewPlaneCapacity
{
    /// <summary>Minimum CPU supply required for one review worker.</summary>
    public const double MinimumCoresPerReview = 2.0;

    public static int SupportedWorkers(double planeCpuCores)
        => (int)Math.Floor(Math.Max(0, planeCpuCores) / MinimumCoresPerReview);
}
