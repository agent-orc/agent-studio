using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Dispatch;

public sealed partial class DispatchStore
{
    /// <summary>One atomic file stores both scheduler state and lease. All pickup plans are fenced by this receipt.</summary>
    public DispatchLeaseV1? ApplyLeaseDecision(string project, long expectedFence, DispatchLeaseDecision decision,
        DateTimeOffset now, string actor)
    {
        lock (PersonaGate(project))
        {
            var old = ReadLease(project);
            if (!decision.Allowed || (old?.Fence ?? 0) != expectedFence) return null;
            if (old is not null && old.Persona == decision.Persona && old.Window == decision.Window
                && old.ExpiresAt > now.AddHours(1) && old.Rollout == decision.State) return old;
            var lease = new DispatchLeaseV1(project, decision.Persona, expectedFence + 1, now.AddHours(24))
            { Rollout = decision.State, Actor = actor, Reason = decision.Reason, Window = decision.Window };
            Write(Path.Combine(Project(project), "lease.json"), lease);
            return lease;
        }
    }

    public DispatchRunDecisionV1? ConflictAdmission(string project, DispatchConflictRecordV1 conflict)
    {
        if (conflict.Card is not null && conflict.PartnerCards.Prepend(conflict.Card).Any(card =>
            !conflict.DispatchDecisionIds.TryGetValue(card, out var id) || id is null)) return null;
        var admitted = ReadRunDecisions(project).Where(r => r.Claimed && r.Admitted && r.At <= conflict.At)
            .Where(r => conflict.DispatchDecisionIds.TryGetValue(r.Card, out var id) && id == r.DecisionId)
            .GroupBy(r => r.Card, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(r => r.At).First()).ToArray();
        var later = MonolithTransitionAdapter.DispatchLaterCard(admitted.Select(r => (r.Card, r.At)).ToArray());
        return admitted.FirstOrDefault(r => r.Card == later);
    }
}
