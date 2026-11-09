namespace AgentStudio.Pipeline;

/// <summary>How long a gate is expected to hold its slot, from its project's recent gate history.</summary>
public enum GateDurationClass
{
    /// <summary>No classification was sent (local gates, older dispatchers): every slot, first come.</summary>
    Unknown,
    Short,
    Long,
}

public sealed record GateDurationEstimate(GateDurationClass Class, TimeSpan? Median, int Samples);

/// <summary>The slots a gate may try now, in the order it tries them, and the rule that chose them.</summary>
public sealed record GateSlotEligibility(IReadOnlyList<int> Slots, int? ReservedSlot, string Rule);

/// <summary>
/// The fair gate-slot rule (AGT-3423). With more than one slot, the last slot
/// serves gates whose expected duration is short; the other slots keep
/// first-come order and a short gate may take any free slot. A long gate that
/// has waited <see cref="AgeLimit"/> may take the short slot too, so no gate
/// starves. With one slot, every gate shares it first come, as before.
/// </summary>
public static class GateSlotFairnessPolicy
{
    /// <summary>A project whose median recent gate holds its slot at most this long is short.</summary>
    public static readonly TimeSpan ShortLimit = TimeSpan.FromMinutes(10);

    /// <summary>After this wait a long gate may also take the short slot.</summary>
    public static readonly TimeSpan AgeLimit = TimeSpan.FromMinutes(45);

    /// <summary>Fewer recent gates than this prove nothing; the gate counts as long.</summary>
    public const int MinimumSamples = 3;

    /// <summary>How many recent gates of a project the estimate reads.</summary>
    public const int HistoryLength = 10;

    public static GateDurationEstimate Classify(IReadOnlyList<TimeSpan> recent)
    {
        var window = recent.Count > HistoryLength ? recent.Skip(recent.Count - HistoryLength).ToArray() : recent;
        if (window.Count < MinimumSamples) return new(GateDurationClass.Long, null, window.Count);
        var sorted = window.OrderBy(duration => duration).ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new(median <= ShortLimit ? GateDurationClass.Short : GateDurationClass.Long, median, window.Count);
    }

    public static int? ReservedSlot(int slotCount) => slotCount > 1 ? slotCount - 1 : null;

    public static GateSlotEligibility Eligible(int slotCount, GateDurationClass durationClass, TimeSpan waited)
    {
        if (slotCount < 1) throw new ArgumentOutOfRangeException(nameof(slotCount));
        var all = Enumerable.Range(0, slotCount).ToArray();
        if (ReservedSlot(slotCount) is not { } reserved)
            return new(all, null, "One slot: every gate shares it in arrival order.");
        var general = all.Where(slot => slot != reserved).ToArray();
        return durationClass switch
        {
            GateDurationClass.Short => new([reserved, .. general], reserved,
                $"Short gate: tries the short slot {reserved} first, then any free slot."),
            GateDurationClass.Long when waited >= AgeLimit => new([.. general, reserved], reserved,
                $"Long gate waited {waited.TotalMinutes:F0} min: the short slot {reserved} is open to it too."),
            GateDurationClass.Long => new(general, reserved,
                $"Long gate: uses the shared slots; slot {reserved} is kept for short gates for the first {AgeLimit.TotalMinutes:F0} min of a wait."),
            _ => new(all, reserved, "Unclassified gate: every slot in arrival order."),
        };
    }

    /// <summary>The rule for <paramref name="slotCount"/> slots in one sentence, for the operator.</summary>
    public static string Describe(int slotCount) => ReservedSlot(slotCount) is { } reserved
        ? $"Slot {reserved} serves short gates (median of recent gates at most {ShortLimit.TotalMinutes:F0} min);"
            + $" the other slots go in arrival order, a short gate may take any free slot, and a long gate may take slot {reserved}"
            + $" after waiting {AgeLimit.TotalMinutes:F0} min."
        : "One slot: every gate shares it in arrival order.";

    /// <summary>Why a gate waits, for the operator: what is busy and which rule keeps it out.</summary>
    public static string WaitReason(int slotCount, GateDurationClass durationClass, TimeSpan waited)
    {
        var eligibility = Eligible(slotCount, durationClass, waited);
        if (eligibility.Slots.Count == slotCount)
            return $"All {slotCount} slot{(slotCount == 1 ? " is" : "s are")} busy.";
        var left = AgeLimit - waited;
        return $"All {eligibility.Slots.Count} shared slots are busy; slot {eligibility.ReservedSlot} is kept for short gates"
            + $" and opens to this long gate in {Math.Max(1, Math.Ceiling(left.TotalMinutes)):F0} min.";
    }
}
