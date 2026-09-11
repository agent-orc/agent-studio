namespace AgentStudio.Clients;

/// <summary>
/// Pure eligibility policy for permanently deleting a retired client identity.
/// Shared by the single-delete endpoint and the bulk retired-purge sweep so
/// both enforce the exact same guards. Facts (lease and attempt state) are
/// gathered by the caller; this type only decides.
/// </summary>
public static class ClientDeletionPolicy
{
    public static ClientDeletionDecision Evaluate(
        ClientIdentity identity,
        bool hasActiveLease,
        bool hasUnresolvedAttempt)
    {
        if (identity.Kind != ClientIdentityKind.Retired)
        {
            return new ClientDeletionDecision(
                false,
                ClientDeletionRefusal.NotRetired,
                "The client must be retired before it can be permanently deleted.");
        }

        if (identity.RunnerActiveSlots.GetValueOrDefault() > 0
            || string.Equals(identity.RunnerDaemonState, "running", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientDeletionDecision(
                false,
                ClientDeletionRefusal.Online,
                "The runner is still reporting active work. Wait for it to go idle, then retry.");
        }

        // An expired-but-unresolved lease means the last known attempt's
        // process fate was never confirmed - the local analogue of the
        // standalone Task Server's `process-unknown` lease state. Refuse
        // before an ordinary still-live lease so the more specific reason
        // reaches the operator.
        if (hasUnresolvedAttempt)
        {
            return new ClientDeletionDecision(
                false,
                ClientDeletionRefusal.ProcessUnknown,
                "An attempt lease for this client expired without a confirmed outcome. Resolve it before deleting.");
        }

        if (hasActiveLease)
        {
            return new ClientDeletionDecision(
                false,
                ClientDeletionRefusal.ActiveLease,
                "The client still holds an active run lease.");
        }

        return ClientDeletionDecision.Allow;
    }
}

public enum ClientDeletionRefusal
{
    NotRetired,
    Online,
    ActiveLease,
    ProcessUnknown,
}

public sealed record ClientDeletionDecision(bool Allowed, ClientDeletionRefusal? Refusal, string? Message)
{
    public static readonly ClientDeletionDecision Allow = new(true, null, null);

    public string RefusalCode => Refusal switch
    {
        ClientDeletionRefusal.NotRetired => "client-must-be-retired-before-delete",
        ClientDeletionRefusal.Online => "client-online",
        ClientDeletionRefusal.ActiveLease => "client-has-active-lease",
        ClientDeletionRefusal.ProcessUnknown => "client-attempt-process-unknown",
        _ => "client-deletion-allowed",
    };
}
