using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewSlotAdmissionPolicyTests
{
    [Theory]
    [InlineData(8.99, 6, 1.5, true)]
    [InlineData(9.00, 6, 1.5, false)]
    [InlineData(31.0, 6, 1.5, false)]
    public void New_slots_require_load_strictly_below_the_core_threshold(
        double load,
        int cores,
        double threshold,
        bool expected)
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(load, cores, activeSlots: 2),
            activeSlots: 2,
            slotCeiling: 4,
            threshold);

        Assert.Equal(expected, decision.Admitted);
        Assert.InRange(
            Math.Abs(load / cores - decision.LoadPerCore!.Value),
            0,
            0.001);
    }

    [Fact]
    public void Missing_load_or_core_evidence_fails_closed()
    {
        var missingLoad = ReviewSlotAdmissionPolicy.Decide(
            Sample(null, 6, activeSlots: 1),
            activeSlots: 1,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);
        var missingSample = ReviewSlotAdmissionPolicy.Decide(
            null,
            activeSlots: 1,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);

        Assert.False(missingLoad.Admitted);
        Assert.False(missingSample.Admitted);
        Assert.Contains("telemetry", missingLoad.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telemetry", missingSample.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Slot_ceiling_remains_a_hard_upper_bound()
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(0.1, 6, activeSlots: 4),
            activeSlots: 4,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);

        Assert.False(decision.Admitted);
        Assert.Contains("ceiling", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_load_closes_only_fresh_admission_while_a_recovered_slot_remains_active()
    {
        const int recoveredSlots = 1;

        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(load: 12, cores: 4, activeSlots: recoveredSlots),
            activeSlots: recoveredSlots,
            slotCeiling: 2,
            maxLoadPerCore: 1.5);

        Assert.False(decision.Admitted);
        Assert.True(decision.ActiveSlotsContinue);
        Assert.Contains("load/core", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// AGT-2866 deliverable 3: admission reads the same budget the per-worker
    /// cgroup is derived from. A 12-core host with 2 coding and 2 review slots
    /// offers 3 cores per slot, and admission says so.
    /// </summary>
    [Fact]
    public void Admission_quotes_the_same_slot_budget_the_envelope_is_derived_from()
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(0.5, 12, activeSlots: 1),
            activeSlots: 1,
            slotCeiling: 4,
            maxLoadPerCore: 1.5,
            WorkerResourceEnvelope.Compute(hostCores: 12, codingSlots: 2, reviewSlots: 2));

        Assert.True(decision.Admitted);
        Assert.Contains("3.00 cores/slot", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_centrally_raised_ceiling_cannot_exceed_the_cores_that_carry_it()
    {
        // Two cores can carry two envelopes, whatever the adaptive advisor
        // recommends. Active slots are never cancelled by the clamp.
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(0.1, 2, activeSlots: 2),
            activeSlots: 2,
            slotCeiling: 6,
            maxLoadPerCore: 1.5,
            WorkerResourceEnvelope.Compute(hostCores: 2, codingSlots: 1, reviewSlots: 1));

        Assert.False(decision.Admitted);
        Assert.True(decision.ActiveSlotsContinue);
        Assert.Contains("envelope budget clamped", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversubscribed_slot_declaration_is_named_instead_of_blamed_on_load()
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(0.1, 4, activeSlots: 0),
            activeSlots: 0,
            slotCeiling: 4,
            maxLoadPerCore: 1.5,
            WorkerResourceEnvelope.Compute(hostCores: 4, codingSlots: 4, reviewSlots: 4));

        Assert.False(decision.Admitted);
        Assert.Contains("envelope budget exhausted", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("RUNNER_HOST_CODING_SLOTS", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_an_envelope_the_pre_agt_2866_decision_is_unchanged()
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(0.5, 12, activeSlots: 1),
            activeSlots: 1,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);

        Assert.True(decision.Admitted);
        Assert.DoesNotContain("envelope", decision.Reason, StringComparison.Ordinal);
    }

    private static HostTelemetrySample Sample(double? load, int cores, int activeSlots)
        => new(
            DateTime.UtcNow,
            CpuPercent: 75,
            Load1: load,
            Load5: load,
            Load15: load,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: 0,
            IoWaitPercent: 0,
            CpuCores: cores,
            ActiveSlots: activeSlots);
}
