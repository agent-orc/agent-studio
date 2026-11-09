namespace AgentStudio.Watcher;

public static class ProposalRecoveryPolicy
{
    public static bool ShouldSupersede(StallInterventionRecord proposal, WatcherCase? current)
        => proposal.Status == StallInterventionStatuses.Proposed
            && proposal.Kind == StallKinds.HostOffline
            && current is { State: WatcherCaseStates.Resolved }
            && current.LastSeenUtc > proposal.DecidedAtUtc;
}
