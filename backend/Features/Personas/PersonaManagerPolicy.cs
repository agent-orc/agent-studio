using AgentStudio.Shared;

namespace AgentStudio.Personas;

public sealed class PersonaManagerRefusal(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class PersonaManagerPolicy
{
    public static PersonaGroundsV1 Grounds(PersonaOccurrenceReceipt receipt,
        PersonaActivationV1 activation, IReadOnlyList<PersonaItemV1> items)
    {
        var typed = items.Where(i => PersonaItemStates.IsDecidable(i.State))
            .OrderByDescending(i => i.Importance).ThenBy(i => i.CreatedAtUtc).Take(100)
            .Select(i => new PersonaManagerGroundItemV1(i.Id, i.Kind, i.Impact, i.Urgency,
                i.Confidence, i.Floor, i.Importance, i.PersonaId, i.Fingerprint,
                i.State, i.Evidence.Select(e => e.Ref).Take(10).ToArray())).ToArray();
        return new PersonaGroundsV1(1, receipt.Key, receipt.PersonaId, receipt.RoleId,
            receipt.WorkspaceId, receipt.DescriptorRevision, activation.DescriptorDigest,
            typed.Select(i => new PersonaGroundsSourceV1("item:" + i.Id, "typed-item",
                null, 0, "", false, i.Floor, Fingerprint: i.Fingerprint)).ToArray(),
            receipt.TriggerRefs.Take(50).ToArray())
        {
            ActivationRevision = activation.ActivationRevision, ManagerItems = typed,
            AllowedProjects = [], AllowedAreas = activation.Scope.Areas,
        };
    }

    public static void RequireRole(string roleId, string operation)
    {
        var chief = operation is "persona.importance.adjust" or "persona.proposal.accept" or "card.requeue";
        var steward = operation is "persona.budget.rebalance" or "persona.pause";
        if (chief && roleId != "chief-of-staff" || steward && roleId != "budget-steward" || !chief && !steward)
            throw new PersonaManagerRefusal("manager-role-forbidden");
    }

    public static void RequireMove(TaskInfo card, string target, int? targetIndex, int movesToday)
    {
        if (movesToday >= 5) throw new PersonaManagerRefusal("daily-move-limit");
        if (card.State is not (TaskStates.Backlog or TaskStates.Ready)
            || target is not (TaskStates.Backlog or TaskStates.Ready)
            || target == card.State && targetIndex is null)
            throw new PersonaManagerRefusal("manager-lane-forbidden");
        if (targetIndex is < 0) throw new PersonaManagerRefusal("manager-order-invalid");
        if (!card.CreationSource.StartsWith("persona-proposal:", StringComparison.Ordinal)
            && !card.Tags.Contains("watcher-proposal", StringComparer.Ordinal))
            throw new PersonaManagerRefusal("proposal-origin-required");
    }

    public static void RequirePromotion(PersonaItemV1 item, string actorPersonaId, int promotionsToday)
    {
        if (promotionsToday >= 3) throw new PersonaManagerRefusal("daily-promotion-limit");
        if (item.Kind != PersonaItemKinds.Proposal || !PersonaItemStates.IsDecidable(item.State))
            throw new PersonaManagerRefusal("proposal-required");
        if (item.PersonaId == actorPersonaId) throw new PersonaManagerRefusal("own-draft-forbidden");
    }

    public static void RequireRebalance(int amount, int fromAllowance, int toAllowance,
        int fromMovedThisWeek, int toMovedThisWeek)
    {
        if (amount <= 0 || fromAllowance <= 0 || toAllowance <= 0
            || fromMovedThisWeek + amount > Math.Floor(fromAllowance * 0.25)
            || toMovedThisWeek + amount > Math.Floor(toAllowance * 0.25))
            throw new PersonaManagerRefusal("weekly-rebalance-limit");
    }
}
