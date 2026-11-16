namespace AgentStudio.OrchestrationDecisions;

/// <summary>What a finished report review does with its planning or research card.</summary>
public enum ReportReviewAction
{
    /// <summary>The review waits for model quota or a model host; the card stays in Auto Review.</summary>
    Wait,

    /// <summary>The report has its requirement-fit verdict; a person decides on it in Human Review.</summary>
    HumanReview,

    /// <summary>No verdict could be produced; the card is parked with the reason.</summary>
    Escalate,
}

/// <param name="Ran">The requirement-fit review returned a verdict for the current delivery.</param>
/// <param name="Status">The verdict status token, for example <c>pass</c> or <c>concerns</c>.</param>
/// <param name="Waiting">The review waits for model quota or for a host with the model capability.</param>
/// <param name="ReportMissing">The delivery has no required report to review.</param>
/// <param name="Detail">The review's own sentence: the verdict summary or why it did not run.</param>
public sealed record ReportReviewOutcomeFacts(bool Ran, string? Status, bool Waiting, bool ReportMissing,
    string Detail);

public sealed record ReportReviewDecision(ReportReviewAction Action, string Reason);

/// <summary>
/// AGT-3797: a planning or research delivery gets no remote ReviewAttempt
/// (<see cref="TransitionDecisionPolicy.CreateReview"/> refuses report-only
/// cards), yet <see cref="StageDecisionPolicy.Coding"/> answers every settled
/// delivery bound for Auto Review with <see cref="TransitionAction.CreateReview"/>.
/// While the engine owns Auto Review, the lightweight report pipeline of the
/// monolith does not run either, so the card had no owner until the stranding
/// watchdog escalated it. The report review on the control plane is that owner.
/// </summary>
public static class ReportReviewPolicy
{
    /// <summary>
    /// True when the control plane's report review owns a report card in Auto
    /// Review. With the monolith owning Auto Review, its lightweight report
    /// pipeline reviews the report instead.
    /// </summary>
    public static bool HostRunsReportReview(bool reportOnly, bool monolithOwnsAutoReview)
        => reportOnly && !monolithOwnsAutoReview;

    /// <summary>
    /// Human Review decides on the report whatever the verdict; completion
    /// refuses a card without a pass. A waiting review keeps its card, and a
    /// review that produced no verdict parks the card with its reason.
    /// </summary>
    public static ReportReviewDecision Settle(ReportReviewOutcomeFacts facts)
    {
        var detail = string.IsNullOrWhiteSpace(facts.Detail) ? "No reason was recorded." : facts.Detail.Trim();
        if (facts.Waiting)
            return new(ReportReviewAction.Wait, $"The report review waits: {detail}");
        if (facts.Ran)
            return new(ReportReviewAction.HumanReview, $"Report review finished. {detail}");
        if (facts.ReportMissing)
            return new(ReportReviewAction.Escalate, $"The report review could not run: {detail}");
        return new(ReportReviewAction.Escalate,
            $"The report review produced no verdict: {detail} Run Review results once the cause is fixed.");
    }
}
