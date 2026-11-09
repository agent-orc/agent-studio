using System.Text.RegularExpressions;
namespace AgentStudio.OrchestrationDecisions;

public enum ReviewConcernActionability { Actionable, Advisory }

public sealed record ReviewPreAdmissionDecision(bool WaitForFixRound, bool DeferToBatch);

public static class ReviewPreAdmissionPolicy
{
    public static ReviewPreAdmissionDecision Decide(bool batchDeferredAccepted,
        AgentStudio.TaskServer.Contracts.ReviewFollowUpAction followUp)
    {
        var wait = followUp == AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.ReviewConcernRound;
        return new(wait, batchDeferredAccepted && !wait);
    }
}

/// <summary>
/// One review follow-up policy shared by local aspect review and Remote Review.
/// Session mechanics, lane writes, and prompt persistence remain application
/// concerns; this class owns only severity, actionability, and the round bound.
/// </summary>
public static partial class ReviewFollowUpPolicy
{
    [GeneratedRegex(@"(?:^|[\s`'(])(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+|\b[A-Za-z0-9_-]+\.(?:cs|ts|tsx|js|scss|css|html|md|json|ya?ml|xml|sh)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileReferenceRegex();

    [GeneratedRegex(@"`(?:[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?|[a-z][a-z0-9]*(?:[A-Z][a-z0-9]+)+)(?:\([^`]*\))?`|\b[A-Z][A-Za-z0-9_]*\.[A-Z][A-Za-z0-9_]*\b|\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b")]
    private static partial Regex SymbolReferenceRegex();

    [GeneratedRegex(@"\b(miss(?:ing|es)?|absent|omit(?:s|ted)?|lack(?:s|ing)?|wrong|incorrect|fails?|breaks?|throws?|prevent(?:s|ed)?|unguarded|unlocked|without\s+(?:(?:its|a|the|any)\s+)?(?:lock|guard|error handling|synchronization|retry|validation|null check)|race|concurren(?:t|cy)|idempoten(?:t|cy)|conflict|mismatch|compar(?:e|es|ed|ing)|scan(?:s|ning)?|reads?\s+every|unbounded|doesn.t|cannot|no.op|dead assertion|contract mismatch)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BehaviourGapRegex();

    [GeneratedRegex(@"\b(?:miss(?:ing|es)?|lack(?:s|ing)?|wrong|incorrect|fails?|breaks?|without)\s+(?:(?:the|a|proper|consistent)\s+)?(?:style|naming|formatting|cosmetic|lint)(?:\s+(?:convention|checks?))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleOnlyBehaviorRegex();

    public static ReviewFollowUpDecision Decide(
        IEnumerable<ReviewFollowUpFinding> findings,
        int concernRoundsUsed,
        int maxConcernRounds)
    {
        var all = ReviewFindingMergePolicy.Merge(findings ?? []);
        var grade = ReviewGradingPolicy.Grade(all.Select(item => item.Status));
        var aspectRetry = all.Where(IsAspectInfrastructureResult).ToArray();
        if (aspectRetry.Length > 0)
        {
            return new ReviewFollowUpDecision(
                grade,
                ReviewFollowUpAction.RetryAspect,
                aspectRetry,
                $"Aspect verdict infrastructure failed for {string.Join(", ", aspectRetry.Select(item => item.Aspect))}; the aspect retry was exhausted.");
        }

        var blocks = all.Where(item => ReviewGradingPolicy.IsBlockingToken(item.Status)).ToArray();
        if (blocks.Length > 0)
        {
            return new ReviewFollowUpDecision(
                grade,
                ReviewFollowUpAction.ReviewFindingRound,
                blocks,
                $"{blocks.Length} blocking review finding(s) require a coding round.");
        }

        var actionable = all.Where(IsActionableConcern).ToArray();
        if (grade == ReviewGrade.PassWithConcerns
            && actionable.Length > 0
            && Math.Max(0, maxConcernRounds) > concernRoundsUsed)
        {
            return new ReviewFollowUpDecision(
                grade,
                ReviewFollowUpAction.ReviewConcernRound,
                actionable,
                $"{actionable.Length} actionable concern(s) qualify for bounded fix round {concernRoundsUsed + 1} of {Math.Max(0, maxConcernRounds)}.");
        }

        return new ReviewFollowUpDecision(
            grade,
            ReviewFollowUpAction.Accept,
            actionable,
            grade == ReviewGrade.PassWithConcerns
                ? "Concerns remain, but the configured concern-round budget is exhausted or no concern is actionable."
                : "Review passed without an actionable follow-up.");
    }

    public static bool IsActionableConcern(ReviewFollowUpFinding item)
    {
        return ReviewGradingPolicy.IsConcernToken(item.Status)
               && Classify(item) == ReviewConcernActionability.Actionable;
    }

    public static ReviewConcernActionability Classify(ReviewFollowUpFinding item)
    {
        if (!ReviewGradingPolicy.IsConcernToken(item.Status)
            && !ReviewGradingPolicy.IsBlockingToken(item.Status)
            || IsAspectInfrastructureResult(item))
            return ReviewConcernActionability.Advisory;
        // EvidenceChecked lists what the reviewer read, not the defective
        // location. Require a location and a behavior gap in the finding or
        // summary. A bare "missing" field or a style request is advisory.
        var description = string.Join(" ", new[] { item.Summary, item.Finding,
                ExtractFindingSection(item.ReportBody) }.Where(Meaningful));
        if (description.Length == 0) return ReviewConcernActionability.Advisory;
        var hasLocation = FileReferenceRegex().IsMatch(description)
                          || SymbolReferenceRegex().IsMatch(description);
        if (!hasLocation) return ReviewConcernActionability.Advisory;
        var hasBehavior = BehaviourGapRegex().IsMatch(StyleOnlyBehaviorRegex().Replace(description, ""));
        return hasBehavior ? ReviewConcernActionability.Actionable : ReviewConcernActionability.Advisory;
    }

    public static string? ExtractFindingSection(string? reportBody)
    {
        if (string.IsNullOrWhiteSpace(reportBody)) return null;
        var lines = reportBody.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line =>
            line.TrimStart().StartsWith("## Finding", StringComparison.OrdinalIgnoreCase)
            || line.TrimStart().StartsWith("## Findings", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var body = lines.Skip(start + 1)
            .TakeWhile(line => !line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        var text = string.Join(" ", body);
        return text.Length == 0 ? null : text;
    }

    // Only runner-authored markers identify review infrastructure; summaries describe product findings too.
    private static bool IsAspectInfrastructureResult(ReviewFollowUpFinding item)
        => item.InfrastructureFailure
           || string.Equals(item.Classification, "review:unparseable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Classification, ReviewOutcomeKinds.ReviewInfra, StringComparison.OrdinalIgnoreCase);

    private static bool Meaningful(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(value.Trim(), "n/a", StringComparison.OrdinalIgnoreCase);
}
