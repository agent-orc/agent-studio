namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// AGT-3858: a deterministic batch red that no isolation child reproduces.
/// Ordered halving deduces the last member red when every tested subset
/// passed, so a flaky red used to send an innocent member to a per-card
/// suite. The cohort's own code is gated once more instead; a second red on
/// that code keeps the existing per-card path.
/// </summary>
public static class BatchFlakySuspectPolicy
{
    /// <summary>
    /// The red run is a flaky suspect when isolation ran at least one child,
    /// every child passed, and each child tested the cohort's own code: the
    /// same member result SHAs on the cohort's base or on a tip this isolation
    /// published from the cohort's green members.
    /// </summary>
    public static bool IsFlakySuspect(int childRuns, bool everyChildPassed, bool everyChildOnCohortCode)
        => childRuns > 0 && everyChildPassed && everyChildOnCohortCode;

    /// <summary>
    /// The members left over for per-card suites are gated together once. A
    /// red that was already re-gated, or an isolation retry, takes the
    /// existing path.
    /// </summary>
    public static bool RegatesCohort(bool flakySuspect, bool alreadyRegated, bool isolationRetry,
        int remainingMembers)
        => flakySuspect && !alreadyRegated && !isolationRetry && remainingMembers > 0;
}
