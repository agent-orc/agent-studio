using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One entry of a merged fix round: the aspects that reported it and every
/// distinct statement they made about it, in report order.
/// </summary>
public sealed record MergedReviewFinding(
    IReadOnlyList<string> Aspects,
    IReadOnlyList<ReviewFollowUpFinding> Sources);

/// <summary>
/// Merges aspect and deep-review findings into one fix round. Two findings are
/// one entry when they cite the same <c>file:line</c> locations, or, without a
/// location, when their normalized finding text is equal. The merge is
/// lossless: an entry keeps each distinct summary, evidence and finding, and
/// drops only exact repeats, so the coding agent fixes a defect once.
/// </summary>
public static partial class ReviewFindingMergePolicy
{
    public static IReadOnlyList<MergedReviewFinding> Merge(IReadOnlyList<ReviewFollowUpFinding> findings)
    {
        var groups = new List<(string Key, List<string> Aspects, List<ReviewFollowUpFinding> Sources)>();
        foreach (var finding in findings ?? [])
        {
            var key = Key(finding);
            var index = groups.FindIndex(item => item.Key == key);
            if (index < 0)
            {
                groups.Add((key, [finding.Aspect], [finding]));
                continue;
            }
            var group = groups[index];
            if (!group.Aspects.Contains(finding.Aspect, StringComparer.OrdinalIgnoreCase)) group.Aspects.Add(finding.Aspect);
            if (!group.Sources.Any(item => SameStatement(item, finding))) group.Sources.Add(finding);
        }
        return groups.Select(group => new MergedReviewFinding(group.Aspects, group.Sources)).ToArray();
    }

    private static string Key(ReviewFollowUpFinding finding)
    {
        var locations = Location().Matches($"{finding.Finding}\n{finding.EvidenceChecked}\n{finding.Summary}")
            .Select(match => match.Value.Replace('\\', '/').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return locations.Length > 0
            ? "at:" + string.Join("|", locations)
            : "text:" + Normalize(string.IsNullOrWhiteSpace(finding.Finding) ? finding.Summary : finding.Finding);
    }

    private static bool SameStatement(ReviewFollowUpFinding left, ReviewFollowUpFinding right)
        => Normalize(left.Summary) == Normalize(right.Summary)
           && Normalize(left.EvidenceChecked) == Normalize(right.EvidenceChecked)
           && Normalize(left.Finding) == Normalize(right.Finding);

    private static string Normalize(string? value)
        => Whitespace().Replace((value ?? string.Empty).Trim().ToLowerInvariant(), " ").TrimEnd('.');

    [GeneratedRegex(@"[A-Za-z0-9_./\\-]+\.[A-Za-z0-9]+:\d+")]
    private static partial Regex Location();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
