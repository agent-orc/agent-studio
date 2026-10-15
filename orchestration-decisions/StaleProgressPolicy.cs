namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Facts for one stale <c>3-progress</c> card that still carries a
/// <c>task.json</c>. Folders without one are not cards; the authority removes or
/// archives that debris as filesystem recovery and never asks for a decision.
/// </summary>
/// <param name="IsActiveJob">The local runner reports this card as its active job.</param>
/// <param name="SecondsSinceActivity">Seconds since the last run-produced activity.</param>
/// <param name="ResumeWindowSeconds">The progress-first resume window.</param>
/// <param name="CompletionSentinel">The completion sentinel keyword found in the log tail, or null.</param>
public sealed record StaleProgressFacts(
    bool IsActiveJob,
    double SecondsSinceActivity,
    double ResumeWindowSeconds,
    string? CompletionSentinel);

public enum StaleProgressAction
{
    /// <summary>The card is the active job or still inside the resume window.</summary>
    Leave,

    /// <summary>A completion sentinel survived; finish the missed move to Auto Review.</summary>
    RecoverToReview,

    /// <summary>The run was interrupted without a sentinel; requeue the same card to Ready.</summary>
    RequeueToReady,
}

public sealed record StaleProgressVerdict(StaleProgressAction Action, string ReasonCode, string Detail);

public static class StaleProgressReasons
{
    public const string ActiveJob = "matches-active-job";
    public const string WithinResumeWindow = "within-resume-window";
    public const string SentinelSurvived = "sentinel-survived";
    public const string RunInterrupted = "run-interrupted";
}

/// <summary>
/// Pure stale-progress decision (formerly inline in the monolith's
/// <c>StaleProgressArchiver</c>). The order is load-bearing: the active job is
/// never touched, the resume window wins over every recovery, and a surviving
/// sentinel finishes the missed hand-off instead of re-running the agent.
/// </summary>
public static class StaleProgressPolicy
{
    public static StaleProgressVerdict Decide(StaleProgressFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.IsActiveJob)
            return new(StaleProgressAction.Leave, StaleProgressReasons.ActiveJob, "matches active job");
        if (facts.SecondsSinceActivity < facts.ResumeWindowSeconds)
            return new(StaleProgressAction.Leave, StaleProgressReasons.WithinResumeWindow, "within resume window");
        if (!string.IsNullOrWhiteSpace(facts.CompletionSentinel))
            return new(StaleProgressAction.RecoverToReview, StaleProgressReasons.SentinelSurvived,
                $"sentinel TASK_{facts.CompletionSentinel} survived; finished missed transition");
        return new(StaleProgressAction.RequeueToReady, StaleProgressReasons.RunInterrupted,
            "stale progress run interrupted with no completion sentinel; requeued to 2-ready to retry the same task");
    }
}
