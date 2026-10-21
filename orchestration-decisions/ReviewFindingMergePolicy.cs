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
            .Select(Fingerprint).Distinct(StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<ReviewFollowUpFinding> Merge(IEnumerable<ReviewFollowUpFinding> findings)
    {
        var ordered = findings.ToArray();
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var merged = new List<ReviewFollowUpFinding>();
        foreach (var item in ordered)
        {
            if (string.Equals(item.Status, "pass", StringComparison.OrdinalIgnoreCase))
            {
                merged.Add(item);
                continue;
            }
            var key = Fingerprint(item);
            if (!keys.TryGetValue(key, out var index))
            {
                keys.Add(key, merged.Count);
                merged.Add(item);
                continue;
            }
            if (ReviewGradingPolicy.IsBlockingToken(item.Status)
                && !ReviewGradingPolicy.IsBlockingToken(merged[index].Status))
                merged[index] = item;
        }
        return merged;
    }

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
