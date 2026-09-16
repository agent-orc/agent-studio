using System.Globalization;
using System.Text;

namespace AgentStudio.Git;

/// <summary>Counts for one ref class in a sweep run.</summary>
public sealed record BranchSweepClassTotals(
    string Class,
    int Total,
    int Eligible,
    int Kept,
    int Deleted);

/// <summary>One age-histogram column.</summary>
public sealed record BranchSweepAgeBucket(string Label, int Refs);

/// <summary>Per-ref outcome of a deletion pass (scheduled reclaim or operator execute).</summary>
public sealed record BranchSweepDeletion(
    string Ref,
    string Class,
    string TipSha,
    bool Deleted,
    string Reason);

/// <summary>
/// The artifact the operator reads before enabling automatic deletion: the
/// branch state of one project repository at one point in time, what the shared
/// retention policy decided about every ref, and what (if anything) was
/// actually deleted. Persisted as JSON plus a short markdown summary by
/// <see cref="BranchSweepReportStore"/>.
/// </summary>
public sealed record BranchSweepReport(
    string Project,
    string? RepositoryPath,
    string Mode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    BranchRetentionWindows Windows,
    int RefsBefore,
    int RefsAfter,
    IReadOnlyList<BranchSweepClassTotals> Totals,
    IReadOnlyList<BranchSweepAgeBucket> AgeHistogram,
    IReadOnlyList<BranchSweepCandidate> Candidates,
    IReadOnlyList<BranchSweepDeletion> Deletions,
    string? Error)
{
    public int TotalRefs => Candidates.Count;
    public int EligibleCount => Candidates.Count(candidate => candidate.Eligible);
    public int DeletedCount => Deletions.Count(deletion => deletion.Deleted);
    public int DeleteFailedCount => Deletions.Count(deletion => !deletion.Deleted);

    /// <summary>Empty report for a project whose repository could not be resolved or refreshed.</summary>
    public static BranchSweepReport Failed(
        string project, string? repositoryPath, string mode, DateTimeOffset at, string error)
        => new(project, repositoryPath, mode, at, at, BranchRetentionWindows.Default,
            0, 0, [], [], [], [], error);
}

/// <summary>
/// Aggregation and markdown rendering for a sweep run. Pure so the report shape
/// is pinned by tests without touching git or the filesystem.
/// </summary>
public static class BranchSweepReportBuilder
{
    public static IReadOnlyList<BranchSweepClassTotals> Totals(
        IReadOnlyList<BranchSweepCandidate> candidates,
        IReadOnlyList<BranchSweepDeletion> deletions)
    {
        var deletedByRef = deletions
            .Where(deletion => deletion.Deleted)
            .Select(deletion => deletion.Ref)
            .ToHashSet(StringComparer.Ordinal);

        return BranchSweepClasses.Order
            .Select(className =>
            {
                var inClass = candidates.Where(c => c.Class == className).ToList();
                var deleted = inClass.Count(c => deletedByRef.Contains(c.Ref));
                return new BranchSweepClassTotals(
                    className,
                    inClass.Count,
                    inClass.Count(c => c.Eligible),
                    inClass.Count - deleted,
                    deleted);
            })
            .Where(totals => totals.Total > 0)
            .ToList();
    }

    public static IReadOnlyList<BranchSweepAgeBucket> AgeHistogram(
        IReadOnlyList<BranchSweepCandidate> candidates)
    {
        var counts = candidates
            .GroupBy(candidate => BranchSweepPolicy.AgeBucketOf(candidate.AgeDays), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return BranchSweepPolicy.AgeBucketLabels()
            .Select(label => new BranchSweepAgeBucket(label, counts.GetValueOrDefault(label)))
            .Where(bucket => bucket.Refs > 0)
            .ToList();
    }

    /// <summary>
    /// The short markdown summary that sits next to the JSON report. It is the
    /// operator-readable half of the artifact: totals by class and decision,
    /// the age distribution, and the deletions actually performed.
    /// </summary>
    public static string RenderMarkdown(BranchSweepReport report)
    {
        var text = new StringBuilder();
        text.Append("# Branch sweep - ").Append(report.Project).Append(" - ")
            .AppendLine(Stamp(report.StartedAtUtc));
        text.AppendLine();
        text.Append("- Mode: `").Append(report.Mode).AppendLine("`");
        text.Append("- Repository: ")
            .AppendLine(string.IsNullOrWhiteSpace(report.RepositoryPath) ? "(unresolved)" : $"`{report.RepositoryPath}`");
        text.Append("- Remote refs before: ").Append(report.RefsBefore)
            .Append(", after: ").AppendLine(report.RefsAfter.ToString(CultureInfo.InvariantCulture));
        text.Append("- Eligible by policy: ").Append(report.EligibleCount)
            .Append(", deleted: ").Append(report.DeletedCount)
            .Append(", delete failed: ").AppendLine(report.DeleteFailedCount.ToString(CultureInfo.InvariantCulture));
        text.Append("- Windows (days): task ").Append(report.Windows.TaskDays)
            .Append(", salvage ").Append(report.Windows.SalvageDays)
            .Append(", quarantine ").Append(report.Windows.QuarantineDays)
            .Append(", abandoned ").AppendLine(report.Windows.AbandonedDays.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(report.Error))
            text.Append("- Error: ").AppendLine(report.Error);

        text.AppendLine();
        text.AppendLine("## Refs by class");
        text.AppendLine();
        text.AppendLine("| Class | Refs | Eligible | Kept | Deleted |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var totals in report.Totals)
        {
            text.Append("| ").Append(totals.Class)
                .Append(" | ").Append(totals.Total)
                .Append(" | ").Append(totals.Eligible)
                .Append(" | ").Append(totals.Kept)
                .Append(" | ").Append(totals.Deleted)
                .AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("## Tip age");
        text.AppendLine();
        text.AppendLine("| Age | Refs |");
        text.AppendLine("|---|---:|");
        foreach (var bucket in report.AgeHistogram)
            text.Append("| ").Append(bucket.Label).Append(" | ").Append(bucket.Refs).AppendLine(" |");

        text.AppendLine();
        text.AppendLine("## Decisions");
        text.AppendLine();
        text.AppendLine("| Decision | Refs |");
        text.AppendLine("|---|---:|");
        foreach (var group in report.Candidates
            .GroupBy(candidate => candidate.Decision, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            text.Append("| ").Append(group.Key).Append(" | ").Append(group.Count()).AppendLine(" |");
        }

        if (report.Deletions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Deletions");
            text.AppendLine();
            text.AppendLine("| Ref | Class | Tip | Outcome | Reason |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (var deletion in report.Deletions)
            {
                text.Append("| `").Append(deletion.Ref)
                    .Append("` | ").Append(deletion.Class)
                    .Append(" | `").Append(ShortSha(deletion.TipSha))
                    .Append("` | ").Append(deletion.Deleted ? "deleted" : "kept")
                    .Append(" | ").Append(deletion.Reason)
                    .AppendLine(" |");
            }
        }

        return text.ToString();
    }

    /// <summary>Filesystem-safe UTC stamp used for both the file name and the heading.</summary>
    public static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string ShortSha(string sha)
        => sha.Length <= 7 ? sha : sha[..7];
}
