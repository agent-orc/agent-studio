using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.TaskServer;

/// <summary>
/// Classifies what the integration executor could publish for an accepted
/// delivery (AGT-3061): the fenced result of the coding RunAttempt, always
/// fetched from and pushed to the registered project repository. A result
/// without a ref and SHA (source bundle only) or recorded against another
/// remote cannot be published; the settlement policy escalates it. No I/O
/// happens here.
/// </summary>
public static class IntegrationSourcePolicy
{
    public static IntegrationSource Classify(
        string? registeredRepositoryUrl,
        string? sourceRunId,
        string? runRepositoryUrl,
        string? resultRef,
        string? resultSha)
    {
        if (string.IsNullOrWhiteSpace(registeredRepositoryUrl) || string.IsNullOrWhiteSpace(sourceRunId)
            || string.IsNullOrWhiteSpace(resultRef) || string.IsNullOrWhiteSpace(resultSha))
            return IntegrationSource.ResultMissing;
        // A remote that is not even canonical is never the registered one.
        if (!string.IsNullOrWhiteSpace(runRepositoryUrl)
            && !ProjectRepositoryPolicy.SameOrigin(registeredRepositoryUrl, runRepositoryUrl))
            return IntegrationSource.RepositoryMismatch;
        return IntegrationSource.Publishable;
    }
}
