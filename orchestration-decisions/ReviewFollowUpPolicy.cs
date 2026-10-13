using System.Text.RegularExpressions;
namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One review follow-up policy shared by local aspect review and Remote Review.
/// Session mechanics, lane writes, and prompt persistence remain application
/// concerns; this class owns only severity, actionability, and the round bound.
/// </summary>
public static partial class ReviewFollowUpPolicy
{
    [GeneratedRegex(@"(?:^|[\s`'(])(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+|\b[A-Za-z0-9_-]+\.(?:cs|ts|tsx|js|scss|css|html|md|json|ya?ml|xml|sh)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileReferenceRegex();

    public static ReviewFollowUpDecision Decide(
        IEnumerable<ReviewFollowUpFinding> findings,
        int concernRoundsUsed,
        int maxConcernRounds)
    {
        var all = findings?.ToArray() ?? [];
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
        if (!ReviewGradingPolicy.IsConcernToken(item.Status) || IsAspectInfrastructureResult(item))
            return false;
        if (Meaningful(item.Finding) || Meaningful(ExtractFindingSection(item.ReportBody))) return true;
        return (Meaningful(item.EvidenceChecked) && FileReferenceRegex().IsMatch(item.EvidenceChecked!))
               || (Meaningful(item.Summary) && FileReferenceRegex().IsMatch(item.Summary!));
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
           || string.Equals(item.Classification, "ReviewInfra", StringComparison.OrdinalIgnoreCase);

    private static bool Meaningful(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(value.Trim(), "n/a", StringComparison.OrdinalIgnoreCase);
}

