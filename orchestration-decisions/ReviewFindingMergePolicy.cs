using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>One actionable item per cited defect across aspect and deep review.</summary>
public static class ReviewFindingMergePolicy
{
    public static IReadOnlyList<string> BlockingFingerprints(IEnumerable<ReviewFollowUpFinding> findings)
        => Merge(findings)
            .Where(item => ReviewGradingPolicy.IsBlockingToken(item.Status)
                && !ReviewRoundBudgetPolicy.IsUncitedBlock(item.Classification))
            .Select(item => item.Aspect + "\n" + Fingerprint(item)).Distinct(StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<ReviewFollowUpFinding> Merge(IEnumerable<ReviewFollowUpFinding> findings)
    {
        var ordered = findings.ToArray();
        var merged = new List<ReviewFollowUpFinding>();
        foreach (var item in ordered)
        {
            if (string.Equals(item.Status, "pass", StringComparison.OrdinalIgnoreCase)
                || IsMalformedReply(item))
            {
                merged.Add(item);
                continue;
            }
            var key = Fingerprint(item);
            var index = merged.FindIndex(existing => Fingerprint(existing) == key
                && (IsDeepReview(existing) || IsDeepReview(item))
                && !IsMalformedReply(existing)
                && !IsProtectedBlock(existing) && !IsProtectedBlock(item));
            if (index < 0)
            {
                merged.Add(item);
                continue;
            }
            if (ReviewGradingPolicy.IsBlockingToken(item.Status)
                && !ReviewGradingPolicy.IsBlockingToken(merged[index].Status))
                merged[index] = item;
        }
        return merged;
    }

    private static bool IsMalformedReply(ReviewFollowUpFinding item)
        => item.InfrastructureFailure
           || item.Classification?.Contains("malformed:", StringComparison.OrdinalIgnoreCase) == true
           || string.Equals(item.Classification, "review:unparseable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Classification, ReviewOutcomeKinds.ReviewInfra, StringComparison.OrdinalIgnoreCase);

    private static bool IsDeepReview(ReviewFollowUpFinding item)
        => string.Equals(item.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedBlock(ReviewFollowUpFinding item)
        => ReviewGradingPolicy.IsBlockingToken(item.Status)
            && item.Aspect is ("build-tests" or "review-verification" or "tests-and-evidence");

    public static string Fingerprint(ReviewFollowUpFinding item)
    {
        static string Normalize(string? value)
            => Regex.Replace(value?.Trim().ToLowerInvariant() ?? string.Empty, @"\s+", " ");
        var cited = Normalize(item.EvidenceChecked);
        var defect = Normalize(item.Finding);
        if (cited.Length > 0 && defect.Length > 0
            && defect is not ("none" or "n/a")) return cited + "\n" + defect;
        return "summary\n" + Normalize(item.Summary);
    }
}
