namespace AgentStudio.Security;

/// <summary>
/// The identity side of persona activation (persona team PT-1, decision
/// PT-D1). The persona store owns activations and forks; this store decides
/// who may change them and keeps the record. Activating a persona issues
/// standing grants in a later slice, so it needs an active Owner with a fresh,
/// single-use step-up like <see cref="CreateStandingGrant"/>; pausing,
/// resuming, deactivating, and forking need the Owner only, because taking
/// authority away never waits for a second factor. Every change is written to
/// the IAM audit with target kind <c>persona</c>.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const string PersonaAuditTargetKind = "persona";

    /// <summary>Refuses anyone but an active Owner with <c>403 owner-required</c>.</summary>
    public void RequirePersonaOwner(HumanPrincipal principal)
    {
        lock (_gate)
            RequireActivePersonaOwnerLocked(StateLocked(), principal.User.Id);
    }

    /// <summary>
    /// Admits an active Owner whose session verified a second factor within the
    /// last five minutes and consumes that step-up; otherwise refuses with
    /// <c>403 owner-required</c> or <c>403 step-up-required</c>.
    /// </summary>
    public void ConsumePersonaActivationStepUp(HumanPrincipal owner)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActivePersonaOwnerLocked(next, owner.User.Id);
            ConsumeStepUpLocked(next, owner, "Verify a second factor before activating a persona.");
            SaveLocked(next);
        }
    }

    /// <summary>Appends one persona change to the IAM audit and the control plane audit chain.</summary>
    public void AppendPersonaAudit(string actingUserId, string action, string workspaceId, string personaId, object details)
    {
        lock (_gate)
            AppendIamAudit(actingUserId, action, PersonaAuditTargetKind, $"{workspaceId}/{personaId}", details);
    }

    private static void RequireActivePersonaOwnerLocked(SecurityState state, string? userId)
    {
        if (userId is null || !ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups).Contains(userId))
            throw new SecurityOperationException(403, "owner-required", "Only the Owner activates and manages personas.");
    }
}
