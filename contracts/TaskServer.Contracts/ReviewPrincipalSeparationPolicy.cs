namespace AgentStudio.TaskServer.Contracts;

/// <summary>Who coded a delivery: the runner of its run attempt and the principal that claimed it.</summary>
public sealed record DeliveryCodingPrincipal(string? RunnerId, string? PrincipalId);

/// <summary>Who reports a review verdict: the leased review executor and the authenticated caller, if any.</summary>
public sealed record ReviewVerdictReporter(string ReviewerRunnerId, string? PrincipalId, string? PrincipalKind);

public sealed record ReviewPrincipalSeparationDecision(bool Accepted, string? Code, string? Message)
{
    public static readonly ReviewPrincipalSeparationDecision Accept = new(true, null, null);
}

/// <summary>
/// SM-11 (security-model Dossier): a review verdict counts only when a review
/// principal other than the delivery's coding principal gives it. The runner
/// identity is compared always; principal identities are compared when both
/// sides were authenticated, so the trusted local profile without principals
/// keeps working. An authenticated caller that is not a runner principal
/// cannot give a verdict at all.
/// </summary>
public static class ReviewPrincipalSeparationPolicy
{
    public const string CoderCode = "review-principal-is-coder";
    public const string NotReviewerCode = "review-principal-not-reviewer";

    public static ReviewPrincipalSeparationDecision Decide(
        DeliveryCodingPrincipal coding, ReviewVerdictReporter reporter)
    {
        if (reporter.PrincipalKind is { Length: > 0 } kind
            && !string.Equals(kind, TaskServerPrincipalKinds.Runner, StringComparison.Ordinal))
            return new(false, NotReviewerCode,
                "Only a review runner principal may report a review verdict.");
        if (Same(coding.RunnerId, reporter.ReviewerRunnerId))
            return new(false, CoderCode,
                "The review executor coded this delivery; a verdict needs a different review principal.");
        if (Same(coding.PrincipalId, reporter.PrincipalId))
            return new(false, CoderCode,
                "The reporting principal claimed this delivery's coding attempt; a verdict needs a different review principal.");
        return ReviewPrincipalSeparationDecision.Accept;
    }

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left, right, StringComparison.Ordinal);
}
