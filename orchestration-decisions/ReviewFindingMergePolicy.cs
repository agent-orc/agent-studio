using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>One actionable item per cited defect across aspect and deep review.</summary>
public static class ReviewFindingMergePolicy
{
    private static readonly HashSet<string> ProtectedGateAspects = new(
        ["build-tests", "review-verification", "tests-and-evidence", "lint"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ReviewVerdictDto> MergeVerdicts(IEnumerable<ReviewVerdictDto> verdicts)
    {
        var pairs = verdicts.Select(verdict => (Verdict: verdict,
            Finding: new ReviewFollowUpFinding(verdict.Aspect, verdict.Status, verdict.Summary,
                verdict.EvidenceChecked, verdict.Missing, verdict.Classification))).ToArray();
        return Merge(pairs.Select(pair => pair.Finding))
            .Select(finding => pairs.First(pair => ReferenceEquals(pair.Finding, finding)).Verdict)
            .ToArray();
    }

    public static IReadOnlyList<string> BlockingFingerprints(IEnumerable<ReviewFollowUpFinding> findings)
        => Merge(findings)
            .Where(item => ReviewGradingPolicy.IsBlockingToken(item.Status)
                && !ReviewRoundBudgetPolicy.IsUncitedBlock(item.Classification))
            .Select(Fingerprint).Distinct(StringComparer.Ordinal).ToArray();

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
            var index = merged.FindIndex(existing =>
                !IsProtectedGate(existing.Aspect) && !IsProtectedGate(item.Aspect)
                &&
                string.Equals(existing.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase)
                != string.Equals(item.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase)
                && Fingerprint(existing) == key);
            if (index < 0)
            {
                merged.Add(item);
                continue;
            }
            if (ReviewGradingPolicy.IsBlockingToken(item.Status)
                && (!ReviewGradingPolicy.IsBlockingToken(merged[index].Status)
                    || string.Equals(item.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase)))
                merged[index] = item;
        }
        return merged;
    }

    private static bool IsMalformedReply(ReviewFollowUpFinding item)
        => item.InfrastructureFailure
           || item.Classification?.Contains("malformed:", StringComparison.OrdinalIgnoreCase) == true
           || string.Equals(item.Classification, "review:unparseable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Classification, ReviewOutcomeKinds.ReviewInfra, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedGate(string aspect)
        => ProtectedGateAspects.Contains(aspect);

    public static string Fingerprint(ReviewFollowUpFinding item)
    {
        static string Normalize(string? value)
            => Regex.Replace(value?.Trim().ToLowerInvariant() ?? string.Empty, @"\s+", " ");
        var cited = Normalize(item.EvidenceChecked);
        var defect = Normalize(item.Finding);
        if (cited.Length > 0 && defect.Length > 0
            && defect is not ("none" or "n/a")) return cited + "\n" + defect;
        // A shared summary without a citation does not prove two aspects found
        // the same defect. Keep the aspect in this fallback identity.
        return "summary\n" + Normalize(item.Aspect) + "\n" + Normalize(item.Summary);
    }
}
