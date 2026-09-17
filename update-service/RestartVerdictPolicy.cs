namespace AgentTaskboard.UpdateService;

/// <summary>What the pipeline does after a restart, given what it observed.</summary>
public enum RestartVerdict
{
    /// <summary>Backend and frontend are both up; continue to the mutation boundary.</summary>
    Verified,

    /// <summary>
    /// The backend runs the candidate and the frontend does not answer. The
    /// run ends here without touching the checkout; the operator decides.
    /// </summary>
    Degraded,

    /// <summary>The backend itself failed; revert the checkout and fail the run.</summary>
    RollBack,
}

/// <summary>
/// The facts the post-restart decision is made from. <see cref="IdentityMatched"/>
/// is only meaningful when <see cref="IdentityRequired"/> is true (the legacy
/// branch-update path has no manifest to compare against).
/// </summary>
public sealed record RestartFacts(
    bool BackendHealthy,
    bool IdentityRequired,
    bool IdentityMatched,
    bool FrontendUp);

/// <summary>
/// AGT-2862. The v0.6.0 upgrade restarted the backend, cleared health and
/// runtime identity, then failed the frontend port check and rolled the
/// checkout back to v0.5.0 underneath a process that was already running the
/// v0.6.0 build (run 1027d2e7, 17.09.2026). Reverting the source of a running
/// backend is worse than the frontend being down: it desyncs HEAD from the
/// live process and needs manual repair.
///
/// So the rollback authority belongs to backend failures only. A frontend-only
/// failure after a healthy, correctly-identified backend is reported as
/// <see cref="RestartVerdict.Degraded"/> and leaves the checkout alone.
/// </summary>
public static class RestartVerdictPolicy
{
    public static RestartVerdict Decide(RestartFacts facts)
    {
        if (!facts.BackendHealthy) return RestartVerdict.RollBack;
        if (facts.IdentityRequired && !facts.IdentityMatched) return RestartVerdict.RollBack;
        return facts.FrontendUp ? RestartVerdict.Verified : RestartVerdict.Degraded;
    }
}
