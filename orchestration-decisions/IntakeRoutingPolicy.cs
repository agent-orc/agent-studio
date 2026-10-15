namespace AgentStudio.OrchestrationDecisions;

/// <summary>The verdict of one background intake run on a Ready card.</summary>
/// <param name="Outcome">The intake outcome name, for example <c>AlreadyDone</c>.</param>
/// <param name="Reason">The intake's reason text.</param>
public sealed record IntakeOperatorFacts(string Outcome, string? Reason);

/// <summary>
/// Done-precheck routing of background intake (formerly inline in the
/// monolith's <c>IntakeHostedService</c>). A card whose prompt declares it
/// already finished is never executed and never completed automatically: a
/// person confirms it in Human Review. Every other outcome leaves the card.
/// </summary>
public static class IntakeRoutingPolicy
{
    public const string AlreadyDone = "AlreadyDone";
    public const string AlreadyDoneReason = "intake-already-done";
    public const string NoRouting = "intake-no-routing";

    public static bool RoutesToHumanReview(IntakeOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return string.Equals(facts.Outcome, AlreadyDone, StringComparison.Ordinal);
    }
}
