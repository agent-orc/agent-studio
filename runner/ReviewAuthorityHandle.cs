using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// The single source of truth for one review slot's write authority while it
/// runs. The heartbeat may re-fence the attempt (adoption after a planned
/// restart, takeover after a refused renewal) concurrently with the executor
/// saving phase transitions, so both read the claim through this handle instead
/// of a captured copy.
/// </summary>
internal sealed class ReviewAuthorityHandle
{
    private readonly object _gate = new();
    private ReviewClaimResponse _claim;

    internal ReviewAuthorityHandle(ReviewClaimResponse claim)
    {
        ArgumentNullException.ThrowIfNull(claim.Lease);
        _claim = claim;
    }

    internal ReviewClaimResponse Claim
    {
        get { lock (_gate) return _claim; }
    }

    internal ReviewLeaseDto Lease => Claim.Lease!;

    internal ReviewAttemptDto Attempt => Claim.Attempt!;

    /// <summary>
    /// Adopts a freshly minted lease for the same immutable subject. The subject
    /// and its plan are never replaced: the detached worker is already executing
    /// them, and swapping the plan under a running worker would report evidence
    /// for work that was not performed.
    /// </summary>
    internal void Rebind(ReviewAttemptDto attempt, ReviewLeaseDto lease)
    {
        lock (_gate)
        {
            _claim = _claim with { Attempt = attempt, Lease = lease };
        }
    }
}
