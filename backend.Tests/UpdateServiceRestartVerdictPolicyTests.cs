extern alias UpdSvc;

using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2862. Direct matrix for the post-restart decision: who may roll the
/// checkout back, and what happens when only the frontend is missing.
///
/// The rule the incident produced is that rollback authority belongs to
/// backend failures alone. The v0.6.0 run reverted the checkout to v0.5.0
/// after a frontend-only failure while the v0.6.0 backend was already
/// serving, which left HEAD and the live process disagreeing and needed
/// manual repair (run 1027d2e7, 17.09.2026).
/// </summary>
public class UpdateServiceRestartVerdictPolicyTests
{
    /// <summary>A dead backend is rolled back no matter what else is true.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void UnhealthyBackend_RollsBack(bool identityRequired, bool identityMatched, bool frontendUp)
    {
        var verdict = RestartVerdictPolicy.Decide(new RestartFacts(
            BackendHealthy: false,
            IdentityRequired: identityRequired,
            IdentityMatched: identityMatched,
            FrontendUp: frontendUp));

        Assert.Equal(RestartVerdict.RollBack, verdict);
    }

    /// <summary>
    /// A healthy backend on the wrong identity is not the candidate, so the
    /// checkout it was built from is not worth keeping either.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HealthyBackendOnTheWrongIdentity_RollsBack(bool frontendUp)
    {
        var verdict = RestartVerdictPolicy.Decide(new RestartFacts(
            BackendHealthy: true,
            IdentityRequired: true,
            IdentityMatched: false,
            FrontendUp: frontendUp));

        Assert.Equal(RestartVerdict.RollBack, verdict);
    }

    /// <summary>The whole stack is up: the run continues to the mutation boundary.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HealthyBackendAndFrontendUp_IsVerified(bool identityRequired, bool identityMatched)
    {
        var verdict = RestartVerdictPolicy.Decide(new RestartFacts(
            BackendHealthy: true,
            IdentityRequired: identityRequired,
            IdentityMatched: identityMatched,
            FrontendUp: true));

        Assert.Equal(RestartVerdict.Verified, verdict);
    }

    /// <summary>
    /// The incident case. The backend is the candidate and only the frontend
    /// is missing, so nothing may touch the checkout under it.
    /// </summary>
    [Fact]
    public void CandidateBackendUpAndFrontendDown_IsDegraded()
    {
        var verdict = RestartVerdictPolicy.Decide(new RestartFacts(
            BackendHealthy: true,
            IdentityRequired: true,
            IdentityMatched: true,
            FrontendUp: false));

        Assert.Equal(RestartVerdict.Degraded, verdict);
    }

    /// <summary>
    /// The legacy branch-update path has no manifest to compare, so a healthy
    /// backend is the only identity statement available. A frontend-only
    /// failure there is degraded for the same reason.
    /// </summary>
    [Fact]
    public void LegacyPathWithNoIdentityCheckAndFrontendDown_IsDegraded()
    {
        var verdict = RestartVerdictPolicy.Decide(new RestartFacts(
            BackendHealthy: true,
            IdentityRequired: false,
            IdentityMatched: false,
            FrontendUp: false));

        Assert.Equal(RestartVerdict.Degraded, verdict);
    }
}
