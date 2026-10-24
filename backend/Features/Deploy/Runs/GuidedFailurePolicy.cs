namespace AgentStudio.Deploy;

/// <summary>Pure transition checks shared by host reconciliation and operator decisions.</summary>
public static class GuidedFailurePolicy
{
    public static bool ShouldPause(bool enabled, string jobState, string trigger)
        => enabled && trigger != DeploymentTriggers.AutomaticRollback && jobState == DeploymentStates.Failed;

    public static string? RejectDecision(string state, string action, bool approver, string? reason)
    {
        if (state != DeploymentStates.WaitingForIntervention) return "This run is not waiting for intervention.";
        if (!approver) return "Only an Owner or Maintainer may decide an intervention.";
        if (action is not ("retry" or "skip" or "abort")) return "Choose retry, skip, or abort.";
        if (action == "skip" && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500))
            return "Skipping a step requires a reason of at most 500 characters.";
        return null;
    }
}
