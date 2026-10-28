using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public enum IntegrationResultAction { HumanReview, Requeue, Escalate }

public sealed record IntegrationResultDecision(
    IntegrationResultAction Action,
    string? TaskState,
    string Reason);

/// <summary>
/// Lane effect of one reported integration of an accepted delivery (AGT-3061,
/// AGT-W65 I11). The accepted card waits in Auto Review until its integration
/// settles: a published or already contained result moves it to Human Review,
/// an integration tip that moved under the publication lease is retried a
/// bounded number of times, and a conflict or a spent retry budget asks an
/// operator. Hosts report the outcome; this policy owns the lane.
/// </summary>
public static class IntegrationResultPolicy
{
    public const int MaximumStaleBaseAttempts = 3;

    public static IntegrationResultDecision Decide(string outcome, int attemptsUsed,
        int maximumAttempts = MaximumStaleBaseAttempts)
    {
        var normalized = (outcome ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is IntegrationOutcomeKinds.Integrated or IntegrationOutcomeKinds.AlreadyIntegrated)
            return new(IntegrationResultAction.HumanReview, TaskStates.HumanReview,
                normalized == IntegrationOutcomeKinds.Integrated
                    ? "The accepted delivery was published to the integration ref."
                    : "The integration ref already contains the accepted delivery.");
        if (normalized == IntegrationOutcomeKinds.StaleBase && attemptsUsed < maximumAttempts)
            return new(IntegrationResultAction.Requeue, null,
                $"The integration ref moved during publication; retry {attemptsUsed + 1} of {maximumAttempts} on the new tip.");
        if (normalized == IntegrationOutcomeKinds.BlockedFailingArea)
            return new(IntegrationResultAction.Requeue, null,
                "This delivery overlaps the failing area of red develop; wait for the repair while unrelated deliveries continue.");
        var reason = normalized switch
        {
            IntegrationOutcomeKinds.Conflict => "The accepted delivery conflicts with the integration ref.",
            IntegrationOutcomeKinds.StaleBase => $"The integration ref kept moving; {attemptsUsed} publication attempts are spent.",
            _ => "The integration of the accepted delivery failed.",
        };
        return new(IntegrationResultAction.Escalate, TaskStates.Escalated, reason);
    }
}
