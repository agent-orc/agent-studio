using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

public static class ReviewCheckoutRetryPolicy
{
    private const int MaximumAttempts = 3;

    public static bool Exhausted(string? classification, int attemptNumber)
        => classification == ReviewCheckoutReadPolicy.FailureClassification && attemptNumber >= MaximumAttempts;

    public static bool FailedOnHost(string? classification, string? failedHost, string claimingHost)
        => classification == ReviewCheckoutReadPolicy.FailureClassification
            && string.Equals(failedHost, claimingHost, StringComparison.Ordinal);
}
