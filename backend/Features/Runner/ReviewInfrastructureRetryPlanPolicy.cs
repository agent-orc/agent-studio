namespace AgentStudio.Runner;

/// <summary>
/// Decides whether a ReviewInfra retry may reuse its frozen plan. Preparation
/// failures are plan-sensitive: the source checkout or build profile may have
/// been repaired after the failed attempt, so retry admission must rebuild.
/// </summary>
public static class ReviewInfrastructureRetryPlanPolicy
{
    public const string PreparationFailed = "PreparationFailed";

    public static bool RequiresRebuild(string? failureClassification)
        => string.Equals(failureClassification, PreparationFailed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(failureClassification, "Environment", StringComparison.OrdinalIgnoreCase);
}
