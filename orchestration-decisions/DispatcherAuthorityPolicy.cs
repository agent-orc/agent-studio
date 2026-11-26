namespace AgentStudio.OrchestrationDecisions;

/// <summary>DS-5 narrow capability authority, independent of persona-team grant storage.</summary>
public sealed record DispatcherOperationFacts(string Operation, string Persona, bool Shadow,
    bool CurrentLease, bool StandingGrant, bool Packaging = false, string? EdgeOrigin = null,
    bool SameProject = true, bool UnstartedBacklogOrReady = true, bool Acyclic = true,
    bool AutomatedSource = true, bool SameDossier = true, bool Undo24Hours = true,
    bool WithinBounds = true, bool ExplicitT2 = false, DateTimeOffset? T2Expires = null,
    int T2UsesRemaining = 0, DateTimeOffset Now = default);

public static class DispatcherAuthorityPolicy
{
    public static bool Admits(DispatcherOperationFacts f)
    {
        if (f.Operation == "dispatch.shadow.record") return f.StandingGrant && f.Shadow;
        if (f.Shadow || !f.CurrentLease || !f.StandingGrant) return false;
        var t2 = f.ExplicitT2 && f.T2Expires > f.Now && f.T2UsesRemaining > 0;
        return f.Operation switch
        {
            "dispatch.plan.write" => f.WithinBounds,
            "dispatch.hold" or "dispatch.release" or "dispatch.exclusive.set" => f.WithinBounds || t2,
            "dispatch.dependency.add" => f.SameProject && f.UnstartedBacklogOrReady && f.Acyclic,
            "dispatch.dependency.remove" => MayRemoveDependency(f.Persona, f.EdgeOrigin, t2),
            "dispatch.recut.propose" => f.Packaging,
            "dispatch.recut.apply" => f.Packaging && f.SameProject && ((f.UnstartedBacklogOrReady
                && f.AutomatedSource && f.SameDossier && f.Undo24Hours && f.WithinBounds) || t2),
            _ => false,
        };
    }

    /// <summary>A redeemed T2 receipt already passed expiry and use-cap checks at the request boundary.</summary>
    public static bool MayRemoveDependency(string persona, string? origin, bool redeemedT2)
        => origin == "dispatch:" + persona || redeemedT2;

    public static bool MayAcquireLease(string persona, bool Active, bool LiveGrants,
        bool ShadowGatePassed, bool CurrentLeaseExpired, long ExpectedFence, long CurrentFence)
        => Active && LiveGrants && (persona == "queue-keeper" || ShadowGatePassed)
           && CurrentLeaseExpired && ExpectedFence == CurrentFence;
}
