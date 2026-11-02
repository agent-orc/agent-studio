using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3423: the fair gate-slot rule as a direct decision matrix.</summary>
public sealed class GateSlotFairnessPolicyTests
{
    private static TimeSpan Minutes(double value) => TimeSpan.FromMinutes(value);

    [Theory]
    // slots, class, waited minutes, expected slot order
    [InlineData(5, GateDurationClass.Short, 0, "4,0,1,2,3")]
    [InlineData(5, GateDurationClass.Long, 0, "0,1,2,3")]
    [InlineData(5, GateDurationClass.Long, 44.9, "0,1,2,3")]
    [InlineData(5, GateDurationClass.Long, 45, "0,1,2,3,4")]
    [InlineData(5, GateDurationClass.Long, 300, "0,1,2,3,4")]
    [InlineData(5, GateDurationClass.Unknown, 0, "0,1,2,3,4")]
    [InlineData(2, GateDurationClass.Short, 0, "1,0")]
    [InlineData(2, GateDurationClass.Long, 0, "0")]
    [InlineData(1, GateDurationClass.Short, 0, "0")]
    [InlineData(1, GateDurationClass.Long, 0, "0")]
    [InlineData(1, GateDurationClass.Long, 300, "0")]
    [InlineData(1, GateDurationClass.Unknown, 0, "0")]
    public void Eligible_slots_follow_the_matrix(int slots, GateDurationClass durationClass, double waited,
        string expected)
    {
        var eligibility = GateSlotFairnessPolicy.Eligible(slots, durationClass, Minutes(waited));

        Assert.Equal(expected, string.Join(',', eligibility.Slots));
        Assert.False(string.IsNullOrWhiteSpace(eligibility.Rule));
    }

    [Fact]
    public void Single_slot_has_no_short_slot_and_behaves_as_before()
    {
        Assert.Null(GateSlotFairnessPolicy.ReservedSlot(1));
        Assert.Null(GateSlotFairnessPolicy.Eligible(1, GateDurationClass.Long, TimeSpan.Zero).ReservedSlot);
        Assert.Equal("All 1 slot is busy.", GateSlotFairnessPolicy.WaitReason(1, GateDurationClass.Long, TimeSpan.Zero));
        Assert.Equal("One slot: every gate shares it in arrival order.", GateSlotFairnessPolicy.Describe(1));
        Assert.StartsWith("Slot 4 serves short gates", GateSlotFairnessPolicy.Describe(5), StringComparison.Ordinal);
    }

    [Fact]
    public void Short_gate_may_take_any_free_slot_so_it_never_waits_behind_a_free_shared_slot()
    {
        var eligibility = GateSlotFairnessPolicy.Eligible(3, GateDurationClass.Short, TimeSpan.Zero);

        Assert.Equal([2, 0, 1], eligibility.Slots);
        Assert.Equal(2, eligibility.ReservedSlot);
    }

    [Fact]
    public void Aged_long_gate_tries_the_shared_slots_before_the_short_slot()
    {
        var eligibility = GateSlotFairnessPolicy.Eligible(3, GateDurationClass.Long, GateSlotFairnessPolicy.AgeLimit);

        Assert.Equal([0, 1, 2], eligibility.Slots);
        Assert.Contains("open to it", eligibility.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Wait_reason_names_the_short_slot_and_when_it_opens_for_a_long_gate()
    {
        Assert.Equal(
            "All 4 shared slots are busy; slot 4 is kept for short gates and opens to this long gate in 35 min.",
            GateSlotFairnessPolicy.WaitReason(5, GateDurationClass.Long, Minutes(10)));
        Assert.Equal("All 5 slots are busy.", GateSlotFairnessPolicy.WaitReason(5, GateDurationClass.Short, Minutes(10)));
        Assert.Equal("All 5 slots are busy.", GateSlotFairnessPolicy.WaitReason(5, GateDurationClass.Long, Minutes(50)));
    }

    [Theory]
    [InlineData("", GateDurationClass.Long, null)]
    [InlineData("1,2", GateDurationClass.Long, null)]
    [InlineData("1,2,3", GateDurationClass.Short, 2.0)]
    [InlineData("1,10,60", GateDurationClass.Short, 10.0)]
    [InlineData("2,60,61", GateDurationClass.Long, 60.0)]
    [InlineData("1,2,60,61", GateDurationClass.Long, 31.0)]
    [InlineData("1,2,3,8", GateDurationClass.Short, 2.5)]
    // Only the last ten gates count: the old hour-long gates have aged out.
    [InlineData("60,60,60,60,60,1,1,1,1,1,1,1,1,1,1", GateDurationClass.Short, 1.0)]
    public void Classify_uses_the_median_of_recent_gates(string minutes, GateDurationClass expected, double? median)
    {
        var recent = minutes.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Minutes(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        var estimate = GateSlotFairnessPolicy.Classify(recent);

        Assert.Equal(expected, estimate.Class);
        Assert.Equal(median is null ? null : Minutes(median.Value), estimate.Median);
        Assert.Equal(Math.Min(recent.Length, GateSlotFairnessPolicy.HistoryLength), estimate.Samples);
    }
}
