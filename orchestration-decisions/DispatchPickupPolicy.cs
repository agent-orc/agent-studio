namespace AgentStudio.OrchestrationDecisions;

public sealed record DispatchPickupDecision(bool Admit, string Reason, string? WaitsFor = null,
    DateTimeOffset? ExpiresAt = null, bool Exclusive = false, int? Position = null);

/// <summary>Applies a separate, fenced plan without changing the owner's lane order or dependency edges.</summary>
public static class DispatchPickupPolicy
{
    public static DispatchPlan PreserveExclusive(DispatchPlan plan, IReadOnlyList<DispatchExclusiveMark> prior,
        IReadOnlySet<string> present)
        => plan with { ExclusiveMarks = prior.Concat(plan.ExclusiveMarks).Where(m => present.Contains(m.CardKey))
            .DistinctBy(m => m.CardKey, StringComparer.OrdinalIgnoreCase).ToArray() };

    public static DispatchPickupDecision Decide(DispatchPlan? plan, string card, DateTimeOffset now,
        bool authority, IReadOnlySet<string> satisfied)
    {
        if (!authority || plan is null) return new(false, "dispatch-authority-unavailable", "a live dispatcher lease");
        var entry = plan.Order.FirstOrDefault(e => e.CardKey.Equals(card, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new(false, "dispatch-plan-refresh", "a plan containing this card");
        var hold = plan.Holds.FirstOrDefault(h => h.CardKey.Equals(card, StringComparison.OrdinalIgnoreCase)
            && h.ExpiresAtUtc > now);
        if (hold is not null) return new(false, hold.ReasonCode, hold.WaitsFor, hold.ExpiresAtUtc, Position: entry.Position);
        var edge = plan.DependenciesToAdd.FirstOrDefault(e => e.CardKey.Equals(card, StringComparison.OrdinalIgnoreCase)
            && !satisfied.Contains(e.DependsOn));
        if (edge is not null) return new(false, edge.ReasonCode, edge.DependsOn, Position: entry.Position);
        if (entry.State == DispatchCandidateState.WaitsForDependency && edge is null
            && !plan.DependenciesToAdd.Any(e => e.CardKey.Equals(card, StringComparison.OrdinalIgnoreCase)))
            return new(false, "dependency-gate", "the card's dependencies", Position: entry.Position);
        var slot = plan.Slots.FirstOrDefault(s => s.CardKey?.Equals(card, StringComparison.OrdinalIgnoreCase) == true);
        if (slot is not null) return new(true, slot.ReasonCode, Exclusive: slot.RunsAlone, Position: entry.Position);
        var pass = plan.Slots.SelectMany(s => s.PassedOver)
            .FirstOrDefault(p => p.CardKey.Equals(card, StringComparison.OrdinalIgnoreCase));
        return new(false, pass?.ReasonCode ?? "dispatch-order", pass?.With
            ?? plan.Admitted.FirstOrDefault() ?? "a free project slot", Position: entry.Position);
    }
}
