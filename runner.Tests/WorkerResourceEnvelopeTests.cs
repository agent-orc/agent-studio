using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2866 matrix: one slot budget, derived the same way for every consumer.
/// The reference host is agent-runner-01, 12 cores shared by 2 coding and
/// 2 review slots, which is the machine the three runaway runs of 17.09.2026
/// happened on.
/// </summary>
public sealed class WorkerResourceEnvelopeTests
{
    [Fact]
    public void Reference_host_grants_one_worker_its_fair_share_with_burst()
    {
        var envelope = WorkerResourceEnvelope.Compute(
            hostCores: 12,
            codingSlots: 2,
            reviewSlots: 2);

        Assert.Equal(4, envelope.TotalSlots);
        Assert.Equal(3.0, envelope.CoresPerSlot, 3);
        // 3 fair-share cores, burst 2x: a normal build keeps the host it had,
        // 24 busy loops do not.
        Assert.Equal(600, envelope.CpuQuotaPercent);
        Assert.Equal("600000 100000", envelope.CpuMax);
        Assert.Equal(384, envelope.TasksMax);
        Assert.Equal(WorkerResourceEnvelope.WorkerCpuWeight, envelope.CpuWeight);
    }

    [Theory]
    // Fewer declared slots mean a bigger share per slot, and the caps follow.
    [InlineData(12, 1, 1, 1200, 768)]
    [InlineData(12, 2, 2, 600, 384)]
    [InlineData(12, 4, 4, 300, 192)]
    [InlineData(24, 2, 2, 1200, 768)]
    public void Caps_scale_with_the_declared_slot_counts(
        int cores,
        int codingSlots,
        int reviewSlots,
        int expectedQuotaPercent,
        int expectedTasksMax)
    {
        var envelope = WorkerResourceEnvelope.Compute(cores, codingSlots, reviewSlots);

        Assert.Equal(expectedQuotaPercent, envelope.CpuQuotaPercent);
        Assert.Equal(expectedTasksMax, envelope.TasksMax);
    }

    [Fact]
    public void Quota_never_exceeds_the_host_and_never_drops_below_one_core()
    {
        var crowded = WorkerResourceEnvelope.Compute(hostCores: 2, codingSlots: 4, reviewSlots: 4);
        var lonely = WorkerResourceEnvelope.Compute(hostCores: 4, codingSlots: 1, reviewSlots: 0);

        Assert.Equal(WorkerResourceEnvelope.MinimumCpuQuotaPercent, crowded.CpuQuotaPercent);
        Assert.Equal(WorkerResourceEnvelope.MinimumTasksMax, crowded.TasksMax);
        // 4 cores, one slot, burst 2 would be 800%; a worker can never be
        // granted more CPU than the machine has.
        Assert.Equal(400, lonely.CpuQuotaPercent);
    }

    [Fact]
    public void Burst_is_the_idle_host_concession_and_is_clamped()
    {
        var none = WorkerResourceEnvelope.Compute(12, 2, 2, cpuBurst: 1.0);
        var absurd = WorkerResourceEnvelope.Compute(12, 2, 2, cpuBurst: 99);

        Assert.Equal(300, none.CpuQuotaPercent);
        // Clamped to MaximumCpuBurst (8x = 2400%) and then to the host itself.
        Assert.Equal(1200, absurd.CpuQuotaPercent);
    }

    [Fact]
    public void A_host_with_no_declared_slots_still_yields_a_usable_envelope()
    {
        var envelope = WorkerResourceEnvelope.Compute(hostCores: 8, codingSlots: 0, reviewSlots: 0);

        Assert.Equal(1, envelope.TotalSlots);
        Assert.Equal(800, envelope.CpuQuotaPercent);
    }

    [Fact]
    public void Options_carry_the_slot_budget_both_roles_read()
    {
        var options = RunnerOptionsFixture.WithSlots(codingSlots: 2, reviewSlots: 2, burst: 2.0);

        var envelope = WorkerResourceEnvelope.FromOptions(options);

        Assert.Equal(4, envelope.TotalSlots);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), envelope.HostCores);
    }

    [Fact]
    public void Slot_ceiling_is_bounded_by_the_cores_that_can_still_carry_an_envelope()
    {
        Assert.Equal(12, WorkerResourceEnvelope.SlotCeilingForCores(12));
        Assert.Equal(2, WorkerResourceEnvelope.SlotCeilingForCores(2));
        Assert.Equal(1, WorkerResourceEnvelope.SlotCeilingForCores(0));
    }
}
